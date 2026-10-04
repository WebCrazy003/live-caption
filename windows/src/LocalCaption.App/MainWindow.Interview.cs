using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LocalCaption.App.Interview.Answers;
using LocalCaption.App.Interview.Platform;
using LocalCaption.App.Interview.Sessions;
using LocalCaption.App.Interview.Views;
using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;
using LocalCaption.Interview;
using LocalCaption.Session;

namespace LocalCaption.App;

/// <summary>
/// Interview mode in the main window (specs/SPEC-16 §5): the mode chooser, the preparation,
/// the interview layout, the replay, the global hotkeys and the Sessions window. Kept apart
/// from the caption shell so Caption only mode reads exactly as it did.
/// </summary>
/// <remarks>
/// <para><b>Stages</b> (Mac <c>ActiveSessionView.captionArea</c>): the chooser until a mode is
/// chosen; then Caption only shows the caption column as always; Interview mode shows the
/// preparation alone, or the interview layout — whose captions slot holds the <i>same</i>
/// caption column, moved there — or, once the interview has ended, the replay above the
/// transport bar.</para>
/// <para><b>Caption only never touches Codex</b> (owner, 2026-10-02): nothing here starts it
/// unless Interview mode is chosen; the Sessions window's archive and summary are held back in
/// Caption only mode (<see cref="InterviewServices.ArchiveThreadAsync"/>,
/// <see cref="InterviewReplayView.AllowSummarize"/>).</para>
/// </remarks>
public partial class MainWindow
{
    private enum Stage { Chooser, Captions, Preparation, Interview, Replay }

    private Stage? _stage;
    private bool _modeChosen;
    private bool _settingsOpen;
    private bool _skillsBlockStart;
    private bool _interviewRefreshQueued;
    private bool _endingInterview;      // End interview is waiting for a streaming answer (≤ 32 s)
    private SessionsWindow? _sessions;
    private string? _shownOpenBlocker;
    private Button? _compactStart, _compactPause, _compactStop;
    // Failed with captions unsaved: Pause and Stop read Retry capture / Retry save (SPEC-16 C3).
    private bool _retryShown;

    /// <summary>
    /// <c>interview.mode</c> says Interview and Interview mode is available. False whenever
    /// <see cref="_services"/> is null, so everything behind it may use the services.
    /// </summary>
    [MemberNotNullWhen(true, nameof(_services), nameof(_interview))]
    private bool IsInterviewMode => _services is not null && _interview is not null && _env.Config.Interview.IsInterviewMode;

    /// <summary>The tooltip on every disabled interview entry point when the services failed to start.</summary>
    private string? InterviewUnavailableText =>
        _interviewUnavailable is { } reason ? "Interview mode is unavailable: " + reason : null;

    private const string WaitForAnswer = "Wait for the current answer to finish";
    private const string ChangeModeTip = "Change mode — back to Caption only / Interview";

