using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using LocalCaption.Asr;
using LocalCaption.Core.Data;
using LocalCaption.Core.Transcripts;
using LocalCaption.Session;

namespace LocalCaption.App;

/// <summary>
/// The shell: session list on the left, active session on the right (§7.1).
/// </summary>
/// <remarks>
/// <para>Code-behind rather than MVVM on purpose. <see cref="SessionController"/> already
/// owns the state and raises one <c>Changed</c> event; a view-model layer between it and
/// these controls would only restate the same properties.</para>
/// <para>Everything here runs on the UI thread. <c>Changed</c> can arrive from a capture or
/// decode thread, so it is marshalled — and coalesced, because during recording it fires far
/// faster than a screen refreshes.</para>
/// <para>The quick toolbar is the one place that writes config outside Settings. Each control
/// saves on change and then asks the controller to apply it; see
/// <see cref="SessionController.ReconfigureAsync"/> for what "apply" costs mid-recording.</para>
/// </remarks>
public partial class MainWindow : ChromeWindow
{
    /// <summary>Below this the session list gives its width to the captions.</summary>
    private const double NarrowWidth = 700;

    private readonly AppEnvironment _env;
    private readonly SessionController _controller;
    private readonly ShortcutManager _shortcuts;
    private readonly DispatcherTimer _meter;
    private bool _refreshQueued;
    // True from the first line of the constructor: sliders report a "change" while XAML is
    // still coercing their initial value, before there is a config to change.
    private bool _syncing = true;     // the toolbar is being filled, not used
    private bool _narrow;             // the window is too narrow for the sidebar
    private bool _narrowOverride;     // ...but the user asked for it anyway
    private SessionPhase? _shownPhase;
    private readonly DispatcherTimer _saveSoon;
    private readonly AnswerSender _sender;
    private readonly ClickThrough _clickThrough;
    private DateTime _viewClosedAt;

    public MainWindow(AppEnvironment env)
    {
        InitializeComponent();

        _env = env;
        _controller = new SessionController(env);
        _controller.Changed += OnControllerChanged;
        _controller.Copied += () => Dispatcher.BeginInvoke(() => Flash("✓ COPIED"));

        _sender = new AnswerSender(() => _env.Config);
        _controller.TurnCompleted += turn =>
        {
            // Off the pipeline's thread and off the UI's: a send may wait on a file or a socket.
            if (_sender.SendsAutomatically) _ = Task.Run(() => Report(_sender.Send(turn)));
        };

        _clickThrough = new ClickThrough(this);
        _clickThrough.Changed += () =>
        {
            ClickThroughButton.IsChecked = _clickThrough.IsOn;
            // Something clicked through must stay on top, or the first click buries it. This
            // borrows the pin for as long as click-through is on, and hands it back after.
            Topmost = _env.Config.Ui.PinOnTop || _clickThrough.IsOn;
            UpdateTooltips();
        };

        // The clipboard belongs to the UI thread and needs the §7.4 retry, so the session
        // layer is handed a function rather than owning one.
        env.Clipboard = ClipboardWriter.Copy;

        Captions.FollowingChanged += following =>
            Dispatcher.BeginInvoke(() => JumpButton.Visibility = following ? Visibility.Collapsed : Visibility.Visible);

        // The level meter is not session state — it is a continuous reading, and binding it
        // to Changed would either miss frames or force a redraw per audio packet.
        _meter = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _meter.Tick += (_, _) =>
            LevelBar.Width = LevelTrack.Width * Math.Clamp(_controller.Orchestrator.Level * 3, 0, 1);
        _meter.Start();

        _shortcuts = new ShortcutManager(this, Run);

        // A slider reports every pixel of a drag. Applying each one is what makes it feel
        // live; writing config.json for each one is not, so the write waits for a pause.
        _saveSoon = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveSoon.Tick += (_, _) => { _saveSoon.Stop(); Save(); };

        WindowPlacement.Restore(this, env.Config);
        ApplySettings();
        FillSortOptions();
        FillQuickBar();
        RefreshSessions();
        ApplyShell();

        ThemeManager.Changed += OnThemeChanged;
        SizeChanged += (_, _) => FitSidebar();

        Loaded += async (_, _) =>
        {
            // Global hotkeys need the window's handle, which does not exist before now.
            _shortcuts.Load(_env.Config.Shortcuts);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
            if (_sender.Apply() is { } problem) Flash(problem, ok: false);
            ConfirmMissingModels();
            await _controller.PrepareAsync();
        };
        Closing += OnClosing;
    }

