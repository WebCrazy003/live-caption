using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LocalCaption.Core.Interview;

namespace LocalCaption.App.Interview.Answers;

/// <summary>
/// One turn of the interview (SPEC-16 §5.4; Mac <c>AnswerCard</c> in <c>InterviewPanel.swift</c>),
/// shared by the live Answers panel and the read-only replay.
/// </summary>
/// <remarks>
/// The controller mutates its <see cref="InterviewTurn"/> objects in place while an answer
/// streams, so the card holds no copy of the turn's state: the owner calls <see cref="Show"/>
/// on every (coalesced) refresh and the card re-reads the turn, touching only what changed.
/// </remarks>
public partial class AnswerCard : UserControl
{
    private static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(1.2);

    private readonly DispatcherTimer _copiedTimer;
    private DateTime _sentClosedAt = DateTime.MinValue;
    private string? _badgeKey;

    public AnswerCard()
    {
        AnswersTheme.Apply(this);      // before InitializeComponent: see AnswersTheme.xaml
        InitializeComponent();
        _copiedTimer = new DispatcherTimer { Interval = CopiedFor };
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            CopyButton.Tag = Glyph.Copy;
        };
    }

    /// <summary>The card was clicked open or shut (the owner keeps the expanded set).</summary>
    public event EventHandler? ToggleRequested;

    /// <summary>Regenerate was pressed (latest card only).</summary>
    public event EventHandler? RegenerateRequested;

    /// <summary>Loads a stored screenshot by name — <c>InterviewController.Image</c>.</summary>
    public Func<string, byte[]?>? ImageSource { get; set; }

    /// <summary>The turn shown.</summary>
    public InterviewTurn? Turn { get; private set; }

    /// <summary>Show <paramref name="turn"/> as it is now.</summary>
    /// <param name="turn">The turn (read, never changed).</param>
    /// <param name="isLatest">The newest card: tinted, never collapsible, the only one that may regenerate.</param>
    /// <param name="isExpanded">Show the answer rather than the one-line summary.</param>
    /// <param name="isStreaming">This turn's answer is streaming now.</param>
    /// <param name="canRegenerate">Regenerate is offered here (the live panel, not a skill turn).</param>
    /// <param name="fontSize">Answer text size (<c>caption.font_size</c>).</param>
    public void Show(InterviewTurn turn, bool isLatest, bool isExpanded, bool isStreaming, bool canRegenerate, double fontSize)
    {
        Turn = turn;
        LatestTint.Visibility = Ui.Shown(isLatest);

        CollapsedRow.Visibility = Ui.Shown(!isExpanded);
        ExpandedBody.Visibility = Ui.Shown(isExpanded);
        UpdateBadge(turn);

        if (!isExpanded)
        {
            var summary = TurnText.SummaryLine(turn);
            SummaryText.Text = summary;
            AutomationProperties.SetName(CollapsedRow, summary);
            CollapsedRow.ToolTip = summary.Length > 60 ? summary : null;
            return;
        }

        CollapseRow.Visibility = Ui.Shown(!isLatest);

        var showQuestion = turn.Kind != InterviewTurnKind.Ask;
        QuestionText.Visibility = Ui.Shown(showQuestion);
        if (showQuestion) QuestionText.Text = TurnText.KindLabel(turn.Kind) + turn.Question;

        var thinking = turn.Answer.Length == 0 && isStreaming;
        ThinkingRow.Visibility = Ui.Shown(thinking);
        Answer.Visibility = Ui.Shown(!thinking);
        Answer.BaseFontSize = fontSize;
        if (!thinking) Answer.Markdown = turn.Answer;

        TtftText.Visibility = Ui.Shown(turn.TtftMs is not null);
        if (turn.TtftMs is { } ttft) TtftText.Text = TurnText.Ttft(ttft);

        RegenerateButton.Visibility = Ui.Shown(isLatest && !isStreaming && canRegenerate && turn.Kind != InterviewTurnKind.Skill);
        CopyButton.IsEnabled = turn.Answer.Length > 0;
    }

    /// <summary>Interrupted (orange) or Failed with its reason (red); nothing while streaming or completed.</summary>
    private void UpdateBadge(InterviewTurn turn)
    {
        var key = turn.Status switch
        {
            InterviewTurnStatus.Interrupted => "i",
            InterviewTurnStatus.Failed => "f:" + turn.Error,
            _ => "",
        };
        if (key == _badgeKey) return;
        _badgeKey = key;
        Fill(CollapsedBadge, turn, maxWidth: 220);
        Fill(ExpandedBadge, turn, maxWidth: double.PositiveInfinity);
    }

    private static void Fill(StackPanel host, InterviewTurn turn, double maxWidth)
    {
        host.Children.Clear();
        var (glyph, text, brush) = turn.Status switch
        {
            InterviewTurnStatus.Interrupted => (Glyph.Pause, "Interrupted", "Caution"),
            InterviewTurnStatus.Failed => (Glyph.Warning, turn.Error ?? "Failed", "Danger"),
            _ => ((string?)null, (string?)null, (string?)null),
        };
        host.Visibility = Ui.Shown(text is not null);
        if (text is null) return;

        var icon = new TextBlock { Text = glyph, FontSize = 11, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 5, 0) };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        icon.SetResourceReference(TextBlock.ForegroundProperty, brush!);
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = 16,
            MaxHeight = 32,
            MaxWidth = maxWidth,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, brush!);
        host.Children.Add(icon);
        host.Children.Add(label);
        AutomationProperties.SetName(host, text);
        host.ToolTip = text.Length > 40 ? text : null;
    }

    private void OnToggle(object sender, RoutedEventArgs e) => ToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnRegenerate(object sender, RoutedEventArgs e) => RegenerateRequested?.Invoke(this, EventArgs.Empty);

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Turn is not { Answer.Length: > 0 } turn) return;
        if (!ClipboardWriter.Copy(turn.Answer)) return;
        CopyButton.Tag = Glyph.Check;
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    // ── What was sent ────────────────────────────────────────────────────────────────────

    private void OnSent(object sender, RoutedEventArgs e)
    {
        // The click that closed the popup (StaysOpen=False) arrives here as well: don't reopen.
        if (SentPopup.IsOpen || DateTime.UtcNow - _sentClosedAt < TimeSpan.FromMilliseconds(250))
        {
            SentPopup.IsOpen = false;
            return;
        }
        SentPopup.IsOpen = true;
    }

    private void OnSentOpened(object? sender, EventArgs e)
    {
        if (Turn is not { } turn) return;
        SentText.Text = turn.Question;
        SentImages.Children.Clear();
        foreach (var name in turn.Images)
        {
            if (PngImages.Decode(ImageSource?.Invoke(name), 960) is not { } bitmap) continue;
            var image = new Image
            {
                Source = bitmap,
                MaxHeight = 160,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0),
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            AutomationProperties.SetName(image, "Screenshot");
            SentImages.Children.Add(image);
        }
        // Keyboard users land in the popup (Esc closes it); the text can be selected from here.
        SentText.Focus();
    }

    private void OnSentClosed(object? sender, EventArgs e)
    {
        _sentClosedAt = DateTime.UtcNow;
        SentImages.Children.Clear();   // the decoded screenshots are not kept
    }

    private void OnSentKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SentPopup.IsOpen = false;
        SentButton.Focus();
        e.Handled = true;
    }
}
