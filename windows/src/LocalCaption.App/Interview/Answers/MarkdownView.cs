using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LocalCaption.Interview;

namespace LocalCaption.App.Interview.Answers;

/// <summary>
/// Renders the coach's Markdown subset (macOS <c>MarkdownText.swift</c>): headings, bullets,
/// numbered items, paragraphs, fenced code, and inline bold / italic / code / strike / links —
/// as a light tree of <see cref="TextBlock"/>s in a <see cref="StackPanel"/>. No FlowDocument,
/// no RichTextBox, no new dependency.
/// </summary>
/// <remarks>
/// <para><b>Streaming.</b> Setting <see cref="Markdown"/> re-parses the text (cheap: see
/// <see cref="MarkdownBlocks"/>) and compares the new blocks with the ones on screen: blocks
/// that are equal keep their elements untouched; a changed block of the same kind is updated in
/// place; only the rest is rebuilt. While an answer streams that is normally just the last
/// paragraph or bullet. The owner views set <see cref="Markdown"/> from a 50 ms
/// <see cref="ChangeThrottle"/>, never per token.</para>
/// <para><b>Selection.</b> TextBlocks cannot be selected in WPF. Code blocks are read-only
/// TextBoxes, so code — what one most often wants to lift out — selects and copies; the card's
/// Copy answer (and this view's context menu) copy the whole Markdown.</para>
/// <para><b>Size.</b> <see cref="BaseFontSize"/> is <c>caption.font_size</c>; headings are
/// 1.15× for levels 1–2, code 0.9×, block spacing 0.35×, as on the Mac.</para>
/// </remarks>
public sealed class MarkdownView : ContentControl
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata("", (d, _) => ((MarkdownView)d).Render(full: false)));

    public static readonly DependencyProperty BaseFontSizeProperty = DependencyProperty.Register(
        nameof(BaseFontSize), typeof(double), typeof(MarkdownView),
        new PropertyMetadata(15.0, (d, _) => ((MarkdownView)d).Render(full: true)), v => v is double x && x > 0 && !double.IsInfinity(x));

    private readonly StackPanel _panel = new();
    private IReadOnlyList<MarkdownBlock> _blocks = [];

    public MarkdownView()
    {
        Focusable = false;
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Content = _panel;
        AnswersTheme.Apply(this);

        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => ClipboardWriter.Copy(Markdown);
        ContextMenu = new ContextMenu { Items = { copy } };
    }

    /// <summary>The Markdown to show.</summary>
    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Body text size in DIP (<c>caption.font_size</c>, or a fraction of it for the summary).</summary>
    public double BaseFontSize
    {
        get => (double)GetValue(BaseFontSizeProperty);
        set => SetValue(BaseFontSizeProperty, value);
    }

    private void Render(bool full)
    {
        var size = BaseFontSize;
        var blocks = MarkdownBlocks.Parse(Markdown ?? "");
        if (full)
        {
            _panel.Children.Clear();
            _blocks = [];
        }

        var keep = 0;
        while (keep < blocks.Count && keep < _blocks.Count && blocks[keep] == _blocks[keep]) keep++;

        for (var i = keep; i < blocks.Count; i++)
        {
            if (i < _panel.Children.Count && i < _blocks.Count && TryUpdate(_panel.Children[i], _blocks[i], blocks[i], size))
                continue;
            var element = Build(blocks[i], size);
            element.Margin = new Thickness(element.Margin.Left, i == 0 ? 0 : element.Margin.Top + size * 0.35, 0, 0);
            if (i < _panel.Children.Count)
            {
                _panel.Children.RemoveAt(i);
                _panel.Children.Insert(i, element);
            }
            else
            {
                _panel.Children.Add(element);
            }
        }
        while (_panel.Children.Count > blocks.Count) _panel.Children.RemoveAt(_panel.Children.Count - 1);
        _blocks = blocks;
    }

    /// <summary>Same kind of block, new text: change the element in place (keeps it, its margin and its layout slot).</summary>
    private static bool TryUpdate(UIElement element, MarkdownBlock old, MarkdownBlock now, double size)
    {
        switch (old, now)
        {
            case (MarkdownBlock.Paragraph, MarkdownBlock.Paragraph(var text)) when element is TextBlock tb:
                SetInlines(tb, text);
                return true;
            case (MarkdownBlock.Heading(_, var a), MarkdownBlock.Heading(var text, var b)) when a == b && element is TextBlock tb:
                SetInlines(tb, text);
                return true;
            case (MarkdownBlock.Bullet(_, var a), MarkdownBlock.Bullet(var text, var b)) when a == b:
                return UpdateItem(element, text);
            case (MarkdownBlock.Numbered(_, var a), MarkdownBlock.Numbered(var text, var b)) when a == b:
                return UpdateItem(element, text);
            case (MarkdownBlock.Code, MarkdownBlock.Code(var text)) when element is Border { Child: TextBox box }:
                // Keeps any selection the user has made in the code so far.
                if (box.Text != text) box.Text = text;
                return true;
            default:
                return false;
        }
    }

    private static bool UpdateItem(UIElement element, string text)
    {
        if (element is not Grid { Children.Count: 2 } grid || grid.Children[1] is not TextBlock body) return false;
        SetInlines(body, text);
        return true;
    }

    private static FrameworkElement Build(MarkdownBlock block, double size)
    {
        switch (block)
        {
            case MarkdownBlock.Heading(var text, var level):
            {
                var tb = Text(text, size * (level <= 2 ? 1.15 : 1.0));
                tb.FontWeight = FontWeights.SemiBold;
                tb.Margin = new Thickness(0, size * 0.3, 0, 0);
                return tb;
            }
            case MarkdownBlock.Bullet(var text, var indent):
                return Item("•", text, size, indent * size);
            case MarkdownBlock.Numbered(var text, var number):
                return Item(number + ".", text, size, 0);
            case MarkdownBlock.Code(var text):
            {
                var box = new TextBox { Text = text, FontSize = size * 0.9 };
                box.SetResourceReference(StyleProperty, "Answers.ReadOnlyText");
                box.SetResourceReference(FontFamilyProperty, "Font.Mono");
                AutomationProperties.SetName(box, "Code");
                var border = new Border
                {
                    Child = box,
                    Padding = new Thickness(8),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                };
                border.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
                border.SetResourceReference(Border.BorderBrushProperty, "Line");
                return border;
            }
            case MarkdownBlock.Paragraph(var text):
                return Text(text, size);
            default:
                return new TextBlock();
        }
    }

    /// <summary>A bullet or numbered item: the marker in its own column, the text wrapping beside it.</summary>
    private static Grid Item(string marker, string text, double size, double indent)
    {
        var grid = new Grid { Margin = new Thickness(indent, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mark = new TextBlock { Text = marker, FontSize = size, Margin = new Thickness(0, 0, 6, 0) };
        Typography.SetNumeralAlignment(mark, FontNumeralAlignment.Tabular);
        var body = Text(text, size);
        Grid.SetColumn(body, 1);
        grid.Children.Add(mark);
        grid.Children.Add(body);
        return grid;
    }

    private static TextBlock Text(string markdown, double size)
    {
        var tb = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap };
        SetInlines(tb, markdown);
        return tb;
    }

    private static void SetInlines(TextBlock tb, string markdown)
    {
        var runs = MarkdownBlocks.Inline(markdown);
        tb.Inlines.Clear();
        foreach (var run in runs) tb.Inlines.Add(ToInline(run, tb.FontSize));
    }

    private static Inline ToInline(MarkdownRun run, double size)
    {
        var text = new Run(run.Text);
        if (run.Style.HasFlag(MarkdownStyle.Bold)) text.FontWeight = FontWeights.SemiBold;
        if (run.Style.HasFlag(MarkdownStyle.Italic)) text.FontStyle = FontStyles.Italic;
        if (run.Style.HasFlag(MarkdownStyle.Strike)) text.TextDecorations = TextDecorations.Strikethrough;
        if (run.Style.HasFlag(MarkdownStyle.Code))
        {
            text.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
            text.SetResourceReference(TextElement.BackgroundProperty, "Bg.Hover");
            text.FontSize = size * 0.92;
        }
        if (!run.Style.HasFlag(MarkdownStyle.Link)) return text;

        // Links open in the browser, http(s) only — as SwiftUI's Text does on the Mac.
        var link = new Hyperlink(text) { Focusable = true };
        link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
        if (Uri.TryCreate(run.Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            link.NavigateUri = uri;
            link.ToolTip = uri.AbsoluteUri;
            link.RequestNavigate += (_, args) =>
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Trace.WriteLine($"[answers] could not open a link: {ex.Message}");
                }
                args.Handled = true;
            };
        }
        return link;
    }
}
