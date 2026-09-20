using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace LocalCaption.App;

/// <summary>
/// The live caption surface: one document, append-only updates, a dimmed provisional tail.
/// </summary>
/// <remarks>
/// <para><b>SPEC-WINDOWS.md §7.2 — "the one UI risk".</b> The macOS build uses a single
/// <c>NSTextView</c> with suffix-only replacement so committed text is never re-laid-out and
/// a selection survives every update. This is the same contract on AvalonEdit: replace only
/// from the first differing offset, never rebuild.</para>
/// <para>Rebuilding the document per update would drop the selection and, over a
/// three-hour session, re-lay-out tens of thousands of lines several times a second.</para>
/// </remarks>
public sealed class CaptionTextView : TextEditor
{
    private readonly ProvisionalColorizer _provisional = new();
    private int _committedLength = -1;
    private bool _updating;

    public CaptionTextView()
    {
        IsReadOnly = true;
        WordWrap = true;
        ShowLineNumbers = false;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.AllowScrollBelowDocument = false;
        TextArea.TextView.LineTransformers.Add(_provisional);
        TextArea.SelectionChanged += (_, _) =>
        {
            // A selection means the user is reading something older. Following would yank
            // the view away from it.
            if (!_updating && SelectionLength > 0) Following = false;
        };
        TextArea.TextView.ScrollOffsetChanged += (_, _) => OnScrolled();
    }

    /// <summary>Whether the view is tracking the end of the transcript.</summary>
    public bool Following { get; private set; } = true;

    /// <summary>Raised when following starts or stops, so the "Jump to latest" button can appear.</summary>
    public event Action<bool>? FollowingChanged;

    /// <summary>Live-applied from Settings (§10).</summary>
    public bool AutoScroll { get; set; } = true;

    /// <summary>
    /// Show the committed paragraphs, the paragraph being built, and the provisional tail.
    /// </summary>
    public void Update(IReadOnlyList<string> paragraphs, string current, string hypothesis)
    {
        var committed = string.Join("\n\n", current.Length == 0 ? paragraphs : [.. paragraphs, current]);
        var separator = hypothesis.Length == 0 || committed.Length == 0
            ? ""
            : current.Length == 0 ? "\n\n" : " ";
        var full = committed + separator + hypothesis;

        var old = Document.Text;
        if (old == full && _committedLength == committed.Length) return;

        _updating = true;
        try
        {
            var selectionStart = SelectionStart;
            var selectionLength = SelectionLength;
            var shouldFollow = AutoScroll && Following && selectionLength == 0;

            if (old != full)
            {
                // Only the changed suffix. Everything before it keeps its layout, and a
                // selection that lies entirely within it is untouched.
                var prefix = CommonPrefix(old, full);
                Document.Replace(prefix, Document.TextLength - prefix, full[prefix..]);
            }

            _committedLength = committed.Length;
            _provisional.ProvisionalStart = committed.Length + separator.Length;
            TextArea.TextView.Redraw();

            if (selectionLength > 0 && selectionStart + selectionLength <= Document.TextLength)
                Select(selectionStart, selectionLength);

            if (shouldFollow) ScrollToEnd();
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Resume following, from the "Jump to latest" button.</summary>
    public void JumpToLatest()
    {
        Select(Document.TextLength, 0);
        ScrollToEnd();
        SetFollowing(true);
    }

    /// <summary>Start a new session with an empty document.</summary>
    public void Reset()
    {
        _updating = true;
        Document.Text = "";
        _committedLength = -1;
        _provisional.ProvisionalStart = 0;
        _updating = false;
        SetFollowing(true);
    }

    private void OnScrolled()
    {
        if (_updating) return;
        var view = TextArea.TextView;
        var atBottom = view.VerticalOffset >= view.DocumentHeight - view.ActualHeight - 8;
        SetFollowing(atBottom && SelectionLength == 0);
    }

    private void SetFollowing(bool value)
    {
        if (Following == value) return;
        Following = value;
        FollowingChanged?.Invoke(value);
    }

    private static int CommonPrefix(string a, string b)
    {
        var limit = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < limit && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>
    /// Dims everything past the committed text, so the provisional tail reads as provisional.
    /// </summary>
    /// <remarks>
    /// A colorizer rather than a document edit: the tail changes several times a second and
    /// styling it through the document would make every update a structural change.
    /// </remarks>
    private sealed class ProvisionalColorizer : DocumentColorizingTransformer
    {
        private readonly Brush _dim = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8E));

        public int ProvisionalStart { get; set; }

        public ProvisionalColorizer() => _dim.Freeze();

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Offset + line.Length <= ProvisionalStart) return;
            var from = Math.Max(line.Offset, ProvisionalStart);
            if (from >= line.Offset + line.Length) return;
            ChangeLinePart(from, line.Offset + line.Length, element => element.TextRunProperties.SetForegroundBrush(_dim));
        }
    }
}
