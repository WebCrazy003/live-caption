using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalCaption.Core.Data;
using LocalCaption.Core.Transcripts;
using LocalCaption.Session;

namespace LocalCaption.App;

/// <summary>
/// The launch-time recovery prompt, one row per leftover journal (§9.4, specs/SPEC-16 C1).
/// </summary>
/// <remarks>
/// <para>Port of the Mac's <c>LC/UI/RecoveryView.swift</c>: each unsaved session with its start
/// time and segment count, <b>Recover &amp; Save</b> or <b>Discard</b> per row, and
/// <b>Discard All</b>. It replaces an all-or-nothing question whose only "no" left every journal
/// to come back at the next launch — there was no way to throw an unwanted one away.</para>
/// <para>One Windows addition: <b>Not now</b> (and Esc) leaves whatever is left for the next
/// launch, as before, because this runs before the main window and a dialog that cannot be
/// put off would hold the whole app hostage. Discard All asks first; a single Discard is
/// already a deliberate click on one named row.</para>
/// </remarks>
public static class RecoveryDialog
{
    public static void Show(AppEnvironment env)
    {
        if (env.PendingRecoveries.Count == 0) return;

        var heading = new TextBlock
        {
            Text = "Recover unsaved sessions",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        };
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");

        var explain = new TextBlock
        {
            Text = "These sessions didn't stop cleanly (the app quit or crashed mid-recording). " +
                   "Their captions were journaled to disk and can be saved as transcripts.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        };

        var rows = new StackPanel();
        var list = new ScrollViewer
        {
            Content = rows,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        var listFrame = new Border { Child = list, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4) };
        listFrame.SetResourceReference(Border.BorderBrushProperty, "Line");
        listFrame.SetResourceReference(Border.BackgroundProperty, "Bg.Surface");

        var discardAll = new Button { Content = "Discard All", MinWidth = 110 };
        discardAll.SetResourceReference(FrameworkElement.StyleProperty, "Button.Danger");
        var later = new Button { Content = "Not now", IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
        later.ToolTip = "Keep them; Local Caption offers them again at the next launch";

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        DockPanel.SetDock(discardAll, Dock.Left);
        DockPanel.SetDock(later, Dock.Right);
        footer.Children.Add(discardAll);
        footer.Children.Add(later);

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        foreach (var child in new UIElement[] { heading, explain, listFrame, footer }) panel.Children.Add(child);

        var dialog = new ChromeWindow
        {
            Title = "Recover unsaved sessions",
            Content = panel,
            Width = 540,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            // Shown before the main window exists, so it stands on its own in the taskbar.
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        void CloseIfDone()
        {
            if (env.PendingRecoveries.Count == 0) dialog.DialogResult = true;
        }

        foreach (var pending in env.PendingRecoveries.ToList())
            rows.Children.Add(Row(env, pending, rows, CloseIfDone));

        discardAll.Click += (_, _) =>
        {
            var count = env.PendingRecoveries.Count;
            if (count == 0) return;
            if (!ConfirmDialog.Ask(dialog, "Discard All",
                    count == 1
                        ? "Delete the unsaved session's captions? This cannot be undone."
                        : $"Delete the captions of all {count} unsaved sessions? This cannot be undone.",
                    "Discard All", "Cancel"))
                return;

            foreach (var pending in env.PendingRecoveries.ToList()) env.Discard(pending);
            rows.Children.Clear();
            CloseIfDone();
        };

        // Keyboard focus starts on the first Recover & Save. There is no default button, so a
        // stray Enter on its own can never throw a session away.
        dialog.Loaded += (_, _) =>
        {
            if (rows.Children.OfType<FrameworkElement>().Select(r => r.Tag).OfType<Button>().FirstOrDefault() is { } first)
                Keyboard.Focus(first);
        };

        dialog.ShowDialog();
    }

    /// <summary>One journal: when it started, how many segments, and its two buttons.</summary>
    private static FrameworkElement Row(AppEnvironment env, RecoveredSession pending, Panel rows, Action changed)
    {
        var when = pending.StartedAt is { } at ? TimeFormat.Human(at.ToLocalTime()) : "Unknown time";
        var count = pending.Segments.Count;

        var time = new TextBlock { Text = when, FontSize = 13.5 };
        var segments = new TextBlock { Text = $"{count} segment{(count == 1 ? "" : "s")}", FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
        segments.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0), Visibility = Visibility.Collapsed };
        problem.SetResourceReference(TextBlock.ForegroundProperty, "Caution");

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(time);
        text.Children.Add(segments);
        text.Children.Add(problem);

        var discard = new Button { Content = "Discard", MinWidth = 84 };
        discard.SetResourceReference(FrameworkElement.StyleProperty, "Button.Danger");
        AutomationProperties(discard, $"Discard the session from {when}");
        var recover = new Button { Content = "Recover & Save", MinWidth = 120, Margin = new Thickness(8, 0, 0, 0) };
        recover.SetResourceReference(FrameworkElement.StyleProperty, "Button.Accent");
        AutomationProperties(recover, $"Recover and save the session from {when}");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        buttons.Children.Add(discard);
        buttons.Children.Add(recover);

        var grid = new Grid { Margin = new Thickness(10, 8, 8, 8), Tag = recover };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(text);
        grid.Children.Add(buttons);

        discard.Click += (_, _) =>
        {
            env.Discard(pending);
            rows.Children.Remove(grid);
            changed();
        };
        recover.Click += (_, _) =>
        {
            if (!env.Recover(pending))
            {
                // The journal is kept for another try (AppEnvironment.Recover), and so is the row.
                problem.Text = "Could not be saved — the database could not be written. It has been kept for another try.";
                problem.Visibility = Visibility.Visible;
                return;
            }
            rows.Children.Remove(grid);
            changed();
        };

        return grid;
    }

    private static void AutomationProperties(Button button, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(button, name);
}