    /// <summary>Wire the interview pieces to this window. Once, from the constructor.</summary>
    private void SetUpInterview()
    {
        ToolTipService.SetShowOnDisabled(StartButton, true);
        ToolTipService.SetShowOnDisabled(ChangeModeButton, true);
        ModeChooser.ModeChosen += OnModeChosen;
        PreviewKeyDown += OnInterviewKeys;

        if (_services is null || _interview is null)
        {
            // Interview mode failed to start (App.OnStartup): Caption only, and say why.
            ModeChooser.InterviewUnavailableReason = InterviewUnavailableText;
            ModeChooser.LastMode = InterviewConfig.CaptionMode;
            ChangeModeButton.ToolTip = InterviewUnavailableText;
            return;
        }

        // Ask reads the transcript at press time: committed finals (no bookmarks), the live
        // interim, and the sample clock — which stops while paused, so pauses don't count.
        _interview.TranscriptSource = () =>
        {
            var segments = _controller.CommittedSegments().Select(s => new AskSelection.Segment(s)).ToList();
            var orchestrator = _controller.Orchestrator;
            return (segments, orchestrator.Hypothesis, orchestrator.RecordedMs);
        };
        _interview.Changed += (_, _) => QueueInterviewRefresh();
        _interview.SessionsChanged += (_, _) =>
        {
            RefreshSessions();
            _sessions?.Reload();
        };
        _services.Library.Changed += () => Dispatcher.BeginInvoke(QueueInterviewRefresh);

        var hotkeys = _services.Hotkeys;
        var interview = _interview;
        hotkeys.AskPressed += () => Fire(interview.AskAsync, "the Ask hotkey");
        hotkeys.ScreenshotPressed += () => Fire(interview.TakeScreenshotAsync, "the Screenshot hotkey");
        hotkeys.StateChanged += ShowHotkeyState;

        ModeChooser.LastMode = _env.Config.Interview.Mode;

        Preparation.Attach(_interview, () => _env.Config, SaveConfig);
        Preparation.OpenSkillsSettingsRequested += (_, _) => OpenSettings("Interview");
        Preparation.Left += (_, _) => UpdateStage();

        InterviewLayout.LayoutStateChanged += OnInterviewLayoutChanged;
        InterviewLayout.FontSizeChangeRequested += (_, size) => SetFont(size);
        InterviewLayout.PreparationRequested += (_, _) => UpdateStage();
        InterviewLayout.CompactTransport = BuildCompactTransport();

        ApplyInterviewSettings();
    }

