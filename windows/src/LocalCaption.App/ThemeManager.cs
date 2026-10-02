using System.Windows;
using Microsoft.Win32;

namespace LocalCaption.App;

/// <summary>
/// Light, dark, or whatever Windows is — by swapping one resource dictionary.
/// </summary>
/// <remarks>
/// <para>Every control names a colour by its role (<c>Bg.Panel</c>, <c>Accent</c>) through a
/// <c>DynamicResource</c>, and the two theme files define the same roles. So a theme change
/// is one dictionary replaced at index 0 of the application's merged dictionaries: nothing
/// is rebuilt, no window reloads, and a recording in progress does not notice.</para>
/// <para><c>system</c> follows the Windows "app mode" setting and keeps following it while
/// the app runs — someone whose machine goes dark at sunset should not have to restart a
/// session to get a readable window.</para>
/// </remarks>
public static class ThemeManager
{
    private static readonly Uri DarkUri = new("/Themes/Dark.xaml", UriKind.Relative);
    private static readonly Uri LightUri = new("/Themes/Light.xaml", UriKind.Relative);

    private static string _preference = "system";
    private static bool _watching;

    /// <summary>The theme actually on screen, after resolving <c>system</c>.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>What was asked for: <c>system</c>, <c>dark</c> or <c>light</c>.</summary>
    public static string Preference => _preference;

    /// <summary>Raised after the palette has been swapped, for anything that caches a brush.</summary>
    public static event Action? Changed;

    public static void Apply(string? preference)
    {
        _preference = Normalise(preference);

        if (!_watching)
        {
            // Raised on a system thread, for many reasons besides theme; Apply is cheap and
            // idempotent, so filtering on the category is all the care it needs.
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category != UserPreferenceCategory.General || _preference != "system") return;
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(_preference));
            };
            _watching = true;
        }

        var dark = _preference switch
        {
            "dark" => true,
            "light" => false,
            _ => !SystemUsesLightTheme(),
        };

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = dark ? DarkUri : LightUri };

        // Index 0 is the palette by convention (App.xaml); Controls.xaml follows it.
        if (dictionaries.Count == 0) dictionaries.Add(palette);
        else if (dark != IsDark || dictionaries[0].Source is null) dictionaries[0] = palette;

        IsDark = dark;
        Changed?.Invoke();
    }

    /// <summary>The next stop on the toggle: dark ⇄ light. Leaves <c>system</c> behind.</summary>
    public static string Toggled() => IsDark ? "light" : "dark";

    public static string Normalise(string? preference) => preference?.Trim().ToLowerInvariant() switch
    {
        "dark" => "dark",
        "light" => "light",
        _ => "system",
    };

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;   // unreadable: dark is the safer guess for a window beside a video call
        }
    }
}
