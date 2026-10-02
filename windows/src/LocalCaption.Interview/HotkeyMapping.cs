using LocalCaption.Core.Interview;

namespace LocalCaption.Interview;

/// <summary>
/// A <see cref="Hotkey"/> (SPEC-11 grammar) as the arguments of Win32 <c>RegisterHotKey</c>
/// (SPEC-16 §4.2). Pure — plain numbers, no Win32 calls — so the table is tested on the Mac.
/// </summary>
/// <remarks>
/// <para>Keys map to <b>virtual-key codes</b>, never characters (SPEC-16 "Compatibility target":
/// any keyboard layout). <c>VK_A</c>…<c>VK_Z</c> and <c>VK_0</c>…<c>VK_9</c> are the keys that
/// layout labels with that letter or digit — on AZERTY the top row still sends <c>VK_1</c>… even
/// though it types <c>&amp;é"</c> — so "Ctrl+Alt+K" is the key marked K on every layout. Function,
/// navigation and editing keys have fixed codes on every layout.</para>
/// <para>Canonical <see cref="HotkeyModifiers.Cmd"/> is the Windows key (<c>MOD_WIN</c>).</para>
/// </remarks>
public static class HotkeyMapping
{
    /// <summary><c>MOD_ALT</c>.</summary>
    public const uint ModAlt = 0x0001;
    /// <summary><c>MOD_CONTROL</c>.</summary>
    public const uint ModControl = 0x0002;
    /// <summary><c>MOD_SHIFT</c>.</summary>
    public const uint ModShift = 0x0004;
    /// <summary><c>MOD_WIN</c>.</summary>
    public const uint ModWin = 0x0008;
    /// <summary><c>MOD_NOREPEAT</c> (Windows 7+): holding the key sends one <c>WM_HOTKEY</c>, not a stream.</summary>
    public const uint ModNoRepeat = 0x4000;

    private static readonly Dictionary<string, uint> Keys = BuildKeys();

    private static Dictionary<string, uint> BuildKeys()
    {
        var t = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (var n = 1; n <= 24; n++) t[$"F{n}"] = 0x70u + (uint)(n - 1);        // VK_F1 = 0x70 … VK_F24 = 0x87
        for (var c = 'A'; c <= 'Z'; c++) t[c.ToString()] = c;                    // VK_A = 0x41 … VK_Z = 0x5A
        for (var c = '0'; c <= '9'; c++) t[c.ToString()] = c;                    // VK_0 = 0x30 … VK_9 = 0x39
        t["Space"] = 0x20;      // VK_SPACE
        t["Enter"] = 0x0D;      // VK_RETURN
        t["Tab"] = 0x09;        // VK_TAB
        t["PageUp"] = 0x21;     // VK_PRIOR
        t["PageDown"] = 0x22;   // VK_NEXT
        t["End"] = 0x23;        // VK_END
        t["Home"] = 0x24;       // VK_HOME
        t["Left"] = 0x25;       // VK_LEFT
        t["Up"] = 0x26;         // VK_UP
        t["Right"] = 0x27;      // VK_RIGHT
        t["Down"] = 0x28;       // VK_DOWN
        return t;
    }

    /// <summary>The virtual-key code of a canonical key name (<see cref="Hotkey.Key"/>), or null.</summary>
    public static uint? VirtualKey(string key) => Keys.TryGetValue(key, out var vk) ? vk : null;

    /// <summary><c>MOD_*</c> flags for <paramref name="modifiers"/>, plus <c>MOD_NOREPEAT</c> unless asked not to.</summary>
    public static uint Modifiers(HotkeyModifiers modifiers, bool noRepeat = true)
    {
        var m = noRepeat ? ModNoRepeat : 0u;
        if (modifiers.HasFlag(HotkeyModifiers.Ctrl)) m |= ModControl;
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) m |= ModAlt;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) m |= ModShift;
        if (modifiers.HasFlag(HotkeyModifiers.Cmd)) m |= ModWin;
        return m;
    }

    /// <summary>
    /// <c>RegisterHotKey</c>'s <c>fsModifiers</c> (with <c>MOD_NOREPEAT</c>) and <c>vk</c>; null
    /// when the key is not in the grammar (only possible for a hand-built <see cref="Hotkey"/>).
    /// </summary>
    public static (uint Modifiers, uint VirtualKey)? ToNative(Hotkey hotkey) =>
        VirtualKey(hotkey.Key) is { } vk ? (Modifiers(hotkey.Modifiers), vk) : null;

    /// <summary>
    /// How Windows people name the hotkey: <c>Ctrl+Alt+Shift+Win+K</c>. The stored spelling stays
    /// <see cref="Hotkey.ToString"/> (<c>Cmd</c>), which is shared with macOS.
    /// </summary>
    public static string DisplayName(Hotkey hotkey)
    {
        var parts = new List<string>(5);
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Cmd)) parts.Add("Win");
        parts.Add(hotkey.Key);
        return string.Join("+", parts);
    }
}
