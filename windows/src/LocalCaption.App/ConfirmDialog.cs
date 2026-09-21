using System.Windows;
using System.Windows.Controls;

namespace LocalCaption.App;

/// <summary>
/// A yes/no question in the app's own clothes.
/// </summary>
/// <remarks>
/// <c>MessageBox</c> is still used where the app is reporting trouble — it is the system's
/// voice, and trouble should sound like the system. This is for choices the app itself is
/// offering, such as a multi-gigabyte download, where a grey Win32 box in the middle of a
/// dark window reads as something having gone wrong.
/// </remarks>
public static class ConfirmDialog
{
    /// <summary>What to do about a session that is still open when the window is closed.</summary>
    public enum Closing { Cancel, SaveAndClose, CloseWithoutSaving }

    /// <summary>
    /// The window is closing on an unsaved session: save it, leave it, or stay.
    /// </summary>
    /// <remarks>
    /// Three named buttons rather than Yes / No / Cancel. "No" to "save before closing?" is a
    /// question people answer wrongly under pressure, and the pressure here is the end of an
    /// interview. It also says what "without saving" really means, because it is gentler
    /// than it sounds: the captions are already on disk in the recovery journal.
    /// </remarks>
    public static Closing AskToClose(Window owner, string sessionName, string elapsed)
    {
        var title = new TextBlock { Text = sessionName, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");

        var length = new TextBlock { Text = $"{elapsed} recorded · not saved yet", FontSize = 12, Margin = new Thickness(0, 3, 0, 14) };
        length.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");

        var explain = new TextBlock
        {
            Text = "Save it as a transcript before closing?",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };

        var note = new TextBlock
        {
            Text = "If you close without saving, nothing is lost: the captions are already in the recovery " +
                   "journal, and Local Caption will offer to save them the next time it starts.",
            Margin = new Thickness(0, 8, 0, 0),
        };
        note.SetResourceReference(FrameworkElement.StyleProperty, "Type.Note");

        var save = new Button { Content = "Save and close", IsDefault = true, MinWidth = 130, Tag = "\uE74E" };
        save.SetResourceReference(FrameworkElement.StyleProperty, "Button.Accent");
        var leave = new Button { Content = "Close without saving", MinWidth = 150, Margin = new Thickness(8, 0, 0, 0) };
        var stay = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(leave);
        buttons.Children.Add(stay);

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        foreach (var child in new UIElement[] { title, length, explain, note, buttons }) panel.Children.Add(child);

        var dialog = new ChromeWindow
        {
            Title = "Session in progress",
            Content = panel,
            Owner = owner,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var choice = Closing.Cancel;
        save.Click += (_, _) => { choice = Closing.SaveAndClose; dialog.DialogResult = true; };
        leave.Click += (_, _) => { choice = Closing.CloseWithoutSaving; dialog.DialogResult = true; };
        dialog.ShowDialog();
        return choice;
    }

    /// <summary>What someone decided to do with a session.</summary>
    public enum Removal { Cancelled, ListOnly, ListAndFiles }

    /// <summary>
    /// Ask how far removing a session should go: out of the list, or off the disk as well.
    /// </summary>
    /// <remarks>
    /// One dialog with a box to tick, rather than two menu commands. "Delete" and "Delete
    /// from disk" side by side in a context menu are one slip apart; here the destructive
    /// path takes a second, deliberate action, and the button changes colour and wording to
    /// say what it has become.
    /// </remarks>
    public static Removal AskToRemove(Window owner, string name, string when, string path, bool filesExist)
    {
        var title = new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");

        var date = new TextBlock { Text = when, FontSize = 12, Margin = new Thickness(0, 3, 0, 14) };
        date.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");

        var explain = new TextBlock { Text = "This takes it out of the session list.", FontSize = 13, TextWrapping = TextWrapping.Wrap };

        var alsoFiles = new CheckBox { Content = "Also delete the transcript files", Margin = new Thickness(0, 16, 0, 0), IsEnabled = filesExist };

        var note = new TextBlock
        {
            Text = filesExist
                ? "They go to the Recycle Bin, so a mistake can still be undone from there."
                : "Its transcript file is no longer on disk, so there is nothing else to delete.",
            Margin = new Thickness(25, 3, 0, 0),
        };
        note.SetResourceReference(FrameworkElement.StyleProperty, "Type.Note");

        var where = new TextBlock { Text = path, FontSize = 11, Margin = new Thickness(25, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path };
        where.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
        where.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
        if (path.Length == 0) where.Visibility = Visibility.Collapsed;

        var remove = new Button { Content = "Remove from list", IsDefault = true, MinWidth = 140 };
        remove.SetResourceReference(FrameworkElement.StyleProperty, "Button.Accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };

        void Restyle()
        {
            var files = alsoFiles.IsChecked == true;
            remove.Content = files ? "Delete files and remove" : "Remove from list";
            remove.SetResourceReference(FrameworkElement.StyleProperty, files ? "Button.Destructive" : "Button.Accent");
        }
        alsoFiles.Checked += (_, _) => Restyle();
        alsoFiles.Unchecked += (_, _) => Restyle();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(remove);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        foreach (var child in new UIElement[] { title, date, explain, alsoFiles, note, where, buttons }) panel.Children.Add(child);

        var dialog = new ChromeWindow
        {
            Title = "Remove session",
            Content = panel,
            Owner = owner,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        remove.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return Removal.Cancelled;
        return alsoFiles.IsChecked == true ? Removal.ListAndFiles : Removal.ListOnly;
    }

    public static bool Ask(Window? owner, string title, string message, string yes, string no = "Cancel")
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 18) };

        var accept = new Button { Content = yes, IsDefault = true, MinWidth = 96 };
        accept.SetResourceReference(FrameworkElement.StyleProperty, "Button.Accent");
        var decline = new Button { Content = no, IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(accept);
        buttons.Children.Add(decline);

        var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        panel.Children.Add(text);
        panel.Children.Add(buttons);

        var dialog = new ChromeWindow
        {
            Title = title,
            Content = panel,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        if (owner is { IsLoaded: true }) dialog.Owner = owner;

        accept.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true;
    }
}
