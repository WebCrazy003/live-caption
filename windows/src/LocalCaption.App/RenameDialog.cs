using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LocalCaption.App;

/// <summary>
/// Asks for a new session name. WPF has no built-in prompt, and this is the only one the app
/// needs — a whole XAML window for one text box would be more moving parts, not fewer.
/// </summary>
public static class RenameDialog
{
    /// <summary>The new name, or null if the user backed out or cleared it.</summary>
    public static string? Ask(Window owner, string current)
    {
        var input = new TextBox
        {
            Text = current,
            Margin = new Thickness(0, 0, 0, 14),
        };

        var ok = new Button { Content = "Rename", IsDefault = true, MinWidth = 88 };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "Button.Accent");
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 88,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(18) };
        var label = new TextBlock { Text = "SESSION NAME", Margin = new Thickness(0, 0, 0, 6) };
        label.SetResourceReference(FrameworkElement.StyleProperty, "Type.Caps");
        panel.Children.Add(label);
        panel.Children.Add(input);
        panel.Children.Add(buttons);

        var dialog = new ChromeWindow
        {
            Title = "Rename session",
            Content = panel,
            Owner = owner,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) dialog.DialogResult = true;
        };

        if (dialog.ShowDialog() != true) return null;

        var name = input.Text.Trim();
        return name.Length == 0 ? null : name;
    }
}
