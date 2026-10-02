using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LocalCaption.Interview;

namespace LocalCaption.App.Interview.Answers;

/// <summary>A global hotkey that could not be registered, for the header's warning chip.</summary>
/// <param name="Hotkey">As shown, e.g. <c>F8</c>.</param>
/// <param name="Reason">Why (from the registration).</param>
public sealed record HotkeyProblem(string Hotkey, string Reason);

/// <summary>
/// Interview mode's live layout (SPEC-16 §5.3): header chips and controls over captions and
/// answers. The host fills <see cref="Captions"/> with its existing caption view and transport
/// bar, sets <see cref="Controller"/>, and persists the layout state this view reports through
/// <see cref="LayoutStateChanged"/> (per-machine UI state: the Windows <c>ui</c> group).
/// </summary>
public partial class InterviewLayoutView : UserControl
{
    /// <summary>Side by side: the captions' default share (Mac 0.58).</summary>
    public const double DefaultCaptionShareWide = 0.58;

    /// <summary>Stacked: the answers' default share (Mac 0.6).</summary>
    public const double DefaultAnswerShareStacked = 0.6;

    /// <summary><c>automatic</c> goes side by side from this width (DIP).</summary>
    public const double SideBySideMinWidth = 820;

    private const double Handle = 9;

    private readonly ChangeThrottle _throttle;
    private InterviewController? _controller;
    private bool _attached;
    private Arrangement _arrangement = Arrangement.None;
    private string? _chipKey;
    private int _fontSize = 18;

    private enum Arrangement { None, AnswersOnly, SideBySide, Stacked }

    public InterviewLayoutView()
    {
        AnswersTheme.Apply(this);      // before InitializeComponent: see AnswersTheme.xaml
        InitializeComponent();
        _throttle = new ChangeThrottle(Refresh, Dispatcher);
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        SplitGrid.SizeChanged += (_, _) => ApplyLayout(force: false);
        HeaderGrid.SizeChanged += (_, _) =>
        {
            UpdateHeaderDensity();
            _chipKey = null;
            _throttle.Request();
        };
        FontValue.Text = _fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateCaptionsButton();
    }

    // ── Host API ─────────────────────────────────────────────────────────────────────────

    /// <summary>The live interview; also handed to <see cref="Answers"/>.</summary>
    public InterviewController? Controller
    {
        get => _controller;
        set
        {
            if (ReferenceEquals(_controller, value)) return;
            Detach();
            _controller = value;
            Answers.Controller = value;
            _chipKey = null;
            if (IsLoaded) Attach();
            Refresh();
        }
    }

    /// <summary>The Answers panel inside (hotkey labels, focus the coach box).</summary>
    public AnswersPanel AnswersPanel => Answers;

    /// <summary>
    /// The caption column: the host's caption view with its transport bar under it. Moved here
    /// from wherever it was (remove it from its old parent first — an element has one parent).
    /// </summary>
    public object? Captions
    {
        get => CaptionsHost.Content;
        set => CaptionsHost.Content = value;
    }

    /// <summary>Start / Pause / Stop, icon-only, shown in the header while the captions are hidden.</summary>
    public object? CompactTransport
    {
        get => CompactTransportHost.Content;
        set
        {
            CompactTransportHost.Content = value;
            ApplyLayout(force: true);
        }
    }

    /// <summary><c>interview.panel_layout</c>: <c>automatic</c>, <c>side_by_side</c> or <c>stacked</c>.</summary>
    public string PanelLayout
    {
        get;
        set
        {
            field = value;
            ApplyLayout(force: true);
        }
    } = "automatic";

    /// <summary>Side by side: the captions' share of the width (0–1).</summary>
    public double CaptionShareWide
    {
        get;
        set
        {
            field = Clamp(value, DefaultCaptionShareWide);
            ApplyLayout(force: true);
        }
    } = DefaultCaptionShareWide;

    /// <summary>Stacked: the answers' share of the height (0–1).</summary>
    public double AnswerShareStacked
    {
        get;
        set
        {
            field = Clamp(value, DefaultAnswerShareStacked);
            ApplyLayout(force: true);
        }
    } = DefaultAnswerShareStacked;

