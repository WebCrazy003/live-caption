using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;

namespace LocalCaption.App.Interview.Sessions;

/// <summary>
/// A saved session's captions, read-only and selectable. AvalonEdit, like the live
/// <see cref="CaptionTextView"/>, because a three-hour transcript is tens of thousands of lines
/// and AvalonEdit only lays out what is on screen.
/// </summary>
/// <remarks>
/// Not <see cref="CaptionTextView"/> itself: that one follows the end of a growing document and
/// dims a provisional tail, neither of which a finished transcript has.
/// </remarks>
public sealed class TranscriptTextView : TextEditor
{
    public TranscriptTextView()
    {
        IsReadOnly = true;
        WordWrap = true;
        ShowLineNumbers = false;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0, 4, 0, 4);
        FontFamily = new FontFamily("Segoe UI");
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.AllowScrollBelowDocument = false;
        Options.HighlightCurrentLine = false;
        TextArea.SelectionForeground = null;
        TextArea.SelectionBorder = null;
        TextArea.SelectionCornerRadius = 2;
        // Read-only: the caret would only suggest typing does something.
        TextArea.Caret.CaretBrush = Brushes.Transparent;

        Loaded += (_, _) =>
        {
            ApplyTheme();
            ThemeManager.Changed += ApplyTheme;
        };
        Unloaded += (_, _) => ThemeManager.Changed -= ApplyTheme;
    }

    /// <summary>Replace the whole text (a different session). Scrolls to the top.</summary>
    public void Show(string text)
    {
        Document = new TextDocument(text);
        if (IsLoaded) ScrollToHome();
    }

    /// <summary>Foreground follows the palette; the selection brush has to be re-read.</summary>
    public void ApplyTheme()
    {
        SetResourceReference(ForegroundProperty, "Text");
        if (TryFindResource("Selection") is Brush selection) TextArea.SelectionBrush = selection;
        TextArea.TextView.Redraw();
    }
}
