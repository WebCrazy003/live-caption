namespace LocalCaption.Core.Interview;

/// <summary>
/// The Ask hotkey as stored in <c>interview.hotkey</c> (SPEC-11 §Hotkey string grammar). Pure
/// and platform-neutral: registering it with the OS is the app's job (<c>RegisterHotKey</c>
/// here, Carbon on macOS); parsing is shared through <c>testdata/hotkey/</c>.
/// </summary>
/// <remarks>
/// <code>
/// hotkey   = *(modifier "+") key
/// modifier = "Ctrl" | "Alt" | "Shift" | "Cmd"
/// key      = "F1".."F24" | "A".."Z" | "0".."9" | "Space" | "Enter" | "Tab"
///          | "Up" | "Down" | "Left" | "Right" | "PageUp" | "PageDown" | "Home" | "End"
/// </code>
/// <para>Read case-insensitively, with the aliases macOS users type (<c>Control</c>,
/// <c>Option</c>/<c>Opt</c>, <c>Command</c>, <c>Return</c>) and <c>Win</c> for
/// <see cref="HotkeyModifiers.Cmd"/>; written canonically by <see cref="ToString"/>,
/// modifiers in the order Ctrl, Alt, Shift, Cmd. Port of <c>Hotkey.swift</c>.</para>
/// </remarks>
/// <param name="Key">Canonical key name, e.g. <c>"F8"</c>, <c>"K"</c>, <c>"Space"</c>.</param>
/// <param name="Modifiers">A set: repeating a modifier in the string changes nothing.</param>
public sealed record Hotkey(string Key, HotkeyModifiers Modifiers)
{
    public const string DefaultString = "F8";
    public static Hotkey Default { get; } = new("F8", HotkeyModifiers.None);

    /// <summary>The screenshot hotkey's default (owner, 2026-10-02).</summary>
    public const string DefaultScreenshotString = "F9";
    public static Hotkey DefaultScreenshot { get; } = new("F9", HotkeyModifiers.None);

    public bool IsFunctionKey => FunctionKeys.Contains(Key);

    /// <summary><c>Ctrl+Alt+Shift+Cmd+Key</c>, modifiers in canonical order.</summary>
    public override string ToString() =>
        string.Join("+", CanonicalOrder.Where(m => Modifiers.HasFlag(m)).Select(m => m.ToString()).Append(Key));

    public static HotkeyParseResult Parse(string text)
    {
        var parts = text.Split('+').Select(SwiftText.TrimWhitespaces).ToList();
        if (parts.All(p => p.Length == 0)) return Fail(HotkeyParseErrorKind.Empty);
        var last = parts[^1];
        if (last.Length == 0) return Fail(HotkeyParseErrorKind.MissingKey);

        var mods = HotkeyModifiers.None;
        foreach (var token in parts.Take(parts.Count - 1))
        {
            if (!ModifierAliases.TryGetValue(token.ToLowerInvariant(), out var m))
                return token.Length == 0
                    ? Fail(HotkeyParseErrorKind.MissingKey)
                    : Fail(HotkeyParseErrorKind.UnknownModifier, token);
            mods |= m;
        }
        if (ModifierAliases.ContainsKey(last.ToLowerInvariant())) return Fail(HotkeyParseErrorKind.MissingKey);
        if (!KeyNames.TryGetValue(last.ToLowerInvariant(), out var key))
            return Fail(HotkeyParseErrorKind.UnknownKey, last);
        if (!FunctionKeys.Contains(key) && (mods & ~HotkeyModifiers.Shift) == HotkeyModifiers.None)
            return Fail(HotkeyParseErrorKind.NeedsModifier, key);
        return new HotkeyParseResult(new Hotkey(key, mods), null);
    }

    /// <summary>The configured hotkey, or <paramref name="fallback"/> (default <see cref="Default"/>)
    /// when the string is empty or invalid.</summary>
    public static Hotkey Resolve(string text, Hotkey? fallback = null) =>
        Parse(text).Value ?? fallback ?? Default;

    private static HotkeyParseResult Fail(HotkeyParseErrorKind kind, string? token = null) =>
        new(null, new HotkeyParseError(kind, token));

    // ── Tables ───────────────────────────────────────────────────────────────────────────

    private static readonly HotkeyModifiers[] CanonicalOrder =
        [HotkeyModifiers.Ctrl, HotkeyModifiers.Alt, HotkeyModifiers.Shift, HotkeyModifiers.Cmd];

    private static readonly Dictionary<string, HotkeyModifiers> ModifierAliases = new(StringComparer.Ordinal)
    {
        ["ctrl"] = HotkeyModifiers.Ctrl, ["control"] = HotkeyModifiers.Ctrl,
        ["alt"] = HotkeyModifiers.Alt, ["option"] = HotkeyModifiers.Alt, ["opt"] = HotkeyModifiers.Alt,
        ["shift"] = HotkeyModifiers.Shift,
        ["cmd"] = HotkeyModifiers.Cmd, ["command"] = HotkeyModifiers.Cmd, ["win"] = HotkeyModifiers.Cmd,
    };

    private static readonly HashSet<string> FunctionKeys =
        new(Enumerable.Range(1, 24).Select(n => $"F{n}"), StringComparer.Ordinal);

    private static readonly Dictionary<string, string> KeyNames = BuildKeyNames();

    private static Dictionary<string, string> BuildKeyNames()
    {
        var t = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in FunctionKeys) t[k.ToLowerInvariant()] = k;
        foreach (var c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789") t[char.ToLowerInvariant(c).ToString()] = c.ToString();
        foreach (var k in new[] { "Space", "Enter", "Tab", "Up", "Down", "Left", "Right", "PageUp", "PageDown", "Home", "End" })
            t[k.ToLowerInvariant()] = k;
        t["return"] = "Enter";
        return t;
    }
}

/// <summary>
/// Hotkey modifiers. The flag values follow the canonical order (Ctrl, Alt, Shift, Cmd), so a
/// combination compares by value like the Swift <c>Set&lt;Modifier&gt;</c>. <c>Cmd</c> is the
/// Windows key on Windows.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Cmd = 8,
}

/// <summary>Why a hotkey string did not parse.</summary>
public enum HotkeyParseErrorKind
{
    Empty,
    UnknownModifier,
    UnknownKey,
    MissingKey,
    /// <summary>
    /// A typing key (letter, digit, Space, arrows…) bound globally with no modifier, or with
    /// Shift alone, would swallow that key in every app.
    /// </summary>
    NeedsModifier,
}

/// <param name="Token">
/// The offending token as typed (trimmed) for <see cref="HotkeyParseErrorKind.UnknownModifier"/>
/// and <see cref="HotkeyParseErrorKind.UnknownKey"/>; the canonical key for
/// <see cref="HotkeyParseErrorKind.NeedsModifier"/>; <c>null</c> otherwise.
/// </param>
public sealed record HotkeyParseError(HotkeyParseErrorKind Kind, string? Token = null);

/// <summary>Swift's <c>Result&lt;Hotkey, ParseError&gt;</c>: exactly one side is set.</summary>
public sealed record HotkeyParseResult(Hotkey? Value, HotkeyParseError? Error)
{
    public bool IsSuccess => Value is not null;
}
