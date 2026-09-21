using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LocalCaption.Asr;
using LocalCaption.Audio;
using LocalCaption.Core.Data;
using LocalCaption.Session;

namespace LocalCaption.App;

/// <summary>
/// Settings (§10), including the two pickers this machine made necessary: the §4.5 process
/// picker and the §4.6 output-endpoint picker.
/// </summary>
/// <remarks>
/// <para>Live-applying: font size, auto-scroll, timestamps, clipboard, theme, shortcuts.
/// Models load as soon as that is safe (see <c>MainWindow.OnSettings</c>); detection and
/// the source take effect at the next Start or Resume, because changing the VAD mid-utterance
/// would mean rebuilding the decode lanes underneath a running session.</para>
/// <para>This window is still Save / Cancel, on purpose. The toolbar in the main window is
/// where things change instantly mid-call; this is where someone sits down and considers
/// them, and being able to back out of that is worth keeping.</para>
/// <para>The <c>summary</c>, <c>always_on_top</c> and <c>opacity</c> keys are deliberately
/// absent from this window but kept in the schema, so a <c>config.json</c> still round-trips
/// with the macOS app (§7.3, §9.2).</para>
/// </remarks>
public partial class SettingsWindow : ChromeWindow
{
    /// <summary>One entry in a picker: what to show, and what to persist.</summary>
    private sealed record Choice(string Label, string? Value);

    /// <summary>One page of the window, as the list on the left shows it.</summary>
    private sealed record PageItem(string Label, string Glyph, FrameworkElement Panel);

    /// <summary>A shortcut being edited: the working copy, and the controls showing it.</summary>
    private sealed class ShortcutRow(ShortcutSlot slot, Button recorder, CheckBox global)
    {
        public ShortcutSlot Slot { get; } = slot;
        public Button Recorder { get; } = recorder;
        public CheckBox Global { get; } = global;
        public string Keys { get; set; } = "";
    }

    private readonly AppEnvironment _env;
    private readonly SessionController? _controller;
    private readonly ShortcutManager? _shortcuts;
    private readonly AnswerSender? _sender;
    private IntPtr _pickedWindow;
    private readonly List<ShortcutRow> _rows = [];
    private readonly string _themeBefore;
    private ShortcutRow? _recording;
    private IReadOnlyList<SystemInfo.Section> _system = [];
    private bool _loading = true;

    public SettingsWindow(AppEnvironment env, SessionController? controller = null,
                          ShortcutManager? shortcuts = null, AnswerSender? sender = null)
    {
        InitializeComponent();
        _env = env;
        _controller = controller;
        _shortcuts = shortcuts;
        _sender = sender;
        _themeBefore = ThemeManager.Preference;

        Load();
        BuildShortcuts();
        _loading = false;

        PreviewKeyDown += OnRecordKey;
        Closed += (_, _) =>
        {
            // The theme previews live. Anything but Save puts back what was there.
            if (DialogResult != true) ThemeManager.Apply(_themeBefore);
        };

        var pages = new[]
        {
            new PageItem("Audio", "\uE767", PageAudio),
            new PageItem("Speech", "\uE720", PageSpeech),
            new PageItem("Captions", "\uE8D2", PageCaptions),
            new PageItem("Send", "\uE724", PageSend),
            new PageItem("Appearance", "\uE790", PageAppearance),
            new PageItem("Shortcuts", "\uE765", PageShortcuts),
            new PageItem("System", "\uE946", PageSystem),
        };
        Pages.ItemsSource = pages;
        Pages.SelectedIndex = 0;
    }

    private void OnPageChanged(object sender, SelectionChangedEventArgs e)
    {
        CancelRecording();
        foreach (var page in Pages.Items.OfType<PageItem>())
            page.Panel.Visibility = ReferenceEquals(page, Pages.SelectedItem) ? Visibility.Visible : Visibility.Collapsed;

        // Read only when someone looks: it is cheap, but it is also a snapshot, and one
        // taken when the window opened would show the speed of a session not yet started.
        if ((Pages.SelectedItem as PageItem)?.Panel == PageSystem) ShowSystem();
    }

