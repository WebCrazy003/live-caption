using System.Globalization;
using System.IO;
using System.Windows.Threading;
using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;
using LocalCaption.Interview;
using Kind = LocalCaption.Core.Interview.InterviewLibraryIndex.DocumentKind;

namespace LocalCaption.App.Interview.Views.Prep;

/// <summary>
/// The preparation form (SPEC-16 §5.2, Mac <c>InterviewSetupSection.swift</c>) over
/// <see cref="InterviewController"/>. Edits replace the controller's immutable
/// <see cref="InterviewDraft"/>; everything else is read back from the controller, the Codex
/// service, the library and the config whenever any of them says it changed.
/// </summary>
/// <remarks>
/// Every member runs on the UI thread (the controller's owner thread). The controller raises
/// <c>Changed</c> on it; <see cref="CodexService.Changed"/> may come from any thread and is
/// marshalled. Refreshes are coalesced to one per dispatcher pass (a streaming step raises
/// <c>Changed</c> per delta).
/// </remarks>
internal sealed class PreparationViewModel : PrepObservable
{
    /// <summary>The Mac stepper's range.</summary>
    public const int MinStep = 1, MaxStep = 20;

    private readonly InterviewController _interview;
    private readonly Func<Config> _config;
    private readonly Action<Config> _saveConfig;
    private readonly PrepRefresher _refresher;
    private bool _attached;

    /// <summary>
    /// True while bindings are being re-read: a ComboBox whose list is replaced pushes a
    /// transient null/-1 selection back, which must not become a draft or config edit.
    /// </summary>
    private bool _refreshing;

    private IReadOnlyList<PrepChoice> _cvChoices = [];
    private IReadOnlyList<PrepChoice> _modelChoices = [];
    private IReadOnlyList<PrepChoice> _prepEffortChoices = [];
    private IReadOnlyList<PrepChoice> _answerEffortChoices = [];
    private string? _uploadError;
    private string? _commandError;

    public PreparationViewModel(InterviewController interview, Func<Config> config, Action<Config> saveConfig,
                                Dispatcher dispatcher)
    {
        _interview = interview;
        _config = config;
        _saveConfig = saveConfig;
        _refresher = new PrepRefresher(dispatcher, Refresh);

        StepDownCommand = new PrepCommand(() => SetStep(Draft.Step - 1), () => CanEdit && Draft.Step > MinStep);
        StepUpCommand = new PrepCommand(() => SetStep(Draft.Step + 1), () => CanEdit && Draft.Step < MaxStep);
        StartCommand = new PrepAsyncCommand(() => _interview.StartPreparationAsync(), CanStart, Fail);
        ContinueCommand = new PrepAsyncCommand(() => _interview.StartPreparationAsync(resume: true), CanStart, Fail);
        RestartCommand = new PrepAsyncCommand(() => _interview.StartPreparationAsync(), CanStart, Fail);
        UpdateLists();
    }

    public InterviewController Interview => _interview;

    private CodexService Codex => _interview.Codex;
    private InterviewLibrary Library => _interview.Library;
    private InterviewDraft Draft => _interview.Draft;

    // ── lifetime ─────────────────────────────────────────────────────────────────────────

