using System.Diagnostics;
using System.Globalization;
using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;
using LocalCaption.Core.Transcripts;
using Kind = LocalCaption.Core.Interview.InterviewLibraryIndex.DocumentKind;

namespace LocalCaption.Interview;

/// <summary>
/// One interview (SPEC-13–15): the setup (CV, pasted JD), the skill steps the user runs by
/// hand, and every turn on the interview's single Codex thread — the UI-free port of macOS
/// <c>InterviewController.swift</c>. The thread opens on first need: a skill step, an Ask, or
/// Start. The record is saved to the database after every state change (answer text streaming
/// in is kept in memory only until the turn ends).
/// </summary>
/// <remarks>
/// <para><b>Threading (the Swift class is <c>@MainActor</c>).</b> The controller has one owner
/// thread: the thread that constructs it, which must have a <see cref="SynchronizationContext"/>
/// that runs posted work back on that same thread — the WPF dispatcher in the app, a
/// single-threaded test context in the tests. Every public method and setter must be called on
/// that thread; a call from another thread throws <see cref="InvalidOperationException"/>
/// synchronously — the <c>…Async</c> methods too, before any task exists, so a fire-and-forget
/// caller (a hotkey) cannot lose it in a discarded task. Nothing here uses
/// <c>ConfigureAwait(false)</c>: every await — on the engine's event stream, on the store, on
/// the platform services — resumes on the owner's context, so all state changes and every
/// <see cref="Changed"/> / <see cref="SessionsChanged"/> event happen on the owner thread.
/// No method blocks; long waits are awaited.</para>
/// <para><b>Switching interviews.</b> <see cref="Load"/>, <see cref="ResetForNewInterview"/>
/// and <see cref="DiscardUnstartedAsync"/> may run while work for the previous interview is
/// still in flight (the UI should wait for <see cref="IsBusy"/> to clear, but need not). That
/// work then finishes on the record it started on — a turn's final status and a summary are
/// saved to that record, never to the one now shown — and nothing from it reaches the new
/// interview's state, queue or status line.</para>
/// <para><b>Caption only mode</b> (SPEC-16 §4.1): constructing the controller, reading it,
/// editing the draft, polling the clipboard and resetting never touch the engine. Codex starts
/// only from an interview action: <see cref="EnsureThreadAsync"/> and everything that sends a
/// turn, <see cref="RecordingStarted"/>, <see cref="GenerateSummaryAsync"/>, or
/// <see cref="DiscardUnstartedAsync"/> of an interview that has a thread.</para>
/// <para>The library's own <see cref="InterviewLibrary.Changed"/> event (skills loaded in
/// Settings) is not forwarded; a view showing <see cref="MissingSkills"/> listens to it too.</para>
/// </remarks>
public sealed class InterviewController
{
    /// <summary>The tray holds at most this many screenshots.</summary>
    public const int MaxPendingImages = 10;

    /// <summary>How many polls in a row may fail to read the same clipboard change before it is skipped.</summary>
    internal const int MaxClipboardReadAttempts = 5;

    private static readonly string TrayFullMessage = $"The prompt already has {MaxPendingImages} screenshots.";

    private readonly Store _store;
    private readonly Func<Config.InterviewGroup> _config;
    private readonly IScreenCapture? _screen;
    private readonly IClipboardImages? _clipboard;
    private readonly InterviewControllerOptions _options;
    private readonly int _ownerThread;

    // The interview shown (Apply).
    private InterviewDraft _draft = new();
    private InterviewThreadState _threadState = new InterviewThreadState.None();
    private InterviewRecord? _record;
    private bool _needsResume;
    private string _summaryText = "";

    // Per-interview transient state (ResetTransient).
    private int _generation;
    private int? _streamingTurn;
    private InterviewRecord? _streamingRecord;
    private string? _status;
    private bool _preparing;
    private InterviewStep? _preparationFailedAt;
    private bool _summarizing;
    private string? _summaryError;
    private List<PendingImage> _pendingImages = [];
    private (Guid Uuid, DateTimeOffset Date)? _recordingStart;
    private Task<string?>? _opening;
    private AskSelection.Mark? _mark;
    private Request? _queued;
    private long? _capturedSequence;
    private (string Id, string Text)? _cvTextCache;
    private TaskCompletionSource _streamEnded = Completed();

    // Controller-wide.
    private bool _showingPreparation = true;
    private bool _capturing;
    private long? _seenSequence;
    private (long Sequence, int Attempts)? _clipboardReadFailure;
    private string? _modelNotice;
    private readonly HashSet<string> _discarded = [];

    /// <param name="store">The database; used on the owner thread only.</param>
    /// <param name="library">CVs and skills.</param>
    /// <param name="codex">The engine service: model resolution, image support, and its <see cref="CodexService.Engine"/>.</param>
    /// <param name="config">The live <c>interview</c> config group, read at every use (Settings may change it).</param>
    /// <param name="screen">Region screenshot; <c>null</c>: the Screenshot hotkey does nothing.</param>
    /// <param name="clipboard">Clipboard images; <c>null</c>: <see cref="PollClipboard"/> does nothing.</param>
    /// <param name="options">Paths, waits and the beep; defaults are the app's.</param>
    /// <param name="existing">
    /// Reopen a saved interview (history viewer): its record, and the ability to (re)generate the
    /// summary on the same thread — resumed first, since this run may not know it.
    /// </param>
    /// <exception cref="InvalidOperationException">The calling thread has no <see cref="SynchronizationContext"/>.</exception>
    public InterviewController(Store store, InterviewLibrary library, CodexService codex,
                               Func<Config.InterviewGroup> config, IScreenCapture? screen = null,
                               IClipboardImages? clipboard = null, InterviewControllerOptions? options = null,
                               InterviewRecord? existing = null)
    {
        if (SynchronizationContext.Current is null)
            throw new InvalidOperationException(
                "InterviewController must be created on a thread with a SynchronizationContext (the UI thread).");
        _ownerThread = Environment.CurrentManagedThreadId;
        _store = store;
        Library = library;
        Codex = codex;
        _config = config;
        _screen = screen;
        _clipboard = clipboard;
        _options = options ?? new InterviewControllerOptions();
        Apply(existing, existing is null ? InitialDraft() : DraftFor(existing));
    }

    /// <summary>Raised on the owner thread after any state property changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// A saved session became an interview (End interview linked it): the sessions list should
    /// re-read. Swift posts <c>.sessionsChanged</c>.
    /// </summary>
    public event EventHandler? SessionsChanged;

    /// <summary>The user's CVs and skills.</summary>
    public InterviewLibrary Library { get; }

    /// <summary>The answer engine's app-wide state.</summary>
    public CodexService Codex { get; }

    private IAnswerEngine Engine => Codex.Engine;
    private Config.InterviewGroup Cfg => _config();

    /// <summary>The live transcript at press time; set by the session screen.</summary>
    public Func<(IReadOnlyList<AskSelection.Segment> Segments, string Interim, int AudioMs)>? TranscriptSource
    {
        get;
        set { VerifyAccess(); field = value; }
    }

