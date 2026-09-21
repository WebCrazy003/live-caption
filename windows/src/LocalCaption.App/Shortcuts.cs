using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using LocalCaption.Core.Data;

namespace LocalCaption.App;

/// <summary>Everything a shortcut can do. The order here is the order Settings lists them in.</summary>
public enum ShortcutAction
{
    Start, PauseResume, Stop, ToggleRecording,
    CopyLastQuestion, SendLastQuestion, CopyLastN, CopyAll, Bookmark, JumpLatest,
    ToggleClickThrough,
    FontBigger, FontSmaller,
    ToggleSidebar, ToggleTheme, TogglePin,
    FocusSearch, Settings,
}

/// <summary>
/// One row of the shortcut table: what it is called, and where config keeps it.
/// </summary>
/// <remarks>
/// Config stores one named property per action rather than a dictionary (see
/// <see cref="Config.ShortcutsGroup"/>), so something has to say which property belongs to
/// which action. This table is that, once, for the window and for Settings alike.
/// </remarks>
public sealed record ShortcutSlot(
    ShortcutAction Action, string Label, string Hint,
    Func<Config.ShortcutsGroup, Config.Shortcut> Get,
    Action<Config.ShortcutsGroup, Config.Shortcut> Set)
{
    public static readonly IReadOnlyList<ShortcutSlot> All =
    [
        new(ShortcutAction.Start, "Start a session", "", g => g.Start, (g, s) => g.Start = s),
        new(ShortcutAction.PauseResume, "Pause / resume", "", g => g.PauseResume, (g, s) => g.PauseResume = s),
        new(ShortcutAction.Stop, "Stop and save", "", g => g.Stop, (g, s) => g.Stop = s),
        new(ShortcutAction.ToggleRecording, "Start / stop with one key", "Starts when idle, stops and saves when recording.",
            g => g.ToggleRecording, (g, s) => g.ToggleRecording = s),
        new(ShortcutAction.CopyLastQuestion, "Copy last question", "Everything since the speaker last paused — what they just asked.",
            g => g.CopyLastQuestion, (g, s) => g.CopyLastQuestion = s),
        new(ShortcutAction.SendLastQuestion, "Send last question to your answer tool", "Where it goes is set in Settings ▸ Send. With nothing set there, it copies instead.",
            g => g.SendLastQuestion, (g, s) => g.SendLastQuestion = s),
        new(ShortcutAction.CopyLastN, "Copy last N sentences", "With text selected in the captions, copies the selection instead.",
            g => g.CopyLastN, (g, s) => g.CopyLastN = s),
        new(ShortcutAction.CopyAll, "Copy whole transcript", "", g => g.CopyAll, (g, s) => g.CopyAll = s),
        new(ShortcutAction.Bookmark, "Bookmark this moment", "Drops a marker in the transcript to find again afterwards.",
            g => g.Bookmark, (g, s) => g.Bookmark = s),
        new(ShortcutAction.ToggleClickThrough, "Click-through on / off", "Clicks pass through the window to the call underneath. Make this one Global, or use the handle that appears.",
            g => g.ToggleClickThrough, (g, s) => g.ToggleClickThrough = s),
        new(ShortcutAction.JumpLatest, "Jump to latest caption", "", g => g.JumpLatest, (g, s) => g.JumpLatest = s),
        new(ShortcutAction.FontBigger, "Larger caption text", "", g => g.FontBigger, (g, s) => g.FontBigger = s),
        new(ShortcutAction.FontSmaller, "Smaller caption text", "", g => g.FontSmaller, (g, s) => g.FontSmaller = s),
        new(ShortcutAction.ToggleSidebar, "Show / hide sessions", "", g => g.ToggleSidebar, (g, s) => g.ToggleSidebar = s),
        new(ShortcutAction.ToggleTheme, "Light / dark", "", g => g.ToggleTheme, (g, s) => g.ToggleTheme = s),
        new(ShortcutAction.TogglePin, "Keep window on top", "", g => g.TogglePin, (g, s) => g.TogglePin = s),
        new(ShortcutAction.FocusSearch, "Search sessions", "", g => g.FocusSearch, (g, s) => g.FocusSearch = s),
        new(ShortcutAction.Settings, "Open Settings", "", g => g.Settings, (g, s) => g.Settings = s),
    ];
}

