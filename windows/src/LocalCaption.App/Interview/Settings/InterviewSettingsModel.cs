using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;
using LocalCaption.Interview;

namespace LocalCaption.App.Interview.Settings;

/// <summary>
/// The view model behind Settings → Interview, Asking, Prompts and Codex (specs/SPEC-16 §5.7,
/// Mac <c>InterviewSettings.swift</c>). One instance is shared by the four pages.
/// </summary>
/// <remarks>
/// <para><b>Edit a copy, then Save.</b> Like the rest of the Windows Settings window, config
/// keys are edited on <see cref="Draft"/> — a copy of <c>config.interview</c> — and reach the
/// real config only through <see cref="ApplyTo"/> when Save is pressed. Cancel discards them.</para>
/// <para><b>Not part of Save/Cancel:</b> loading or removing a skill (the library on disk, as on
/// the Mac) and Codex sign-in / sign-out / refresh act at once.</para>
/// <para><b>Codex is never started by opening these pages</b> (SPEC-16 §9.1): only "Check again",
/// "Sign in…" or "Refresh" in the usage section start it, unless the host sets
/// <see cref="StartCodexOnOpen"/> (Interview mode already chosen).</para>
/// <para>All members are used on the UI thread; library and Codex notifications are marshalled
/// to it.</para>
/// </remarks>
public sealed class InterviewSettingsModel : SettingsObservable, IDisposable
{
    private readonly InterviewLibrary? _library;
    private readonly CodexService? _codex;
    private readonly Dispatcher _dispatcher;
    private bool _privacyReset;
    private bool _signingOut;
    private string? _codexActionError;
    private string? _skillMessage;
    private bool _skillMessageIsError;
    private string? _askRegistrationError;
    private string? _screenshotRegistrationError;
    private IReadOnlyList<SettingsChoice> _models = [];
    private IReadOnlyList<SettingsChoice> _answerEfforts = [];
    private IReadOnlyList<SettingsChoice> _prepEfforts = [];
    private IReadOnlyList<UsageWindowModel> _usageWindows = [];
    private bool _disposed;

    /// <param name="draft">The copy to edit — <see cref="CopyOf"/>(<c>config.Interview</c>).</param>
    /// <param name="library">Skills; null hides nothing but makes Load fail with a message.</param>
    /// <param name="codex">The app's Codex service; null shows Codex as unavailable.</param>
    public InterviewSettingsModel(Config.InterviewGroup draft, InterviewLibrary? library, CodexService? codex)
    {
        Draft = draft;
        _library = library;
        _codex = codex;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Skills = new ObservableCollection<SkillSlotModel>(InterviewSteps.All.Select(s => new SkillSlotModel(s)));
        RefreshSkills();
        RefreshModelLists();
        RefreshCodex();

        if (_library is not null) _library.Changed += OnLibraryChanged;
        if (_codex is not null) _codex.Changed += OnCodexChanged;
    }

    /// <summary>The edited copy of <c>config.interview</c>.</summary>
    public Config.InterviewGroup Draft { get; }

    /// <summary>A copy to edit. Every member of the group is a string, number or bool.</summary>
    public static Config.InterviewGroup CopyOf(Config.InterviewGroup source) => source with { };

    /// <summary>
    /// Write the keys these pages edit into <paramref name="target"/> (the real config, at Save).
    /// <c>mode</c> and <c>engine</c> are not edited here and are left alone;
    /// <c>privacy_acknowledged</c> is only ever cleared, by "Show the notice again next time".
    /// </summary>
    public void ApplyTo(Config.InterviewGroup target)
    {
        target.AnswerLength = Draft.AnswerLength;
        target.BusyPolicy = Draft.BusyPolicy;
        target.ClearClipboardImagesAfterSend = Draft.ClearClipboardImagesAfterSend;
        target.CodexPath = Draft.CodexPath.Trim();
        target.CustomInstructions = Draft.CustomInstructions;
        target.Hotkey = Draft.Hotkey;
        target.IncludeClipboardImages = Draft.IncludeClipboardImages;
        target.MaxWords = Draft.MaxWords;
        target.Model = Draft.Model;
        target.PanelLayout = Draft.PanelLayout;
        target.PrepReasoningEffort = Draft.PrepReasoningEffort;
        target.ReasoningEffort = Draft.ReasoningEffort;
        target.ScreenshotHotkey = Draft.ScreenshotHotkey;
        target.SendMode = Draft.SendMode;
        target.SendSentences = Draft.SendSentences;
        if (_privacyReset) target.PrivacyAcknowledged = false;
    }