    // ── State ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The preparation form. Immutable: replace it (<c>Draft = Draft with { … }</c>); a
    /// different value raises <see cref="Changed"/>.
    /// </summary>
    public InterviewDraft Draft
    {
        get => _draft;
        set { VerifyAccess(); Set(ref _draft, value); }
    }

    /// <summary>Where the thread stands.</summary>
    public InterviewThreadState ThreadState => _threadState;

    /// <summary>The interview, once something created it. Treat as read-only: the controller owns it.</summary>
    public InterviewRecord? Record => _record;

    /// <summary>The turn whose answer is streaming, if any.</summary>
    public int? StreamingTurn => _streamingTurn;

    /// <summary>The orange status line, if any.</summary>
    public string? Status => _status;

    /// <summary>
    /// SPEC-16 §9.4: the configured model is not offered, so another one is used (the Mac falls
    /// back silently). Set once per controller, when a thread first opens with the fallback;
    /// kept apart from <see cref="Status"/> so later status lines don't overwrite it.
    /// </summary>
    public string? ModelNotice => _modelNotice;

    /// <summary>Start preparation is running.</summary>
    public bool Preparing => _preparing;

    /// <summary>
    /// Interview mode shows the preparation alone until it's done, then only the captions and
    /// answers (owner, 2026-10-02). Completing Start preparation, Skip, Start, or opening a saved
    /// interview leaves it; the top bar's Preparation button returns before recording.
    /// </summary>
    public bool ShowingPreparation
    {
        get => _showingPreparation;
        set { VerifyAccess(); Set(ref _showingPreparation, value); }
    }

    /// <summary>The step Start preparation stopped at, so Continue can resume there.</summary>
    public InterviewStep? PreparationFailedAt => _preparationFailedAt;

    /// <summary>The summary as it streams, then as saved.</summary>
    public string SummaryText => _summaryText;

    /// <summary>A summary turn is running.</summary>
    public bool Summarizing => _summarizing;

    /// <summary>Why the last summary failed.</summary>
    public string? SummaryError => _summaryError;

    /// <summary>Screenshots added to the current prompt; the next Ask or Send takes them all.</summary>
    public IReadOnlyList<PendingImage> PendingImages => _pendingImages;

    /// <summary>The area selector is on screen.</summary>
    public bool Capturing => _capturing;

    /// <summary>Turns shown in the Answers panel.</summary>
    public IReadOnlyList<InterviewTurn> Turns => _record?.Turns ?? [];

    /// <summary>An answer is streaming.</summary>
    public bool IsStreaming => _streamingTurn is not null;

    /// <summary>
    /// Something is in flight, so the panel should not switch to another interview — as Swift's
    /// <c>isBusy</c>: streaming, preparing or summarizing. A thread that is only
    /// <see cref="InterviewThreadState.Opening"/> does not count (Swift excludes it too): switching
    /// then is safe — the thread, when it arrives, is archived instead of being written into the
    /// interview now shown.
    /// </summary>
    public bool IsBusy => IsStreaming || _preparing || _summarizing;

    /// <summary>A preparation that hasn't started recording yet — opening another interview discards it.</summary>
    public bool HasUnstartedPreparation => _record is not null && _record.StartedAt is null && _recordingStart is null;

    /// <summary>Briefing from a schema-1 record made with the old Prepare button (shown in history).</summary>
    public string LegacyBriefing => _record?.Prep.Briefing ?? "";

    /// <summary>The thread is open.</summary>
    public bool IsOpen => _threadState is InterviewThreadState.Open;

    /// <summary>End interview has run.</summary>
    public bool IsFinished => _record?.EndedAt is not null;

    // ── Loading ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sessions → Open in interview panel: show a saved interview here, as right after End
    /// interview — summary and follow-ups continue its thread (resumed on first use).
    /// </summary>
    public void Load(InterviewRecord saved)
    {
        VerifyAccess();
        ResetTransient();
        Apply(saved, DraftFor(saved));
        _showingPreparation = false;
        OnChanged();
    }

    /// <summary>Show <paramref name="rec"/> (or none) with <paramref name="draft"/>: the constructor, Load, Reset and Discard.</summary>
    private void Apply(InterviewRecord? rec, InterviewDraft draft)
    {
        _record = rec;
        _draft = draft;
        _threadState = rec?.ThreadId is null ? new InterviewThreadState.None() : new InterviewThreadState.Open();
        _needsResume = rec?.ThreadId is not null;   // a saved thread may be unknown to this Codex run
        _summaryText = rec?.SummaryText ?? "";
    }

    /// <summary>
    /// Forget everything that belongs to the interview shown: work still in flight for it sees
    /// the generation change and stops touching this controller's state (it still finishes on
    /// its own record).
    /// </summary>
    private void ResetTransient()
    {
        _generation++;
        SetStreaming(null, null);
        _status = null;
        _preparing = false;
        _preparationFailedAt = null;
        _summarizing = false;
        _summaryError = null;
        _pendingImages = [];
        _recordingStart = null;
        _opening = null;
        _mark = null;
        _queued = null;
        _capturedSequence = null;
        _cvTextCache = null;
    }

    private static InterviewDraft DraftFor(InterviewRecord rec) => new()
    {
        Candidate = rec.Setup.Candidate ?? "",
        Company = rec.Setup.Company,
        Step = rec.Setup.Step ?? 1,
        CvId = rec.Setup.DocumentIds.FirstOrDefault(),
        JobDescription = rec.Setup.JdTextInline ?? "",
        Profile = InterviewSteps.ProfileFromRaw(rec.ActiveProfile),
        LiveCoding = rec.LiveCodingActive,
    };

    /// <summary>Prefill from the most recent interview: interviewee, CV (if it still exists), profile, live coding; else the newest CV.</summary>
    private InterviewDraft InitialDraft()
    {
        var draft = new InterviewDraft();
        InterviewRecord? last = null;
        try { last = _store.LatestInterview(); }
        catch (Exception e) { InterviewLog.Write($"reading the last interview failed: {e.Message}"); }
        var cvs = Library.Documents(Kind.Cv);
        if (last is not null)
        {
            var ids = last.Setup.DocumentIds.ToHashSet();
            draft = draft with
            {
                Candidate = last.Setup.Candidate ?? "",
                CvId = cvs.FirstOrDefault(c => ids.Contains(c.Id))?.Id,
                Profile = InterviewSteps.ProfileFromRaw(last.ActiveProfile),
                LiveCoding = last.LiveCodingActive,
            };
        }
        return draft.CvId is null ? draft with { CvId = cvs.LastOrDefault()?.Id } : draft;
    }

    // ── Thread ───────────────────────────────────────────────────────────────────────────

    /// <summary>The interview's thread id, opening it (and creating the record) on first need; <c>null</c> when that fails.</summary>
    public Task<string?> EnsureThreadAsync()
    {
        VerifyAccess();
        return EnsureThreadCoreAsync();
    }