    /// <summary>The caption column is hidden: the answers take the whole space.</summary>
    public bool CaptionsHidden
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            UpdateCaptionsButton();
            ApplyLayout(force: true);
        }
    }

    /// <summary>
    /// Show the Preparation button — the host's rule, as the Mac's: Interview mode, not
    /// recording or paused, not saving.
    /// </summary>
    public bool ShowPreparationButton
    {
        get => PreparationButton.Visibility == Visibility.Visible;
        set => PreparationButton.Visibility = Ui.Shown(value);
    }

    /// <summary><c>caption.font_size</c>, applied to the answers too.</summary>
    public int AnswerFontSize
    {
        get => _fontSize;
        set
        {
            _fontSize = Math.Clamp(value, MinFontSize, MaxFontSize);
            FontValue.Text = _fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Answers.AnswerFontSize = _fontSize;
        }
    }

    /// <summary>The Mac's range (SPEC-16 C9).</summary>
    public const int MinFontSize = 10, MaxFontSize = 48;

    /// <summary>The Ask hotkey could not be registered (null: fine).</summary>
    public HotkeyProblem? AskHotkeyUnavailable { get; set { field = value; _chipKey = null; _throttle.Request(); } }

    /// <summary>The Screenshot hotkey could not be registered (null: fine).</summary>
    public HotkeyProblem? ScreenshotHotkeyUnavailable { get; set { field = value; _chipKey = null; _throttle.Request(); } }

    /// <summary>The user changed the split or hid / showed the captions: persist <see cref="CaptionShareWide"/>, <see cref="AnswerShareStacked"/>, <see cref="CaptionsHidden"/>.</summary>
    public event EventHandler? LayoutStateChanged;

    /// <summary>−/+ pressed: the new size (10–48). The host saves <c>caption.font_size</c> and resizes its caption view.</summary>
    public event EventHandler<int>? FontSizeChangeRequested;

    /// <summary>Preparation pressed (the controller's <c>ShowingPreparation</c> is already set).</summary>
    public event EventHandler? PreparationRequested;

    private static double Clamp(double share, double fallback) =>
        double.IsFinite(share) && share > 0 && share < 1 ? share : fallback;

    // ── Wiring ───────────────────────────────────────────────────────────────────────────

    private void Attach()
    {
        if (_attached || _controller is null) return;
        _controller.Changed += OnChanged;
        _controller.Codex.Changed += OnChanged;
        _attached = true;
        _throttle.Request();
        ApplyLayout(force: true);
    }

    private void Detach()
    {
        _throttle.Cancel();
        if (!_attached || _controller is null) return;
        _controller.Changed -= OnChanged;
        _controller.Codex.Changed -= OnChanged;
        _attached = false;
    }

    // Controller: UI thread. CodexService: any thread — the throttle marshals.
    private void OnChanged(object? sender, EventArgs e) => _throttle.Request();

    private void Refresh() => RefreshChips();

    // ── Header chips (Mac InterviewHeaderChips) ──────────────────────────────────────────

    private void RefreshChips()
    {
        if (_controller is not { } c)
        {
            Chips.Children.Clear();
            _chipKey = null;
            return;
        }
        var codex = c.Codex;
        var lowest = codex.UsageIsLow ? codex.Usage?.Lowest : null;
        var key = string.Join("|", c.Record?.Name, c.ThreadState, c.ActiveProfile, lowest?.RemainingPercent, lowest?.Label,
                              AskHotkeyUnavailable, ScreenshotHotkeyUnavailable, HeaderGrid.ActualWidth);
        if (key == _chipKey) return;
        _chipKey = key;

        Fill(c, compact: false);
        var available = HeaderGrid.ActualWidth - Ui.NaturalWidth(HeaderRight);
        if (available > 0 && Ui.NaturalWidth(Chips) > available) Fill(c, compact: true);
    }

    private void Fill(InterviewController c, bool compact)
    {
        Chips.Children.Clear();

        if (!compact && c.Record?.Name is { Length: > 0 } name)
        {
            var title = new TextBlock
            {
                Text = name,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 280,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = name,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            Chips.Children.Add(title);
        }

        // Coach chip.
        switch (c.ThreadState)
        {
            case InterviewThreadState.None:
                Chips.Children.Add(Chip(compact ? "" : "Coach not started", "Coach not started", Dot(dashed: true), "Text.Muted"));
                break;
            case InterviewThreadState.Opening:
                Chips.Children.Add(Chip(compact ? "" : "Starting coach…", "Starting coach…", Dot(dashed: false), "Text.Muted"));
                break;
            case InterviewThreadState.Open:
                var full = c.ActiveProfile is { } p ? "Coach ready · " + p.Label() : "Coach ready";
                Chips.Children.Add(Chip(compact ? c.ActiveProfile?.Label() ?? "" : full, full, Icon(Glyph.Check), "Ok"));
                break;
            case InterviewThreadState.Failed failed:
                var chip = Chip(compact ? "" : "Coach unavailable", "Coach unavailable", Icon(Glyph.Warning), "Caution");
                chip.ToolTip = failed.Message;
                Chips.Children.Add(chip);
                break;
        }

        // Usage, only when low (< 20 % left in any window).
        if (c.Codex.UsageIsLow && c.Codex.Usage is { Lowest: { } w } usage)
        {
            var text = $"{w.Label}: {w.RemainingPercent}% left";
            var chip = Chip(compact ? $"{w.RemainingPercent}%" : text, text, Icon(Glyph.Warning), "Caution");
            chip.ToolTip = string.Join("\n", usage.Windows.Select(UsageLine.For));
            Chips.Children.Add(chip);
        }

        if (AskHotkeyUnavailable is { } ask)
        {
            var chip = Chip(compact ? "" : $"{ask.Hotkey} unavailable", $"{ask.Hotkey} unavailable", Icon(Glyph.Keyboard), "Caution");
            chip.ToolTip = $"Hotkey {ask.Hotkey} unavailable: {ask.Reason} — change it in Settings. The Ask button still works.";
            Chips.Children.Add(chip);
        }
        if (ScreenshotHotkeyUnavailable is { } shot)
        {
            var chip = Chip(compact ? "" : $"{shot.Hotkey} unavailable", $"{shot.Hotkey} unavailable", Icon(Glyph.Camera), "Caution");
            chip.ToolTip = $"Screenshot hotkey {shot.Hotkey} unavailable: {shot.Reason} — change it in Settings. The screenshot button still works.";
            Chips.Children.Add(chip);
        }
    }

    /// <summary>A capsule tinted with <paramref name="brush"/> (10 %), its icon and text in that colour.</summary>
    private static FrameworkElement Chip(string text, string accessibleName, FrameworkElement icon, string brush)
    {
        var tint = new Border { CornerRadius = new CornerRadius(10), Opacity = 0.12 };
        tint.SetResourceReference(Border.BackgroundProperty, brush);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(7, 2, 7, 2) };
        row.Children.Add(icon);
        if (text.Length > 0)
        {
            var label = new TextBlock { Text = text, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 1) };
            label.SetResourceReference(TextBlock.ForegroundProperty, brush);
            row.Children.Add(label);
        }
        icon.SetResourceReference(icon is Shape ? Shape.StrokeProperty : TextBlock.ForegroundProperty, brush);

        var chip = new Grid { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        chip.Children.Add(tint);
        chip.Children.Add(row);
        AutomationProperties.SetName(chip, accessibleName);
        ToolTipService.SetShowDuration(chip, 20000);
        return chip;
    }

    private static TextBlock Icon(string glyph)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        return icon;
    }

    private static Ellipse Dot(bool dashed)
    {
        var dot = new Ellipse { Width = 9, Height = 9, StrokeThickness = 1.3, VerticalAlignment = VerticalAlignment.Center };
        if (dashed) dot.StrokeDashArray = new DoubleCollection { 1.5, 1.5 };
        return dot;
    }

    // ── Layout (Mac interviewLayout + ResizableSplit) ────────────────────────────────────

    private bool SideBySide(double width) => PanelLayout switch
    {
        "side_by_side" => true,
        "stacked" => false,
        _ => width >= SideBySideMinWidth,
    };

    private void ApplyLayout(bool force)
    {
        var wanted = CaptionsHidden ? Arrangement.AnswersOnly
            : SideBySide(SplitGrid.ActualWidth > 0 ? SplitGrid.ActualWidth : ActualWidth) ? Arrangement.SideBySide
            : Arrangement.Stacked;
        if (wanted == _arrangement && !force) return;
        _arrangement = wanted;

        SplitGrid.RowDefinitions.Clear();
        SplitGrid.ColumnDefinitions.Clear();
        foreach (UIElement child in SplitGrid.Children)
        {
            Grid.SetRow(child, 0);
            Grid.SetColumn(child, 0);
        }

        var showSplit = wanted != Arrangement.AnswersOnly;
        CaptionsHost.Visibility = Ui.Shown(showSplit);
        Splitter.Visibility = Ui.Shown(showSplit);
        SplitLine.Visibility = Ui.Shown(showSplit);
        CompactTransportHost.Visibility = Ui.Shown(!showSplit && CompactTransport is not null);

        switch (wanted)
        {
            case Arrangement.SideBySide:
                // Captions first; minimums 220 / 300.
                SplitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CaptionShareWide, GridUnitType.Star), MinWidth = 220 });
                SplitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Handle) });
                SplitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - CaptionShareWide, GridUnitType.Star), MinWidth = 300 });
                Grid.SetColumn(CaptionsHost, 0);
                Grid.SetColumn(SplitLine, 1);
                Grid.SetColumn(Splitter, 1);
                Grid.SetColumn(Answers, 2);
                Splitter.ResizeDirection = GridResizeDirection.Columns;
                SplitLine.Width = 1;
                SplitLine.Height = double.NaN;
                SplitLine.HorizontalAlignment = HorizontalAlignment.Center;
                SplitLine.VerticalAlignment = VerticalAlignment.Stretch;
                break;
            case Arrangement.Stacked:
                // Answers on top; minimums 160 / 120.
                SplitGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(AnswerShareStacked, GridUnitType.Star), MinHeight = 160 });
                SplitGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Handle) });
                SplitGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - AnswerShareStacked, GridUnitType.Star), MinHeight = 120 });
                Grid.SetRow(Answers, 0);
                Grid.SetRow(SplitLine, 1);
                Grid.SetRow(Splitter, 1);
                Grid.SetRow(CaptionsHost, 2);
                Splitter.ResizeDirection = GridResizeDirection.Rows;
                SplitLine.Height = 1;
                SplitLine.Width = double.NaN;
                SplitLine.HorizontalAlignment = HorizontalAlignment.Stretch;
                SplitLine.VerticalAlignment = VerticalAlignment.Center;
                break;
        }
    }

    /// <summary>Read the share actually shown after a drag or arrow key, store it as stars, report it.</summary>
    private void CaptureSplit()
    {
        switch (_arrangement)
        {
            case Arrangement.SideBySide when SplitGrid.ColumnDefinitions.Count == 3:
            {
                double a = SplitGrid.ColumnDefinitions[0].ActualWidth, b = SplitGrid.ColumnDefinitions[2].ActualWidth;
                if (a + b <= 0) return;
                CaptionShareWide = a / (a + b);   // re-applies the columns as fractions
                break;
            }
            case Arrangement.Stacked when SplitGrid.RowDefinitions.Count == 3:
            {
                double a = SplitGrid.RowDefinitions[0].ActualHeight, b = SplitGrid.RowDefinitions[2].ActualHeight;
                if (a + b <= 0) return;
                AnswerShareStacked = a / (a + b);
                break;
            }
            default:
                return;
        }
        LayoutStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSplitterDragged(object sender, DragCompletedEventArgs e) => CaptureSplit();

    private void OnSplitterKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) CaptureSplit();
    }

    /// <summary>Double-click the border: back to the default split.</summary>
    private void OnSplitterMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        e.Handled = true;
        if (_arrangement == Arrangement.SideBySide) CaptionShareWide = DefaultCaptionShareWide;
        else if (_arrangement == Arrangement.Stacked) AnswerShareStacked = DefaultAnswerShareStacked;
        else return;
        LayoutStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Header controls ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// In a narrow strip Preparation and Captions lose their text (icon, tooltip and
    /// accessible name stay), so the font size and the chips keep room — the same give-way
    /// order as the Mac's header.
    /// </summary>
    private void UpdateHeaderDensity()
    {
        PreparationButton.Content = "Preparation";
        CaptionsButton.Content = "Captions";
        const double roomForChips = 90;
        if (Ui.NaturalWidth(HeaderRight) + roomForChips <= HeaderGrid.ActualWidth) return;
        PreparationButton.Content = null;
        CaptionsButton.Content = null;
    }

    private void UpdateCaptionsButton()
    {
        var hidden = CaptionsHidden;
        CaptionsButton.Tag = hidden ? Glyph.Hide : Glyph.View;
        CaptionsButton.ToolTip = hidden ? "Show the caption panel" : "Hide the caption panel — answers get the whole width";
        AutomationProperties.SetName(CaptionsButton, hidden ? "Show captions" : "Hide captions");
    }

    private void OnCaptions(object sender, RoutedEventArgs e)
    {
        CaptionsHidden = !CaptionsHidden;
        LayoutStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreparation(object sender, RoutedEventArgs e)
    {
        if (_controller is { } c) c.ShowingPreparation = true;
        PreparationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnFontSmaller(object sender, RoutedEventArgs e) => StepFont(-1);

    private void OnFontLarger(object sender, RoutedEventArgs e) => StepFont(1);

    private void StepFont(int by)
    {
        var size = Math.Clamp(_fontSize + by, MinFontSize, MaxFontSize);
        if (size == _fontSize) return;
        AnswerFontSize = size;
        FontSizeChangeRequested?.Invoke(this, size);
    }
}