    /// <summary>Check Codex as soon as the Codex page is shown (Interview mode is in use).</summary>
    public bool StartCodexOnOpen { get; set; }

    /// <summary>
    /// Whether a <c>codex</c> process is up right now. Showing the Codex page refreshes the usage
    /// of a signed-in Codex only when this says so (or <see cref="StartCodexOnOpen"/> is set) —
    /// a status left "ready" by an earlier run must not start one in Caption only mode.
    /// Null: treated as running (the page's original behaviour).
    /// </summary>
    public Func<bool>? CodexRunning { get; set; }

    // ── Interview → Skills ───────────────────────────────────────────────────────────────

    public ObservableCollection<SkillSlotModel> Skills { get; }

    /// <summary>"Loaded discovery-cv." / the ignored files / the error, under the slots.</summary>
    public string? SkillMessage { get => _skillMessage; private set => Set(ref _skillMessage, value); }

    public bool SkillMessageIsError { get => _skillMessageIsError; private set => Set(ref _skillMessageIsError, value); }

    /// <summary>Load a <c>.md</c> file or a skill folder into a slot (Mac <c>SkillSlotRow.load</c>).</summary>
    public void LoadSkill(SkillSlotModel slot, string path)
    {
        if (_library is null)
        {
            SkillMessageIsError = true;
            SkillMessage = "The skill library isn't available.";
            return;
        }
        try
        {
            var result = _library.LoadSkill(slot.Slot, path);
            SkillMessageIsError = false;
            SkillMessage = result.Ignored.Count == 0
                ? $"Loaded {slot.Slot}."
                : $"Loaded {slot.Slot}. Left out (Codex can't use them): {string.Join(", ", result.Ignored)}";
        }
        catch (Exception e) when (e is InterviewLibrary.LibraryError or DocumentText.Failure
                                       or IOException or UnauthorizedAccessException)
        {
            SkillMessageIsError = true;
            SkillMessage = e.Message;
        }
        RefreshSkills();
    }

    public void RemoveSkill(SkillSlotModel slot)
    {
        _library?.RemoveSkill(slot.Slot);
        RefreshSkills();
    }

    private void OnLibraryChanged() => Post(RefreshSkills);

    private void RefreshSkills()
    {
        foreach (var slot in Skills) slot.Update(_library?.SkillBySlug(slot.Slot));
        if (_library?.LastError is { } error)
        {
            SkillMessageIsError = true;
            SkillMessage = error;
        }
    }

    // ── Interview → Model & answers ──────────────────────────────────────────────────────

    /// <summary>"Recommended (gpt-6-luna)", Codex's models, and the configured one if not listed.</summary>
    public IReadOnlyList<SettingsChoice> Models { get => _models; private set => Set(ref _models, value); }

    public IReadOnlyList<SettingsChoice> AnswerEfforts { get => _answerEfforts; private set => Set(ref _answerEfforts, value); }

    public IReadOnlyList<SettingsChoice> PrepEfforts { get => _prepEfforts; private set => Set(ref _prepEfforts, value); }

    public string RecommendedModel => InterviewConfig.RecommendedModel;

    /// <summary>The Mac's footer, naming the recommended model.</summary>
    public string ModelNote =>
        $"Low effort answers fastest (about 1–2 s to first words with {InterviewConfig.RecommendedModel}). "
        + "Model, answer length and custom instructions apply when the next interview's coach starts.";

    public string Model
    {
        get => Draft.Model;
        set
        {
            // A ComboBox whose list was just replaced pushes null; that is not a choice.
            if (value is null || value == Draft.Model) return;
            Draft.Model = value;
            Raise();
            RefreshEfforts();
        }
    }