    private async Task<string?> EnsureThreadCoreAsync()
    {
        if (_record?.ThreadId is { } t) return t;
        if (_opening is { } opening) return await opening;
        var task = OpenThreadAsync();
        _opening = task;
        try { return await task; }
        finally
        {
            if (_opening == task) _opening = null;   // a reset may have cleared it, and a newer open set it
        }
    }

    private async Task<string?> OpenThreadAsync()
    {
        if (_record is null && !CreateRecord()) return null;
        var rec = _record!;
        var gen = _generation;
        Set(ref _threadState, new InterviewThreadState.Opening());
        var cfg = Cfg;
        var model = Codex.ResolvedModel(cfg.Model);
        string thread;
        try
        {
            thread = await Engine.StartThreadAsync(new ThreadConfig(
                model, InterviewPrompt.BaseInstructions(cfg.AnswerLengthValue, cfg.CustomInstructions)));
        }
        catch (Exception e)
        {
            if (!StillCurrent(rec, gen))
            {
                InterviewLog.Write($"opening a thread for interview {rec.Id} failed after it was closed: {e.Message}");
                return null;
            }
            _threadState = new InterviewThreadState.Failed(e.Message);
            _status = e.Message;
            OnChanged();
            return null;
        }
        if (!StillCurrent(rec, gen))
        {
            // Load, Reset or Discard ran while the thread opened: it belongs to no interview shown
            // now, so it must not be written into the current record. Archive it (it may hold the CV).
            InterviewLog.Write($"interview {rec.Id} was closed while its thread opened; archiving {thread}");
            await ArchiveAsync(thread);
            return null;
        }
        rec.ThreadId = thread;
        rec.Model = model;
        _needsResume = false;   // a thread this run opened needs no resume
        Persist();
        _threadState = new InterviewThreadState.Open();
        // SPEC-16 §9.4: the Mac falls back to another model silently; say so once.
        if (_modelNotice is null && Codex.ModelFallbackNotice(cfg.Model) is { } notice) _modelNotice = notice;
        OnChanged();
        return thread;
    }

    private bool CreateRecord()
    {
        var cfg = Cfg;
        var rec = new InterviewRecord(
            name: NameFromJd(_draft.JobDescription), createdAt: TimeFormat.Iso(DateTimeOffset.Now),
            model: Codex.ResolvedModel(cfg.Model), reasoningEffort: cfg.ReasoningEffort,
            setup: new InterviewSetup
            {
                Company = _draft.Company.Trim(),
                DocumentIds = _draft.CvId is { } cv ? [cv] : [],
                JdTextInline = _draft.JobDescription.Length == 0 ? null : _draft.JobDescription,
                Instructions = cfg.CustomInstructions,
                AnswerLength = cfg.AnswerLengthValue.Raw(),
            });
        rec.Setup.Candidate = Trimmed(_draft.Candidate);
        rec.Setup.Step = _draft.Step;
        StampRecordingStart(rec);
        try
        {
            _store.SaveInterview(rec);
        }
        catch (Exception e)
        {
            var message = $"Could not save the interview: {e.Message}";
            _threadState = new InterviewThreadState.Failed(message);
            _status = message;
            OnChanged();
            return false;
        }
        _record = rec;
        OnChanged();
        return true;
    }

    /// <summary>Link <paramref name="rec"/> to the capture started by <see cref="RecordingStarted"/>, if any.</summary>
    private void StampRecordingStart(InterviewRecord rec)
    {
        if (_recordingStart is not { } start) return;
        rec.StartedAt = TimeFormat.Iso(start.Date);
        rec.CaptureSessionUuid = UuidString(start.Uuid);
    }

    // ── Interview details (owner, 2026-10-02) ────────────────────────────────────────────

    /// <summary><c>&lt;interviewee&gt;-&lt;company&gt;-&lt;step&gt;-&lt;date&gt;</c>, or null until the interviewee or company is entered.</summary>
    public string? SessionName(DateTimeOffset date) =>
        InterviewRecord.SessionName(_draft.Candidate, _draft.Company, _draft.Step, date);

    /// <summary>
    /// The setup form changed: keep the record's interviewee, company and step in step with it.
    /// Each call that changes them writes the record to the database, so the form calls it when
    /// an edit is committed (the field loses focus, Enter, a step picked) — not per keystroke.
    /// </summary>
    public void DetailsChanged()
    {
        VerifyAccess();
        if (_record is not { } rec) return;
        var candidate = Trimmed(_draft.Candidate);
        var company = _draft.Company.Trim();
        var step = _draft.Step;
        if (rec.Setup.Candidate == candidate && rec.Setup.Company == company && rec.Setup.Step == step) return;
        rec.Setup.Candidate = candidate;
        rec.Setup.Company = company;
        rec.Setup.Step = step;
        OnChanged();
        Persist();
    }

    /// <summary>"Interview", or the JD's first non-blank line (usually the role) — up to 60 characters.</summary>
    public static string NameFromJd(string jd)
    {
        var first = jd.Split(NewlineChars, StringSplitOptions.None)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "";
        if (first.Length == 0) return "Interview";
        var info = new StringInfo(first);
        return info.LengthInTextElements <= 60 ? first : info.SubstringByTextElements(0, 60);
    }

    /// <summary>Swift's <c>Character.isNewline</c>.</summary>
    private static readonly char[] NewlineChars =
        ['\n', '\r', '\u000B', '\u000C', '\u0085', (char)0x2028, (char)0x2029];

    private static string? Trimmed(string s)
    {
        var t = s.Trim();
        return t.Length == 0 ? null : t;
    }

    // ── Skill steps (SPEC-13) ────────────────────────────────────────────────────────────

    /// <summary>The library skill loaded in this step's slot.</summary>
    public InterviewLibraryIndex.Skill? Skill(InterviewStep step) => Library.SkillBySlug(step.Slug());

    /// <summary>Completed at least once on this thread.</summary>
    public bool IsDone(InterviewStep step) => Turns.Any(t =>
        t.Kind == InterviewTurnKind.Skill && t.Status == InterviewTurnStatus.Completed
        && t.Question.StartsWith("/" + step.Slug(), StringComparison.Ordinal));

    /// <summary>The <c>apply-instruction</c> profile in force.</summary>
    public InterviewProfile? ActiveProfile => InterviewSteps.ProfileFromRaw(_record?.ActiveProfile);

    /// <summary><c>live-coding-design</c> is active.</summary>
    public bool LiveCodingActive => _record?.LiveCodingActive ?? false;

    /// <summary>The four skills must be loaded in Settings → Skills before an interview (owner, 2026-10-02).</summary>
    public IReadOnlyList<InterviewStep> MissingSkills => InterviewSteps.All.Where(s => Skill(s) is null).ToList();

    /// <summary>All four skill slots are loaded.</summary>
    public bool AllSkillsLoaded => MissingSkills.Count == 0;

    /// <summary>Why a step can't run right now, or null when it can.</summary>
    public string? Blocker(InterviewStep step)
    {
        if (Skill(step) is null) return $"Load the {step.Slug()} skill in Settings → Skills";
        return step switch
        {
            InterviewStep.DiscoveryCv => _draft.CvId is null ? "Select or upload a CV" : null,
            InterviewStep.DiscoveryJd => _draft.JobDescription.Trim().Length == 0 ? "Paste the job description" : null,
            InterviewStep.ApplyInstruction => null,
            InterviewStep.LiveCoding => ActiveProfile == InterviewProfile.Tech ? null : "Apply the Tech profile first",
            _ => null,
        };
    }

