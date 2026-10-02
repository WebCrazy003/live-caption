using System.Text.Json;
using LocalCaption.Core.Data;
using Xunit;

namespace LocalCaption.Core.Tests;

/// <summary>
/// The Windows-only <c>shortcuts</c> and <c>ui</c> groups: they must behave like every other
/// group — defaults when absent, identity on round-trip — or a config written before they
/// existed would be "repaired" out from under its owner.
/// </summary>
public sealed class ConfigShellTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lc-shell-" + Guid.NewGuid().ToString("N"));

    public ConfigShellTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string ConfigPath => Path.Combine(_directory, "config.json");

    [Fact]
    public void A_config_from_before_these_groups_loads_with_their_defaults_and_is_not_repaired()
    {
        File.WriteAllText(ConfigPath, """{ "schema_version": 2, "caption": { "font_size": 22 } }""");

        var (config, repaired) = Config.LoadOrRepair(ConfigPath);

        Assert.False(repaired);
        Assert.Equal(22, config.Caption.FontSize);
        Assert.Equal("system", config.Ui.Theme);
        Assert.False(config.Ui.PinOnTop);
        Assert.False(config.Ui.SidebarCollapsed);
        Assert.Equal("Space", config.Shortcuts.PauseResume.Keys);
        Assert.Equal("Ctrl+C", config.Shortcuts.CopyLastN.Keys);
    }

    [Fact]
    public void No_shortcut_is_global_until_someone_asks_for_it()
    {
        // A global hotkey takes its combination away from every other app. Doing that on
        // first launch, unasked, is how a captioning tool breaks someone's editor.
        var shortcuts = new Config().Shortcuts;
        var all = typeof(Config.ShortcutsGroup).GetProperties()
            .Select(p => p.GetValue(shortcuts)).OfType<Config.Shortcut>().ToList();

        Assert.NotEmpty(all);
        Assert.All(all, s => Assert.False(s.Global));
    }

    [Fact]
    public void Default_shortcuts_do_not_collide()
    {
        var shortcuts = new Config().Shortcuts;
        var keys = typeof(Config.ShortcutsGroup).GetProperties()
            .Select(p => ((Config.Shortcut)p.GetValue(shortcuts)!).Keys)
            .Where(k => k.Length > 0).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Shell_settings_survive_a_round_trip_by_value()
    {
        var config = new Config();
        config.Ui.Theme = "light";
        config.Ui.PinOnTop = true;
        config.Ui.SidebarCollapsed = true;
        config.Ui.SidebarWidth = 312;
        config.Shortcuts.PauseResume = new Config.Shortcut("Ctrl+Alt+P", global: true);
        config.Shortcuts.ToggleRecording = new Config.Shortcut("Ctrl+Alt+R", global: true);
        config.Write(ConfigPath);

        var (reloaded, repaired) = Config.LoadOrRepair(ConfigPath);

        Assert.False(repaired);
        Assert.True(config == reloaded, "write→read must be the identity, including the new groups");
        Assert.True(reloaded.Shortcuts.PauseResume.Global);
    }

    [Fact]
    public void Interview_layout_and_window_state_round_trip_in_the_ui_group()
    {
        var config = new Config();
        config.Ui.InterviewAnswerShareStacked = 0.45;
        config.Ui.InterviewCaptionShareWide = 0.7;
        config.Ui.InterviewCaptionsHidden = true;
        config.Ui.SessionsWindow = new Config.WindowBounds { X = -1200, Y = 40.5, Width = 1010, Height = 655 };
        config.Ui.SettingsPage = "Codex";
        config.Write(ConfigPath);

        var (reloaded, repaired) = Config.LoadOrRepair(ConfigPath);

        Assert.False(repaired);
        Assert.True(config == reloaded, "write→read must be the identity for the new ui keys");
        Assert.Equal(-1200, reloaded.Ui.SessionsWindow!.X);

        // Per-machine state lives in "ui", never in the shared "interview" group (SPEC-16 §5.3).
        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var ui = document.RootElement.GetProperty("ui");
        Assert.True(ui.GetProperty("interview_captions_hidden").GetBoolean());
        Assert.Equal(1010, ui.GetProperty("sessions_window").GetProperty("width").GetDouble());
        Assert.False(document.RootElement.GetProperty("interview").TryGetProperty("interview_captions_hidden", out _));

        // Alphabetical, as every group (diffs cleanly against the Mac's sorted keys).
        var names = ui.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);
    }

    [Fact]
    public void Interview_layout_defaults_and_no_sessions_window_until_one_is_saved()
    {
        File.WriteAllText(ConfigPath, """{ "schema_version": 2, "ui": { "theme": "dark" } }""");

        var (config, repaired) = Config.LoadOrRepair(ConfigPath);

        Assert.False(repaired);
        Assert.Equal(0.6, config.Ui.InterviewAnswerShareStacked);
        Assert.Equal(0.58, config.Ui.InterviewCaptionShareWide);
        Assert.False(config.Ui.InterviewCaptionsHidden);
        Assert.Null(config.Ui.SessionsWindow);
        Assert.Equal("", config.Ui.SettingsPage);

        config.Write(ConfigPath);
        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        Assert.False(document.RootElement.GetProperty("ui").TryGetProperty("sessions_window", out _));
    }

    [Fact]
    public void The_reserved_always_on_top_key_is_left_alone()
    {
        // §7.3 keeps window.always_on_top = true for macOS interchange. The pin is a separate
        // key precisely so that default never pins a Windows install.
        new Config().Write(ConfigPath);
        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));

        Assert.True(document.RootElement.GetProperty("window").GetProperty("always_on_top").GetBoolean());
        Assert.False(document.RootElement.GetProperty("ui").GetProperty("pin_on_top").GetBoolean());
    }
}