    public string ReasoningEffort
    {
        get => Draft.ReasoningEffort;
        set
        {
            if (value is null || value == Draft.ReasoningEffort) return;
            Draft.ReasoningEffort = value;
            Raise();
        }
    }

    public string PrepReasoningEffort
    {
        get => Draft.PrepReasoningEffort;
        set
        {
            if (value is null || value == Draft.PrepReasoningEffort) return;
            Draft.PrepReasoningEffort = value;
            Raise();
        }
    }

    /// <summary><c>short</c> / <c>medium</c> / <c>long</c>.</summary>
    public string AnswerLength
    {
        get => Draft.AnswerLength;
        set
        {
            if (value is null || value == Draft.AnswerLength) return;
            Draft.AnswerLength = value;
            Raise();
        }
    }

    private void RefreshModelLists()
    {
        var list = new List<SettingsChoice> { new($"Recommended ({InterviewConfig.RecommendedModel})", "") };
        foreach (var m in _codex?.Models ?? [])
            list.Add(new(m.Description.Length == 0 ? m.DisplayName : $"{m.DisplayName} — {m.Description}", m.Id));
        if (Draft.Model.Length > 0 && !list.Any(c => c.Value == Draft.Model))
            list.Add(new(Draft.Model, Draft.Model));
        if (!list.SequenceEqual(Models))
        {
            Models = list;
            Raise(nameof(Model));     // re-select after the list changed
        }
        RefreshEfforts();
    }

    private void RefreshEfforts()
    {
        var model = InterviewConfig.EffectiveModel(Draft.Model);
        var answer = Efforts(model, Draft.ReasoningEffort);
        var prep = Efforts(model, Draft.PrepReasoningEffort);
        if (!answer.SequenceEqual(AnswerEfforts))
        {
            AnswerEfforts = answer;
            Raise(nameof(ReasoningEffort));
        }
        if (!prep.SequenceEqual(PrepEfforts))
        {
            PrepEfforts = prep;
            Raise(nameof(PrepReasoningEffort));
        }
    }

    /// <summary>The model's efforts (low/medium/high when unknown), the current one first if missing.</summary>
    private List<SettingsChoice> Efforts(string model, string current)
    {
        var list = (_codex?.Efforts(model) ?? ["low", "medium", "high"]).ToList();
        if (!list.Contains(current)) list.Insert(0, current);
        return list.Select(e => new SettingsChoice(Capitalized(e), e)).ToList();
    }

    // ── Interview → Layout, Privacy ──────────────────────────────────────────────────────

    public IReadOnlyList<SettingsChoice> Layouts { get; } =
    [
        new("Automatic (by window width)", "automatic"),
        new("Side by side", "side_by_side"),
        new("Stacked (answers on top)", "stacked"),
    ];

    public string PanelLayout
    {
        get => Draft.PanelLayout;
        set
        {
            if (value is null || value == Draft.PanelLayout) return;
            Draft.PanelLayout = value;
            Raise();
        }
    }

    /// <summary>"Show the notice again next time" is enabled while the notice is acknowledged.</summary>
    public bool CanShowNoticeAgain => Draft.PrivacyAcknowledged && !_privacyReset;

    public void ShowNoticeAgain()
    {
        _privacyReset = true;
        Draft.PrivacyAcknowledged = false;
        Raise(nameof(CanShowNoticeAgain));
    }

    // ── Asking ───────────────────────────────────────────────────────────────────────────

    /// <summary><c>interview.hotkey</c>, canonical text.</summary>
    public string Hotkey
    {
        get => Draft.Hotkey;
        set
        {
            if (value is null || value == Draft.Hotkey) return;
            Draft.Hotkey = value;
            Raise();
        }
    }

    /// <summary><c>interview.screenshot_hotkey</c>, canonical text.</summary>
    public string ScreenshotHotkey
    {
        get => Draft.ScreenshotHotkey;
        set
        {
            if (value is null || value == Draft.ScreenshotHotkey) return;
            Draft.ScreenshotHotkey = value;
            Raise();
        }
    }