/// <summary>A key plus its modifiers, and the one spelling of it that config stores.</summary>
public readonly record struct Gesture(ModifierKeys Modifiers, Key Key)
{
    /// <summary>
    /// Keys whose enum names nobody would type. Config holds the character on the keycap, so
    /// <c>Ctrl+.</c> rather than <c>Ctrl+OemPeriod</c> — it is a file people edit by hand.
    /// </summary>
    private static readonly (Key Key, string Name)[] Friendly =
    [
        (Key.OemPeriod, "."), (Key.OemComma, ","), (Key.OemPlus, "="), (Key.OemMinus, "-"),
        (Key.Oem1, ";"), (Key.Oem2, "/"), (Key.Oem3, "`"), (Key.Oem4, "["), (Key.Oem5, "\\"),
        (Key.Oem6, "]"), (Key.Oem7, "'"),
        (Key.D0, "0"), (Key.D1, "1"), (Key.D2, "2"), (Key.D3, "3"), (Key.D4, "4"),
        (Key.D5, "5"), (Key.D6, "6"), (Key.D7, "7"), (Key.D8, "8"), (Key.D9, "9"),
        (Key.Return, "Enter"), (Key.Escape, "Esc"), (Key.Prior, "PageUp"), (Key.Next, "PageDown"),
    ];

    /// <summary>F1–F24. They type nothing, which is what makes them safe where letters are not.</summary>
    public bool IsFunctionKey => Key is >= Key.F1 and <= Key.F24;

    /// <summary>
    /// A system-wide hotkey must not be something ordinary typing would trip: so a modifier,
    /// or a function key. Function keys matter here — with the call in a virtual machine,
    /// Ctrl+Alt is the VM's own "let go of the keyboard", and F8 is nobody's.
    /// </summary>
    public bool CanBeGlobal =>
        (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0 || IsFunctionKey;

    /// <summary>A bare key (or Shift+key) is typing, and belongs to a focused text box.</summary>
    public bool IsTyping => (Modifiers & ~ModifierKeys.Shift) == 0 && !IsFunctionKey;

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        var key = Key;
        var friendly = Array.Find(Friendly, f => f.Key == key);
        parts.Add(friendly.Name ?? Key.ToString());
        return string.Join("+", parts);
    }

    public static Gesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // "Ctrl++" and "Ctrl+=" are the same key; a trailing '+' is the key, not a separator.
        var trimmed = text.Trim();
        var keyIsPlus = trimmed.EndsWith("++", StringComparison.Ordinal) || trimmed == "+";
        var pieces = trimmed.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pieces.Length == 0 && !keyIsPlus) return null;

        var modifiers = ModifierKeys.None;
        Key? key = keyIsPlus ? Key.OemPlus : null;

        foreach (var piece in pieces)
        {
            switch (piece.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; continue;
                case "alt": modifiers |= ModifierKeys.Alt; continue;
                case "shift": modifiers |= ModifierKeys.Shift; continue;
                case "win" or "windows" or "meta" or "cmd": modifiers |= ModifierKeys.Windows; continue;
            }

            if (key is not null) return null;   // two keys is not a gesture

            var named = Array.Find(Friendly, f => f.Name.Equals(piece, StringComparison.OrdinalIgnoreCase));
            if (named.Name is not null) key = named.Key;
            else if (Enum.TryParse<Key>(piece, ignoreCase: true, out var parsed) && parsed != Key.None) key = parsed;
            else return null;
        }

        return key is { } k ? new Gesture(modifiers, k) : null;
    }

    /// <summary>The gesture a key event represents, or null while only modifiers are down.</summary>
    public static Gesture? From(KeyEventArgs e)
    {
        // Alt combinations arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.ImeProcessed) key = e.ImeProcessedKey;

        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
                   or Key.RightShift or Key.LWin or Key.RWin or Key.None or Key.DeadCharProcessed
            ? null
            : new Gesture(Keyboard.Modifiers, key);
    }
}

/// <summary>
/// Turns the shortcut table in config into live keys — inside the window, and system-wide.
/// </summary>
/// <remarks>
/// <para><b>Window shortcuts</b> are matched in one tunnelling <c>PreviewKeyDown</c> on the
/// window, which sees a key before the focused control does. That matters here: the caption
/// view is an AvalonEdit text area and swallows most keys itself, which is why the original
/// fixed bindings needed <c>InputBindings</c> rather than command gestures. A single
/// handler over a dictionary does the same job and can be rebuilt when Settings saves.</para>
/// <para><b>Global shortcuts</b> are <c>RegisterHotKey</c>. During a call the meeting app
/// has focus, not this window, so a window-only key to pause or copy is a key you cannot
/// press. Registration can fail — another app may own the combination — and that is
/// reported rather than hidden, because a shortcut that silently does nothing is worse than
/// one that says it is taken.</para>
/// </remarks>
public sealed class ShortcutManager : IDisposable
{
    private const int HotkeyMessage = 0x0312;      // WM_HOTKEY
    private const uint NoRepeat = 0x4000;          // MOD_NOREPEAT