    // ── controls ─────────────────────────────────────────────────────────────────────────

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        Captions.Reset();
        await _controller.StartAsync();
    }

    private async void OnPauseResume(object sender, RoutedEventArgs e)
    {
        if (_controller.Phase == SessionPhase.Recording) await _controller.PauseAsync();
        else await _controller.ResumeAsync();
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        await _controller.StopAsync();
        RefreshSessions();
    }

    private void OnCopy(object sender, RoutedEventArgs e) => _controller.CopyLastN();

    private void OnCopyAll(object sender, RoutedEventArgs e) => _controller.CopyAll();

    private void OnCopySelection(object sender, RoutedEventArgs e)
    {
        if (Captions.SelectionLength > 0) Captions.Copy();
    }

    private void OnJump(object sender, RoutedEventArgs e) => Captions.JumpToLatest();

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        var asr = _env.Config.Asr;
        var engineBefore = (asr.InterimModel, asr.FinalModel, asr.Backend, asr.Threads, asr.Vocabulary, asr.FinalBeamSize);

        _clickThrough.Set(false);
        var settings = new SettingsWindow(_env, _controller, _shortcuts, _sender) { Owner = this };
        _shortcuts.Suspend();
        var saved = settings.ShowDialog() == true;
        _shortcuts.Load(_env.Config.Shortcuts);
        if (!saved) return;
        if (_sender.Apply() is { } problem) Flash(problem, ok: false);

        ApplySettings();
        FillQuickBar();
        ApplyShell();

        // A model picked in Settings used to wait for the next launch: the engine loads once
        // and PrepareModelAsync is idempotent. Now it loads as soon as that is safe — at once
        // when idle or paused, at the next pause when recording. Settings is a considered,
        // modal change, so unlike the toolbar it never interrupts a recording to apply one.
        asr = _env.Config.Asr;
        if (engineBefore != (asr.InterimModel, asr.FinalModel, asr.Backend, asr.Threads, asr.Vocabulary, asr.FinalBeamSize))
            await _controller.ReconfigureAsync(reloadModels: true, interrupt: false);
    }

    /// <summary>§10: font size, auto-scroll and timestamps apply live; the rest at next Start.</summary>
    private void ApplySettings()
    {
        Captions.FontSize = _env.Config.Caption.FontSize;
        Captions.AutoScroll = _env.Config.Caption.AutoScroll;
    }

    /// <summary>Write config now. A read-only volume runs from memory, as everywhere else.</summary>
    private void Save()
    {
        try { _env.Update(_env.Config); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ── shell: theme, pin, sidebar ───────────────────────────────────────────────────────

    /// <summary>Put the window in the state config describes. Safe to call repeatedly.</summary>
    private void ApplyShell()
    {
        var ui = _env.Config.Ui;
        if (ThemeManager.Preference != ThemeManager.Normalise(ui.Theme)) ThemeManager.Apply(ui.Theme);

        Topmost = ui.PinOnTop || _clickThrough.IsOn;
        SendButton.Visibility = _env.Config.Send.Target == "off" ? Visibility.Collapsed : Visibility.Visible;
        PinButton.IsChecked = ui.PinOnTop;
        PinButton.Tag = ui.PinOnTop ? "\uE840" : "\uE718";

        OnThemeChanged();
        FitSidebar();
        UpdateTooltips();
    }

    private void OnThemeChanged()
    {
        // The button shows where a click goes, not where you are: a sun in the dark.
        ThemeButton.Tag = ThemeManager.IsDark ? "\uE706" : "\uE708";
        Captions.ApplyTheme();
        ApplyOpacity();          // the faded grounds are made from the palette, so remake them
        _shownPhase = null;      // so the phase dot is repainted from the new palette
        Refresh();
    }

    // ── view flyout: text size and see-through ───────────────────────────────────────────

    /// <summary>The popup follows the button's checked state, so mouse, keyboard and UI automation all open it.</summary>
    private void OnViewChecked(object sender, RoutedEventArgs e)
    {
        // A StaysOpen=False popup closes on the mouse-down of any click outside it — including
        // the click on this button that was meant to close it. That click then arrives here
        // and would open it straight back up.
        if ((DateTime.UtcNow - _viewClosedAt).TotalMilliseconds < 250)
        {
            ViewButton.IsChecked = false;
            return;
        }

        ViewPopup.PlacementTarget = ViewButton;
        ViewPopup.IsOpen = true;
    }

    private void OnViewUnchecked(object sender, RoutedEventArgs e) => ViewPopup.IsOpen = false;

    private void OnViewClosed(object? sender, EventArgs e)
    {
        _viewClosedAt = DateTime.UtcNow;
        ViewButton.IsChecked = false;
    }

    private void OnFontSlid(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing) SetFont((int)Math.Round(e.NewValue));
    }

    private void OnSeeThroughSlid(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;

        // The last few percent are indistinguishable from solid to look at, but not to the
        // window: anything under 100% swaps the frame for the see-through one and gives up
        // its shadow. Nobody drags to 1% on purpose, so treat the bottom of the slider as off.
        var seeThrough = e.NewValue < 4 ? 0 : e.NewValue;
        _env.Config.Window.Opacity = Math.Clamp(1 - seeThrough / 100, 0.1, 1);
        ApplyOpacity();
        SaveSoon();
    }

    private void SaveSoon()
    {
        _saveSoon.Stop();
        _saveSoon.Start();
    }

    /// <summary>The grounds that fade. Inputs, menus and popups stay solid — they are read, not looked through.</summary>
    private static readonly string[] Grounds = ["Bg.Window", "Bg.Panel", "Bg.Surface"];

    /// <summary>
    /// Fade the window's background to <c>window.opacity</c>, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>Whole-window opacity is the easy version and the useless one: at 40% the captions
    /// are 40% too, over a moving video, which is unreadable exactly when it matters. Here
    /// only the ground brushes are replaced — in this window's own resources, which shadow
    /// the palette for everything inside it and leave Settings untouched — so text, icons
    /// and controls stay at full strength.</para>
    /// <para>Panels sit on top of the window ground, so their fade compounds and they are
    /// always a little more solid than the slider says. That is kept on purpose, and the
    /// caption surface is given more still — it should be the last thing to disappear.</para>
    /// <para>Below three-quarters solid the captions and header also get a soft halo in the
    /// ground's own colour, so a bright frame of video behind a word cannot wash it out.</para>
    /// </remarks>
    private void ApplyOpacity()
    {
        var opacity = Math.Clamp(_env.Config.Window.Opacity, 0.1, 1.0);
        if (opacity > 0.96) opacity = 1.0;      // see OnSeeThroughSlid: the bottom of the slider is "off"
        var solid = opacity >= 0.995;
        SeeThrough = !solid;

        foreach (var key in Grounds)
        {
            if (solid || Application.Current.TryFindResource(key) is not SolidColorBrush ground)
            {
                Resources.Remove(key);
                continue;
            }

            // The caption surface gives way more slowly than everything around it. It is the
            // one part being read rather than glanced at, so it keeps a third of whatever the
            // slider took away — see-through enough to watch a face, never so thin that a
            // bright frame of video behind a sentence makes the sentence disappear.
            var share = key == "Bg.Surface" ? opacity + (1 - opacity) * 0.35 : opacity;

            var colour = ground.Color;
            var faded = new SolidColorBrush(Color.FromArgb((byte)Math.Round(colour.A * share),
                                                           colour.R, colour.G, colour.B));
            faded.Freeze();
            Resources[key] = faded;
        }

        Effect? halo = null;
        if (opacity < 0.75)
        {
            halo = new DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 5,
                Opacity = 1,
                Color = ThemeManager.IsDark ? Colors.Black : Colors.White,
                RenderingBias = RenderingBias.Performance,
            };
            halo.Freeze();
        }
        Captions.Effect = halo;
        HeaderGrid.Effect = halo;

        var seeThrough = Math.Round((1 - opacity) * 100);
        SeeThroughValue.Text = $"{seeThrough:0}%";
        if (Math.Abs(SeeThroughSlider.Value - seeThrough) > 0.5)
        {
            var syncing = _syncing;
            _syncing = true;
            SeeThroughSlider.Value = seeThrough;
            _syncing = syncing;
        }
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        _env.Config.Ui.Theme = ThemeManager.Toggled();
        ThemeManager.Apply(_env.Config.Ui.Theme);
        Save();
        UpdateTooltips();
    }

    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        _env.Config.Ui.PinOnTop = !_env.Config.Ui.PinOnTop;
        Save();
        ApplyShell();
    }

    private void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        var ui = _env.Config.Ui;
        if (_narrow)
        {
            // Too narrow to show it by default. The toggle still works, but what it flips is
            // "just for now" — it must not rewrite the preference for a full-width window.
            _narrowOverride = !SidebarShown;
            if (_narrowOverride) ui.SidebarCollapsed = false;
        }
        else
        {
            ui.SidebarCollapsed = !ui.SidebarCollapsed;
        }

        Save();
        FitSidebar();
        UpdateTooltips();
    }

    private bool SidebarShown => Sidebar.Visibility == Visibility.Visible;

    /// <summary>
    /// Show or hide the session list, from the saved preference and the room available.
    /// </summary>
    /// <remarks>
    /// Docked beside a video call the window is a few hundred pixels wide, and a 260-pixel
    /// list of old sessions is the last thing worth spending them on. So it steps aside by
    /// itself when narrow and comes back when there is room, without touching the preference.
    /// </remarks>
    private void FitSidebar()
    {
        var ui = _env.Config.Ui;
        var narrow = ActualWidth > 0 && ActualWidth < NarrowWidth;
        if (narrow != _narrow)
        {
            _narrow = narrow;
            _narrowOverride = false;
        }

        var show = !ui.SidebarCollapsed && (!_narrow || _narrowOverride);
        var width = Math.Clamp(ui.SidebarWidth, 190, 440);

        // The button draws what a click will do: a pane closing, or a pane opening.
        SidebarButton.Tag = show ? "\uE89F" : "\uE8A0";

        // Slide only when the list is actually appearing or going — not on the first layout,
        // not while the window is being dragged narrower, and not for someone who has asked
        // Windows to stop animating things.
        if (IsLoaded && show != _sidebarShown && SystemParameters.ClientAreaAnimation)
        {
            SlideSidebar(show, width);
        }
        else if (_sidebarSlide is null)
        {
            SettleSidebar(show, width);
        }
        _sidebarShown = show;
    }

    private bool _sidebarShown = true;
    private EventHandler? _sidebarSlide;

    /// <summary>The resting state: a real column with real limits, or none at all.</summary>
    private void SettleSidebar(bool show, double width)
    {
        Sidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = Sidebar.Visibility;
        SidebarContent.Width = double.NaN;
        SidebarContent.HorizontalAlignment = HorizontalAlignment.Stretch;
        SidebarColumn.MinWidth = show ? 190 : 0;
        SidebarColumn.MaxWidth = show ? 440 : 0;
        SidebarColumn.Width = new GridLength(show ? width : 0);
    }

    /// <summary>
    /// Slide the list in or out over a fifth of a second.
    /// </summary>
    /// <remarks>
    /// <para>WPF cannot animate a <c>GridLength</c>, so the column's width is set by hand on
    /// each rendered frame, eased so it arrives gently. The list inside keeps its full width
    /// and its right edge, and is clipped by the narrowing column — so it slides away as a
    /// whole rather than being squashed, which is the difference between a panel closing and
    /// a layout having a bad moment.</para>
    /// <para>The captions re-wrap on every frame of this, which is why it is short.</para>
    /// </remarks>
    private void SlideSidebar(bool show, double width)
    {
        if (_sidebarSlide is not null) CompositionTarget.Rendering -= _sidebarSlide;

        var from = Sidebar.Visibility == Visibility.Visible ? SidebarColumn.ActualWidth : 0;
        var to = show ? width : 0;

        Sidebar.Visibility = Visibility.Visible;
        SidebarSplitter.Visibility = Visibility.Collapsed;
        SidebarContent.Width = width;
        SidebarContent.HorizontalAlignment = HorizontalAlignment.Right;
        SidebarColumn.MinWidth = 0;
        SidebarColumn.MaxWidth = 440;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        const double duration = 200;

        _sidebarSlide = (_, _) =>
        {
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / duration);
            var eased = 1 - Math.Pow(1 - t, 3);                   // ease-out cubic
            SidebarColumn.Width = new GridLength(Math.Max(0, from + (to - from) * eased));
            if (t < 1) return;

            CompositionTarget.Rendering -= _sidebarSlide;
            _sidebarSlide = null;
            SettleSidebar(show, width);
        };
        CompositionTarget.Rendering += _sidebarSlide;
    }

    private void OnSidebarResized(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _env.Config.Ui.SidebarWidth = SidebarColumn.ActualWidth;
        Save();
    }

    /// <summary>Tooltips name the key, and the keys are the user's — so they are built, not written.</summary>
    private void UpdateTooltips()
    {
        var keys = _env.Config.Shortcuts;
        string With(string text, ShortcutAction action) =>
            ShortcutManager.Describe(keys, action) is { Length: > 0 } key ? $"{text}  ({key})" : text;

        SidebarButton.ToolTip = With(SidebarShown ? "Hide sessions" : "Show sessions", ShortcutAction.ToggleSidebar);
        PinButton.ToolTip = With(_env.Config.Ui.PinOnTop ? "Stop keeping on top" : "Keep on top of other windows", ShortcutAction.TogglePin);
        ThemeButton.ToolTip = With(ThemeManager.IsDark ? "Switch to light" : "Switch to dark", ShortcutAction.ToggleTheme);
        SettingsButton.ToolTip = With("Settings", ShortcutAction.Settings);
        ClickThroughButton.ToolTip = With("Click-through: let clicks pass to the call underneath", ShortcutAction.ToggleClickThrough);
        _clickThrough.ShortcutHint = ShortcutManager.Describe(keys, ShortcutAction.ToggleClickThrough);
        QuestionButton.ToolTip = With("Copy everything since the speaker last paused", ShortcutAction.CopyLastQuestion);
        SendButton.ToolTip = With("Send the last question to your answer tool", ShortcutAction.SendLastQuestion);
        BookmarkButton.ToolTip = With("Bookmark this moment in the transcript", ShortcutAction.Bookmark);
        ViewButton.ToolTip = "Text size and see-through";

        StartButton.ToolTip = With("Start a new session", ShortcutAction.Start);
        PauseButton.ToolTip = With("Pause or resume", ShortcutAction.PauseResume);
        StopButton.ToolTip = With("Stop and save the transcript", ShortcutAction.Stop);
        CopyButton.ToolTip = With($"Copy the last {_env.Config.Clipboard.RecentSentences} sentences", ShortcutAction.CopyLastN);
        CopyAllButton.ToolTip = With("Copy the whole transcript so far", ShortcutAction.CopyAll);
        JumpButton.ToolTip = With("Jump to the latest caption", ShortcutAction.JumpLatest);
    }

    // ── quick toolbar ────────────────────────────────────────────────────────────────────

    /// <summary>One detection setting: what it is called, and its <c>vad_sensitivity</c>.</summary>
    private sealed record SensitivityChoice(string Label, int Value);

    /// <summary>Fill the toolbar from config. Runs at launch and whenever Settings saves.</summary>
    private void FillQuickBar()
    {
        var config = _env.Config;
        _syncing = true;
        try
        {
            FillSources();

            FillModels();

            var backends = new[] { "auto", "cuda", "cpu" };
            BackendBox.ItemsSource = backends;
            BackendBox.SelectedItem = backends.Contains(config.Asr.Backend) ? config.Asr.Backend : "auto";

            var sensitivities = new[]
            {
                new SensitivityChoice("Low", 0), new SensitivityChoice("Medium", 1),
                new SensitivityChoice("High", 2), new SensitivityChoice("Highest", 3),
            };
            SensitivityBox.ItemsSource = sensitivities;
            SensitivityBox.SelectedItem = sensitivities[Math.Clamp(config.Audio.VadSensitivity, 0, 3)];

            FontValue.Text = $"{config.Caption.FontSize} pt";
            FontSlider.Value = config.Caption.FontSize;
            CopyValue.Text = config.Clipboard.RecentSentences.ToString();
            CopyButton.Content = $"Copy last {config.Clipboard.RecentSentences}";
            FollowToggle.IsChecked = config.Caption.AutoScroll;
            AutoCopyToggle.IsChecked = config.Clipboard.AutoUpdate;
        }
        finally
        {
            _syncing = false;
        }

        SourceBox.ToolTip = "What to listen to. Changing it while recording switches over at once, " +
                            "finishing the sentence in progress first.";
        InterimBox.ToolTip = "The fast model behind the grey live text. Smaller is quicker.";
        FinalBox.ToolTip = "The accurate model behind the saved transcript. Changing a model while " +
                           "recording pauses for a few seconds to load it, then carries on.";
        BackendBox.ToolTip = "Where the models run. 'auto' uses the GPU when a CUDA runtime is present. " +
                             "Moving from cpu to cuda may need the app restarted.";
        SensitivityBox.ToolTip = "How quiet speech can be and still count. Raise it for soft voices, " +
                                 "lower it if background noise is being captioned.";
        CopyChip.ToolTip = "How many sentences 'Copy last N' takes";
        FollowToggle.ToolTip = "Keep the newest caption in view";
        AutoCopyToggle.ToolTip = "Copy the latest captions to the clipboard as they arrive";
    }

    private void FillSources()
    {
        var choices = AudioChoices.All(_env.Config);
        SourceBox.ItemsSource = choices;
        SourceBox.SelectedItem = AudioChoices.Current(choices, _env.Config);
    }

    /// <summary>Applications come and go during a call; list the ones there now.</summary>
    private void OnSourceOpening(object? sender, EventArgs e)
    {
        _syncing = true;
        try { FillSources(); }
        finally { _syncing = false; }
    }

    private async void OnQuickSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SourceBox.SelectedItem is not SourceChoice choice) return;

        var audio = _env.Config.Audio;
        audio.CaptureMode = choice.Mode;
        if (choice.Mode == "process") audio.TargetProcess = choice.Value;
        else audio.OutputDevice = choice.Value;

        Save();
        await _controller.ReconfigureAsync(reloadModels: false);
    }

    /// <summary>One model in a picker: its name, and whether picking it means a download.</summary>
    private sealed record ModelChoice(string Name, string Label, long DownloadBytes);

    private static string Size(long bytes) =>
        bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000.0:0.0} GB" : $"{bytes / 1_000_000.0:0} MB";

    private void FillModels()
    {
        var asr = _env.Config.Asr;
        var names = ModelCatalog.All.Select(m => m.Name).ToList();
        foreach (var chosen in new[] { asr.InterimModel, asr.FinalModel })
            if (!names.Contains(chosen, StringComparer.OrdinalIgnoreCase)) names.Add(chosen);

        var choices = names.Select(name =>
        {
            var spec = ModelCatalog.Resolve(name);
            var here = File.Exists(ModelCatalog.PathFor(spec, LocalCaption.Core.AppPaths.Models));
            return new ModelChoice(name, here ? name : $"{name}  ↓ {Size(spec.ApproxBytes)}", here ? 0 : spec.ApproxBytes);
        }).ToList();

        InterimBox.ItemsSource = choices;
        InterimBox.SelectedItem = choices.First(m => m.Name.Equals(asr.InterimModel, StringComparison.OrdinalIgnoreCase));
        FinalBox.ItemsSource = choices;
        FinalBox.SelectedItem = choices.First(m => m.Name.Equals(asr.FinalModel, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A model downloaded since the list was built should stop saying it needs downloading.</summary>
    private void OnModelsOpening(object? sender, EventArgs e)
    {
        _syncing = true;
        try { FillModels(); }
        finally { _syncing = false; }
    }

    /// <summary>
    /// At launch: if config names a model that is not here, ask before fetching gigabytes.
    /// </summary>
    /// <remarks>
    /// A first run has no models at all and downloads without asking — that is what a first
    /// run is for. This is the other case: models are already here and working, and config
    /// names one that is not, usually because it was picked once and the download never
    /// happened. Quietly starting 1.5 GB on the next launch is not what anyone meant.
    /// </remarks>
    private void ConfirmMissingModels()
    {
        var missing = StreamingOrchestrator.MissingModels(_env.Config);
        if (missing.Count == 0) return;

        var here = ModelCatalog.All.Where(m => File.Exists(ModelCatalog.PathFor(m, LocalCaption.Core.AppPaths.Models))).ToList();
        if (here.Count == 0) return;      // a first run

        var names = string.Join(" and ", missing.Select(m => $"{m.Name} ({Size(m.ApproxBytes)})"));
        if (ConfirmDialog.Ask(this, "Download speech model?",
                $"Your settings ask for {names}, which is not on this PC yet.\n\n" +
                "Download it now, or carry on with the models you already have?",
                "Download", "Use what I have")) return;

        // Smallest for the live lane, largest for the final one — the same shape as the defaults.
        var asr = _env.Config.Asr;
        if (missing.Any(m => m.Name.Equals(asr.InterimModel, StringComparison.OrdinalIgnoreCase))) asr.InterimModel = here[0].Name;
        if (missing.Any(m => m.Name.Equals(asr.FinalModel, StringComparison.OrdinalIgnoreCase))) asr.FinalModel = here[^1].Name;
        Save();
        FillQuickBar();
    }

    private async void OnQuickModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;

        var asr = _env.Config.Asr;

        // Picking a model that is not here is a request for a download, and a download this
        // size is asked about — once, with the number in front of the person paying for it.
        if ((sender as ComboBox)?.SelectedItem is ModelChoice { DownloadBytes: > 0 } wanted &&
            !ConfirmDialog.Ask(this, "Download speech model?",
                $"{wanted.Name} is not on this PC yet. It is a {Size(wanted.DownloadBytes)} download, kept in your " +
                "Local Caption folder and only ever fetched once.\n\nRecording carries on with the current model " +
                "while it downloads, and switches over when it is ready.", "Download"))
        {
            OnModelsOpening(sender, EventArgs.Empty);      // put the picker back on what is loaded
            return;
        }

        asr.InterimModel = (InterimBox.SelectedItem as ModelChoice)?.Name ?? asr.InterimModel;
        asr.FinalModel = (FinalBox.SelectedItem as ModelChoice)?.Name ?? asr.FinalModel;
        asr.Backend = BackendBox.SelectedItem as string ?? asr.Backend;

        Save();
        await _controller.ReconfigureAsync(reloadModels: true);
    }

    private async void OnQuickSensitivityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SensitivityBox.SelectedItem is not SensitivityChoice choice) return;

        _env.Config.Audio.VadSensitivity = choice.Value;
        Save();
        await _controller.ReconfigureAsync(reloadModels: false);
    }

    private void StepFont(int by) => SetFont(_env.Config.Caption.FontSize + by);

    /// <summary>One way in for the slider and for the Ctrl+= / Ctrl+- shortcuts, so they agree.</summary>
    private void SetFont(int size)
    {
        var caption = _env.Config.Caption;
        size = Math.Clamp(size, 12, 32);
        if (size == caption.FontSize) return;

        caption.FontSize = size;
        Captions.FontSize = size;
        FontValue.Text = $"{size} pt";

        var syncing = _syncing;
        _syncing = true;
        FontSlider.Value = size;
        _syncing = syncing;

        SaveSoon();
    }

    private void OnCopyFewer(object sender, RoutedEventArgs e) => StepCopy(-1);

    private void OnCopyMore(object sender, RoutedEventArgs e) => StepCopy(+1);

    private void StepCopy(int by)
    {
        var clipboard = _env.Config.Clipboard;
        var count = Math.Clamp(clipboard.RecentSentences + by, 1, 50);
        if (count == clipboard.RecentSentences) return;

        clipboard.RecentSentences = count;
        CopyValue.Text = count.ToString();
        CopyButton.Content = $"Copy last {count}";
        Save();
        UpdateTooltips();
    }

    private void OnQuickFollow(object sender, RoutedEventArgs e)
    {
        _env.Config.Caption.AutoScroll = FollowToggle.IsChecked == true;
        Captions.AutoScroll = _env.Config.Caption.AutoScroll;
        if (Captions.AutoScroll) Captions.JumpToLatest();
        Save();
    }

    private void OnQuickAutoCopy(object sender, RoutedEventArgs e)
    {
        _env.Config.Clipboard.AutoUpdate = AutoCopyToggle.IsChecked == true;
        Save();
    }

    /// <summary>
    /// The wheel scrolls the toolbar sideways, and never spins a drop-down.
    /// </summary>
    /// <remarks>
    /// A WPF combo box changes its selection when the wheel turns over it with focus. Here a
    /// selection change reloads a speech model, so a stray scroll mid-call would cost several
    /// seconds of captions. An open list still scrolls — that wheel is meant.
    /// </remarks>
    private void OnQuickWheel(object sender, MouseWheelEventArgs e)
    {
        if (QuickBar.Children.OfType<ComboBox>().Any(c => c.IsDropDownOpen)) return;

        QuickScroll.ScrollToHorizontalOffset(QuickScroll.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    /// <summary>
    /// A two-finger sideways swipe on a touchpad, which WPF does not hear at all.
    /// </summary>
    /// <remarks>
    /// WPF raises <c>MouseWheel</c> for WM_MOUSEWHEEL and has never had an event for
    /// WM_MOUSEHWHEEL, so a horizontal swipe reaches a WPF window and stops. The toolbar is
    /// the one thing here that scrolls sideways, and on a laptop that gesture is the obvious
    /// way to do it — so it is picked up from the message loop directly.
    /// </remarks>
    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int horizontalWheel = 0x020E;      // WM_MOUSEHWHEEL
        if (message != horizontalWheel || !QuickScroll.IsMouseOver || QuickScroll.ScrollableWidth <= 0) return IntPtr.Zero;
        if (QuickBar.Children.OfType<ComboBox>().Any(c => c.IsDropDownOpen)) return IntPtr.Zero;

        var delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));     // positive: towards the right
        QuickScroll.ScrollToHorizontalOffset(QuickScroll.HorizontalOffset + delta / 2.0);
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>
    /// Fade whichever end of the toolbar has more beyond it.
    /// </summary>
    /// <remarks>
    /// A row of chips cut off square at the edge looks like a layout bug; one that fades out
    /// looks like it continues. An opacity mask rather than a gradient drawn on top, so it
    /// needs no colour and is right in either theme.
    /// </remarks>
    private void OnQuickScrolled(object sender, ScrollChangedEventArgs e)
    {
        var width = QuickScroll.ActualWidth;
        var more = QuickScroll.ScrollableWidth - QuickScroll.HorizontalOffset > 1;
        var less = QuickScroll.HorizontalOffset > 1;

        if (width <= 0 || (!more && !less))
        {
            QuickScroll.OpacityMask = null;
            return;
        }

        var fade = Math.Min(0.25, 36 / width);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        mask.GradientStops.Add(new GradientStop(less ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, less ? fade : 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, more ? 1 - fade : 1));
        mask.GradientStops.Add(new GradientStop(more ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        QuickScroll.OpacityMask = mask;
    }

    // ── session list ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One row of the session list: the record, plus its date in the reader's own time.
    /// </summary>
    /// <remarks>
    /// <c>created_at</c> is stored as ISO UTC (§9.1), because that is what keeps transcripts
    /// diffable against the macOS build. Bound straight to the list it puts
    /// "2026-09-20T12:30:19Z" in front of someone who recorded at half past two.
    /// </remarks>
    private sealed record SessionRow(SessionRecord Record, string SessionName, string When);

    /// <summary>§17.1's sort orders, in the order someone would reach for them.</summary>
    private sealed record SortChoice(string Label, SessionSort Sort);

    private void FillSortOptions()
    {
        SortBox.ItemsSource = new[]
        {
            new SortChoice("Newest first", SessionSort.CreatedDesc),
            new SortChoice("Oldest first", SessionSort.CreatedAsc),
            new SortChoice("Name A–Z", SessionSort.NameAsc),
            new SortChoice("Name Z–A", SessionSort.NameDesc),
            new SortChoice("Longest first", SessionSort.DurationDesc),
            new SortChoice("Shortest first", SessionSort.DurationAsc),
        };
        SortBox.SelectedIndex = 0;
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e) => RefreshSessions();

    private void RefreshSessions()
    {
        var search = SearchBox.Text;
        var sort = (SortBox.SelectedItem as SortChoice)?.Sort ?? SessionSort.CreatedDesc;
        var sessions = _env.Store.All(sort, string.IsNullOrWhiteSpace(search) ? null : search);

        SessionList.ItemsSource = sessions.Select(s => new SessionRow(
            s, s.SessionName,
            TimeFormat.ParseIso(s.CreatedAt) is { } at
                ? TimeFormat.HumanShort(at.ToLocalTime())
                : s.CreatedAt)).ToList();

        EmptyList.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyList.Text = string.IsNullOrWhiteSpace(search)
            ? "No saved sessions yet. Press Start to record one."
            : "No session matches that search.";
        SessionCount.Text = sessions.Count.ToString();
    }

    private void OnSearchChanged(object sender, RoutedEventArgs e) => RefreshSessions();

    private void OnOpenTranscript(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OnOpenSelected(object sender, RoutedEventArgs e) => OpenSelected();

    /// <summary>
    /// Select whatever was right-clicked, before the context menu acts on it.
    /// </summary>
    /// <remarks>
    /// WPF does not do this. Without it the menu operates on the previous selection — or on
    /// nothing at all, which looked like the commands were simply broken.
    /// </remarks>
    private void OnItemRightClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item) item.IsSelected = true;
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row) return;
        var path = row.Record.TranscriptFile;

        if (string.IsNullOrEmpty(path))
        {
            Explain(row, "This session has no transcript file recorded against it.");
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
                return;
            }

            // The file has moved or been deleted. Its folder is still the useful place to
            // land, and saying so beats doing nothing.
            var folder = Path.GetDirectoryName(path);
            if (folder is { Length: > 0 } && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                Explain(row, $"The transcript is no longer in this folder.\n\n{path}");
                return;
            }

            Explain(row, $"That folder no longer exists.\n\n{path}");
        }
        catch (Exception ex)
        {
            Explain(row, $"Windows could not open that location.\n\n{path}\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Say what went wrong with a session's file, and offer to drop the row.
    /// </summary>
    /// <remarks>
    /// The transcript is the durable artefact and the database row only points at it, so a
    /// row whose file has gone is stale bookkeeping — worth clearing, never worth silence.
    /// </remarks>
    private void Explain(SessionRow row, string message)
    {
        var remove = ConfirmDialog.Ask(this, "Transcript not found",
            $"{message}\n\nRemove “{row.SessionName}” from the list?", "Remove from list", "Keep");

        if (!remove || row.Record.Id is not { } id) return;
        _env.Store.Delete(id);
        RefreshSessions();
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row || row.Record.Id is not { } id) return;

        var name = RenameDialog.Ask(this, row.SessionName);
        if (name is null || name == row.SessionName) return;

        // The name on the row, not the transcript's filename. Renaming the file would break
        // the path already recorded in the database and in the .json sidecar.
        _env.Store.Rename(id, name);
        RefreshSessions();
    }

    private void OnSessionKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        OnDelete(sender, e);
        e.Handled = true;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row || row.Record.Id is not { } id) return;

        var text = row.Record.TranscriptFile;
        var sidecar = string.IsNullOrEmpty(text) ? "" : Path.ChangeExtension(text, ".json");
        var files = new[] { text, sidecar }.Where(f => !string.IsNullOrEmpty(f) && File.Exists(f)).ToList();

        var choice = ConfirmDialog.AskToRemove(this, row.SessionName, row.When, text ?? "", files.Count > 0);
        if (choice == ConfirmDialog.Removal.Cancelled) return;

        // The list row is only a pointer; the transcript is the interview (§7.4). So the
        // default leaves the files alone, deleting them is a box that has to be ticked, and
        // even then they go to the Recycle Bin — one wrong click must not be able to cost
        // someone a transcript for good.
        if (choice == ConfirmDialog.Removal.ListAndFiles)
        {
            var failed = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file!,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                catch (Exception) { failed.Add(Path.GetFileName(file!)); }
            }

            if (failed.Count > 0)
            {
                // Keep the row: it is the only thing still pointing at files that are still there.
                Flash($"Could not delete {string.Join(", ", failed)} — is it open somewhere?", ok: false);
                return;
            }
            Flash("✓ MOVED TO THE RECYCLE BIN");
        }

        _env.Store.Delete(id);
        RefreshSessions();
    }

    private SessionRow? Selected() => SessionList.SelectedItem as SessionRow;

    private void OpenSelected()
    {
        if (Selected() is not { } row) return;
        var path = row.Record.TranscriptFile;

        if (string.IsNullOrEmpty(path))
        {
            Explain(row, "This session has no transcript file recorded against it.");
            return;
        }

        if (!File.Exists(path))
        {
            Explain(row, $"That transcript is no longer on disk.\n\n{path}");
            return;
        }

        // The read-only transcript viewer §7.1 asks for is the OS's — a text file in the
        // default editor beats a window that only shows text files.
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Windows could not open the transcript.\n\n{path}\n\n{ex.Message}",
                "Local Caption", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── state → screen ───────────────────────────────────────────────────────────────────

    private void OnControllerChanged()
    {
        // Coalesce: during recording this arrives on every hypothesis, several times a
        // second, from threads that must not wait for a layout pass.
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        var phase = _controller.Phase;
        var orchestrator = _controller.Orchestrator;

        SessionTitle.Text = _controller.DisplayName;
        ElapsedText.Text = _controller.Elapsed;
        PhaseText.Text = Describe(phase);
        SourceText.Text = orchestrator.SourceName.Length > 0
            ? orchestrator.SourceName
            : orchestrator.ModelLabel;

        Captions.Update(_controller.Paragraphs, _controller.Current, orchestrator.Hypothesis);
        CaptionPlaceholder.Visibility =
            _controller.Paragraphs.Count == 0 && _controller.Current.Length == 0 &&
            orchestrator.Hypothesis.Length == 0 && phase is SessionPhase.Ready or SessionPhase.Recording
                ? Visibility.Visible
                : Visibility.Collapsed;

        StartButton.IsEnabled = phase is SessionPhase.Ready or SessionPhase.Saved or SessionPhase.Failed &&
                                orchestrator.ModelReady && !_controller.HasUnsavedSession;
        PauseButton.IsEnabled = phase is SessionPhase.Recording or SessionPhase.Paused;
        PauseButton.Content = phase == SessionPhase.Paused ? "Resume" : "Pause";
        PauseButton.Tag = phase == SessionPhase.Paused ? "\uE768" : "\uE769";
        StopButton.IsEnabled = phase is SessionPhase.Recording or SessionPhase.Paused ||
                               (phase == SessionPhase.Failed && _controller.HasUnsavedSession);
        CopyButton.IsEnabled = _controller.HasTranscript;
        CopyAllButton.IsEnabled = _controller.HasTranscript;
        QuestionButton.IsEnabled = SendButton.IsEnabled = _controller.HasTranscript || orchestrator.Hypothesis.Length > 0;
        BookmarkButton.IsEnabled = phase is SessionPhase.Recording or SessionPhase.Paused;

        // The engine cannot be swapped while it is loading, draining or saving; the pickers
        // say so by greying out rather than by accepting a choice and ignoring it.
        var settled = phase is not (SessionPhase.Preparing or SessionPhase.Pausing or SessionPhase.Saving);
        InterimBox.IsEnabled = FinalBox.IsEnabled = BackendBox.IsEnabled = settled;
        SourceBox.IsEnabled = SensitivityBox.IsEnabled = settled;

        ShowPhase(phase);
        ShowSpeed(orchestrator);

        // §7.1's states, in the order they matter. A save failure outranks everything — it is
        // the only one where the user still has to do something to keep the transcript.
        // "Catching up" is last because it is transient and self-clearing, but it has to be
        // shown at all: it is the pipeline saying the decode queue is losing ground, which is
        // the warning before an automatic pause.
        var message = _controller.SaveError
                      ?? orchestrator.ErrorText
                      ?? (orchestrator.IsDownloading || !orchestrator.ModelReady ? orchestrator.Status : null)
                      ?? (orchestrator.Detail.Length > 0 ? orchestrator.Detail : null);

        StatusText.Text = message ?? "";
        StatusStrip.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;

        var downloading = orchestrator.IsDownloading && _controller.SaveError is null && orchestrator.ErrorText is null;
        DownloadTrack.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadFill.Width = downloading
            ? Math.Max(0, DownloadTrack.ActualWidth * Math.Clamp(orchestrator.DownloadFraction, 0, 1))
            : 0;

        // A save failure is not a transient notice — it stays until the session is saved.
        var failed = _controller.SaveError is not null;
        StatusStrip.SetResourceReference(Border.BackgroundProperty, failed ? "Notice.Error.Bg" : "Notice.Warn.Bg");
        StatusStrip.SetResourceReference(Border.BorderBrushProperty, failed ? "Notice.Error.Line" : "Notice.Warn.Line");
        StatusIcon.SetResourceReference(TextBlock.ForegroundProperty, failed ? "Danger" : "Caution");
        StatusIcon.Text = failed ? "\uE783" : downloading ? "\uE896" : "\uE7BA";
        if (downloading) StatusIcon.SetResourceReference(TextBlock.ForegroundProperty, "Live");
        DownloadFill.SetResourceReference(Border.BackgroundProperty, "Live");
    }

    /// <summary>
    /// The dot beside the phase: red and breathing while recording, amber while paused.
    /// </summary>
    /// <remarks>
    /// The animation runs only while recording and at a low frame rate. A permanently
    /// running storyboard keeps WPF's render clock ticking for the life of the window, which
    /// is a poor trade for a dot nobody is looking at.
    /// </remarks>
    private void ShowPhase(SessionPhase phase)
    {
        if (_shownPhase == phase) return;
        _shownPhase = phase;

        PhaseDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, phase switch
        {
            SessionPhase.Recording => "Danger",
            SessionPhase.Paused or SessionPhase.Pausing => "Caution",
            SessionPhase.Ready or SessionPhase.Saved => "Ok",
            SessionPhase.Failed => "Danger",
            _ => "Live",                     // preparing, saving: something is happening
        });

        if (phase == SessionPhase.Recording)
        {
            var breathe = new DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(0.9))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(breathe, 20);
            PhaseDot.BeginAnimation(OpacityProperty, breathe);
        }
        else
        {
            PhaseDot.BeginAnimation(OpacityProperty, null);
            PhaseDot.Opacity = 1;
        }
    }

    /// <summary>The status bar's readout: how long each model takes, and how far behind it is.</summary>
    private void ShowSpeed(StreamingOrchestrator orchestrator)
    {
        var speed = orchestrator.Speed;

        SpeedLive.Text = speed.InterimMs > 0 ? $"{speed.InterimMs} ms" : "—";
        SpeedFinal.Text = speed.FinalMs > 0 ? $"{speed.FinalMs} ms" : "—";
        SpeedLag.Text = speed.Decodes > 0 ? $"{speed.LagMs / 1000.0:0.0} s" : "—";
        SpeedFactor.Text = speed.RealtimeFactor > 0 ? $"{speed.RealtimeFactor:0.0}× realtime" : "—";

        // The interim lane gives up at 2 s (its budget), and captions more than 3 s behind
        // are discarded unseen — so these are the thresholds that mean something.
        // Blue while all is well — these are live measurements, and blue is what live looks
        // like here — giving way to amber and red when a number needs looking at.
        var idle = speed.Decodes == 0 ? "Text.Faint" : "Live";
        Tone(SpeedLive, speed.InterimMs > 1800 ? "Danger" : speed.InterimMs > 1000 ? "Caution" : idle);
        Tone(SpeedFinal, idle);
        Tone(SpeedLag, speed.LagMs > 3000 ? "Danger" : speed.LagMs > 1500 ? "Caution" : idle);
        Tone(SpeedFactor, speed.RealtimeFactor is > 0 and < 1.5 ? "Danger" : idle);

        var boost = orchestrator.GainDb;
        var boosting = boost >= 2.5;
        GainName.Visibility = GainValue.Visibility = boosting ? Visibility.Visible : Visibility.Collapsed;
        if (boosting)
        {
            GainValue.Text = $"+{boost:0} dB";
            GainValue.ToolTip = "The audio is arriving quietly — usually because the system volume is low — " +
                                "and is being brought up so speech is still detected.";
            Tone(GainValue, boost >= 27 ? "Caution" : "Live");      // near the cap: it may not be enough
        }

        if (orchestrator.EngineInfo is { } engine)
        {
            EngineText.Text = $"{engine.Library.ToUpperInvariant()} · {orchestrator.ModelLabel}";
            EngineText.ToolTip = engine.ToString();
            Tone(EngineText, engine.FellBack ? "Caution" : "Text.Muted");
        }
        else
        {
            EngineText.Text = "";
        }

        static void Tone(TextBlock text, string key) => text.SetResourceReference(TextBlock.ForegroundProperty, key);
    }

    private static string Describe(SessionPhase phase) => phase switch
    {
        SessionPhase.Preparing => "Loading speech models…",
        SessionPhase.Ready => "Ready",
        SessionPhase.Recording => "Recording",
        SessionPhase.Pausing => "Finishing speech…",
        SessionPhase.Paused => "Paused",
        SessionPhase.Saving => "Saving…",
        SessionPhase.Saved => "Saved",
        _ => "Failed",
    };

    private DispatcherTimer? _flashTimer;

    /// <summary>A few words in the status bar, for a moment: what just happened.</summary>
    private void Flash(string text, bool ok = true)
    {
        CopiedFlag.Text = text;
        CopiedFlag.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Ok" : "Caution");
        CopiedFlag.Visibility = Visibility.Visible;

        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ok ? 1.6 : 4) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer?.Stop();
            CopiedFlag.Visibility = Visibility.Collapsed;
        };
        _flashTimer.Start();
    }

    private void Report(SendResult result) =>
        Dispatcher.BeginInvoke(() => Flash(result.Ok ? "✓ " + result.Message.ToUpperInvariant() : result.Message, result.Ok));

    // ── question, send, bookmark, click-through ──────────────────────────────────────────

    private void OnCopyQuestion(object sender, RoutedEventArgs e)
    {
        if (!_controller.CopyLastQuestion()) Flash("Nothing said yet", ok: false);
    }

    private void OnSendQuestion(object sender, RoutedEventArgs e)
    {
        var question = _controller.LastTurn();

        // Pasting into another window takes the foreground and sleeps between keystrokes;
        // neither belongs on the UI thread.
        _ = Task.Run(() => Report(_sender.Send(question)));
    }

    private async void OnBookmark(object sender, RoutedEventArgs e)
    {
        if (await _controller.BookmarkAsync()) Flash("★ BOOKMARKED");
    }

    /// <summary>Follows the button's state, so the mouse, the keyboard and automation all work it.</summary>
    private void OnClickThroughChecked(object sender, RoutedEventArgs e) =>
        _clickThrough?.Set(ClickThroughButton.IsChecked == true);

    // ── keyboard (§7.5) ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Carry out a shortcut. Returns false when it could not run, so the key falls through.
    /// </summary>
    /// <remarks>
    /// The keys themselves live in config and are matched by <see cref="ShortcutManager"/> —
    /// this is only what each one does. A global shortcut arrives here while another app has
    /// focus, so nothing in it may assume this window is in front.
    /// </remarks>
    private bool Run(ShortcutAction action)
    {
        var nothing = new RoutedEventArgs();
        switch (action)
        {
            case ShortcutAction.Start:
                if (!StartButton.IsEnabled) return false;
                OnStart(this, nothing);
                return true;

            case ShortcutAction.PauseResume:
                if (!PauseButton.IsEnabled) return false;
                OnPauseResume(this, nothing);
                return true;

            case ShortcutAction.Stop:
                if (!StopButton.IsEnabled) return false;
                OnStop(this, nothing);
                return true;

            case ShortcutAction.ToggleRecording:
                if (StopButton.IsEnabled) OnStop(this, nothing);
                else if (StartButton.IsEnabled) OnStart(this, nothing);
                else return false;
                return true;

            case ShortcutAction.CopyLastN:
                // §7.5: with a selection, Ctrl+C is an ordinary copy of it.
                if (IsActive && Captions.SelectionLength > 0) Captions.Copy();
                else OnCopy(this, nothing);
                return true;

            case ShortcutAction.CopyLastQuestion: OnCopyQuestion(this, nothing); return true;
            case ShortcutAction.SendLastQuestion: OnSendQuestion(this, nothing); return true;
            case ShortcutAction.Bookmark: OnBookmark(this, nothing); return true;
            case ShortcutAction.ToggleClickThrough: _clickThrough.Toggle(); return true;
            case ShortcutAction.CopyAll: OnCopyAll(this, nothing); return true;
            case ShortcutAction.JumpLatest: Captions.JumpToLatest(); return true;
            case ShortcutAction.FontBigger: StepFont(+1); return true;
            case ShortcutAction.FontSmaller: StepFont(-1); return true;
            case ShortcutAction.ToggleSidebar: OnToggleSidebar(this, nothing); return true;
            case ShortcutAction.ToggleTheme: OnToggleTheme(this, nothing); return true;
            case ShortcutAction.TogglePin: OnTogglePin(this, nothing); return true;
            case ShortcutAction.Settings: OnSettings(this, nothing); return true;

            case ShortcutAction.FocusSearch:
                // The box may be hidden with the sidebar; a search key should still find it.
                if (!SidebarShown) OnToggleSidebar(this, nothing);
                SearchBox.Focus();
                SearchBox.SelectAll();
                return true;

            default: return false;
        }
    }

    // ── shutdown ─────────────────────────────────────────────────────────────────────────

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // An unsaved session is an interview in progress. Closing on it would leave a
        // journal to recover from rather than a transcript, so ask first (§8).
        if (_controller.HasUnsavedSession)
        {
            _clickThrough.Set(false);      // a dialog nobody can click is no dialog
            var answer = ConfirmDialog.AskToClose(this, _controller.DisplayName, _controller.Elapsed);

            if (answer == ConfirmDialog.Closing.Cancel) { e.Cancel = true; return; }
            if (answer == ConfirmDialog.Closing.SaveAndClose)
            {
                e.Cancel = true;
                await _controller.StopAsync();
                Close();
                return;
            }
        }

        _meter.Stop();
        _saveSoon.Stop();
        _clickThrough.Set(false);
        _sender.Dispose();
        _shortcuts.Dispose();
        ThemeManager.Changed -= OnThemeChanged;
        WindowPlacement.Save(this, _env.Config);
        try { _env.Config.Write(); } catch (IOException) { /* read-only volume */ }
        await _controller.DisposeAsync();
    }
}