    /// <summary>The host's "unavailable" reason for the Ask hotkey (e.g. "another app is using F8"), or null.</summary>
    public string? AskRegistrationError { get => _askRegistrationError; set => Set(ref _askRegistrationError, value); }

    /// <summary>The same for the Screenshot hotkey.</summary>
    public string? ScreenshotRegistrationError
    {
        get => _screenshotRegistrationError;
        set => Set(ref _screenshotRegistrationError, value);
    }

    /// <summary><c>since_last_ask</c> / <c>last_sentences</c>.</summary>
    public string SendMode
    {
        get => Draft.SendMode;
        set
        {
            if (value is null || value == Draft.SendMode) return;
            Draft.SendMode = value;
            RaiseMany(nameof(SendMode), nameof(IsLastSentences));
        }
    }

    public bool IsLastSentences => Draft.SendMode == InterviewConfig.SendModeLastSentences;

    /// <summary>"The latest N sentences", N clamped as the Ask uses it.</summary>
    public string LatestSentencesLabel => $"The latest {Draft.ClampedSendSentences} sentences";

    /// <summary>Sentences, 1–20 (a slider value).</summary>
    public double SendSentences
    {
        get => Draft.ClampedSendSentences;
        set
        {
            var n = (int)Math.Clamp(Math.Round(value), 1, 20);
            if (n == Draft.SendSentences) return;
            Draft.SendSentences = n;
            RaiseMany(nameof(SendSentences), nameof(SendSentencesText), nameof(LatestSentencesLabel));
        }
    }

    public string SendSentencesText => Draft.ClampedSendSentences.ToString(CultureInfo.CurrentCulture);

    /// <summary>At most N words, 50–2000 in steps of 50 (a slider value).</summary>
    public double MaxWords
    {
        get => Draft.ClampedMaxWords;
        set
        {
            var n = (int)Math.Clamp(Math.Round(value / 50) * 50, 50, 2000);
            if (n == Draft.MaxWords) return;
            Draft.MaxWords = n;
            RaiseMany(nameof(MaxWords), nameof(MaxWordsText));
        }
    }

    public string MaxWordsText => $"{Draft.ClampedMaxWords.ToString(CultureInfo.CurrentCulture)} words";

    public IReadOnlyList<SettingsChoice> BusyPolicies { get; } =
    [
        new("Interrupt it and answer the new question", InterviewConfig.BusyPolicyInterrupt),
        new("Queue the new question", InterviewConfig.BusyPolicyQueue),
    ];

    public string BusyPolicy
    {
        get => Draft.BusyPolicy;
        set
        {
            if (value is null || value == Draft.BusyPolicy) return;
            Draft.BusyPolicy = value;
            Raise();
        }
    }

    public bool IncludeClipboardImages
    {
        get => Draft.IncludeClipboardImages;
        set
        {
            if (value == Draft.IncludeClipboardImages) return;
            Draft.IncludeClipboardImages = value;
            Raise();
        }
    }

    public bool ClearClipboardImagesAfterSend
    {
        get => Draft.ClearClipboardImagesAfterSend;
        set
        {
            if (value == Draft.ClearClipboardImagesAfterSend) return;
            Draft.ClearClipboardImagesAfterSend = value;
            Raise();
        }
    }

    /// <summary>The Screenshots footer, with the tray limit.</summary>
    public string ScreenshotsNote =>
        "When on, every screenshot you copy during an interview (Win+Shift+S) is added to the current "
        + $"prompt — up to {InterviewController.MaxPendingImages}. The next Ask or Send takes "
        + "them all to OpenAI. Images already on the clipboard when the interview opens are ignored; "
        + "only images are read, never text.";

    // ── Prompts ──────────────────────────────────────────────────────────────────────────

    public string CustomInstructions
    {
        get => Draft.CustomInstructions;
        set
        {
            value ??= "";
            if (value == Draft.CustomInstructions) return;
            Draft.CustomInstructions = value;
            Raise();
        }
    }

    // ── Codex ────────────────────────────────────────────────────────────────────────────