    private void Load()
    {
        var config = _env.Config;

        // §4.5: remember the executable, never the PID — it is meaningless once the user
        // restarts Teams, and it is re-resolved at every Start.
        var processes = new List<Choice> { new("(choose an application)", null) };
        processes.AddRange(AudioSessions.List()
            .Select(s => new Choice(s.Active ? $"{s.Name} — playing now" : s.Name, s.Executable)));
        if (config.Audio.TargetProcess is { Length: > 0 } target &&
            !processes.Any(p => string.Equals(p.Value, target, StringComparison.OrdinalIgnoreCase)))
            processes.Add(new Choice($"{target} (not running)", target));
        ProcessPicker.ItemsSource = processes;
        ProcessPicker.SelectedItem = processes.FirstOrDefault(
            p => string.Equals(p.Value, config.Audio.TargetProcess, StringComparison.OrdinalIgnoreCase))
            ?? processes[0];

        // §4.6: "Follow system default" is the default, because the default endpoint moves
        // when a monitor, headphones or a remote session arrives.
        var endpoints = new List<Choice> { new("Follow system default", null) };
        endpoints.AddRange(AudioChoices.Endpoints().Select(e => new Choice(e.Label, e.Id)));
        EndpointPicker.ItemsSource = endpoints;
        EndpointPicker.SelectedItem = endpoints.FirstOrDefault(e => e.Value == config.Audio.OutputDevice)
                                      ?? endpoints[0];

        AutoGainBox.IsChecked = config.Audio.AutoGain;

        var process = config.Audio.CaptureMode.Equals("process", StringComparison.OrdinalIgnoreCase);
        // "auto" is chosen from the toolbar and has no radio button here. Leaving both clear
        // is what lets Save tell "never touched" from "changed", and keep auto when untouched.
        var auto = config.Audio.CaptureMode.Equals("auto", StringComparison.OrdinalIgnoreCase);
        ModeProcess.IsChecked = process && !auto;
        ModeEndpoint.IsChecked = !process && !auto;

        var models = ModelCatalog.All.Select(m => m.Name).ToList();
        InterimModel.ItemsSource = models;
        InterimModel.SelectedItem = models.Contains(config.Asr.InterimModel) ? config.Asr.InterimModel : models[0];
        FinalModel.ItemsSource = models;
        FinalModel.SelectedItem = models.Contains(config.Asr.FinalModel) ? config.Asr.FinalModel : models[^1];

        var care = new[] { new Choice("Standard — fastest", "0"), new Choice("Careful — beam search, slower", "5") };
        FinalCare.ItemsSource = care;
        FinalCare.SelectedItem = config.Asr.FinalBeamSize > 1 ? care[1] : care[0];

        Backend.ItemsSource = new[] { "auto", "cuda", "cpu" };
        Backend.SelectedItem = config.Asr.Backend;
        BackendNote.Text = BackendProbe.HasCudaRuntime()
            ? "A CUDA runtime was found. 'auto' will use the GPU."
            : "No CUDA runtime was found, so 'auto' will run on the CPU — much slower. " +
              "See BENCH-RESULTS.md for what that costs.";

        FontSizeSlider.Value = config.Caption.FontSize;
        AutoScroll.IsChecked = config.Caption.AutoScroll;
        ShowTimestamps.IsChecked = config.Caption.ShowTimestamps;

        AutoUpdate.IsChecked = config.Clipboard.AutoUpdate;
        RecentSentences.Text = config.Clipboard.RecentSentences.ToString(CultureInfo.InvariantCulture);

        VadSensitivity.Value = config.Audio.VadSensitivity;
        EndpointSilence.Text = config.Asr.EndpointSilenceMs.ToString(CultureInfo.InvariantCulture);
        MaxUtterance.Text = config.Asr.MaxUtteranceS.ToString(CultureInfo.InvariantCulture);

        TranscriptFolder.Text = config.General.TranscriptFolder;
        Vocabulary.Text = config.Asr.Vocabulary;

        var send = config.Send;
        (send.Target switch { "window" => SendWindow, "address" => SendAddress, "file" => SendFile, _ => SendOff }).IsChecked = true;
        WindowTitle.Text = send.WindowTitle;
        SendPort.Text = send.Port.ToString(CultureInfo.InvariantCulture);
        SendFilePath.Text = send.File;
        SendSubmit.IsChecked = send.Submit;
        SendAuto.IsChecked = send.Auto;
        TurnGap.Text = send.TurnGapMs.ToString(CultureInfo.InvariantCulture);
        SendTemplate.Text = send.Template;
        ShowSendOptions();
        VersionText.Text = "v" + SystemInfo.Version();
        ShowShortcutState(null);

        switch (ThemeManager.Normalise(config.Ui.Theme))
        {
            case "dark": ThemeDark.IsChecked = true; break;
            case "light": ThemeLight.IsChecked = true; break;
            default: ThemeSystem.IsChecked = true; break;
        }
        PinOnTop.IsChecked = config.Ui.PinOnTop;
        SidebarShown.IsChecked = !config.Ui.SidebarCollapsed;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var config = _env.Config;

        config.Audio.CaptureMode = ModeProcess.IsChecked == true ? "process"
                                 : ModeEndpoint.IsChecked == true ? "endpoint"
                                 : config.Audio.CaptureMode;
        config.Audio.TargetProcess = (ProcessPicker.SelectedItem as Choice)?.Value;
        config.Audio.OutputDevice = (EndpointPicker.SelectedItem as Choice)?.Value;
        config.Audio.VadSensitivity = (int)VadSensitivity.Value;
        config.Audio.AutoGain = AutoGainBox.IsChecked == true;

        config.Asr.InterimModel = InterimModel.SelectedItem as string ?? config.Asr.InterimModel;
        config.Asr.FinalModel = FinalModel.SelectedItem as string ?? config.Asr.FinalModel;
        config.Asr.Backend = Backend.SelectedItem as string ?? "auto";
        config.Asr.FinalBeamSize = (FinalCare.SelectedItem as Choice)?.Value == "5" ? 5 : 0;
        config.Asr.EndpointSilenceMs = Number(EndpointSilence.Text, config.Asr.EndpointSilenceMs);
        config.Asr.MaxUtteranceS = Number(MaxUtterance.Text, config.Asr.MaxUtteranceS);
        config.Asr.Vocabulary = string.Join(", ", Vocabulary.Text
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        config.Caption.FontSize = (int)FontSizeSlider.Value;
        config.Caption.AutoScroll = AutoScroll.IsChecked == true;
        config.Caption.ShowTimestamps = ShowTimestamps.IsChecked == true;

        config.Clipboard.AutoUpdate = AutoUpdate.IsChecked == true;
        config.Clipboard.RecentSentences = Math.Clamp(Number(RecentSentences.Text, config.Clipboard.RecentSentences), 1, 50);

        if (TranscriptFolder.Text is { Length: > 0 } folder) config.General.TranscriptFolder = folder;

        ReadSend(config.Send);
        if (_pickedWindow != IntPtr.Zero) _sender?.Pick(_pickedWindow);

        config.Ui.Theme = PickedTheme();
        config.Ui.PinOnTop = PinOnTop.IsChecked == true;
        config.Ui.SidebarCollapsed = SidebarShown.IsChecked != true;

        foreach (var row in _rows)
            row.Slot.Set(config.Shortcuts, new Config.Shortcut(row.Keys, row.Global.IsChecked == true));

        _env.Update(config);
        DialogResult = true;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(TranscriptFolder.Text);
            Process.Start(new ProcessStartInfo(TranscriptFolder.Text) { UseShellExecute = true });
        }
        catch (Exception) { /* an unopenable folder is not worth an error dialog */ }
    }