    /// <summary>Run one skill step as a turn on the thread.</summary>
    public Task RunAsync(InterviewStep step, InterviewProfile? profile = null)
    {
        VerifyAccess();
        return RunCoreAsync(step, profile);
    }

    private async Task RunCoreAsync(InterviewStep step, InterviewProfile? profile)
    {
        if (Blocker(step) is not null || Skill(step) is not { } skill) return;
        var gen = _generation;
        if (await EnsureThreadCoreAsync() is null || gen != _generation) return;
        if (_record is not { } rec) return;
        var slug = step.Slug();
        var command = profile is { } p ? $"/{slug} {p.Raw()}" : $"/{slug}";
        var definition = rec.SkillsReceived.Contains(slug) ? null : Library.PromptSkill(skill.Id);
        var attachments = new List<InterviewPrompt.Attachment>();
        switch (step)
        {
            case InterviewStep.DiscoveryCv:
                if (_draft.CvId is { } id)
                {
                    var text = Library.Text(id);
                    attachments.Add(new InterviewPrompt.Attachment("MY CV", text));
                    if (!rec.Setup.DocumentIds.Contains(id)) rec.Setup.DocumentIds.Insert(0, id);
                    // Snapshot it: history shows the CV the coach saw, whatever happens to the library.
                    rec.Setup.CvTitle = Library.Document(id)?.Title;
                    rec.CvText = text;
                }
                break;
            case InterviewStep.DiscoveryJd:
                attachments.Add(new InterviewPrompt.Attachment("JOB DESCRIPTION", _draft.JobDescription));
                rec.Setup.JdTextInline = _draft.JobDescription;
                if (rec.Name == "Interview") rec.Name = NameFromJd(_draft.JobDescription);
                break;
        }
        if (!rec.Setup.SkillIds.Contains(skill.Id)) rec.Setup.SkillIds.Add(skill.Id);
        OnChanged();
        Persist();
        await SubmitAsync(new Request(InterviewTurnKind.Skill,
                                      InterviewPrompt.SkillMessage(command, definition, attachments), command)
        {
            Effort = Cfg.PrepReasoningEffort,
        });
    }

    // ── Start preparation (owner, 2026-10-02) ────────────────────────────────────────────

    /// <summary>What Start preparation runs, in order: ① ② ③ and, when ticked, ④.</summary>
    public IReadOnlyList<(InterviewStep Step, InterviewProfile? Profile)> PreparationPlan
    {
        get
        {
            var plan = new List<(InterviewStep, InterviewProfile?)>
            {
                (InterviewStep.DiscoveryCv, null), (InterviewStep.DiscoveryJd, null),
                (InterviewStep.ApplyInstruction, _draft.Profile),
            };
            if (_draft.LiveCoding) plan.Add((InterviewStep.LiveCoding, null));
            return plan;
        }
    }

    /// <summary>Why Start preparation can't run yet, or null when it can.</summary>
    public string? PreparationBlocker
    {
        get
        {
            if (!AllSkillsLoaded) return "Load the 4 skills in Settings → Interview → Skills";
            if (Trimmed(_draft.Candidate) is null) return "Enter the interviewee's name";
            if (Trimmed(_draft.Company) is null) return "Enter the company";
            if (_draft.CvId is null) return "Choose or upload a CV (①)";
            if (_draft.JobDescription.Trim().Length == 0) return "Paste the job description (②)";
            if (_draft.Profile is null) return "Choose a mode (③)";
            if (_draft.LiveCoding && _draft.Profile != InterviewProfile.Tech) return "Live coding needs the Tech mode (③)";
            return null;
        }
    }

    /// <summary>Every planned step has completed on this thread, with the chosen mode and live-coding state.</summary>
    public bool IsPrepared =>
        IsDone(InterviewStep.DiscoveryCv) && IsDone(InterviewStep.DiscoveryJd) && ActiveProfile == _draft.Profile
        && _draft.Profile is not null && LiveCodingActive == _draft.LiveCoding;

    /// <summary>
    /// Run the plan in order; stop at the first step that doesn't complete. <paramref name="resume"/>
    /// continues from the failed step instead of starting over.
    /// </summary>
    public Task StartPreparationAsync(bool resume = false)
    {
        VerifyAccess();
        return StartPreparationCoreAsync(resume);
    }

    private async Task StartPreparationCoreAsync(bool resume)
    {
        if (PreparationBlocker is not null || _preparing) return;
        var gen = _generation;
        Set(ref _preparing, true);
        try
        {
            var plan = PreparationPlan.ToList();
            if (resume && _preparationFailedAt is { } failed && plan.FindIndex(p => p.Step == failed) is var i and >= 0)
                plan = plan.GetRange(i, plan.Count - i);
            Set(ref _preparationFailedAt, null);
            foreach (var (step, profile) in plan)
            {
                var before = Turns.Count;
                await RunCoreAsync(step, profile);
                if (gen != _generation) return;   // another interview is shown now
                var turn = Turns.Count > before ? Turns[^1] : null;
                if (turn?.Status != InterviewTurnStatus.Completed)
                {
                    _preparationFailedAt = step;
                    _status = $"{step.Title()} didn't finish" + (turn?.Error is { } e ? $": {e}" : ".");
                    OnChanged();
                    return;
                }
            }
            _status = null;
            _showingPreparation = false;
            OnChanged();
        }
        finally
        {
            if (gen == _generation) Set(ref _preparing, false);
        }
    }

    /// <summary>Which part Start preparation is running now (for the panel's progress marks).</summary>
    public InterviewStep? RunningStep
    {
        get
        {
            if (!IsStreaming || Turns.Count == 0) return null;
            var last = Turns[^1];
            if (last.Kind != InterviewTurnKind.Skill || InterviewRecord.SkillName(last.Question) is not { } name) return null;
            return InterviewSteps.StepFromSlug(name);
        }
    }

    /// <summary>
    /// History and wrap-up: the CV this interview used — its snapshot, else the library CV's
    /// text (read from disk once per interview and CV, not per call).
    /// </summary>
    public string CvText
    {
        get
        {
            if (_record?.CvText is { } snapshot) return snapshot;
            if (_record?.Setup.DocumentIds.FirstOrDefault() is not { } id) return "";
            if (_cvTextCache is { } cached && cached.Id == id) return cached.Text;
            var text = Library.Text(id);
            _cvTextCache = (id, text);
            return text;
        }
    }

    /// <summary>History and wrap-up: the CV's title.</summary>
    public string? CvTitle =>
        _record?.Setup.CvTitle ?? (_record?.Setup.DocumentIds.FirstOrDefault() is { } id ? Library.Document(id)?.Title : null);

    /// <summary>History and wrap-up: the JD this interview used.</summary>
    public string JdText => _record?.Setup.JdTextInline ?? "";

