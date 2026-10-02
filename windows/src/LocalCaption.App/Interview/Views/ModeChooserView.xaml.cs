using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LocalCaption.Core.Interview;

namespace LocalCaption.App.Interview.Views;

/// <summary>
/// The first screen (SPEC-16 §5.1, Mac <c>ModeChooserView.swift</c>): Caption only or
/// Interview. Shown on every launch and from the header's Change mode button. It only reports
/// the choice — the host shows <see cref="PrivacyNoticeDialog"/> when Interview is chosen
/// before <c>privacy_acknowledged</c>, and writes <c>interview.mode</c>.
/// </summary>
public partial class ModeChooserView : UserControl
{
    /// <summary>Below this width (DIP) the cards stack, as the Mac's <c>ViewThatFits</c> does.</summary>
    private const double StackBelow = 560;

    private string _lastMode = InterviewConfig.CaptionMode;
    private bool? _stacked;

    public ModeChooserView()
    {
        InitializeComponent();
        ApplyLastMode();
        // Put the keyboard on the default card when the screen appears, so Tab / Enter work at once.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(FocusDefault, System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    /// <summary>
    /// The user chose a mode: <see cref="InterviewConfig.CaptionMode"/> (<c>caption</c>) or
    /// <see cref="InterviewConfig.InterviewMode"/> (<c>interview</c>).
    /// </summary>
    public event EventHandler<string>? ModeChosen;

    /// <summary>
    /// <c>interview.mode</c> as last saved: that card says "Last used" and is the default
    /// button. Anything other than <c>interview</c> counts as Caption only.
    /// </summary>
    public string LastMode
    {
        get => _lastMode;
        set
        {
            _lastMode = value == InterviewConfig.InterviewMode ? InterviewConfig.InterviewMode : InterviewConfig.CaptionMode;
            ApplyLastMode();
        }
    }

    /// <summary>
    /// Null normally. Otherwise the tooltip of a disabled Interview card ("Interview mode is
    /// unavailable: …") — the app could not set Interview mode up at launch, so only Caption
    /// only can be chosen, and it is the default whatever <see cref="LastMode"/> says.
    /// </summary>
    public string? InterviewUnavailableReason
    {
        get;
        set
        {
            field = value;
            ApplyLastMode();
        }
    }

    /// <summary>Move keyboard focus to the last-used card.</summary>
    public void FocusDefault()
    {
        (IsInterviewLast ? InterviewCard : CaptionCard).Focus();
    }

    private bool IsInterviewLast => _lastMode == InterviewConfig.InterviewMode && InterviewUnavailableReason is null;

    private void ApplyLastMode()
    {
        var interview = IsInterviewLast;
        InterviewCard.IsDefault = interview;
        CaptionCard.IsDefault = !interview;
        InterviewCard.Style = (Style)Resources[interview ? "Card.Last" : "Card"];
        CaptionCard.Style = (Style)Resources[interview ? "Card" : "Card.Last"];
        InterviewBadge.Visibility = interview ? Visibility.Visible : Visibility.Collapsed;
        CaptionBadge.Visibility = interview ? Visibility.Collapsed : Visibility.Visible;
        // Read out with the card, as the Mac's accessibility label plus its visible badge.
        AutomationProperties.SetHelpText(InterviewCard, interview ? "Last used" : "");
        AutomationProperties.SetHelpText(CaptionCard, interview ? "" : "Last used");

        // Interview mode unavailable: the card stays visible but disabled, and says why — on
        // hover (ShowOnDisabled), in its badge line, and to a screen reader.
        var unavailable = InterviewUnavailableReason;
        InterviewCard.IsEnabled = unavailable is null;
        InterviewCard.ToolTip = unavailable;
        ToolTipService.SetShowOnDisabled(InterviewCard, true);
        if (unavailable is not null)
        {
            InterviewBadge.Text = unavailable;
            InterviewBadge.Visibility = Visibility.Visible;
            AutomationProperties.SetHelpText(InterviewCard, unavailable);
        }
        else
        {
            InterviewBadge.ClearValue(TextBlock.TextProperty);      // back to the style's "Last used"
        }
    }

    private void OnCardsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        LayOutCards(e.NewSize.Width < StackBelow);
    }

    /// <summary>Side by side, or one above the other. Tab order stays Caption only → Interview.</summary>
    private void LayOutCards(bool stacked)
    {
        if (_stacked == stacked) return;
        _stacked = stacked;
        if (stacked)
        {
            FirstColumn.Width = new GridLength(1, GridUnitType.Star);
            GapColumn.Width = new GridLength(0);
            SecondColumn.Width = new GridLength(0);
            GapRow.Height = new GridLength(12);
            Grid.SetColumn(InterviewCard, 0);
            Grid.SetRow(InterviewCard, 2);
        }
        else
        {
            FirstColumn.Width = new GridLength(1, GridUnitType.Star);
            GapColumn.Width = new GridLength(16);
            SecondColumn.Width = new GridLength(1, GridUnitType.Star);
            GapRow.Height = new GridLength(0);
            Grid.SetColumn(InterviewCard, 2);
            Grid.SetRow(InterviewCard, 0);
        }
    }

    private void OnCaptionChosen(object sender, RoutedEventArgs e) =>
        ModeChosen?.Invoke(this, InterviewConfig.CaptionMode);

    private void OnInterviewChosen(object sender, RoutedEventArgs e)
    {
        if (InterviewUnavailableReason is not null) return;
        ModeChosen?.Invoke(this, InterviewConfig.InterviewMode);
    }
}