    /// <summary>The preparation's model row writes the same keys as Settings; a read-only volume runs from memory.</summary>
    private void SaveConfig(Config config)
    {
        try { _env.Update(config); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Run an interview action from an event; it never throws into the dispatcher.</summary>
    private static async void Fire(Func<Task> action, string what)
    {
        try { await action(); }
        catch (Exception e) { InterviewPlatformLog.Write("app", $"{what} failed: {e.Message}"); }
    }

    // ── mode (§5.1) ──────────────────────────────────────────────────────────────────────

    /// <summary>The chooser's choice. Interview asks for the privacy notice once.</summary>
    private void OnModeChosen(object? sender, string mode)
    {
        var cfg = _env.Config;
        if (mode == InterviewConfig.InterviewMode && _services is null)
        {
            ModeChooser.FocusDefault();      // the card is disabled; a stray Enter lands here
            return;
        }
        if (mode == InterviewConfig.InterviewMode && !cfg.Interview.PrivacyAcknowledged)
        {
            _clickThrough.Set(false);
            if (!PrivacyNoticeDialog.Ask(this))
            {
                ModeChooser.FocusDefault();      // Cancel: stay on the chooser
                return;
            }
            cfg.Interview.PrivacyAcknowledged = true;
        }
        cfg.Interview.Mode = mode;
        Save();
        EnterChosenMode();
    }

    /// <summary>Leave the chooser for the mode in <c>interview.mode</c>.</summary>
    private void EnterChosenMode()
    {
        _modeChosen = true;
        if (IsInterviewMode)
        {
            // The Mac's status row checks Codex when it appears; here only in Interview mode.
            _services.CheckCodexOnce();
        }
        else if (_services is not null && _interview is not null)
        {
            // Caption only: forget the clipboard's sequence number without reading it, so
            // nothing copied in this mode is picked up when Interview mode comes back; and no
            // codex process carries on from Interview mode (SPEC-16 §11.2) — stopped now, or
            // once the work still in flight finishes (StopCodexForCaptionOnly retries).
            _interview.PollClipboard();
            _services.StopCodexForCaptionOnly();
        }
        UpdateTooltips();
        UpdateStage();
        RefreshTransport();
        // An open Sessions window's replay offers Summarize only in Interview mode: repaint it.
        _sessions?.RefreshOpenState();
    }

    /// <summary>Header → Change mode: back to the chooser (only while nothing is live, unsaved or saving).</summary>
    private void OnChangeMode(object sender, RoutedEventArgs e)
    {
        if (!CanChangeMode) return;
        _modeChosen = false;
        ModeChooser.LastMode = _env.Config.Interview.Mode;
        UpdateStage();
        RefreshTransport();
    }

    private bool CanChangeMode => ChangeModeBlocker() is null;

    /// <summary>
    /// Why Change mode is off now, or null. Recording, unsaved or saving (the button's own
    /// tooltip covers those, as before); an answer, preparation or summary in flight, or End
    /// interview still waiting for one — leaving Interview mode then would leave codex running
    /// into Caption only mode; or Interview mode is unavailable, so there is nothing to change to.
    /// </summary>
    private string? ChangeModeBlocker()
    {
        if (InterviewUnavailableText is { } unavailable) return unavailable;
        if (_controller.Phase is SessionPhase.Recording or SessionPhase.Pausing or SessionPhase.Paused or SessionPhase.Saving ||
            _controller.HasUnsavedSession)
            return ChangeModeTip;
        if (_endingInterview || _interview?.IsBusy == true) return WaitForAnswer;
        return null;
    }

    // ── stages ───────────────────────────────────────────────────────────────────────────

    private Stage CurrentStage()
    {
        if (!_modeChosen) return Stage.Chooser;
        if (!IsInterviewMode) return Stage.Captions;      // also whenever Interview mode is unavailable
        if (_controller.Phase == SessionPhase.Saved && _interview.IsFinished) return Stage.Replay;
        return _interview.ShowingPreparation ? Stage.Preparation : Stage.Interview;
    }

    /// <summary>Show the stage the state calls for. Cheap when nothing changed; safe to call on every refresh.</summary>
    private void UpdateStage()
    {
        var stage = CurrentStage();
        if (stage == _stage) return;
        _stage = stage;

        ModeChooser.Visibility = Shown(stage == Stage.Chooser);
        SessionPane.Visibility = Shown(stage != Stage.Chooser);
        if (stage == Stage.Chooser) ModeChooser.LastMode = _env.Config.Interview.Mode;

        // The caption column — caption view plus transport bar — is moved, never copied: into
        // the interview layout's captions slot, or back home. An element has one parent.
        if (stage == Stage.Interview)
        {
            if (!ReferenceEquals(InterviewLayout.Captions, CaptionColumn))
            {
                CaptionHome.Content = null;
                InterviewLayout.Captions = CaptionColumn;
            }
        }
        else if (!ReferenceEquals(CaptionHome.Content, CaptionColumn))
        {
            InterviewLayout.Captions = null;
            CaptionHome.Content = CaptionColumn;
        }
        CaptionHome.Visibility = Shown(stage is Stage.Captions or Stage.Replay);

        // Views listen to the controller only while they are the stage shown: an answer
        // streaming into a collapsed panel is work nobody sees.
        Preparation.Visibility = Shown(stage == Stage.Preparation);
        InterviewLayout.Visibility = Shown(stage == Stage.Interview);
        InterviewLayout.Controller = stage == Stage.Interview && IsInterviewMode ? _interview : null;

        CaptionArea.Visibility = Shown(stage != Stage.Replay);
        Replay.Visibility = Shown(stage == Stage.Replay);
        if (stage == Stage.Replay && IsInterviewMode)
        {
            Replay.Interactive = true;
            Replay.AllowSummarize = () => IsInterviewMode;
            Replay.AnswerFontSize = _env.Config.Caption.FontSize;
            Replay.Transcript = _controller.CommittedText;
            Replay.Controller = _interview;
        }
        else
        {
            Replay.Controller = null;
        }

        // The preparation fills the window: no quick toolbar, no captions, no transport.
        QuickScroll.Visibility = Shown(stage != Stage.Preparation);
        PlaceStatusStrip(top: stage is Stage.Preparation or Stage.Interview or Stage.Replay);

        if (stage == Stage.Preparation)
        {
            Preparation.Refresh();
            Dispatcher.BeginInvoke(Preparation.FocusFirstField, DispatcherPriority.Input);
        }

        ApplyModeLabels();
        UpdateHotkeys();
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The status strip sits under the captions in Caption only mode (as it always has), and
    /// over the stage in Interview mode — where the captions may be hidden or not shown at all.
    /// </summary>
    private void PlaceStatusStrip(bool top)
    {
        var target = top ? StatusSlotTop : StatusSlotCaption;
        if (ReferenceEquals(StatusStrip.Parent, target)) return;
        if (StatusStrip.Parent is Border old) old.Child = null;
        target.Child = StatusStrip;
        StatusStrip.Margin = top ? new Thickness(0, 0, 0, 10) : new Thickness(0, 10, 0, 0);
    }

    /// <summary>The words that change with the mode: the header button and Stop / End interview.</summary>
    private void ApplyModeLabels()
    {
        var interview = IsInterviewMode;
        ChangeModeButton.Content = interview ? "Interview" : "Caption only";
        AutomationProperties.SetName(ChangeModeButton, $"Change mode (now {(interview ? "Interview" : "Caption only")})");

        var stop = _retryShown ? "Retry save" : interview ? "End interview" : "Stop";
        StopButton.Content = stop;
        StopButton.Tag = _retryShown ? "\uE74E" : interview ? "" : "";       // save / flag / stop
        AutomationProperties.SetName(StopButton, stop);
        if (_compactStop is { } compact)
        {
            compact.ToolTip = stop;
            AutomationProperties.SetName(compact, stop);
        }
        UpdateTooltips();
    }

    /// <summary>Coalesce the controller's <c>Changed</c> (it fires per streamed token) into one refresh.</summary>
    private void QueueInterviewRefresh()
    {
        if (_interviewRefreshQueued) return;
        _interviewRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _interviewRefreshQueued = false;
            RefreshInterviewState();
        });
    }

    /// <summary>Everything that follows the session phase or the interview's state, minus the captions.</summary>
    private void RefreshInterviewState()
    {
        UpdateStage();
        RefreshTransport();
        UpdateHotkeys();
        PushOpenState();

        // Caption only runs no codex: a stop deferred while an answer or summary was in flight
        // happens once it has finished (the services retry too, for Sessions-window replays).
        // Not while Settings is open — its Codex page may be signing in at the user's request.
        if (_modeChosen && !IsInterviewMode && !_settingsOpen) _services?.StopCodexForCaptionOnly();
    }

    // ── transport ────────────────────────────────────────────────────────────────────────

    /// <summary>Start / Pause / Stop, the compact copies, Change mode and the Preparation button.</summary>
    private void RefreshTransport()
    {
        var phase = _controller.Phase;
        var orchestrator = _controller.Orchestrator;

        // Interview mode: Start waits for the four skills (owner, 2026-10-02 — loaded by hand in
        // Settings → Interview → Skills). The chooser on screen means no mode yet: no Start.
        var skillsBlock = IsInterviewMode && !_interview.AllSkillsLoaded;
        // End interview is still waiting for a streaming answer: a new session would race it.
        var ending = _endingInterview;
        if (skillsBlock != _skillsBlockStart)
        {
            _skillsBlockStart = skillsBlock;
            UpdateTooltips();
        }

        StartButton.IsEnabled = _modeChosen && !skillsBlock && !ending &&
                                phase is SessionPhase.Ready or SessionPhase.Saved or SessionPhase.Failed &&
                                orchestrator.ModelReady && !_controller.HasUnsavedSession;
        // SPEC-16 C3 (Mac ActiveSessionView): capture or the save failed with captions still
        // unsaved. Pause becomes Retry capture (ResumeAsync carries the session on) and Stop
        // becomes Retry save; the journal keeps everything meanwhile.
        var retry = phase == SessionPhase.Failed && _controller.HasUnsavedSession;
        if (retry != _retryShown)
        {
            _retryShown = retry;
            ApplyModeLabels();
        }
        PauseButton.IsEnabled = phase is SessionPhase.Recording or SessionPhase.Paused || retry;
        PauseButton.Content = retry ? "Retry capture" : phase == SessionPhase.Paused ? "Resume" : "Pause";
        PauseButton.Tag = phase == SessionPhase.Paused || retry ? "" : "";
        StopButton.IsEnabled = phase is SessionPhase.Recording or SessionPhase.Paused ||
                               (phase == SessionPhase.Failed && _controller.HasUnsavedSession);

        if (_compactStart is { } start && _compactPause is { } pause && _compactStop is { } stop)
        {
            start.IsEnabled = StartButton.IsEnabled;
            pause.IsEnabled = PauseButton.IsEnabled;
            pause.Tag = PauseButton.Tag;
            var label = retry ? "Retry capture" : phase == SessionPhase.Paused ? "Resume" : "Pause";
            pause.ToolTip = label;
            AutomationProperties.SetName(pause, label);
            stop.IsEnabled = StopButton.IsEnabled;
        }

        var changeBlocker = ChangeModeBlocker();
        ChangeModeButton.IsEnabled = _modeChosen && changeBlocker is null;
        ChangeModeButton.ToolTip = changeBlocker ?? ChangeModeTip;
        InterviewLayout.ShowPreparationButton =
            phase is not (SessionPhase.Recording or SessionPhase.Pausing or SessionPhase.Paused or SessionPhase.Saving);
    }

    /// <summary>
    /// Icon-only Start / Pause / End interview for the interview header, shown there only while
    /// the captions (and so the transport bar) are hidden. Same handlers as the bar.
    /// </summary>
    private FrameworkElement BuildCompactTransport()
    {
        Button Make(string glyph, string name, RoutedEventHandler click)
        {
            var button = new Button { Tag = glyph, ToolTip = name, Focusable = true, Margin = new Thickness(0, 0, 2, 0) };
            button.SetResourceReference(StyleProperty, "Button.Ghost");
            AutomationProperties.SetName(button, name);
            ToolTipService.SetShowOnDisabled(button, true);
            button.Click += click;
            return button;
        }

        _compactStart = Make("", "Start", OnStart);
        _compactPause = Make("", "Pause", OnPauseResume);
        _compactStop = Make("", "End interview", OnStop);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_compactStart);
        row.Children.Add(_compactPause);
        row.Children.Add(_compactStop);
        return row;
    }