    public string CodexPath
    {
        get => Draft.CodexPath;
        set
        {
            value ??= "";
            if (value == Draft.CodexPath) return;
            Draft.CodexPath = value;
            Raise();
        }
    }

    public bool CodexAvailable => _codex is not null;

    public bool IsReady => _codex?.IsReady == true;

    public bool Checking => _codex?.Checking == true;

    /// <summary>"Checking Codex…", the status summary, or why there is none.</summary>
    public string StatusText => _codex switch
    {
        null => "Codex isn't available in this build.",
        { Checking: true } => "Checking Codex…",
        { Status: null } => "Not checked yet. Check again to look for Codex.",
        { Status: { } s } => s.Summary,
    };

    /// <summary>Ready (✓), a problem (⚠), or neither yet (checking / unchecked).</summary>
    public CodexStatusKind StatusKind => _codex switch
    {
        null => CodexStatusKind.Problem,
        { Checking: true } or { Status: null } => CodexStatusKind.Unknown,
        { Status.IsReady: true } => CodexStatusKind.Ready,
        _ => CodexStatusKind.Problem,
    };

    public bool IsSignedOut => _codex is { Checking: false, Status: EngineStatus.SignedOut };

    public bool SigningIn => _codex?.SignIn is not null;

    /// <summary>The row's "Sign in…" (signed out, no sign-in running).</summary>
    public bool ShowSignIn => IsSignedOut && !SigningIn;

    /// <summary>The row's "Cancel" (a sign-in is waiting for the browser).</summary>
    public bool ShowCancelSignIn => IsSignedOut && SigningIn;

    /// <summary>The row's own "Check again" (a status that isn't ready or signed out).</summary>
    public bool ShowRowCheckAgain => _codex is { Checking: false, Status: { IsReady: false } and not EngineStatus.SignedOut };

    public string? SignInError => _codex?.SignInError;

    /// <summary>The tightest usage window's line, when signed in.</summary>
    public string? LowestUsageLine =>
        _codex is { IsReady: true, Usage.Lowest: { } w } ? UsageLine.For(w) : null;

    public bool UsageIsLow => _codex?.UsageIsLow == true;

    public bool SigningOut { get => _signingOut; private set => Set(ref _signingOut, value); }

    public bool CanCheckAgain => _codex is not null && !Checking && !SigningOut;

    /// <summary>A Codex call that failed outright (the service keeps its own sign-in errors).</summary>
    public string? CodexActionError { get => _codexActionError; private set => Set(ref _codexActionError, value); }

    public IReadOnlyList<UsageWindowModel> UsageWindows { get => _usageWindows; private set => Set(ref _usageWindows, value); }

    public bool HasUsage => UsageWindows.Count > 0;

    public string? PlanText => _codex?.Usage?.PlanType is { Length: > 0 } plan ? $"Plan: {Capitalized(plan)}" : null;