    /// <summary>After End interview: a follow-up prompt on the same thread (SPEC-15 §Ending the interview).</summary>
    public Task SendFollowUpAsync(string text) => SendTypedAsync(text);

    /// <summary>Setup → Upload…: import a CV file into the library and select it.</summary>
    /// <exception cref="DocumentText.Failure">The file cannot be turned into text.</exception>
    /// <exception cref="IOException">The library folder cannot be written.</exception>
    public void UploadCv(string path)
    {
        VerifyAccess();
        var doc = Library.ImportDocument(path, Kind.Cv);
        Draft = _draft with { CvId = doc.Id };
    }

    /// <summary>Start over: drop an interview that never started recording — its rows and its thread. The draft is kept.</summary>
    public Task DiscardUnstartedAsync()
    {
        VerifyAccess();
        return DiscardUnstartedCoreAsync();
    }

    private async Task DiscardUnstartedCoreAsync()
    {
        if (_record is not { } rec || rec.StartedAt is not null || _recordingStart is not null) return;
        _discarded.Add(rec.Id);   // a turn still streaming on it must not save it back
        try { _store.DeleteInterview(rec.Id); }
        catch (Exception e) { InterviewLog.Write($"deleting interview {rec.Id} failed: {e.Message}"); }
        ResetTransient();
        Apply(null, _draft);
        OnChanged();
        if (rec.ThreadId is { } thread) await ArchiveAsync(thread);
    }

    // ── End of interview (SPEC-15) ───────────────────────────────────────────────────────

    /// <summary>
    /// After End interview has saved the transcript: link the record to its session row and let
    /// a streaming answer finish (≤ 30 s, then interrupt with a 2.5 s grace). Never delays the
    /// save. Summarizing is the user's choice — nothing is sent from here.
    /// </summary>
    public Task SessionSavedAsync(long? sessionId, string transcript)
    {
        VerifyAccess();
        return SessionSavedCoreAsync(sessionId, transcript);
    }

    private async Task SessionSavedCoreAsync(long? sessionId, string transcript)
    {
        if (_record is not { } rec) return;
        rec.EndedAt = TimeFormat.Iso(DateTimeOffset.Now);
        rec.SessionId = sessionId;
        rec.Transcript = transcript;
        OnChanged();
        Persist();
        if (sessionId is { } id)
        {
            try { _store.MarkInterview(id); }
            catch (Exception e) { InterviewLog.Write($"marking session {id} as an interview failed: {e.Message}"); }
            RaiseSessionsChanged();
        }

        _queued = null;
        await WaitWhileStreamingAsync(_options.EndAnswerWait);
        if (IsStreaming)
        {
            await StopStreamingCoreAsync();
            await WaitWhileStreamingAsync(_options.EndInterruptGrace);
        }
        // No automatic summary: End interview asks the user to summarize or send a follow-up.
    }

    /// <summary>The summary turn (SPEC-15 §Summary message), with the preparation effort. Retryable.</summary>
    public Task GenerateSummaryAsync(string transcript)
    {
        VerifyAccess();
        return GenerateSummaryCoreAsync(transcript);
    }

    private async Task GenerateSummaryCoreAsync(string transcript)
    {
        if (_record is not { ThreadId: { } thread } rec || _summarizing) return;
        var gen = _generation;
        _summarizing = true;
        _summaryError = null;
        _summaryText = "";
        rec.Summary.Status = InterviewStatus.Running;
        OnChanged();
        Persist();
        try
        {
            if (await ResumeIfNeededAsync(rec, gen, thread) is { } error)
            {
                FailSummary(rec, gen, error);
                return;
            }

            var message = InterviewPrompt.Summary(transcript);
            var effort = Cfg.PrepReasoningEffort;
            var model = ModelSwitch(rec);
            await foreach (var e in Events(() => Engine.Send(thread, [new CodexRpc.Input.Text(message)], effort, model)))
            {
                switch (e)
                {
                    case AnswerEvent.Delta(var d):
                        if (gen != _generation) break;
                        _summaryText += d;
                        OnChanged();
                        break;
                    case AnswerEvent.Completed(var full):
                        rec.SummaryText = full;
                        rec.Summary = new InterviewSummary
                        {
                            Status = InterviewStatus.Done, CompletedAt = TimeFormat.Iso(DateTimeOffset.Now),
                        };
                        if (gen == _generation)
                        {
                            _summaryText = full;
                            OnChanged();
                        }
                        SaveFinal(rec);
                        break;
                    case AnswerEvent.Interrupted:
                        FailSummary(rec, gen, "The summary was interrupted.");
                        break;
                    case AnswerEvent.Failed(var err, _):
                        FailSummary(rec, gen, err.Message);
                        break;
                }
            }
        }
        finally
        {
            if (gen == _generation) Set(ref _summarizing, false);
        }
    }

