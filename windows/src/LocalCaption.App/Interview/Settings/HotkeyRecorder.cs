using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using LocalCaption.Core.Interview;

namespace LocalCaption.App.Interview.Settings;

/// <summary>
/// Settings → Asking → Hotkeys: record a shortcut, validate it with the shared grammar
/// (<see cref="Core.Interview.Hotkey.Parse"/>) and show whether Windows accepted it — the port of
/// Mac <c>HotkeyRecorder.swift</c>. One per action (Ask, Screenshot).
/// </summary>
/// <remarks>
/// <para>Record… listens for the next key-down while it has keyboard focus. Esc cancels; a
/// modifier on its own keeps listening; the key is read by virtual key (<see cref="Key"/>),
/// never by the character it types, so any keyboard layout records the same thing
/// (SPEC-16 compatibility target). Focus leaving the control cancels too.</para>
/// <para>Every key is swallowed while recording — Enter and Esc included, which would otherwise
/// reach the Settings window's Save and Cancel. The window's own shortcut recorder does the
/// same from its <c>PreviewKeyDown</c>, which runs first but only acts on its own rows.</para>
/// <para>The stored text stays canonical (<c>Ctrl+Alt+Shift+Cmd+Key</c>, the shared format);
/// it is <i>shown</i> with Win for Cmd.</para>
/// </remarks>
public sealed class HotkeyRecorder : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(HotkeyRecorder),
        new PropertyMetadata("", (d, _) => ((HotkeyRecorder)d).Render()));

    /// <summary>The config text, e.g. <c>F8</c> or <c>Ctrl+Shift+K</c>. Two-way.</summary>
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyRecorder),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((HotkeyRecorder)d).Render()));

    /// <summary>The default (<c>F8</c> for Ask, <c>F9</c> for Screenshot); also the fallback for invalid text.</summary>
    public static readonly DependencyProperty DefaultHotkeyProperty = DependencyProperty.Register(
        nameof(DefaultHotkey), typeof(string), typeof(HotkeyRecorder),
        new PropertyMetadata(Core.Interview.Hotkey.DefaultString, (d, _) => ((HotkeyRecorder)d).Render()));

    /// <summary>The other action's name ("Screenshot" / "Ask"): one combination can't do both.</summary>
    public static readonly DependencyProperty OtherNameProperty = DependencyProperty.Register(
        nameof(OtherName), typeof(string), typeof(HotkeyRecorder), new PropertyMetadata(null));

    /// <summary>The other action's hotkey text.</summary>
    public static readonly DependencyProperty OtherHotkeyProperty = DependencyProperty.Register(
        nameof(OtherHotkey), typeof(string), typeof(HotkeyRecorder), new PropertyMetadata(null));

    /// <summary>The other action's default, used when its text is invalid.</summary>
    public static readonly DependencyProperty OtherDefaultHotkeyProperty = DependencyProperty.Register(
        nameof(OtherDefaultHotkey), typeof(string), typeof(HotkeyRecorder),
        new PropertyMetadata(Core.Interview.Hotkey.DefaultScreenshotString));

    /// <summary>
    /// Why Windows would not register it (from the host's hotkey service, e.g. "another app is
    /// using F8"), or null. Shown as "Can't use it: …" when there is no recording error.
    /// </summary>
    public static readonly DependencyProperty RegistrationErrorProperty = DependencyProperty.Register(
        nameof(RegistrationError), typeof(string), typeof(HotkeyRecorder),
        new PropertyMetadata(null, (d, _) => ((HotkeyRecorder)d).Render()));

    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _keys = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _chip = new()
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(9, 4, 9, 4),
        MinWidth = 64,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Button _record = new() { MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _reset = new() { Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _error = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _hint = new() { Visibility = Visibility.Collapsed };
    private bool _recording;
    private string? _localError;

    public HotkeyRecorder()
    {
        Focusable = false;
        IsTabStop = false;

        _keys.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
        _chip.Child = _keys;
        _error.SetResourceReference(StyleProperty, "Type.Note");
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Caution");
        _hint.SetResourceReference(StyleProperty, "Type.Note");
        // Errors are announced as they appear.
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);

        _record.Click += (_, _) =>
        {
            if (_recording) Stop();
            else Start();
        };
        _reset.Click += (_, _) =>
        {
            Stop();
            _localError = null;
            SetCurrentValue(HotkeyProperty, Default.ToString());
            Render();
        };

        // Title above, the chip and its buttons on the next line: Settings can be as narrow
        // as ~330 px of content, too narrow for the Mac's single row.
        var controls = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        controls.Children.Add(_chip);
        controls.Children.Add(_record);
        controls.Children.Add(_reset);

        var panel = new StackPanel();
        panel.Children.Add(_title);
        panel.Children.Add(controls);
        panel.Children.Add(_error);
        panel.Children.Add(_hint);
        Content = panel;

        PreviewKeyDown += OnPreviewKeyDown;
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is false) Stop();
        };
        Unloaded += (_, _) => Stop();
        Render();
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Hotkey { get => (string)GetValue(HotkeyProperty); set => SetValue(HotkeyProperty, value); }
    public string DefaultHotkey { get => (string)GetValue(DefaultHotkeyProperty); set => SetValue(DefaultHotkeyProperty, value); }
    public string? OtherName { get => (string?)GetValue(OtherNameProperty); set => SetValue(OtherNameProperty, value); }
    public string? OtherHotkey { get => (string?)GetValue(OtherHotkeyProperty); set => SetValue(OtherHotkeyProperty, value); }
    public string? OtherDefaultHotkey { get => (string?)GetValue(OtherDefaultHotkeyProperty); set => SetValue(OtherDefaultHotkeyProperty, value); }
    public string? RegistrationError { get => (string?)GetValue(RegistrationErrorProperty); set => SetValue(RegistrationErrorProperty, value); }

    /// <summary>Recording is in progress (for tests and the host).</summary>
    public bool IsRecording => _recording;

    private Hotkey Default => Core.Interview.Hotkey.Parse(DefaultHotkey ?? "").Value ?? Core.Interview.Hotkey.Default;

    private Hotkey Resolved => Core.Interview.Hotkey.Resolve(Hotkey ?? "", Default);

    /// <summary>Canonical text shown the Windows way: <c>Cmd</c> is the Windows key.</summary>
    public static string Display(Hotkey hotkey) =>
        string.Join("+", hotkey.ToString().Split('+').Select(p => p == "Cmd" ? "Win" : p));

    private void Start()
    {
        _localError = null;
        _recording = true;
        Render();
        _record.Focus();
    }

    private void Stop()
    {
        if (!_recording) return;
        _recording = false;
        Render();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };

        // A modifier on its own: keep listening for the key that goes with it.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
            or Key.RightShift or Key.LWin or Key.RWin) return;

        var modifiers = Keyboard.Modifiers;
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            Stop();
            return;
        }

        if (KeyName(key) is not { } name)
        {
            _localError = "That key can't be used.";
            Stop();
            return;
        }

        var text = string.Join("+", ModifierNames(modifiers).Append(name));
        var parsed = Core.Interview.Hotkey.Parse(text);
        if (parsed.Value is { } hotkey)
        {
            var other = OtherHotkey is null
                ? null
                : Core.Interview.Hotkey.Resolve(OtherHotkey,
                    Core.Interview.Hotkey.Parse(OtherDefaultHotkey ?? "").Value ?? Core.Interview.Hotkey.DefaultScreenshot);
            if (other is not null && other == hotkey)
            {
                _localError = $"{Display(hotkey)} is already the {OtherName} hotkey.";
            }
            else
            {
                _localError = null;
                SetCurrentValue(HotkeyProperty, hotkey.ToString());
            }
        }
        else if (parsed.Error is { Kind: HotkeyParseErrorKind.NeedsModifier } error)
        {
            _localError = $"{error.Token} needs Ctrl, Alt or Win so it doesn't block typing.";
        }
        else
        {
            _localError = $"“{text.Replace("Cmd", "Win", StringComparison.Ordinal)}” isn't a valid shortcut.";
        }
        Stop();
    }

    /// <summary>Modifier names in the grammar's order; the Windows key is <c>Cmd</c>.</summary>
    private static IEnumerable<string> ModifierNames(ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Control)) yield return "Ctrl";
        if (modifiers.HasFlag(ModifierKeys.Alt)) yield return "Alt";
        if (modifiers.HasFlag(ModifierKeys.Shift)) yield return "Shift";
        if (modifiers.HasFlag(ModifierKeys.Windows)) yield return "Cmd";
    }

    /// <summary>
    /// The grammar's key name for a virtual key (Mac <c>HotkeyKeys</c>), or null for a key the
    /// grammar has no name for. Digits are the top row only: a global hotkey on <c>1</c> is
    /// registered as VK_1, which the numeric keypad does not send.
    /// </summary>
    public static string? KeyName(Key key)
    {
        if (key is >= Key.F1 and <= Key.F24) return "F" + (key - Key.F1 + 1);
        if (key is >= Key.A and <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        return key switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Home => "Home",
            Key.End => "End",
            _ => null,
        };
    }

    private void Render()
    {
        var resolved = Resolved;
        var title = Title ?? "";
        _title.Text = title;
        _keys.Text = _recording ? "Press a shortcut…" : Display(resolved);
        _chip.SetResourceReference(Border.BackgroundProperty, _recording ? "Accent.Soft" : "Bg.Raised");
        _chip.SetResourceReference(Border.BorderBrushProperty, _recording ? "Accent.Border" : "Line");
        _chip.BorderThickness = new Thickness(1);
        AutomationProperties.SetName(_chip, $"{title}: {Display(resolved)}");

        _record.Content = _recording ? "Cancel" : "Record…";
        AutomationProperties.SetName(_record, _recording ? $"Cancel recording the {title}" : $"Record the {title}");
        _record.SetResourceReference(StyleProperty, _recording ? "Button.Accent" : typeof(Button));

        var fallback = Default;
        _reset.Content = $"Reset to {Display(fallback)}";
        _reset.IsEnabled = resolved != fallback;

        var message = _localError ?? (RegistrationError is { Length: > 0 } reason
            ? $"Can't use it: {reason}. The on-screen Ask button still works."
            : null);
        _error.Text = message ?? "";
        _error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;

        // Laptops send media keys from the F-row unless Fn is held or Fn Lock is on
        // (SPEC-16 W-I2). The Mac's hint names its own keyboard setting; this is the PC one.
        var bare = resolved.IsFunctionKey && resolved.Modifiers == HotkeyModifiers.None;
        _hint.Text = bare
            ? "On many laptops hold Fn with the F-key, or turn on Fn Lock (often Fn+Esc) so the F-keys "
              + "act as standard function keys. Otherwise it's a media key."
            : "";
        _hint.Visibility = bare ? Visibility.Visible : Visibility.Collapsed;
    }
}
