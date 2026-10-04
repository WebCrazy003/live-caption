using System.Windows.Controls;
using LocalCaption.Asr;

namespace LocalCaption.App;

/// <summary>
/// The compute-backend picker's items, shared by Settings ▸ Speech recognition and the quick
/// toolbar so the two can never offer different things.
/// </summary>
/// <remarks>
/// Items are <see cref="ComboBoxItem"/>s rather than strings so that Vulkan can be listed but
/// disabled, with its reason in a tooltip that still shows while disabled (ARM64, no Vulkan
/// driver). The entries and their wording come from <see cref="BackendChoice.Options"/>.
/// </remarks>
internal static class BackendPicker
{
    /// <summary>Fill <paramref name="box"/> and select <paramref name="configured"/>.</summary>
    /// <param name="fallback">
    /// What to select when <paramref name="configured"/> is not one of the entries: null
    /// leaves nothing selected (Settings, which then saves "auto"); "auto" is the toolbar's.
    /// </param>
    public static void Fill(ComboBox box, string configured, string? fallback)
    {
        var items = BackendChoice.Options(PlatformFacts.Current()).Select(option =>
        {
            var item = new ComboBoxItem
            {
                Content = option.Label,
                Tag = option.Value,
                IsEnabled = option.Enabled,
                ToolTip = option.ToolTip,
            };
            ToolTipService.SetShowOnDisabled(item, true);
            return item;
        }).ToList();

        box.ItemsSource = items;
        // Case-insensitive like BackendChoice.Parse, so a hand-edited "Vulkan" is not reset to auto.
        bool Is(ComboBoxItem i, string? value) => string.Equals((string)i.Tag, value, StringComparison.OrdinalIgnoreCase);
        box.SelectedItem = items.FirstOrDefault(i => Is(i, configured))
                           ?? (fallback is null ? null : items.FirstOrDefault(i => Is(i, fallback)));
    }

    /// <summary>The selected entry's <c>asr.backend</c> value, or null when nothing is selected.</summary>
    public static string? Value(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;
}