    /// <summary>
    /// A saved interview's thread may be unknown to this Codex run: resume it before the first
    /// turn. Returns the error message when that fails.
    /// </summary>
    private async Task<string?> ResumeIfNeededAsync(InterviewRecord rec, int gen, string thread)
    {
        if (!_needsResume) return null;
        var length = InterviewConfig.ParseAnswerLengthOrMedium(rec.Setup.AnswerLength);
        try
        {
            await Engine.ResumeThreadAsync(thread, new ThreadConfig(
                rec.Model, InterviewPrompt.BaseInstructions(length, rec.Setup.Instructions)));
            if (gen == _generation) _needsResume = false;
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    private void FailSummary(InterviewRecord rec, int gen, string message)
    {
        rec.Summary.Status = InterviewStatus.Failed;
        if (gen == _generation)
        {
            _summaryError = message;
            OnChanged();
        }
        SaveFinal(rec);
    }

    /// <summary>The session screen starts a new recording after Results: start a fresh interview.</summary>
    public void ResetForNewInterview()
    {
        VerifyAccess();
        ResetTransient();
        Apply(null, InitialDraft());
        _showingPreparation = true;
        OnChanged();
    }

    // ── Recording hooks ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Start in Interview mode: link the capture and open the thread now (in the background), so
    /// the cold first turn is paid before the first question (SPEC-13 §The thread). Call it only
    /// in Interview mode — it starts Codex.
    /// </summary>
    public void RecordingStarted(Guid uuid, DateTimeOffset date)
    {
        VerifyAccess();
        _recordingStart = (uuid, date);
        _showingPreparation = false;
        if (_record is { } rec)
        {
            StampRecordingStart(rec);
            Persist();
        }
        OnChanged();
        _ = EnsureThreadCoreAsync();   // never throws: failures land in ThreadState
    }

    // ── Turns ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Send one turn on the interview's thread and stream its answer into the record. Returns
    /// when the turn ends: its final status, or null when the thread could not be opened (or
    /// another interview was shown meanwhile).
    /// </summary>
    private async Task<InterviewTurnStatus?> RunTurnAsync(Request r)
    {
        var gen = _generation;
        if (await EnsureThreadCoreAsync() is not { } thread || gen != _generation || _record is not { } rec) return null;
        if (await ResumeIfNeededAsync(rec, gen, thread) is { } error)
        {
            if (gen == _generation) Set(ref _status, $"Could not reopen this interview's conversation: {error}");
            return InterviewTurnStatus.Failed;
        }
        if (gen != _generation) return null;
        var n = rec.NextTurnNumber;
        // Screenshots are kept in the database; Codex reads them from short-lived files.
        var names = r.Images.Select((_, i) => Store.ImageName(n, i + 1)).ToList();
        for (var i = 0; i < names.Count; i++)
        {
            try { _store.AddInterviewImage(rec.Id, names[i], n, r.Images[i]); }
            catch (Exception e) { InterviewLog.Write($"saving screenshot {names[i]} failed: {e.Message}"); }
        }
        var files = WriteOutbox(r.Images);
        try
        {
            rec.Turns.Add(new InterviewTurn
            {
                N = n, Kind = r.Kind, Question = r.Question, AudioFromMs = r.Span?.From, AudioToMs = r.Span?.To,
                Images = names, AskedAt = TimeFormat.Iso(DateTimeOffset.Now),
            });
            SetStreaming(rec, n);
            OnChanged();
            Persist();

            var clock = Stopwatch.StartNew();
            var input = new List<CodexRpc.Input> { new CodexRpc.Input.Text(r.Text) };
            input.AddRange(files.Select(f => new CodexRpc.Input.LocalImage(f)));
            var effort = r.Effort ?? Cfg.ReasoningEffort;
            var model = ModelSwitch(rec);
            var final = InterviewTurnStatus.Failed;
            try
            {
                await foreach (var e in Events(() => Engine.Send(thread, input, effort, model)))
                {
                    switch (e)
                    {
                        case AnswerEvent.Delta(var d):
                            Update(rec, n, t =>
                            {
                                t.TtftMs ??= Ms(clock);
                                t.Answer += d;
                            });
                            break;
                        case AnswerEvent.Completed(var full):
                            Update(rec, n, t => { t.Answer = full; t.Status = InterviewTurnStatus.Completed; t.TotalMs = Ms(clock); });
                            final = InterviewTurnStatus.Completed;
                            break;
                        case AnswerEvent.Interrupted(var partial):
                            Update(rec, n, t => { t.Answer = partial; t.Status = InterviewTurnStatus.Interrupted; t.TotalMs = Ms(clock); });
                            final = InterviewTurnStatus.Interrupted;
                            break;
                        case AnswerEvent.Failed(var err, var partial):
                            Update(rec, n, t =>
                            {
                                t.Answer = partial; t.Status = InterviewTurnStatus.Failed; t.Error = err.Message;
                                t.TotalMs = Ms(clock);
                            });
                            final = InterviewTurnStatus.Failed;
                            break;
                        case AnswerEvent.Slow:
                            if (gen == _generation) Set(ref _status, "Still thinking…");
                            break;
                        case AnswerEvent.Started:
                            Invoke(r.OnAccepted, "clearing the clipboard after a send");
                            break;
                    }
                }
            }
            finally
            {
                // Whatever happened above, the turn is over: never leave the controller "streaming".
                if (ReferenceEquals(_streamingRecord, rec) && _streamingTurn == n) SetStreaming(null, null);
                if (gen == _generation)
                {
                    _status = null;
                    OnChanged();
                }
                SaveFinal(rec);
            }
            return final;
        }
        finally
        {
            foreach (var f in files)
            {
                try { File.Delete(f); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static int Ms(Stopwatch clock) => (int)clock.Elapsed.TotalMilliseconds;

    /// <summary>
    /// Change turn <paramref name="n"/> of the record it was added to — always that record, even
    /// when another interview is shown now; <see cref="Changed"/> only while it is the current one.
    /// </summary>
    private void Update(InterviewRecord rec, int n, Action<InterviewTurn> change)
    {
        if (rec.Turns.FirstOrDefault(t => t.N == n) is not { } turn) return;
        change(turn);
        if (ReferenceEquals(_record, rec)) OnChanged();
    }

    // ── Asking (SPEC-14) ─────────────────────────────────────────────────────────────────

    /// <summary>One turn to send, already built at press time.</summary>
    private sealed record Request(InterviewTurnKind Kind, string Text, string Question)
    {
        public IReadOnlyList<byte[]> Images { get; init; } = [];
        public (int From, int To)? Span { get; init; }
        public Action? OnAccepted { get; init; }
        /// <summary>Skill steps use <c>prep_reasoning_effort</c>; null = <c>reasoning_effort</c>.</summary>
        public string? Effort { get; init; }
        /// <summary>For merging queued asks: the transcript text behind <see cref="Text"/>.</summary>
        public string AskText { get; init; } = "";
    }

    /// <summary>The Ask hotkey / button: the interviewer's latest words plus every pending screenshot.</summary>
    public Task AskAsync()
    {
        VerifyAccess();
        return AskCoreAsync();
    }

    private async Task AskCoreAsync()
    {
        var cfg = Cfg;
        var src = TranscriptSource?.Invoke() ?? ((IReadOnlyList<AskSelection.Segment>)[], "", 0);
        var sel = AskSelection.Select(src.Segments, src.Interim, cfg.AskMode, _mark, src.AudioMs, cfg.ClampedMaxWords);
        _mark = sel.Mark;   // advances on every press, in both modes
        if (sel.Text.Length == 0 && _pendingImages.Count == 0)
        {
            Set(ref _status, "Nothing new since your last ask");
            return;
        }
        var gen = _generation;
        if (await EnsureThreadCoreAsync() is null)
        {
            if (gen == _generation) _options.Beep?.Invoke();
            return;
        }
        if (gen != _generation) return;
        var (images, onAccepted) = TakePending();
        await SubmitAsync(new Request(InterviewTurnKind.Ask, InterviewPrompt.Ask(sel.Text, images.Count),
                                      sel.Text.Length == 0 ? "(screenshot)" : sel.Text)
        {
            Images = images, Span = (sel.FromMs, sel.ToMs), OnAccepted = onAccepted, AskText = sel.Text,
        });
    }

    /// <summary>The Send button: typed text plus every pending screenshot.</summary>
    public Task SendTypedAsync(string text)
    {
        VerifyAccess();
        return SendTypedCoreAsync(text);
    }

    private async Task SendTypedCoreAsync(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 && _pendingImages.Count == 0) return;
        var (images, onAccepted) = TakePending();
        var body = t.Length == 0 ? "(see the attached screenshot(s))" : t;
        var message = images.Count == 0 ? body : body + $"\n({images.Count} screenshot(s) attached.)";
        await SubmitAsync(new Request(InterviewTurnKind.Typed, message, t.Length == 0 ? "(screenshot)" : t)
        {
            Images = images, OnAccepted = onAccepted,
        });
    }

    /// <summary>A different answer to the latest question (latest card only).</summary>
    public Task RegenerateAsync()
    {
        VerifyAccess();
        if (Turns.LastOrDefault(t => t.Kind != InterviewTurnKind.Skill) is not { } last) return Task.CompletedTask;
        return SubmitAsync(new Request(InterviewTurnKind.Regenerate, InterviewPrompt.Regenerate,
                                       $"Another answer: {last.Question}"));
    }

    /// <summary>
    /// One turn at a time per thread; what happens to a new request while an answer is still
    /// streaming is <c>interview.busy_policy</c> (SPEC-14 §Busy policy).
    /// </summary>
    private async Task SubmitAsync(Request request)
    {
        var gen = _generation;
        if (IsStreaming)
        {
            if (Cfg.QueuesWhenBusy)
            {
                _queued = Merge(_queued, request);
                Set(ref _status, "Queued — sends when this answer finishes.");
                return;
            }
            await StopStreamingCoreAsync();
            await WaitWhileStreamingAsync(_options.BusyInterruptWait);
            if (gen != _generation) return;
        }
        Request? next = request;
        while (next is { } r)
        {
            await RunTurnAsync(r);
            if (gen != _generation) return;   // the queue now belongs to the interview shown
            next = _queued;
            _queued = null;
        }
    }

    /// <summary>A further Ask merges into the queued one; anything else replaces it.</summary>
    private static Request Merge(Request? old, Request @new)
    {
        if (old is null || old.Kind != InterviewTurnKind.Ask || @new.Kind != InterviewTurnKind.Ask) return @new;
        var text = string.Join(" ", new[] { old.AskText, @new.AskText }.Where(s => s.Length > 0));
        var images = old.Images.Concat(@new.Images).ToList();
        var both = new[] { old.OnAccepted, @new.OnAccepted }.OfType<Action>().ToList();
        return new Request(InterviewTurnKind.Ask, InterviewPrompt.Ask(text, images.Count),
                           text.Length == 0 ? "(screenshot)" : text)
        {
            Images = images,
            Span = (old.Span?.From ?? @new.Span?.From ?? 0, @new.Span?.To ?? old.Span?.To ?? 0),
            // Every callback runs, even when an earlier one throws.
            OnAccepted = both.Count == 0 ? null : () => both.ForEach(a => Invoke(a, "clearing the clipboard after a send")),
            AskText = text,
        };
    }

    /// <summary>Stop the streaming answer, if any.</summary>
    public Task StopStreamingAsync()
    {
        VerifyAccess();
        return StopStreamingCoreAsync();
    }

    private async Task StopStreamingCoreAsync()
    {
        if (_record?.ThreadId is not { } thread || !IsStreaming) return;
        try { await Engine.InterruptAsync(thread); }
        catch (Exception e) { InterviewLog.Write($"interrupting {thread} failed: {e.Message}"); }
    }

    /// <summary>
    /// The model picker changed since the thread started → send the new model with this turn
    /// (Codex keeps it for later turns). Null when unchanged.
    /// </summary>
    private string? ModelSwitch(InterviewRecord rec)
    {
        var wanted = Codex.ResolvedModel(Cfg.Model);
        if (wanted == rec.Model) return null;
        rec.Model = wanted;
        return wanted;
    }

    // ── Screenshots (SPEC-14 §Screenshots) ───────────────────────────────────────────────

    /// <summary>
    /// Call about twice a second while the interview panel is on screen. With
    /// <c>include_clipboard_images</c> on, every new image on the clipboard is added to the
    /// current prompt. What was already there when watching began is left alone — and watching
    /// begins again whenever Interview mode is entered, so nothing copied in Caption only mode
    /// is picked up later. With the setting off, or outside Interview mode, the clipboard is
    /// never read; with the tray full, it is never decoded. Never throws: a clipboard held by
    /// another process is retried on the next poll.
    /// </summary>
    public void PollClipboard()
    {
        VerifyAccess();
        // SPEC-16 §11.2: Caption only mode never reads the clipboard, whoever calls this.
        if (_clipboard is not { } clipboard || !Cfg.IsInterviewMode)
        {
            _seenSequence = null;
            return;
        }
        try { PollClipboard(clipboard); }
        catch (Exception e) { InterviewLog.Write($"polling the clipboard failed: {e.Message}"); }
    }

    private void PollClipboard(IClipboardImages clipboard)
    {
        var count = clipboard.SequenceNumber;
        if (_seenSequence is not { } seen)
        {
            _seenSequence = count;
            return;
        }
        if (count == seen) return;
        if (!Cfg.IncludeClipboardImages)
        {
            _seenSequence = count;
            return;
        }
        if (_pendingImages.Count >= MaxPendingImages)
        {
            _seenSequence = count;
            if (clipboard.ContainsImages) Set(ref _status, TrayFullMessage);
            return;
        }
        IReadOnlyList<byte[]> images;
        try
        {
            images = clipboard.ReadImages();
        }
        catch (Exception e)
        {
            // Probably held by another process. Keep the old sequence so the next poll retries —
            // a few times, then give this change up.
            var attempts = _clipboardReadFailure is { } f && f.Sequence == count ? f.Attempts + 1 : 1;
            InterviewLog.Write($"reading clipboard images failed (attempt {attempts}): {e.Message}");
            if (attempts < MaxClipboardReadAttempts)
            {
                _clipboardReadFailure = (count, attempts);
                return;
            }
            _clipboardReadFailure = null;
            _seenSequence = count;
            return;
        }
        _clipboardReadFailure = null;
        _seenSequence = count;
        if (images.Count == 0) return;
        AddPending(images.Take(MaxPendingImages - _pendingImages.Count));
        _capturedSequence = count;
    }

    /// <summary>
    /// The Screenshot hotkey or button: select an area; it joins the current prompt, and the next
    /// Ask or Send takes it. Esc cancels quietly.
    /// </summary>
    public Task TakeScreenshotAsync()
    {
        VerifyAccess();
        return TakeScreenshotCoreAsync();
    }

    private async Task TakeScreenshotCoreAsync()
    {
        if (_capturing || _screen is not { } screen) return;
        if (_pendingImages.Count >= MaxPendingImages)
        {
            Set(ref _status, TrayFullMessage);
            return;
        }
        Set(ref _capturing, true);
        try
        {
            byte[]? png;
            try { png = await screen.SelectAreaAsync(); }
            catch (Exception e)
            {
                InterviewLog.Write($"the area screenshot failed: {e.Message}");
                png = null;   // as Esc
            }
            if (png is null) return;
            if (_pendingImages.Count >= MaxPendingImages)   // the clipboard filled it meanwhile
            {
                Set(ref _status, TrayFullMessage);
                return;
            }
            AddPending([png]);
        }
        finally
        {
            Set(ref _capturing, false);
        }
    }

    private void AddPending(IEnumerable<byte[]> pngs)
    {
        _pendingImages = [.. _pendingImages, .. pngs.Select(png => new PendingImage(png))];
        _status = $"Screenshot added — {_pendingImages.Count} in this prompt";
        OnChanged();
    }

    /// <summary>Remove one screenshot from the tray.</summary>
    public void RemovePending(Guid id)
    {
        VerifyAccess();
        _pendingImages = _pendingImages.Where(p => p.Id != id).ToList();
        OnChanged();
    }

    /// <summary>Empty the tray.</summary>
    public void ClearPending()
    {
        VerifyAccess();
        _pendingImages = [];
        OnChanged();
    }

    /// <summary>
    /// Take the tray for a send: the images, plus a hook that clears the last screenshot from the
    /// clipboard once Codex accepts the turn (if that setting is on and nothing new was copied).
    /// </summary>
    private (IReadOnlyList<byte[]> Images, Action? OnAccepted) TakePending()
    {
        var images = _pendingImages.Select(p => p.Png).ToList();
        _pendingImages = [];
        OnChanged();
        if (images.Count == 0) return ([], null);
        var cfg = Cfg;
        if (!Codex.AcceptsImages(_record?.Model ?? cfg.EffectiveModel))
        {
            Set(ref _status, "This model can't read images — sending the text only.");
            return ([], null);
        }
        if (!cfg.ClearClipboardImagesAfterSend || _capturedSequence is not { } captured || _clipboard is not { } clipboard)
            return (images, null);
        return (images, () =>
        {
            // ClearIfUnchanged checks the sequence itself (nothing new copied since).
            if (clipboard.ClearIfUnchanged(captured)) _seenSequence = clipboard.SequenceNumber;
        });
    }

    /// <summary>A screenshot stored with this interview (PNG), for thumbnails and "what was sent".</summary>
    public byte[]? Image(string name)
    {
        if (_record is not { } rec) return null;
        try { return _store.InterviewImage(rec.Id, name); }
        catch (Exception e)
        {
            InterviewLog.Write($"reading screenshot {name} failed: {e.Message}");
            return null;
        }
    }

    /// <summary>Short-lived PNG files for Codex's <c>localImage</c> input; deleted when the turn ends (or by the launch sweep).</summary>
    private List<string> WriteOutbox(IReadOnlyList<byte[]> pngs)
    {
        var files = new List<string>();
        foreach (var png in pngs)
        {
            var path = Path.Combine(_options.OutboxRoot, $"{UuidString(Guid.NewGuid())}.png");
            try
            {
                // Written whole, then renamed in (Swift's `.atomic`), so Codex never reads half a file.
                Files.WriteAllBytesAtomic(path, png);
                files.Add(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                InterviewLog.Write($"writing an outbox image failed: {e.Message}");
            }
        }
        return files;
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Engine events, with an exception from the stream folded into
    /// <see cref="AnswerEvent.Failed"/> (Swift's <c>streamEvents</c>). The engine contract says
    /// a stream never throws; this keeps one that does from escaping a UI handler.
    /// </summary>
    private static async IAsyncEnumerable<AnswerEvent> Events(Func<IAsyncEnumerable<AnswerEvent>> send)
    {
        IAsyncEnumerator<AnswerEvent>? stream = null;
        Exception? failure = null;
        try { stream = send().GetAsyncEnumerator(); }
        catch (Exception e) { failure = e; }
        if (stream is not null)
        {
            try
            {
                while (true)
                {
                    AnswerEvent current;
                    try
                    {
                        if (!await stream.MoveNextAsync()) break;
                        current = stream.Current;
                    }
                    catch (Exception e)
                    {
                        failure = e;
                        break;
                    }
                    yield return current;
                }
            }
            finally
            {
                try { await stream.DisposeAsync(); }
                catch (Exception e) { InterviewLog.Write($"closing an answer stream failed: {e.Message}"); }
            }
        }
        if (failure is not null) yield return new AnswerEvent.Failed(new EngineError.Other(failure.Message), "");
    }

    /// <summary>
    /// Waits (without blocking) until no answer streams, or <paramref name="timeout"/> passes. The
    /// timer is cancelled as soon as streaming ends, so a 30 s wait doesn't outlive a 1 s answer.
    /// </summary>
    private async Task WaitWhileStreamingAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (IsStreaming)
        {
            var left = timeout - clock.Elapsed;
            if (left <= TimeSpan.Zero) return;
            using var timer = new CancellationTokenSource();
            await Task.WhenAny(_streamEnded.Task, Task.Delay(left, timer.Token));
            timer.Cancel();   // the stream ended first: stop the delay now
        }
    }

    /// <summary>Mark turn <paramref name="n"/> of <paramref name="rec"/> as streaming; <c>null</c> = nothing streams (wakes every waiter).</summary>
    private void SetStreaming(InterviewRecord? rec, int? n)
    {
        _streamingTurn = n;
        _streamingRecord = n is null ? null : rec;
        // RunContinuationsAsynchronously: a waiter resumes later on the owner's context, never
        // inline in the middle of whoever ended the stream.
        if (n is null) _streamEnded.TrySetResult();
        else if (_streamEnded.Task.IsCompleted) _streamEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource Completed()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    /// <summary><paramref name="rec"/> is still the interview shown, as when work for it began in generation <paramref name="gen"/>.</summary>
    private bool StillCurrent(InterviewRecord rec, int gen) => gen == _generation && ReferenceEquals(_record, rec);

    /// <summary>Save the record; a failure goes to the status line.</summary>
    private void Persist()
    {
        if (_record is not { } rec) return;
        try { _store.SaveInterview(rec); }
        catch (Exception e) { Set(ref _status, $"Could not save the interview record: {e.Message}"); }
    }

    /// <summary>
    /// The end of a turn or summary: save the record it ran on. That is the current record, or
    /// one replaced meanwhile by Load/Reset — saved unless it was discarded, or the same
    /// interview has been loaded again (that copy owns the row now; the launch sweep fails a
    /// turn it shows as streaming).
    /// </summary>
    private void SaveFinal(InterviewRecord rec)
    {
        if (ReferenceEquals(_record, rec))
        {
            Persist();
            return;
        }
        if (_discarded.Contains(rec.Id) || _record?.Id == rec.Id) return;
        try { _store.SaveInterview(rec); }
        catch (Exception e) { InterviewLog.Write($"saving interview {rec.Id} after it was closed failed: {e.Message}"); }
    }

    /// <summary>Archive a thread, best effort.</summary>
    private async Task ArchiveAsync(string thread)
    {
        try { await Engine.ArchiveThreadAsync(thread); }
        catch (Exception e) { InterviewLog.Write($"archiving {thread} failed: {e.Message}"); }
    }

    /// <summary>Run a platform callback; a throw is logged, never propagated (H1: it must not skip the end of a turn).</summary>
    private static void Invoke(Action? callback, string what)
    {
        if (callback is null) return;
        try { callback(); }
        catch (Exception e) { InterviewLog.Write($"{what} failed: {e.Message}"); }
    }

    /// <summary>Swift's <c>UUID.uuidString</c>: upper-case, hyphenated.</summary>
    private static string UuidString(Guid uuid) => uuid.ToString("D").ToUpperInvariant();

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("InterviewController must be used from the thread that created it.");
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged();
    }

    private void OnChanged() => Raise(Changed);

    private void RaiseSessionsChanged() => Raise(SessionsChanged);

    /// <summary>Raise an event; a throwing handler is logged and the rest still run.</summary>
    private void Raise(EventHandler? handlers)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception e)
            {
                InterviewLog.Write($"an interview event handler failed: {e.Message}");
            }
        }
    }
}