    // ── layout, font and hotkeys ─────────────────────────────────────────────────────────

    /// <summary>Feed the layout, the replay and the answers what config says. At launch and after Settings.</summary>
    private void ApplyInterviewSettings()
    {
        var ui = _env.Config.Ui;
        InterviewLayout.PanelLayout = _env.Config.Interview.PanelLayout;
        InterviewLayout.CaptionShareWide = ui.InterviewCaptionShareWide;
        InterviewLayout.AnswerShareStacked = ui.InterviewAnswerShareStacked;
        InterviewLayout.CaptionsHidden = ui.InterviewCaptionsHidden;
        InterviewLayout.AnswerFontSize = _env.Config.Caption.FontSize;
        Replay.AnswerFontSize = _env.Config.Caption.FontSize;
        ShowHotkeyState();
    }

    /// <summary>After Settings closes, saved or not: skills may have been loaded, keys and layout changed.</summary>
    private void AfterInterviewSettings()
    {
        ApplyInterviewSettings();
        Preparation.Refresh();
        UpdateTooltips();
        RefreshInterviewState();
    }

    /// <summary>The split moved or the captions were hidden: per-machine state, in the Windows <c>ui</c> group.</summary>
    private void OnInterviewLayoutChanged(object? sender, EventArgs e)
    {
        var ui = _env.Config.Ui;
        ui.InterviewCaptionShareWide = InterviewLayout.CaptionShareWide;
        ui.InterviewAnswerShareStacked = InterviewLayout.AnswerShareStacked;
        ui.InterviewCaptionsHidden = InterviewLayout.CaptionsHidden;
        SaveSoon();
    }

