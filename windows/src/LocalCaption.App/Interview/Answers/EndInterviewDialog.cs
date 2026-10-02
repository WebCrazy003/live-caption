using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using LocalCaption.Interview;

namespace LocalCaption.App.Interview.Answers;

/// <summary>
/// Shown right after End interview (SPEC-16 §5.5; Mac <c>EndInterviewSheet.swift</c>):
/// summarize the interview, send a follow-up prompt on the same thread, or not now. Nothing is
/// sent automatically; both stay available afterwards in the replay view.
/// </summary>
public static class EndInterviewDialog
{
    /// <summary>What was chosen.</summary>
    public abstract record Choice
    {
        private Choice() { }

        /// <summary>Not now (Esc, or the window closed).</summary>
        public sealed record NotNow : Choice;

        /// <summary>Summarize the interview.</summary>
        public sealed record Summarize : Choice;

        /// <summary>A follow-up prompt (trimmed, non-empty).</summary>
        public sealed record FollowUp(string Text) : Choice;
    }

    /// <summary>
    /// Ask, then do it: the summary (<see cref="InterviewController.GenerateSummaryAsync"/>) or
    /// the follow-up (<see cref="InterviewController.SendFollowUpAsync"/>) runs after the dialog
    /// has closed, as on the Mac. Returns at once after the choice.
    /// </summary>
    /// <param name="owner">The main window.</param>
    /// <param name="interview">The interview just ended.</param>
    /// <param name="transcript">The saved transcript (the summary is built from it).</param>
    public static Choice ShowAndRun(Window owner, InterviewController interview, string transcript)
    {
        var choice = Show(owner, interview);
        switch (choice)
        {
            case Choice.Summarize:
                Ui.Fire(() => interview.GenerateSummaryAsync(transcript));
                break;
            case Choice.FollowUp(var text):
                Ui.Fire(() => interview.SendFollowUpAsync(text));
                break;
        }
        return choice;
    }

    /// <summary>Show the dialog (modal) and return the choice without acting on it.</summary>
    public static Choice Show(Window owner, InterviewController interview)
    {
        var flag = new TextBlock { Text = Glyph.Flag, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 10, 0) };
        flag.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        flag.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var title = new TextBlock { Text = "Interview ended", FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(flag);
        heading.Children.Add(title);

        var explain = new TextBlock
        {
            Text = "The transcript is saved. What would you like the coach to do?",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 16),
        };
        explain.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");

        var summarize = new Button
        {
            Content = "Summarize the interview",
            IsDefault = true,
            MinHeight = 38,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = Glyph.List,
        };
        summarize.SetResourceReference(FrameworkElement.StyleProperty, "Button.Accent");

        var followLabel = new TextBlock { Text = "Or send a follow-up prompt", FontSize = 13, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 18, 0, 6) };

        var followUp = new TextBox
        {
            MinLines = 2,
            MaxLines = 5,
            Tag = "e.g. Draft a thank-you email to the interviewer",
            ToolTip = "Enter sends · Shift+Enter adds a line",
        };
        followUp.SetResourceReference(FrameworkElement.StyleProperty, "Answers.InputBox");
        AutomationProperties.SetName(followUp, "Follow-up prompt");

        var send = new Button { Content = "Send", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };

        var note = new TextBlock
        {
            Text = "The coach never started in this interview, so there's nothing to ask.",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        note.SetResourceReference(FrameworkElement.StyleProperty, "Type.Note");
        var notNow = new Button { Content = "Not now", IsCancel = true, MinWidth = 88 };
        var bottom = new DockPanel { Margin = new Thickness(0, 22, 0, 0), LastChildFill = true };
        DockPanel.SetDock(notNow, Dock.Right);
        bottom.Children.Add(notNow);
        bottom.Children.Add(note);

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        foreach (var child in new UIElement[] { heading, explain, summarize, followLabel, followUp, send, bottom })
            panel.Children.Add(child);

        var dialog = new ChromeWindow
        {
            Title = "Interview ended",
            Content = panel,
            Owner = owner,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        AnswersTheme.Apply(dialog);

        Choice choice = new Choice.NotNow();

        void Update()
        {
            var hasThread = interview.Record?.ThreadId is not null;
            summarize.IsEnabled = hasThread;
            send.IsEnabled = hasThread && followUp.Text.Trim().Length > 0;
            note.Visibility = Ui.Shown(!hasThread);
        }

        void SendFollowUp()
        {
            var text = followUp.Text.Trim();
            if (text.Length == 0 || interview.Record?.ThreadId is null) return;
            choice = new Choice.FollowUp(text);
            dialog.DialogResult = true;
        }

        summarize.Click += (_, _) =>
        {
            choice = new Choice.Summarize();
            dialog.DialogResult = true;
        };
        send.Click += (_, _) => SendFollowUp();
        followUp.TextChanged += (_, _) => Update();
        followUp.PreviewKeyDown += (_, e) =>
        {
            // Enter sends (the Mac's onSubmit); Shift+Enter is a new line.
            if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            e.Handled = true;
            SendFollowUp();
        };

        // The thread may still be opening when the interview ends; follow it.
        EventHandler changed = (_, _) => Update();
        interview.Changed += changed;
        dialog.Closed += (_, _) => interview.Changed -= changed;
        dialog.Loaded += (_, _) => (summarize.IsEnabled ? summarize : (UIElement)notNow).Focus();

        Update();
        dialog.ShowDialog();
        return choice;
    }
}
