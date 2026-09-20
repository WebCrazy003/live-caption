using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
/// </remarks>
public partial class MainWindow : Window
{
    private readonly AppEnvironment _env;
    private readonly SessionController _controller;
    private readonly DispatcherTimer _meter;
    private bool _refreshQueued;

    public MainWindow(AppEnvironment env)
    {
        InitializeComponent();

        _env = env;
        _controller = new SessionController(env);
        _controller.Changed += OnControllerChanged;
        _controller.Copied += () => Dispatcher.BeginInvoke(FlashCopied);

        // The clipboard belongs to the UI thread and needs the §7.4 retry, so the session
        // layer is handed a function rather than owning one.
        env.Clipboard = ClipboardWriter.Copy;

        Captions.FollowingChanged += following =>
            Dispatcher.BeginInvoke(() => JumpButton.Visibility = following ? Visibility.Collapsed : Visibility.Visible);

        // The level meter is not session state — it is a continuous reading, and binding it
        // to Changed would either miss frames or force a redraw per audio packet.
        _meter = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _meter.Tick += (_, _) => LevelBar.Width = 160 * Math.Clamp(_controller.Orchestrator.Level * 3, 0, 1);
        _meter.Start();

        WindowPlacement.Restore(this, env.Config);
        ApplySettings();
        FillSortOptions();
        RefreshSessions();
        InstallShortcuts();

        Loaded += async (_, _) => await _controller.PrepareAsync();
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

    private void OnJump(object sender, RoutedEventArgs e) => Captions.JumpToLatest();

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_env) { Owner = this };
        if (settings.ShowDialog() != true) return;
        ApplySettings();
    }

    /// <summary>§10: font size, auto-scroll and timestamps apply live; the rest at next Start.</summary>
    private void ApplySettings()
    {
        Captions.FontSize = _env.Config.Caption.FontSize;
        Captions.AutoScroll = _env.Config.Caption.AutoScroll;
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
    }

    private void OnSearchChanged(object sender, RoutedEventArgs e) => RefreshSessions();

    private void OnOpenTranscript(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OnOpenSelected(object sender, RoutedEventArgs e) => OpenSelected();

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (Selected()?.Record.TranscriptFile is not { Length: > 0 } path || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")); }
        catch (Exception) { /* explorer is missing or the path moved */ }
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

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row || row.Record.Id is not { } id) return;

        var answer = MessageBox.Show(this,
            $"Remove \"{row.SessionName}\" from the list?\n\nThe transcript file is left on disk.",
            "Local Caption", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        // Index row only. Deleting an interview transcript on one click of a context menu is
        // not a trade this app should offer — the file is the durable artefact (§7.4).
        _env.Store.Delete(id);
        RefreshSessions();
    }

    private SessionRow? Selected() => SessionList.SelectedItem as SessionRow;

    private void OpenSelected()
    {
        if (Selected()?.Record.TranscriptFile is not { Length: > 0 } path || !File.Exists(path)) return;

        // The read-only transcript viewer §7.1 asks for is the OS's — a text file in the
        // default editor beats a window that only shows text files.
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { /* no association, or the file moved */ }
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
        StopButton.IsEnabled = phase is SessionPhase.Recording or SessionPhase.Paused ||
                               (phase == SessionPhase.Failed && _controller.HasUnsavedSession);
        CopyButton.IsEnabled = _controller.HasTranscript;

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

        // A save failure is not a transient notice — it stays until the session is saved.
        StatusStrip.Background = _controller.SaveError is not null
            ? new SolidColorBrush(Color.FromRgb(0xFD, 0xE7, 0xE9))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xCE));
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

    private void FlashCopied()
    {
        CopiedFlag.Visibility = Visibility.Visible;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CopiedFlag.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }

    // ── keyboard (§7.5) ──────────────────────────────────────────────────────────────────

    private void InstallShortcuts()
    {
        Bind(Key.N, ModifierKeys.Control, () => { if (StartButton.IsEnabled) OnStart(this, new RoutedEventArgs()); });
        Bind(Key.OemComma, ModifierKeys.Control, () => OnSettings(this, new RoutedEventArgs()));
        Bind(Key.OemPeriod, ModifierKeys.Control, () => { if (StopButton.IsEnabled) OnStop(this, new RoutedEventArgs()); });
        Bind(Key.C, ModifierKeys.Control, () => { if (Captions.SelectionLength > 0) Captions.Copy(); else OnCopy(this, new RoutedEventArgs()); });
        Bind(Key.F, ModifierKeys.Control, () => SearchBox.Focus());

        // Space is pause/resume, but only when the caption view does not want it — the
        // search box does.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Space || SearchBox.IsKeyboardFocusWithin) return;
            if (!PauseButton.IsEnabled) return;
            OnPauseResume(this, new RoutedEventArgs());
            e.Handled = true;
        };
    }

    /// <summary>
    /// A window-level shortcut that survives the caption view having focus.
    /// </summary>
    /// <remarks>
    /// A <see cref="RoutedCommand"/> carrying an input gesture is not enough: AvalonEdit's
    /// text area handles keys first, so Ctrl+. never reached the window. An explicit
    /// <see cref="KeyBinding"/> in <see cref="UIElement.InputBindings"/> is matched before
    /// the focused control sees the key.
    /// </remarks>
    private void Bind(Key key, ModifierKeys modifiers, Action action)
    {
        var command = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(command, (_, e) => { action(); e.Handled = true; }));
        InputBindings.Add(new KeyBinding(command, key, modifiers));
    }

    // ── shutdown ─────────────────────────────────────────────────────────────────────────

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // An unsaved session is an interview in progress. Closing on it would leave a
        // journal to recover from rather than a transcript, so ask first (§8).
        if (_controller.HasUnsavedSession)
        {
            var answer = MessageBox.Show(this,
                "This session has not been saved. Stop and save it before closing?",
                "Local Caption", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (answer == MessageBoxResult.Yes)
            {
                e.Cancel = true;
                await _controller.StopAsync();
                Close();
                return;
            }
        }

        _meter.Stop();
        WindowPlacement.Save(this, _env.Config);
        try { _env.Config.Write(); } catch (IOException) { /* read-only volume */ }
        await _controller.DisposeAsync();
    }
}