    private readonly Window _window;
    private readonly Func<ShortcutAction, bool> _run;
    private readonly Dictionary<Gesture, ShortcutAction> _local = [];
    private readonly Dictionary<int, ShortcutAction> _global = [];
    private HwndSource? _source;

    /// <param name="window">The window that owns the keys.</param>
    /// <param name="run">Carry out an action; false if it could not run right now.</param>
    public ShortcutManager(Window window, Func<ShortcutAction, bool> run)
    {
        _window = window;
        _run = run;
        _window.PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Global shortcuts Windows refused, by action — shown in Settings.</summary>
    public IReadOnlyDictionary<ShortcutAction, string> Failures => _failures;
    private readonly Dictionary<ShortcutAction, string> _failures = [];

    /// <summary>Drop every binding and rebuild from config. Call after Settings saves.</summary>
    public void Load(Config.ShortcutsGroup shortcuts)
    {
        Unregister();
        _local.Clear();
        _failures.Clear();

        foreach (var slot in ShortcutSlot.All)
        {
            var binding = slot.Get(shortcuts);
            if (Gesture.Parse(binding.Keys) is not { } gesture) continue;

            if (binding.Global && gesture.CanBeGlobal) Register(slot.Action, gesture);
            else _local[gesture] = slot.Action;
        }
    }

    /// <summary>
    /// Let go of every key while Settings is open, so a combination being recorded there is
    /// typed into the recorder rather than carried out. <see cref="Load"/> takes them back.
    /// </summary>
    public void Suspend()
    {
        Unregister();
        _local.Clear();
    }

    /// <summary>The key shown in a tooltip for an action, or empty when it is unbound.</summary>
    public static string Describe(Config.ShortcutsGroup shortcuts, ShortcutAction action)
    {
        var slot = ShortcutSlot.All.First(s => s.Action == action);
        return Gesture.Parse(slot.Get(shortcuts).Keys)?.ToString() ?? "";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Gesture.From(e) is not { } gesture) return;
        if (!_local.TryGetValue(gesture, out var action)) return;

        // Someone typing a space into the search box is not asking to pause the recording,
        // and Ctrl+C in a text box is a copy. A read-only box (the captions) types nothing,
        // so it does not count.
        if (Keyboard.FocusedElement is TextBoxBase { IsReadOnly: false } &&
            (gesture.IsTyping || IsEditingKey(gesture))) return;

        // A drop-down owns the keyboard while it has focus: Space and the arrows drive it.
        // Buttons deliberately do not get the same courtesy — Space has always meant pause
        // here, and letting it "click" whichever button was last used would make it mean
        // Stop straight after someone clicks Stop's neighbour.
        if (gesture.IsTyping && Keyboard.FocusedElement is DependencyObject focused &&
            OwnsTypingKeys(focused)) return;

        if (_run(action)) e.Handled = true;
    }

    private static bool IsEditingKey(Gesture gesture) =>
        gesture.Modifiers == ModifierKeys.Control &&
        gesture.Key is Key.C or Key.V or Key.X or Key.A or Key.Z or Key.Y;

    private static bool OwnsTypingKeys(DependencyObject focused) =>
        focused is System.Windows.Controls.ComboBox or System.Windows.Controls.ComboBoxItem
            or System.Windows.Controls.Slider;

    // ── global ───────────────────────────────────────────────────────────────────────────

    private void Register(ShortcutAction action, Gesture gesture)
    {
        if (_source is null)
        {
            var handle = new WindowInteropHelper(_window).EnsureHandle();
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(OnMessage);
        }
        if (_source is null) return;

        var id = 0x4C43 + (int)action;      // 'LC' + action: unique within this window
        var modifiers = NoRepeat;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= 0x1;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= 0x2;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= 0x4;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= 0x8;

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        if (RegisterHotKey(_source.Handle, id, modifiers, virtualKey))
        {
            _global[id] = action;
            return;
        }

        // Keep it working inside the window at least, and say why it is not global.
        _local[gesture] = action;
        _failures[action] = $"{gesture} is already taken by Windows or another app, so it only works while Local Caption is focused.";
    }

    private void Unregister()
    {
        if (_source is { } source)
            foreach (var id in _global.Keys) UnregisterHotKey(source.Handle, id);
        _global.Clear();
    }

    private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != HotkeyMessage || !_global.TryGetValue(wParam.ToInt32(), out var action)) return IntPtr.Zero;
        _run(action);
        handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _window.PreviewKeyDown -= OnPreviewKeyDown;
        Unregister();
        _source?.RemoveHook(OnMessage);
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