    /// <summary>Listen to the controller, Codex and the library (the view is on screen).</summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _interview.Changed += OnChanged;
        Codex.Changed += OnChanged;
        Library.Changed += OnLibraryChanged;
        Refresh();
    }

    /// <summary>Stop listening: the services outlive the view.</summary>
    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _interview.Changed -= OnChanged;
        Codex.Changed -= OnChanged;
        Library.Changed -= OnLibraryChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => _refresher.Request();

    private void OnLibraryChanged() => _refresher.Request();

    /// <summary>Re-read everything — also after Settings changed the config or the skills.</summary>
    public void Refresh()
    {
        _refreshing = true;
        try
        {
            UpdateLists();
            RaiseAll();
        }
        finally
        {
            _refreshing = false;
        }
        StepDownCommand.RaiseCanExecuteChanged();
        StepUpCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        ContinueCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Replace a picker's list only when its content changed, so an open drop-down is left alone.</summary>
    private void UpdateLists()
    {
        Replace(ref _cvChoices, BuildCvChoices());
        var cfg = _config().Interview;
        Replace(ref _modelChoices, BuildModelChoices(cfg.Model));
        var model = InterviewConfig.EffectiveModel(cfg.Model);
        Replace(ref _prepEffortChoices, BuildEfforts(model, cfg.PrepReasoningEffort));
        Replace(ref _answerEffortChoices, BuildEfforts(model, cfg.ReasoningEffort));
    }

    private static void Replace(ref IReadOnlyList<PrepChoice> current, List<PrepChoice> next)
    {
        if (!current.SequenceEqual(next)) current = next;
    }

    // ── banner and Codex ─────────────────────────────────────────────────────────────────

    /// <summary>Mac: <c>!codex.isReady || codex.signIn != nil || codex.signInError != nil</c>.</summary>
    public bool ShowCodexRow => !Codex.IsReady || Codex.SignIn is not null || Codex.SignInError is not null;

    public bool ShowMissingSkills => !_interview.AllSkillsLoaded;

    public string MissingSkillsText => "Missing: " + string.Join(", ", _interview.MissingSkills.Select(s => s.Slug()));

    // ── Interview details ────────────────────────────────────────────────────────────────

    /// <summary>The details and the four parts are locked while Start preparation runs (as on the Mac).</summary>
    public bool CanEdit => !_interview.Preparing;

    public string Candidate
    {
        get => Draft.Candidate;
        set
        {
            if (_refreshing || value is null || value == Draft.Candidate) return;
            _interview.Draft = Draft with { Candidate = value };
        }
    }

    public string Company
    {
        get => Draft.Company;
        set
        {
            if (_refreshing || value is null || value == Draft.Company) return;
            _interview.Draft = Draft with { Company = value };
        }
    }

    public string StepText => Draft.Step.ToString(CultureInfo.CurrentCulture);

    public PrepCommand StepDownCommand { get; }
    public PrepCommand StepUpCommand { get; }

    private void SetStep(int step)
    {
        step = Math.Clamp(step, MinStep, MaxStep);
        if (step == Draft.Step) return;
        _interview.Draft = Draft with { Step = step };
        _interview.DetailsChanged();   // a step picked is a committed edit
    }

    /// <summary>The interviewee or company field lost focus or took Enter: save them to the record.</summary>
    public void CommitDetails() => _interview.DetailsChanged();

    public string SessionNameText => _interview.SessionName(DateTimeOffset.Now) is { } name
        ? $"Session name: {name}"
        : "The session is named <interviewee>-<company>-<step>-<date>.";

    // ── ① CV ─────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<PrepChoice> CvChoices => _cvChoices;

    /// <summary>The library CV's id, or "" for "Choose a CV…".</summary>
    public string CvSelection
    {
        get => Draft.CvId ?? "";
        set
        {
            if (_refreshing || value is null) return;
            var id = value.Length == 0 ? null : value;
            if (id == Draft.CvId) return;
            _interview.Draft = Draft with { CvId = id };
        }
    }

    private List<PrepChoice> BuildCvChoices()
    {
        var list = new List<PrepChoice> { new("", "Choose a CV…") };
        list.AddRange(Library.Documents(Kind.Cv).Select(d => new PrepChoice(d.Id, d.Title)));
        return list;
    }

    public string? UploadError => _uploadError;

    /// <summary>Upload CV…: import into the library and select it; a failure is shown under the picker.</summary>
    public void UploadCv(string path)
    {
        try
        {
            _interview.UploadCv(path);
            _uploadError = null;
        }
        catch (Exception e) when (e is DocumentText.Failure or IOException or UnauthorizedAccessException)
        {
            _uploadError = e.Message;
        }
        Refresh();
    }

    // ── ② JD ─────────────────────────────────────────────────────────────────────────────

    public string JobDescription
    {
        get => Draft.JobDescription;
        set
        {
            if (_refreshing || value is null || value == Draft.JobDescription) return;
            _interview.Draft = Draft with { JobDescription = value };
        }
    }

    public bool ShowJdPlaceholder => Draft.JobDescription.Length == 0;

    // ── ③ mode ───────────────────────────────────────────────────────────────────────────

    public bool IsIntro { get => Draft.Profile == InterviewProfile.Intro; set => SetProfile(value, InterviewProfile.Intro); }
    public bool IsTech { get => Draft.Profile == InterviewProfile.Tech; set => SetProfile(value, InterviewProfile.Tech); }
    public bool IsBehavioral { get => Draft.Profile == InterviewProfile.Cultural; set => SetProfile(value, InterviewProfile.Cultural); }

    /// <summary>A radio button turning off is the other one turning on; only "on" is an edit.</summary>
    private void SetProfile(bool on, InterviewProfile profile)
    {
        if (_refreshing || !on || Draft.Profile == profile) return;
        _interview.Draft = Draft with { Profile = profile };
    }

    public string ProfileNote => Draft.Profile is null
        ? "Choose the mode for this interview."
        : "Applied by Start preparation. During the interview you can switch modes from the header.";

    // ── ④ live coding ────────────────────────────────────────────────────────────────────

    public bool LiveCoding
    {
        get => Draft.LiveCoding;
        set
        {
            if (_refreshing || value == Draft.LiveCoding) return;
            _interview.Draft = Draft with { LiveCoding = value };
        }
    }

    // ── part marks ───────────────────────────────────────────────────────────────────────

    public PrepPartStatus Part1 => Part(1, InterviewStep.DiscoveryCv, "Discovery CV");
    public PrepPartStatus Part2 => Part(2, InterviewStep.DiscoveryJd, "Discovery JD");
    public PrepPartStatus Part3 => Part(3, InterviewStep.ApplyInstruction, "Apply instruction");
    public PrepPartStatus Part4 => Part(4, InterviewStep.LiveCoding, "Live coding & design (optional)");

    private PrepPartStatus Part(int n, InterviewStep step, string title)
    {
        var running = _interview.RunningStep == step;
        var failed = _interview.PreparationFailedAt == step && !_interview.Preparing;
        var done = step switch
        {
            InterviewStep.ApplyInstruction => _interview.ActiveProfile is not null && _interview.ActiveProfile == Draft.Profile,
            InterviewStep.LiveCoding => _interview.LiveCodingActive,
            _ => _interview.IsDone(step),
        };
        return new PrepPartStatus(n, title, running, failed && !running, done && !running && !failed);
    }

    // ── model row ────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<PrepChoice> ModelChoices => _modelChoices;

    /// <summary><c>interview.model</c>: "" is Recommended.</summary>
    public string ModelId
    {
        get => _config().Interview.Model;
        set => EditConfig(value, cfg => cfg.Model, (cfg, v) => cfg.Model = v);
    }

    public IReadOnlyList<PrepChoice> PrepEffortChoices => _prepEffortChoices;

    /// <summary><c>interview.prep_reasoning_effort</c>.</summary>
    public string PrepEffort
    {
        get => _config().Interview.PrepReasoningEffort;
        set => EditConfig(value, cfg => cfg.PrepReasoningEffort, (cfg, v) => cfg.PrepReasoningEffort = v);
    }

    public IReadOnlyList<PrepChoice> AnswerEffortChoices => _answerEffortChoices;

    /// <summary><c>interview.reasoning_effort</c>.</summary>
    public string AnswerEffort
    {
        get => _config().Interview.ReasoningEffort;
        set => EditConfig(value, cfg => cfg.ReasoningEffort, (cfg, v) => cfg.ReasoningEffort = v);
    }

    /// <summary>Write one <c>interview</c> key the way Settings does: mutate the live config, then save it.</summary>
    private void EditConfig(string? value, Func<Config.InterviewGroup, string> read, Action<Config.InterviewGroup, string> write)
    {
        if (_refreshing || value is null) return;
        var config = _config();
        if (read(config.Interview) == value) return;
        write(config.Interview, value);
        try
        {
            _saveConfig(config);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _commandError = $"Couldn't save the setting: {e.Message}";
        }
        // A new model brings its own efforts.
        _refresher.Request();
    }

    private List<PrepChoice> BuildModelChoices(string configured)
    {
        var list = new List<PrepChoice> { new("", $"Recommended ({InterviewConfig.RecommendedModel})") };
        var models = Codex.Models;
        list.AddRange(models.Select(m => new PrepChoice(m.Id, m.DisplayName)));
        if (configured.Length > 0 && models.All(m => m.Id != configured)) list.Add(new PrepChoice(configured, configured));
        return list;
    }

    /// <summary>The model's efforts (low/medium/high when unlisted), with the current value first if missing.</summary>
    private List<PrepChoice> BuildEfforts(string model, string current)
    {
        var values = Codex.Efforts(model).ToList();
        if (!values.Contains(current)) values.Insert(0, current);
        return values.Select(v => new PrepChoice(v, Capitalized(v))).ToList();
    }

    private static string Capitalized(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];

    // ── start row ────────────────────────────────────────────────────────────────────────

    public bool Preparing => _interview.Preparing;

    public string RunningText => $"Preparing — {_interview.RunningStep?.Title() ?? "starting"}…";

    /// <summary>Stopped at a step, and not running now: Continue / Start over.</summary>
    public bool IsFailed => !_interview.Preparing && _interview.PreparationFailedAt is not null;

    public string FailedText => _interview.PreparationFailedAt is { } step ? $"Stopped at {step.Title()}" : "";

    /// <summary>Neither running nor stopped: the Start preparation button.</summary>
    public bool IsIdle => !_interview.Preparing && _interview.PreparationFailedAt is null;

    public bool ShowPreparedSeal => IsIdle && _interview.IsPrepared;

    public string StartLabel => _interview.IsPrepared ? "Prepa_re again" : "Sta_rt preparation";

    public string? Blocker => _interview.PreparationBlocker;

    public bool ShowBlocker => Blocker is not null && !_interview.Preparing;

    /// <summary>
    /// Why the run stopped (the controller's status line) — the Mac shows it only in the
    /// answers panel, which is hidden during the preparation stage; or a button's own failure.
    /// </summary>
    public string? StatusText => _commandError ?? (IsFailed ? _interview.Status : null);

    /// <summary>SPEC-16 §9.4: the configured model is not offered and another one is used.</summary>
    public string? ModelNotice => _interview.ModelNotice;

    private bool CanStart() => _interview.PreparationBlocker is null && !_interview.Preparing;

    public PrepAsyncCommand StartCommand { get; }
    public PrepAsyncCommand ContinueCommand { get; }
    public PrepAsyncCommand RestartCommand { get; }

    private void Fail(Exception e)
    {
        _commandError = e.Message;
        Refresh();
    }

    // ── start over, footer ───────────────────────────────────────────────────────────────

    /// <summary>The "Start over" link: an interview record exists, never started recording, nothing running.</summary>
    public bool ShowStartOver => _interview.Record is { StartedAt: null } && !_interview.Preparing;

    /// <summary>Confirmed Start over: delete the unstarted record and its thread, then a fresh form (Mac).</summary>
    public async Task DiscardAsync()
    {
        _commandError = null;
        try
        {
            await _interview.DiscardUnstartedAsync();
            _interview.ResetForNewInterview();
        }
        catch (Exception e)
        {
            _commandError = e.Message;
        }
        Refresh();
    }

    public string LeaveLabel => _interview.IsPrepared ? "_Back to the interview" : "S_kip preparation";

    public string LeaveToolTip => _interview.IsPrepared
        ? "Show the captions and answers"
        : "Go to the captions and answers without preparing — the coach still works";

    public bool CanLeave => !_interview.Preparing;

    /// <summary>Skip preparation / Back to the interview (Mac: <c>showingPreparation = false</c>).</summary>
    public void Leave()
    {
        if (!CanLeave) return;
        _interview.ShowingPreparation = false;
    }
}
