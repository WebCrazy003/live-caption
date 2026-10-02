using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace LocalCaption.App.Interview.Views;

/// <summary>
/// The one-time notice before Interview mode is used (SPEC-16 §5.1; Mac
/// <c>InterviewPrivacyNotice</c>). Built like <see cref="ConfirmDialog"/>: a modal
/// <see cref="ChromeWindow"/>, so it wears the app's theme.
/// </summary>
/// <remarks>
/// Windows adds one line the Mac omits: screenshots taken with the Screenshot hotkey are sent
/// too. The host shows this when Interview is chosen while <c>privacy_acknowledged</c> is
/// false; on <c>true</c> it sets <c>privacy_acknowledged</c> and <c>mode = interview</c> and
/// saves the config.
/// </remarks>
public static class PrivacyNoticeDialog
{
    /// <summary>Show the notice; <c>true</c> when the user chose <b>Use Interview mode</b>.</summary>
    public static bool Ask(Window? owner)
    {
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var globe = new TextBlock { Text = "", FontSize = 18, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        globe.SetResourceReference(FrameworkElement.StyleProperty, "Type.Icon");
        globe.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        DockPanel.SetDock(globe, Dock.Left);
        var title = new TextBlock
        {
            Text = "Interview mode sends data to OpenAI",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        heading.Children.Add(globe);
        heading.Children.Add(title);

        var intro = new TextBlock
        {
            Text = "Caption only mode keeps everything on this PC. Interview mode is different:",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };

        var points = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        points.Children.Add(Point("", "Your CV, the job description and your skills are sent when you run a skill step."));
        points.Children.Add(Point("", "The interviewer's recent words are sent each time you press Ask."));
        points.Children.Add(Point("", "Screenshots on your clipboard are sent only if you turn that on in Settings."));
        // SPEC-16 §5.1: the Mac notice omits this; Windows includes it.
        points.Children.Add(Point("", "Screenshots you take with the Screenshot hotkey are sent with your next Ask or Send."));

        var where = new TextBlock
        {
            Text = "They go to OpenAI through the Codex app, signed in with your ChatGPT account. "
                   + "Codex is locked down: it can't read your files or run commands. It may search the "
                   + "web when a skill asks it to research the company.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        where.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

        var accept = new Button { Content = "_Use Interview mode", IsDefault = true, MinWidth = 150 };
        accept.SetResourceReference(FrameworkElement.StyleProperty, "Button.Accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };

        // A WrapPanel rather than a StackPanel: at 300 % in a narrow dialog the second button
        // drops to a new line instead of being cut off.
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        foreach (var child in new UIElement[] { heading, intro, points, where, buttons }) panel.Children.Add(child);

        // Scrolls rather than running off a short screen at high scaling.
        var scroll = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 120),
        };

        var dialog = new ChromeWindow
        {
            Title = "Interview mode",
            Content = scroll,
            Width = Math.Min(500, Math.Max(320, SystemParameters.WorkArea.Width - 40)),
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        AutomationProperties.SetName(dialog, "Interview mode sends data to OpenAI");

        accept.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => accept.Focus();
        return dialog.ShowDialog() == true;
    }

    /// <summary>One bullet: an icon and a wrapped line, as the Mac's <c>Label</c>s.</summary>
    private static UIElement Point(string glyph, string text)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        var icon = new TextBlock { Text = glyph, Width = 18, Margin = new Thickness(2, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(FrameworkElement.StyleProperty, "Type.Icon");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        return row;
    }
}
