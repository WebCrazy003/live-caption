using System.Windows;
using System.Windows.Controls;

namespace LocalCaption.App.Interview.Sessions;

/// <summary>
/// Delete a session from the Sessions window, with the Mac's choices (specs/SPEC-16 §5.6):
/// interviews keep or drop their interview data, either kind may take its transcript file.
/// </summary>
/// <remarks>
/// The Mac's <c>confirmationDialog</c> is a column of named buttons, one per outcome; this is
/// the same in the app's own dialog clothes (<see cref="ConfirmDialog"/>'s look). A column of
/// full-width buttons rather than a check box, because three outcomes do not fold into one
/// box. Enter takes the first (safest data-keeping) choice for a caption session and the
/// Mac's default for an interview; Esc cancels.
/// </remarks>
public static class SessionDeleteDialog
{
    /// <summary>What to delete.</summary>
    public enum Choice
    {
        Cancel,
        /// <summary>The row (and its caption segments) only.</summary>
        SessionOnly,
        /// <summary>The row and the <c>.txt</c>/<c>.json</c> export (Recycle Bin).</summary>
        SessionAndFile,
        /// <summary>The row and the interview's rows, turns and screenshots (+ thread archive).</summary>
        SessionAndInterview,
        /// <summary>All of it.</summary>
        SessionInterviewAndFile,
    }

    /// <param name="hasFile">The session's transcript export is on disk; otherwise no file choice is offered.</param>
    public static Choice Ask(Window owner, string sessionName, bool isInterview, bool hasFile)
    {
        var title = new TextBlock
        {
            Text = $"Delete “{sessionName}”?",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");

        var message = new TextBlock
        {
            Text = isInterview
                ? "Interview data is the CV text, questions, answers and screenshots. The transcript file is kept unless you choose to delete it."
                : "The transcript file is kept unless you choose to delete it.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        panel.Children.Add(title);
        panel.Children.Add(message);
        if (hasFile)
        {
            var note = new TextBlock { Text = "A deleted transcript file goes to the Recycle Bin." };
            note.SetResourceReference(FrameworkElement.StyleProperty, "Type.Note");
            panel.Children.Add(note);
        }

        var buttons = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        panel.Children.Add(buttons);

        var dialog = new ChromeWindow
        {
            Title = "Delete session",
            Content = panel,
            Owner = owner,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var result = Choice.Cancel;

        void Add(string text, Choice choice, string style, bool isDefault = false)
        {
            var button = new Button
            {
                Content = text,
                IsDefault = isDefault,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 8),
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, style);
            button.Click += (_, _) =>
            {
                result = choice;
                dialog.DialogResult = true;
            };
            buttons.Children.Add(button);
        }

        if (isInterview)
        {
            // Interview data (CV text, Q&A, screenshots) goes by default (SPEC-15 §History).
            Add("Delete Session and Interview Data", Choice.SessionAndInterview, "Button.Accent", isDefault: true);
            if (hasFile)
                Add("Delete Session, Interview Data and Transcript File", Choice.SessionInterviewAndFile, "Button.Destructive");
            Add("Delete Session Only (keep interview data)", Choice.SessionOnly, "Button.Base");
        }
        else
        {
            Add("Delete Session Only", Choice.SessionOnly, "Button.Accent", isDefault: true);
            if (hasFile)
                Add("Delete Session and Transcript File", Choice.SessionAndFile, "Button.Destructive");
        }

        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        buttons.Children.Add(cancel);

        dialog.ShowDialog();
        return result;
    }
}