    /// <summary>
    /// The Ask and Screenshot hotkeys are live only in Interview mode, past the preparation,
    /// until the session is saved (SPEC-16 §4.2; Mac <c>ActiveSessionView.updateHotkey</c>) —
    /// and not while Settings is open, so its recorder can hear them. Re-evaluated on every
    /// change of any of those; the service ignores calls that change nothing.
    /// </summary>
    private void UpdateHotkeys()
    {
        if (_services is null || _interview is null) return;
        var active = _modeChosen && IsInterviewMode && !_settingsOpen &&
                     !_interview.ShowingPreparation && _controller.Phase != SessionPhase.Saved;
        _services.Hotkeys.Update(_services.AskHotkey, _services.ScreenshotHotkey, active);
    }

    /// <summary>"F8 unavailable" chips, and the keys the Ask / camera buttons name.</summary>
    private void ShowHotkeyState()
    {
        if (_services is null) return;
        var hotkeys = _services.Hotkeys;
        InterviewLayout.AskHotkeyUnavailable = Problem(hotkeys.AskState);
        InterviewLayout.ScreenshotHotkeyUnavailable = Problem(hotkeys.ScreenshotState);
        InterviewLayout.AnswersPanel.AskHotkeyLabel = Label(hotkeys.AskState, _services.AskHotkey);
        InterviewLayout.AnswersPanel.ScreenshotHotkeyLabel = Label(hotkeys.ScreenshotState, _services.ScreenshotHotkey);

        static HotkeyProblem? Problem(GlobalHotkeyState state) => state is GlobalHotkeyState.Unavailable unavailable
            ? new HotkeyProblem(HotkeyMapping.DisplayName(unavailable.Hotkey), unavailable.Reason)
            : null;

        static string Label(GlobalHotkeyState state, Hotkey configured) =>
            HotkeyMapping.DisplayName(state is GlobalHotkeyState.Registered registered ? registered.Hotkey : configured);
    }

