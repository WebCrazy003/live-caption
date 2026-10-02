using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LocalCaption.Core.Interview;
using LocalCaption.Interview;

namespace LocalCaption.App.Interview.Answers;

/// <summary>
/// A finished interview (SPEC-16 §5.5; Mac <c>InterviewReplayView</c>). Interactive right
/// after End interview (follow-up bar; cards follow new answers), read-only from the Sessions
/// window. The summary can be (re)generated in both while the interview has a thread, as on
/// the Mac.
/// </summary>
public partial class InterviewReplayView : UserControl
{
    /// <summary>Below this width (DIP) the panes become a Conversation / Transcript switch.</summary>
    public const double TwoPaneMinWidth = 700;

    private static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(1.2);

    private readonly bool _ready;
    private readonly ChangeThrottle _throttle;
    private readonly DispatcherTimer _copiedTimer;
    private readonly List<AnswerCard> _cards = [];
    private InterviewController? _controller;
    private bool _attached;
    private InterviewRecord? _shownRecord;
    private HashSet<int>? _expanded;   // null: the default — every turn but skill steps
    private bool? _twoPanes;
    private int _lastTurnCount = -1;
    private int _lastAnswerLength;
    private double _fontSize = 18;
    private string _transcript = "";

    public InterviewReplayView()
    {
        AnswersTheme.Apply(this);      // before InitializeComponent: see AnswersTheme.xaml
        InitializeComponent();
        _throttle = new ChangeThrottle(Refresh, Dispatcher);
        _copiedTimer = new DispatcherTimer { Interval = CopiedFor };
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            CopyQaButton.Content = "Copy Q&A";
            CopyQaButton.Tag = Glyph.Copy;
        };
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        Body.SizeChanged += (_, _) => ApplyBodyLayout(force: false);
        _ready = true;
        ApplyBodyLayout(force: true);
    }

    // ── Host API ─────────────────────────────────────────────────────────────────────────

    /// <summary>The interview: the live controller after End interview, or one built with <c>existing:</c> for history.</summary>
    public InterviewController? Controller
    {
        get => _controller;
        set
        {
            if (ReferenceEquals(_controller, value)) return;
            Detach();
            _controller = value;
            _shownRecord = null;
            if (IsLoaded) Attach();
            Refresh();
        }
    }

    /// <summary>Right after End interview: the follow-up bar, and the list follows new answers.</summary>
    public bool Interactive
    {
        get;
        set
        {
            field = value;
            _throttle.Request();
        }
    }

    /// <summary>The saved transcript: shown in the Transcript pane and sent with Summarize interview.</summary>
    public string Transcript
    {
        get => _transcript;
        set
        {
            _transcript = value ?? "";
            ShowSource();
        }
    }

    /// <summary>
    /// Whether to offer Summarize interview (default: always). Asked on every repaint and again
    /// on the click, so it follows the app's mode: the host returns false in Caption only mode,
    /// where summarizing — which resumes the thread and so starts Codex — must not happen
    /// (SPEC-16 §4.1, §9.1). Call <see cref="Requery"/> when the answer may have changed.
    /// </summary>
    public Func<bool> AllowSummarize
    {
        get;
        set
        {
            field = value ?? (() => true);
            _throttle.Request();
        }
    } = () => true;

    /// <summary>Re-read <see cref="AllowSummarize"/> (the mode changed) and repaint.</summary>
    public void Requery() => _throttle.Request();

    /// <summary><c>caption.font_size</c>: answers at this size, the summary at 0.85×, the transcript at 0.9×.</summary>
    public double AnswerFontSize
    {
        get => _fontSize;
        set
        {
            if (_fontSize.Equals(value) || value <= 0) return;
            _fontSize = value;
            SourceText.FontSize = value * 0.9;
            _throttle.Request();
        }
    }

    // ── Wiring ───────────────────────────────────────────────────────────────────────────

    private void Attach()
    {
        if (_attached || _controller is null) return;
        _controller.Changed += OnChanged;
        _attached = true;
        _throttle.Request();
    }

    private void Detach()
    {
        _throttle.Cancel();
        if (!_attached || _controller is null) return;
        _controller.Changed -= OnChanged;
        _attached = false;
    }

    private void OnChanged(object? sender, EventArgs e) => _throttle.Request();

    // ── Refresh ──────────────────────────────────────────────────────────────────────────

    private void Refresh()
    {
        if (_controller is not { } c)
        {
            IsEnabled = false;
            return;
        }
        IsEnabled = true;

        if (!ReferenceEquals(c.Record, _shownRecord))
        {
            _shownRecord = c.Record;
            _expanded = null;
            _lastTurnCount = -1;
            ShowSource();
        }

        RefreshToolbar(c);
        RefreshSummary(c);
        RefreshConversation(c);
        RefreshFollowUp(c);
    }

    private void RefreshToolbar(InterviewController c)
    {
        StateIcon.Text = Interactive ? Glyph.Flag : Glyph.Lock;
        StateText.Text = Interactive ? "Interview ended" : "Read-only";
        ProfileText.Visibility = Ui.Shown(c.ActiveProfile is not null);
        ProfileText.Text = c.ActiveProfile is { } p ? "Profile: " + p.Label() : "";
        CopyQaButton.IsEnabled = c.Turns.Count > 0;
    }

    private void RefreshSummary(InterviewController c)
    {
        var status = c.Record?.Summary.Status;
        var hasThread = c.Record?.ThreadId is not null;
        var text = c.SummaryText;
        var error = c.SummaryError;
        var writing = c.Summarizing;

        SummarySection.Visibility = Ui.Shown(text.Length > 0 || writing || error is not null || hasThread);

        SummarizeButton.Visibility = Ui.Shown(AllowSummarize() && hasThread && (status != InterviewStatus.Done || text.Length == 0));
        SummarizeButton.IsEnabled = !writing;

        var open = SummaryToggle.IsChecked == true;
        SummaryBody.Visibility = Ui.Shown(open);
        SummaryChevron.Text = open ? Glyph.ChevronDown : Glyph.ChevronRight;

        SummaryWriting.Visibility = Ui.Shown(writing && text.Length == 0);
        SummaryError.Visibility = Ui.Shown(error is not null);
        SummaryErrorText.Text = error ?? "";
        SummaryScroll.Visibility = Ui.Shown(text.Length > 0);
        SummaryMarkdown.BaseFontSize = _fontSize * 0.85;
        if (text.Length > 0) SummaryMarkdown.Markdown = text;
        SummaryEmpty.Visibility = Ui.Shown(text.Length == 0 && !writing && error is null);
        SummaryEmpty.Text = status == InterviewStatus.Skipped
            ? "No summary — summarizing is off in Settings."
            : "No summary yet.";
    }

    private void RefreshConversation(InterviewController c)
    {
        var briefing = c.LegacyBriefing;
        BriefingBox.Visibility = Ui.Shown(briefing.Length > 0);
        if (briefing.Length > 0)
        {
            BriefingMarkdown.BaseFontSize = _fontSize * 0.8;
            BriefingMarkdown.Markdown = briefing;
        }

        var turns = c.Turns;
        NoQuestions.Visibility = Ui.Shown(turns.Count == 0);
        var atBottom = ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset < 32;

        while (_cards.Count < turns.Count)
        {
            var card = new AnswerCard { Margin = new Thickness(0, 0, 0, 10), ImageSource = name => _controller?.Image(name) };
            card.ToggleRequested += OnCardToggle;
            _cards.Add(card);
            CardList.Children.Add(card);
        }
        while (_cards.Count > turns.Count)
        {
            var card = _cards[^1];
            card.ToggleRequested -= OnCardToggle;
            _cards.RemoveAt(_cards.Count - 1);
            CardList.Children.Remove(card);
        }

        var open = OpenSet(turns);
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            var streaming = c.StreamingTurn == turn.N;
            // No card is "latest" here: nothing is tinted and nothing regenerates (Mac: isLatest false, regenerate nil).
            _cards[i].Show(turn, isLatest: false, isExpanded: open.Contains(turn.N) || streaming,
                           isStreaming: streaming, canRegenerate: false, fontSize: _fontSize);
        }

        var lastLength = turns.Count == 0 ? 0 : turns[^1].Answer.Length;
        if (Interactive && (turns.Count != _lastTurnCount && _lastTurnCount >= 0 || lastLength != _lastAnswerLength && atBottom))
            ConversationScroll.ScrollToEnd();
        _lastTurnCount = turns.Count;
        _lastAnswerLength = lastLength;
    }

    private HashSet<int> OpenSet(IReadOnlyList<InterviewTurn> turns) =>
        _expanded ?? turns.Where(t => t.Kind != InterviewTurnKind.Skill).Select(t => t.N).ToHashSet();

    private void OnCardToggle(object? sender, EventArgs e)
    {
        if (sender is not AnswerCard { Turn: { } turn } || _controller is not { } c) return;
        var set = OpenSet(c.Turns);
        if (!set.Remove(turn.N)) set.Add(turn.N);
        _expanded = set;
        _throttle.Request();
    }

    private void RefreshFollowUp(InterviewController c)
    {
        FollowUpBar.Visibility = Ui.Shown(Interactive);
        var hasThread = c.Record?.ThreadId is not null;
        FollowUpBox.IsEnabled = hasThread;
        FollowUpSend.IsEnabled = hasThread && !c.IsStreaming && FollowUpBox.Text.Trim().Length > 0;
    }

    // ── Panes ────────────────────────────────────────────────────────────────────────────

    private void ApplyBodyLayout(bool force)
    {
        if (!_ready) return;
        var width = Body.ActualWidth > 0 ? Body.ActualWidth : ActualWidth;
        var two = width >= TwoPaneMinWidth;
        if (two == _twoPanes && !force) return;
        _twoPanes = two;

        BodyGrid.ColumnDefinitions.Clear();
        PaneSwitch.Visibility = Ui.Shown(!two);
        BodySplitter.Visibility = Ui.Shown(two);
        BodySplitLine.Visibility = Ui.Shown(two);
        if (two)
        {
            // Transcript about 45 % (min 220), conversation the rest (min 300).
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.45, GridUnitType.Star), MinWidth = 220 });
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.55, GridUnitType.Star), MinWidth = 300 });
            Grid.SetColumn(SourcePane, 0);
            Grid.SetColumn(BodySplitLine, 1);
            Grid.SetColumn(BodySplitter, 1);
            Grid.SetColumn(ConversationPane, 2);
            SourcePane.Visibility = Visibility.Visible;
            ConversationPane.Visibility = Visibility.Visible;
        }
        else
        {
            Grid.SetColumn(SourcePane, 0);
            Grid.SetColumn(ConversationPane, 0);
            ShowNarrowPane();
        }
    }

    private void ShowNarrowPane()
    {
        if (_twoPanes != false) return;
        var conversation = ConversationTab.IsChecked == true;
        ConversationPane.Visibility = Ui.Shown(conversation);
        SourcePane.Visibility = Ui.Shown(!conversation);
    }

    private void OnPaneSwitch(object sender, RoutedEventArgs e)
    {
        if (_ready) ShowNarrowPane();
    }

    private void OnSourceSwitch(object sender, RoutedEventArgs e)
    {
        if (_ready) ShowSource();
    }

    /// <summary>Transcript, CV or JD in the left pane; a muted line when there is none.</summary>
    private void ShowSource()
    {
        if (!_ready) return;
        var c = _controller;
        string text, empty, name;
        if (SourceCv.IsChecked == true)
        {
            (text, empty, name) = (c?.CvText ?? "", "No CV was used in this interview.", "CV");
        }
        else if (SourceJd.IsChecked == true)
        {
            (text, empty, name) = (c?.JdText ?? "", "No job description was pasted.", "Job description");
        }
        else
        {
            (text, empty, name) = (_transcript, "No transcript.", "Transcript");
        }

        var title = SourceCv.IsChecked == true ? c?.CvTitle : null;
        CvTitleText.Visibility = Ui.Shown(title is not null);
        CvTitleText.Text = title ?? "";

        SourceText.Text = text.Length == 0 ? empty : text;
        SourceText.SetResourceReference(ForegroundProperty, text.Length == 0 ? "Text.Muted" : "Text");
        AutomationProperties.SetName(SourceText, name);
        SourceText.ScrollToHome();
    }

    // ── Actions ──────────────────────────────────────────────────────────────────────────

    private void OnCopyQa(object sender, RoutedEventArgs e)
    {
        if (_controller?.Record is not { } record) return;
        if (!ClipboardWriter.Copy(record.QaMarkdown())) return;
        CopyQaButton.Content = "Copied";
        CopyQaButton.Tag = Glyph.Check;
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private void OnSummaryToggle(object sender, RoutedEventArgs e) => _throttle.Request();

    private void OnSummarize(object sender, RoutedEventArgs e)
    {
        if (!AllowSummarize())
        {
            _throttle.Request();      // the mode changed under the button: hide it
            return;
        }
        if (_controller is { } c) Ui.Fire(() => c.GenerateSummaryAsync(_transcript));
    }

    private void OnFollowUpChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready && _controller is { } c) RefreshFollowUp(c);
    }

    private void OnFollowUpKey(object sender, KeyEventArgs e)
    {
        // Enter sends (the Mac's onSubmit); Shift+Enter is a new line.
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        SendFollowUp();
    }

    private void OnFollowUpSend(object sender, RoutedEventArgs e) => SendFollowUp();

    private void SendFollowUp()
    {
        if (_controller is not { } c || c.Record?.ThreadId is null || c.IsStreaming) return;
        var text = FollowUpBox.Text.Trim();
        if (text.Length == 0) return;
        FollowUpBox.Clear();
        Ui.Fire(() => c.SendFollowUpAsync(text));
    }
}
