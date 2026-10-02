using System.Runtime.CompilerServices;
using System.Text.Json;
using LocalCaption.Core.Interview;

namespace LocalCaption.Interview.Tests;

/// <summary>Hotkey → <c>RegisterHotKey</c> arguments (SPEC-16 §4.2): virtual keys, never characters.</summary>
public sealed class HotkeyMappingTests
{
    private static Hotkey Parse(string text) => Hotkey.Parse(text).Value ?? throw new InvalidOperationException(text);

    [Fact]
    public void FunctionKeysAreVkF1ToVkF24()
    {
        for (var n = 1; n <= 24; n++) Assert.Equal(0x70u + (uint)(n - 1), HotkeyMapping.VirtualKey($"F{n}"));
        Assert.Equal(0x77u, HotkeyMapping.VirtualKey("F8"));
        Assert.Equal(0x87u, HotkeyMapping.VirtualKey("F24"));
    }

    [Fact]
    public void LettersAndDigitsAreTheirAsciiVirtualKeys()
    {
        Assert.Equal(0x41u, HotkeyMapping.VirtualKey("A"));
        Assert.Equal(0x4Bu, HotkeyMapping.VirtualKey("K"));
        Assert.Equal(0x5Au, HotkeyMapping.VirtualKey("Z"));
        Assert.Equal(0x30u, HotkeyMapping.VirtualKey("0"));
        Assert.Equal(0x39u, HotkeyMapping.VirtualKey("9"));
    }

    [Theory]
    [InlineData("Space", 0x20)]
    [InlineData("Enter", 0x0D)]
    [InlineData("Tab", 0x09)]
    [InlineData("Up", 0x26)]
    [InlineData("Down", 0x28)]
    [InlineData("Left", 0x25)]
    [InlineData("Right", 0x27)]
    [InlineData("PageUp", 0x21)]
    [InlineData("PageDown", 0x22)]
    [InlineData("Home", 0x24)]
    [InlineData("End", 0x23)]
    public void NamedKeys(string key, int vk) => Assert.Equal((uint)vk, HotkeyMapping.VirtualKey(key));

    [Fact]
    public void KeysOutsideTheGrammarHaveNone()
    {
        Assert.Null(HotkeyMapping.VirtualKey("F25"));
        Assert.Null(HotkeyMapping.VirtualKey("k"));        // canonical names only; Hotkey.Parse canonicalises
        Assert.Null(HotkeyMapping.VirtualKey("Esc"));
        Assert.Null(HotkeyMapping.ToNative(new Hotkey("Esc", HotkeyModifiers.Ctrl)));
    }

    [Fact]
    public void ModifiersMapToModFlagsWithNoRepeat()
    {
        Assert.Equal((0x4000u, 0x77u), HotkeyMapping.ToNative(Parse("F8")));
        Assert.Equal((0x4000u | 0x2 | 0x1 | 0x4 | 0x8, 0x4Bu), HotkeyMapping.ToNative(Parse("Ctrl+Alt+Shift+Cmd+K")));
        Assert.Equal(0x4000u | 0x8, HotkeyMapping.Modifiers(HotkeyModifiers.Cmd));      // Cmd is the Windows key
        Assert.Equal(0x2u, HotkeyMapping.Modifiers(HotkeyModifiers.Ctrl, noRepeat: false));
        Assert.Equal(0u, HotkeyMapping.Modifiers(HotkeyModifiers.None, noRepeat: false));
    }

    [Fact]
    public void DisplayNameSaysWinForCmd()
    {
        Assert.Equal("F8", HotkeyMapping.DisplayName(Parse("F8")));
        Assert.Equal("Ctrl+Win+K", HotkeyMapping.DisplayName(Parse("cmd+ctrl+k")));
        Assert.Equal("Ctrl+Alt+Shift+Win+Space", HotkeyMapping.DisplayName(Parse("Win+Shift+Alt+Ctrl+Space")));
    }

    [Fact]
    public void EveryKeyTheGrammarAcceptsCanBeRegistered()
    {
        var keys = Enumerable.Range(1, 24).Select(n => $"F{n}")
            .Concat("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".Select(c => c.ToString()))
            .Concat(["Space", "Enter", "Tab", "Up", "Down", "Left", "Right", "PageUp", "PageDown", "Home", "End"]);
        foreach (var key in keys)
            Assert.True(HotkeyMapping.ToNative(Parse($"Ctrl+{key}")) is not null, key);
    }

    [Fact]
    public void EveryValidSharedVectorCanBeRegistered()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData(), "hotkey", "parse.json")));
        var valid = 0;
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (!c.TryGetProperty("expect", out var expect) || expect.ValueKind != JsonValueKind.String) continue;
            var hotkey = Parse(c.GetProperty("input").GetString()!);
            Assert.True(HotkeyMapping.ToNative(hotkey) is not null, expect.GetString());
            valid++;
        }
        Assert.True(valid > 0);
    }

    private static string TestData([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", "testdata"));
}