    /// <summary>Ctrl+L opens the Sessions window; Ctrl+K puts the caret in the coach box.</summary>
    private void OnInterviewKeys(object sender, KeyEventArgs e)
    {
        // A configurable shortcut on the same keys was matched first (ShortcutManager) and wins.
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.L)
        {
            OpenSessions();
            e.Handled = true;
        }
        else if (e.Key == Key.K && _stage == Stage.Interview)
        {
            InterviewLayout.AnswersPanel.FocusCoachInput();
            e.Handled = true;
        }
    }

    // ── Sessions window (§5.6) ───────────────────────────────────────────────────────────

    private void OnOpenSessions(object sender, RoutedEventArgs e) => OpenSessions();

    /// <summary>One Sessions window, kept and re-activated; <paramref name="select"/> shows that session.</summary>
    private void OpenSessions(long? select = null)
    {
        if (_sessions is null)
        {
            _sessions = new SessionsWindow(new SessionsHost
            {
                Store = _env.Store,
                ShowTimestamps = () => _env.Config.Caption.ShowTimestamps,
                CaptionFontSize = () => _env.Config.Caption.FontSize,
                OpenBlocker = OpenInPanelBlocker,
                HasUnstartedPreparation = () => _interview?.HasUnstartedPreparation == true,
                OpenInPanel = OpenSavedInterview,
                ArchiveThread = id =>
                {
                    if (_services is { } services) _ = services.ArchiveThreadAsync(id);
                    else InterviewPlatformLog.Write("sessions", $"left thread {id} unarchived: Interview mode is unavailable");
                },
                // Without the services the window shows the saved transcript instead.
                CreateReplay = _services is null ? null : CreateReplay,
                SessionsChanged = RefreshSessions,
                LoadPlacement = () => _env.Config.Ui.SessionsWindow is { } b ? new Rect(b.X, b.Y, b.Width, b.Height) : (Rect?)null,
                SavePlacement = rect =>
                {
                    _env.Config.Ui.SessionsWindow = new Config.WindowBounds
                    {
                        X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height,
                    };
                    Save();
                },
            })
            {
                Owner = this,      // above a pinned main window, minimised with it
            };
            _sessions.Closed += (_, _) => _sessions = null;
            _shownOpenBlocker = OpenInPanelBlocker();
            _sessions.Show();
        }
        if (select is { } id) _sessions.Select(id);
        else
        {
            if (_sessions.WindowState == WindowState.Minimized) _sessions.WindowState = WindowState.Normal;
            _sessions.Activate();
        }
    }

    /// <summary>
    /// Why a saved interview can't be opened here now, or null (Mac
    /// <c>AppEnvironment.openInPanelBlocker</c>).
    /// </summary>
    private string? OpenInPanelBlocker()
    {
        if (InterviewUnavailableText is { } unavailable) return unavailable;
        if (!_controller.CanOpenSaved)
            return _controller.HasUnsavedSession ? "Stop the current session first" : "Wait until the speech model is ready";
        if (_endingInterview || _interview?.IsBusy == true) return WaitForAnswer;
        return null;
    }

    /// <summary>Tell an open Sessions window when Open in Interview Panel becomes possible or not.</summary>
    private void PushOpenState()
    {
        if (_sessions is null) return;
        var blocker = OpenInPanelBlocker();
        if (blocker == _shownOpenBlocker) return;
        _shownOpenBlocker = blocker;
        _sessions.RefreshOpenState();
    }

    /// <summary>
    /// Sessions → Open in Interview Panel (Mac <c>AppEnvironment.openInPanel</c>): the saved
    /// interview in the main window, as right after End interview — its captions in the caption
    /// view, the replay with summary and follow-ups on the same thread.
    /// </summary>
    private async void OpenSavedInterview(InterviewRecord saved)
    {
        try
        {
            if (OpenInPanelBlocker() is not null || _interview is not { } interview ||
                saved.SessionId is not { } sessionId || _env.Store.Fetch(sessionId) is not { } record) return;

            // Opening it switches to Interview mode, whose terms come first.
            if (!_env.Config.Interview.PrivacyAcknowledged)
            {
                _clickThrough.Set(false);
                if (!PrivacyNoticeDialog.Ask(this)) return;
                _env.Config.Interview.PrivacyAcknowledged = true;
            }

            // The user already agreed to lose an unstarted preparation (the Sessions window asked).
            if (interview.HasUnstartedPreparation) await interview.DiscardUnstartedAsync();
            if (OpenInPanelBlocker() is not null) return;      // something started meanwhile

            var segments = _env.Store.Segments(sessionId);
            _env.Config.Interview.Mode = InterviewConfig.InterviewMode;
            Save();
            interview.Load(saved);
            Captions.Reset();
            _controller.OpenSaved(record, segments);
            _stage = null;      // re-enter the replay: its transcript is this session's now
            EnterChosenMode();
            Activate();
        }
        catch (Exception e)
        {
            InterviewPlatformLog.Write("sessions", $"opening a saved interview failed: {e.Message}");
            MessageBox.Show(this, $"The interview could not be opened.\n\n{e.Message}", "Local Caption",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>The Sessions window's read-only replay of a saved interview, on its own controller.</summary>
    private object? CreateReplay(InterviewRecord record, string transcript) => _services is null ? null : new InterviewReplayView
    {
        Controller = _services.OpenSaved(record),
        Interactive = false,
        // Summarizing resumes the thread, which starts Codex: not in Caption only mode. Asked
        // each time (the button's state and the click), so a replay left open across a switch
        // to Caption only can't start it; EnterChosenMode repaints an open one.
        AllowSummarize = () => IsInterviewMode,
        AnswerFontSize = _env.Config.Caption.FontSize,
        Transcript = transcript.Length > 0 ? transcript : record.Transcript ?? "",
    };
}