    public string? UpdatedText => _codex?.UsageUpdatedAt is { } at
        ? "Updated " + at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)
        : null;

    /// <summary>What the usage section says when there is nothing to show.</summary>
    public string NoUsageText => IsReady ? "No usage reported yet." : "Sign in to see your ChatGPT usage.";

    /// <summary>The Codex page became visible: refresh usage if signed in, and check if allowed.</summary>
    public async Task CodexPageShownAsync()
    {
        if (_codex is null) return;
        if (_codex.IsReady)
        {
            if (StartCodexOnOpen || CodexRunning?.Invoke() != false) await Run(_codex.RefreshUsageAsync);
        }
        else if (_codex.Status is null && !_codex.Checking && StartCodexOnOpen) await Run(_codex.RefreshAsync);
    }

    public Task CheckAgainAsync() => _codex is null ? Task.CompletedTask : Run(_codex.RefreshAsync);

    public Task SignInAsync() => _codex is null ? Task.CompletedTask : Run(_codex.StartSignInAsync);

    public Task CancelSignInAsync() => _codex is null ? Task.CompletedTask : Run(_codex.CancelSignInAsync);

    /// <summary>The usage section's Refresh: usage when signed in, else a full check.</summary>
    public Task RefreshUsageAsync() =>
        _codex is null ? Task.CompletedTask : Run(_codex.IsReady ? _codex.RefreshUsageAsync : _codex.RefreshAsync);

    /// <summary>Sign LocalCaption's own Codex home out (the caller has confirmed).</summary>
    public async Task SignOutAsync()
    {
        if (_codex is null || SigningOut) return;
        SigningOut = true;
        Raise(nameof(CanCheckAgain));
        try
        {
            await Run(_codex.SignOutAsync);
        }
        finally
        {
            SigningOut = false;
            Raise(nameof(CanCheckAgain));
        }
    }

    private async Task Run(Func<Task> work)
    {
        CodexActionError = null;
        try
        {
            await work();
        }
        catch (Exception e)
        {
            CodexActionError = e.Message;
        }
    }

    private void OnCodexChanged(object? sender, EventArgs e) => Post(() =>
    {
        RefreshCodex();
        RefreshModelLists();
    });

    private void RefreshCodex()
    {
        UsageWindows = _codex?.Usage is { } usage
            ? usage.Windows.Select(w => new UsageWindowModel(w)).ToList()
            : [];
        RaiseMany(nameof(IsReady), nameof(Checking), nameof(StatusText), nameof(StatusKind), nameof(IsSignedOut),
                  nameof(SigningIn), nameof(ShowSignIn), nameof(ShowCancelSignIn), nameof(ShowRowCheckAgain),
                  nameof(SignInError), nameof(LowestUsageLine), nameof(UsageIsLow), nameof(CanCheckAgain),
                  nameof(HasUsage), nameof(PlanText), nameof(UpdatedText), nameof(NoUsageText));
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────

    private void Post(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(() => { if (!_disposed) action(); });
    }

    /// <summary>Foundation's <c>capitalized</c>: each word's first letter upper-case.</summary>
    internal static string Capitalized(string s) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

    /// <summary>Stop listening to the library and Codex (the Settings window closed).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_library is not null) _library.Changed -= OnLibraryChanged;
        if (_codex is not null) _codex.Changed -= OnCodexChanged;
    }
}

/// <summary>The Codex status row's icon.</summary>
public enum CodexStatusKind { Unknown, Ready, Problem }

/// <summary>One Settings → Skills slot.</summary>
public sealed class SkillSlotModel : SettingsObservable
{
    private bool _isLoaded;
    private string _detail = "";

    public SkillSlotModel(InterviewStep step)
    {
        Step = step;
        Slot = step.Slug();
    }

    public InterviewStep Step { get; }

    /// <summary>The slot name, e.g. <c>discovery-cv</c>.</summary>
    public string Slot { get; }

    public bool IsLoaded { get => _isLoaded; private set => Set(ref _isLoaded, value); }

    /// <summary>"title · N characters", or "Not loaded".</summary>
    public string Detail { get => _detail; private set => Set(ref _detail, value); }

    /// <summary>"Load…" / "Replace…".</summary>
    public string LoadLabel => IsLoaded ? "Replace…" : "Load…";

    public string RemoveName => $"Remove {Slot}";

    public string LoadFolderName => $"Load the {Slot} skill from a folder";

    internal void Update(InterviewLibraryIndex.Skill? skill)
    {
        IsLoaded = skill is not null;
        Detail = skill is not null
            ? $"{skill.Title} · {skill.Chars.ToString("N0", CultureInfo.CurrentCulture)} characters"
            : Step == InterviewStep.LiveCoding ? "Not loaded (used only if you tick it)" : "Not loaded";
        Raise(nameof(LoadLabel));
    }
}

/// <summary>One Plus usage window: label, % left, bar, reset line.</summary>
public sealed class UsageWindowModel(CodexRpc.Usage.Window window)
{
    public string Label { get; } = window.Label;
    public double Remaining { get; } = window.RemainingPercent;
    public string RemainingText { get; } = $"{window.RemainingPercent}% left";
    public bool IsLow { get; } = window.RemainingPercent < 20;
    public string Line { get; } = UsageLine.For(window);
}