    private static int Number(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    // ── appearance ───────────────────────────────────────────────────────────────────────

    private string PickedTheme() =>
        ThemeDark.IsChecked == true ? "dark" : ThemeLight.IsChecked == true ? "light" : "system";

    private void OnThemePicked(object sender, RoutedEventArgs e)
    {
        if (!_loading) ThemeManager.Apply(PickedTheme());
    }

    // ── send ─────────────────────────────────────────────────────────────────────────────

    private string PickedTarget() =>
        SendWindow.IsChecked == true ? "window" : SendAddress.IsChecked == true ? "address" : SendFile.IsChecked == true ? "file" : "off";

    private void ReadSend(Config.SendGroup send)
    {
        send.Target = PickedTarget();
        send.WindowTitle = WindowTitle.Text.Trim();
        send.Port = Math.Clamp(Number(SendPort.Text, send.Port), 1024, 65535);
        if (SendFilePath.Text.Trim() is { Length: > 0 } file) send.File = file;
        send.Submit = SendSubmit.IsChecked == true;
        send.Auto = SendAuto.IsChecked == true && send.Target is "address" or "file";
        send.TurnGapMs = Math.Clamp(Number(TurnGap.Text, send.TurnGapMs), 800, 15000);
        send.Template = SendTemplate.Text.Contains("{text}", StringComparison.Ordinal) ? SendTemplate.Text : "{text}";
    }

    private void OnSendTargetPicked(object sender, RoutedEventArgs e)
    {
        if (!_loading) ShowSendOptions();
    }

    private void ShowSendOptions()
    {
        var target = PickedTarget();
        SendWindowOptions.Visibility = target == "window" ? Visibility.Visible : Visibility.Collapsed;
        SendAddressOptions.Visibility = target == "address" ? Visibility.Visible : Visibility.Collapsed;
        SendFileOptions.Visibility = target == "file" ? Visibility.Visible : Visibility.Collapsed;

        SendAuto.IsEnabled = target is "address" or "file";
        SendAutoNote.Visibility = target == "window" ? Visibility.Visible : Visibility.Collapsed;
        SendSubmit.IsEnabled = target is "window" or "address";
        SendTestButton.IsEnabled = target is "window" or "file";
        SendTestState.Text = target == "address" ? "Save, then press your Send key — the extension picks it up." : "";

        AddressNote.Text =
            $"Listens on http://127.0.0.1:{SendPort.Text.Trim()} — this PC only, and closed to web pages. " +
            "To install the extension: open chrome://extensions (or edge://extensions), turn on Developer mode, " +
            "choose “Load unpacked”, and pick the extension folder. It works on ChatGPT, DeepSeek, Claude, Gemini and Copilot tabs. " +
            "Your own tool: GET /next?since=N waits for new questions; /latest returns the newest.";
    }

    private void OnWindowsOpening(object? sender, EventArgs e)
    {
        WindowPicker.ItemsSource = AnswerSender.OpenWindows()
            .OrderByDescending(w => w.Process is "chrome" or "msedge" or "firefox" or "brave" or "opera")
            .ThenBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void OnWindowPicked(object sender, SelectionChangedEventArgs e)
    {
        if (WindowPicker.SelectedItem is not WindowChoice choice) return;
        _pickedWindow = choice.Handle;

        // Browsers append " - Google Chrome" and the like; the page's own title is the part
        // that will still match tomorrow.
        var title = choice.Title;
        foreach (var suffix in new[] { " - Google Chrome", " — Mozilla Firefox", " - Mozilla Firefox", " - Brave", " - Opera" })
            if (title.EndsWith(suffix, StringComparison.Ordinal)) title = title[..^suffix.Length];
        var edge = title.IndexOf(" - Microsoft​ Edge", StringComparison.Ordinal);
        if (edge < 0) edge = title.IndexOf(" - Microsoft Edge", StringComparison.Ordinal);
        if (edge > 0) title = title[..edge];
        WindowTitle.Text = title.Trim();
    }

    private void OnOpenExtension(object sender, RoutedEventArgs e)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "extension");
        try { Process.Start(new ProcessStartInfo(Directory.Exists(folder) ? folder : AppContext.BaseDirectory) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    /// <summary>Try the settings as they are on screen, saved or not.</summary>
    private async void OnSendTest(object sender, RoutedEventArgs e)
    {
        var trial = new Config();
        ReadSend(trial.Send);
        trial.Send.Submit = false;      // a test should never post "this is a test" into a real chat

        using var tester = new AnswerSender(() => trial);
        if (_pickedWindow != IntPtr.Zero) tester.Pick(_pickedWindow);

        SendTestState.Text = "Sending…";
        var result = await Task.Run(() => tester.Send("Local Caption test — if you can read this where your answers come from, it works."));
        SendTestState.Text = result.Ok ? "Done — look in the target. (Enter is never pressed by a test.)" : result.Message;
        SendTestState.SetResourceReference(TextBlock.ForegroundProperty, result.Ok ? "Ok" : "Caution");
        Activate();
    }

    // ── desktop shortcut ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Always clickable, and always safe to click: it only ever adds the icon when it is
    /// missing, and says which of the two happened.
    /// </summary>
    private void OnCreateShortcut(object sender, RoutedEventArgs e)
    {
        if (DesktopShortcut.Exists)
        {
            ShowShortcutState("Already on your desktop — nothing to do.");
            return;
        }

        var problem = DesktopShortcut.EnsureExists();
        ShowShortcutState(problem is null ? "Added to your desktop." : $"Could not create it: {problem}", problem is not null);
    }

    private void ShowShortcutState(string? text, bool failed = false)
    {
        ShortcutState.Text = text ?? (DesktopShortcut.Exists ? "There is one on your desktop." : "There is none on your desktop yet.");
        ShortcutState.SetResourceReference(TextBlock.ForegroundProperty,
            failed ? "Danger" : text is null ? "Text.Muted" : "Ok");
    }

    // ── shortcuts ────────────────────────────────────────────────────────────────────────

    private void BuildShortcuts()
    {
        var index = 0;
        foreach (var slot in ShortcutSlot.All)
        {
            ShortcutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var saved = slot.Get(_env.Config.Shortcuts);

            var label = new TextBlock { Text = slot.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            if (slot.Hint.Length > 0) label.ToolTip = slot.Hint;

            var recorder = new Button { Margin = new Thickness(0, 3, 0, 3), HorizontalContentAlignment = HorizontalAlignment.Center };
            recorder.SetResourceReference(FontFamilyProperty, "Font.Mono");

            var global = new CheckBox { Content = "Global", Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsChecked = saved.Global };

            var clear = new Button { Tag = "\uE711", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Remove this shortcut", Focusable = false };
            clear.SetResourceReference(StyleProperty, "Button.Ghost");

            var row = new ShortcutRow(slot, recorder, global) { Keys = Gesture.Parse(saved.Keys)?.ToString() ?? "" };
            _rows.Add(row);

            recorder.Click += (_, _) => StartRecording(row);
            recorder.LostKeyboardFocus += (_, _) => { if (_recording == row) CancelRecording(); };
            clear.Click += (_, _) => { CancelRecording(); row.Keys = ""; Show(row); };

            foreach (var (element, column) in new (UIElement, int)[] { (label, 0), (recorder, 1), (global, 2), (clear, 3) })
            {
                Grid.SetRow(element, index);
                Grid.SetColumn(element, column);
                ShortcutGrid.Children.Add(element);
            }

            Show(row);
            index++;
        }

        ShowShortcutNote(_shortcuts?.Failures.Values.FirstOrDefault());
    }

    /// <summary>Paint a row from its working copy.</summary>
    private void Show(ShortcutRow row)
    {
        var gesture = Gesture.Parse(row.Keys);
        var recording = _recording == row;

        row.Recorder.Content = recording ? "Press keys…" : gesture?.ToString() ?? "—";
        row.Recorder.SetResourceReference(StyleProperty, recording ? "Button.Accent" : typeof(Button));

        // Windows will not register a bare key system-wide, and should not: a global Space
        // would swallow every space typed into every other app.
        var canBeGlobal = gesture?.CanBeGlobal == true;
        row.Global.IsEnabled = canBeGlobal;
        if (!canBeGlobal) row.Global.IsChecked = false;
        row.Global.ToolTip = canBeGlobal
            ? "Works while another app has focus"
            : "A global shortcut needs Ctrl, Alt or Win in it";
    }

    private void StartRecording(ShortcutRow row)
    {
        CancelRecording();
        _recording = row;
        Show(row);
        row.Recorder.Focus();
    }

    private void CancelRecording()
    {
        if (_recording is not { } row) return;
        _recording = null;
        Show(row);
    }

    /// <summary>
    /// While a row is recording, every key belongs to it — including Esc and Enter, which
    /// would otherwise reach Cancel and Save.
    /// </summary>
    private void OnRecordKey(object sender, KeyEventArgs e)
    {
        if (_recording is not { } row) return;
        e.Handled = true;

        if (Gesture.From(e) is not { } gesture) return;      // modifiers only, so far

        if (gesture is { Modifiers: ModifierKeys.None, Key: Key.Escape }) { CancelRecording(); return; }
        if (gesture is { Modifiers: ModifierKeys.None, Key: Key.Back or Key.Delete })
        {
            row.Keys = "";
            CancelRecording();
            return;
        }

        // One key, one action. Taking a combination that is already in use moves it, the
        // way every editor does it — and says so, because the other row just went blank.
        var text = gesture.ToString();
        var previous = _rows.FirstOrDefault(r => r != row && r.Keys == text);
        if (previous is not null)
        {
            previous.Keys = "";
            Show(previous);
        }
        ShowShortcutNote(previous is null ? null : $"{text} was moved here from “{previous.Slot.Label}”, which now has no shortcut.");

        row.Keys = text;
        CancelRecording();
    }

    private void ShowShortcutNote(string? text)
    {
        ShortcutNote.Text = text ?? "";
        ShortcutNote.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnResetShortcuts(object sender, RoutedEventArgs e)
    {
        CancelRecording();
        var defaults = new Config.ShortcutsGroup();
        foreach (var row in _rows)
        {
            var standard = row.Slot.Get(defaults);
            row.Keys = Gesture.Parse(standard.Keys)?.ToString() ?? "";
            row.Global.IsChecked = standard.Global;
            Show(row);
        }
        ShowShortcutNote(null);
    }

    // ── system ───────────────────────────────────────────────────────────────────────────

    private void ShowSystem()
    {
        _system = SystemInfo.Collect(_env, _controller?.Orchestrator);
        SystemRows.Children.Clear();

        foreach (var section in _system)
        {
            var heading = new TextBlock { Text = section.Title.ToUpperInvariant(), Margin = new Thickness(0, 22, 0, 8) };
            heading.SetResourceReference(StyleProperty, "Type.Caps");
            SystemRows.Children.Add(heading);

            foreach (var item in section.Rows)
            {
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());

                var name = new TextBlock { Text = item.Label, FontSize = 12.5 };
                name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");

                var value = new TextBlock { Text = item.Value, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                value.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
                value.SetResourceReference(TextBlock.ForegroundProperty, item.Flag ? "Caution" : "Text");
                Grid.SetColumn(value, 1);

                grid.Children.Add(name);
                grid.Children.Add(value);
                SystemRows.Children.Add(grid);
            }
        }
    }

    private void OnRefreshSystem(object sender, RoutedEventArgs e) => ShowSystem();

    private void OnCopySystem(object sender, RoutedEventArgs e)
    {
        if (!ClipboardWriter.Copy(SystemInfo.AsText(_system))) return;

        CopySystemButton.Content = "Copied";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        timer.Tick += (_, _) => { timer.Stop(); CopySystemButton.Content = "Copy"; };
        timer.Start();
    }
}
