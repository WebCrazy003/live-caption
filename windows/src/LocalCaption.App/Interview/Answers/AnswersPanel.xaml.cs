using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalCaption.Core.Interview;
using LocalCaption.Interview;

namespace LocalCaption.App.Interview.Answers;

/// <summary>
/// The live Answers panel (SPEC-16 §5.4; Mac <c>InterviewPanel</c>). Bind it by setting
/// <see cref="Controller"/>; it listens to <see cref="InterviewController.Changed"/> while
/// loaded, coalesced to one refresh per 50 ms, and polls the clipboard twice a second while
/// visible (the controller itself refuses outside Interview mode).
/// </summary>
public partial class AnswersPanel : UserControl
{
    private const double FullInputWidth = 260;    // the Mac's ideal widths for the coach box
    private const double CompactInputWidth = 200;

    private readonly ChangeThrottle _throttle;
    private readonly DispatcherTimer _clipboardPoll;
    private readonly CoachInput _input = new();
    private readonly List<AnswerCard> _cards = [];
    private readonly HashSet<int> _expanded = [];
    private readonly Dictionary<Guid, BitmapSource?> _thumbs = [];
    private readonly Dictionary<InterviewProfile, ToggleButton> _profileButtons = [];

    private InterviewController? _controller;
    private bool _attached;
    private InterviewRecord? _shownRecord;
    private int _lastTurnCount = -1;
    private int _lastAnswerLength;
    private List<Guid> _shownTray = [];
    private double _fontSize = 18;
    private BarDensity _density = BarDensity.Full;
    private (int, string)? _askKey;
    private string? _densityKey;
    private DateTime _typeClosedAt = DateTime.MinValue;

    private enum BarDensity { Full, IconAsk, Popup }

    public AnswersPanel()
    {
        AnswersTheme.Apply(this);      // before InitializeComponent: see AnswersTheme.xaml
        InitializeComponent();
        _throttle = new ChangeThrottle(Refresh, Dispatcher);
        _clipboardPoll = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _clipboardPoll.Tick += (_, _) =>
        {
            if (PollsClipboard && IsVisible && _controller is { } c) c.PollClipboard();
        };

        foreach (var profile in InterviewSteps.Profiles)
        {
            var button = new ToggleButton { Content = profile.Label(), Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            button.SetResourceReference(StyleProperty, "Toggle.Chip");
            ToolTipService.SetShowOnDisabled(button, true);
            var p = profile;
            button.Click += (_, _) => ApplyProfile(p);
            _profileButtons[profile] = button;
            ProfileButtons.Children.Add(button);
        }
        foreach (var element in new FrameworkElement[] { ProfileMenuButton, LiveCodingFull, LiveCodingIcon, AskFull, AskIcon, CameraButton })
            ToolTipService.SetShowOnDisabled(element, true);

        _input.SendRequested += (_, text) => { if (_controller is { } c) Ui.Fire(() => c.SendTypedAsync(text)); };
        InputHost.Content = _input;

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        SizeChanged += (_, _) => UpdateDensities();
        IsVisibleChanged += (_, _) => { if (IsVisible) _throttle.Request(); };
    }

    // ── Host API ─────────────────────────────────────────────────────────────────────────

    /// <summary>The interview shown. Set once the controller exists; may be swapped.</summary>
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

    /// <summary>Answer text size: <c>caption.font_size</c> (the header's −/+ change both).</summary>
    public double AnswerFontSize
    {
        get => _fontSize;
        set
        {
            if (_fontSize.Equals(value) || value <= 0) return;
            _fontSize = value;
            _throttle.Request();
        }
    }

    /// <summary>The Ask hotkey as shown ("F8"): the registered key, else <c>interview.hotkey</c> resolved.</summary>
    public string AskHotkeyLabel { get; set { field = value; _throttle.Request(); } } = Hotkey.DefaultString;

    /// <summary>The Screenshot hotkey as shown ("F9").</summary>
    public string ScreenshotHotkeyLabel { get; set { field = value; _throttle.Request(); } } = Hotkey.DefaultScreenshotString;

    /// <summary>Poll the clipboard for screenshots while shown (Mac: every 0.5 s). Default on.</summary>
    public bool PollsClipboard { get; set; } = true;

    /// <summary>Put the caret in the coach box (opens the popup in the narrowest layout).</summary>
    public void FocusCoachInput()
    {
        if (_density == BarDensity.Popup) TypePopup.IsOpen = true;
        else _input.FocusInput();
    }

    // ── Wiring ───────────────────────────────────────────────────────────────────────────

    private void Attach()
    {
        if (_attached || _controller is null) return;
        _controller.Changed += OnControllerChanged;
        _controller.Codex.Changed += OnCodexChanged;
        _attached = true;
        _clipboardPoll.Start();
        _throttle.Request();
    }

    private void Detach()
    {
        _clipboardPoll.Stop();
        _throttle.Cancel();
        if (!_attached || _controller is null) return;
        _controller.Changed -= OnControllerChanged;
        _controller.Codex.Changed -= OnCodexChanged;
        _attached = false;
    }

    private void OnControllerChanged(object? sender, EventArgs e) => _throttle.Request();

    // CodexService raises on any thread; the throttle marshals.
    private void OnCodexChanged(object? sender, EventArgs e) => _throttle.Request();

    // ── Refresh ──────────────────────────────────────────────────────────────────────────

    private void Refresh()
    {
        if (_controller is not { } c)
        {
            CardList.Children.Clear();
            _cards.Clear();
            IsEnabled = false;
            return;
        }
        IsEnabled = true;

        RefreshHeader(c);
        SetLine(StatusText, c.Status);
        SetLine(ModelNoticeText, c.ModelNotice);
        RefreshCards(c);
        RefreshTray(c);
        RefreshBar(c);
        UpdateDensities();
    }

    private static void SetLine(TextBlock block, string? text)
    {
        block.Visibility = Ui.Shown(!string.IsNullOrEmpty(text));
        block.Text = text ?? "";
        block.ToolTip = text is { Length: > 80 } ? text : null;
    }

    private void RefreshHeader(InterviewController c)
    {
        var blocked = c.Blocker(InterviewStep.ApplyInstruction);
        var active = c.ActiveProfile;
        foreach (var (profile, button) in _profileButtons)
        {
            var on = active == profile;
            button.IsChecked = on;
            button.IsEnabled = blocked is null;
            button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            Ui.Tip(button, blocked ?? $"/apply-instruction {profile.Raw()}" + (on ? " (active)" : ""));
        }
        ProfileMenuText.Text = active?.Label() ?? "Profile";
        ProfileMenuButton.IsEnabled = blocked is null;
        Ui.Tip(ProfileMenuButton, blocked ?? "Apply an answering profile (/apply-instruction)");

        var liveBlocked = c.Blocker(InterviewStep.LiveCoding);
        var liveOn = c.LiveCodingActive;
        foreach (var button in new[] { LiveCodingFull, LiveCodingIcon })
        {
            button.IsChecked = liveOn;
            button.IsEnabled = liveBlocked is null;
            Ui.Tip(button, liveBlocked ?? (liveOn ? "Live coding & design is active" : "Apply /live-coding-design (optional)"));
        }

        StopButton.Visibility = Ui.Shown(c.IsStreaming);
    }

    private void RefreshCards(InterviewController c)
    {
        if (!ReferenceEquals(c.Record, _shownRecord))
        {
            _shownRecord = c.Record;
            _expanded.Clear();
            _lastTurnCount = -1;
        }

        var turns = c.Turns;
        EmptyHint.Visibility = Ui.Shown(turns.Count == 0);
        EmptyHint.Text = $"Press {AskHotkeyLabel} (or Ask) when the interviewer finishes a question. "
                         + "You can also just start — the coach is ready either way.";

        // Follow the newest answer only while the reader is already at the bottom; a new turn always scrolls.
        var atBottom = CardScroll.ScrollableHeight - CardScroll.VerticalOffset < 32;

        while (_cards.Count < turns.Count)
        {
            var card = new AnswerCard { Margin = new Thickness(0, 0, 0, 10), ImageSource = Image };
            card.ToggleRequested += OnCardToggle;
            card.RegenerateRequested += OnCardRegenerate;
            _cards.Add(card);
            CardList.Children.Add(card);
        }
        while (_cards.Count > turns.Count)
        {
            var card = _cards[^1];
            card.ToggleRequested -= OnCardToggle;
            card.RegenerateRequested -= OnCardRegenerate;
            _cards.RemoveAt(_cards.Count - 1);
            CardList.Children.Remove(card);
        }

        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            var latest = i == turns.Count - 1;
            _cards[i].Show(turn, isLatest: latest, isExpanded: latest || _expanded.Contains(turn.N),
                           isStreaming: c.StreamingTurn == turn.N, canRegenerate: true, fontSize: _fontSize);
        }

        var lastLength = turns.Count == 0 ? 0 : turns[^1].Answer.Length;
        if (turns.Count != _lastTurnCount || (lastLength != _lastAnswerLength && atBottom)) CardScroll.ScrollToEnd();
        _lastTurnCount = turns.Count;
        _lastAnswerLength = lastLength;
    }

    private byte[]? Image(string name) => _controller?.Image(name);

    private void OnCardToggle(object? sender, EventArgs e)
    {
        if (sender is not AnswerCard { Turn: { } turn }) return;
        if (!_expanded.Remove(turn.N)) _expanded.Add(turn.N);
        _throttle.Request();
    }

    private void OnCardRegenerate(object? sender, EventArgs e)
    {
        if (_controller is { } c) Ui.Fire(c.RegenerateAsync);
    }

    private void RefreshTray(InterviewController c)
    {
        var pending = c.PendingImages;
        TrayPanel.Visibility = Ui.Shown(pending.Count > 0);
        TrayText.Text = $"{pending.Count} screenshot{(pending.Count == 1 ? "" : "s")} in this prompt";

        var ids = pending.Select(p => p.Id).ToList();
        if (ids.SequenceEqual(_shownTray)) return;
        _shownTray = ids;

        foreach (var gone in _thumbs.Keys.Except(ids).ToList()) _thumbs.Remove(gone);
        Thumbs.Children.Clear();
        foreach (var item in pending)
        {
            if (!_thumbs.TryGetValue(item.Id, out var bitmap))
                _thumbs[item.Id] = bitmap = PngImages.Decode(item.Png, 240);   // 72 DIP at up to 300 %
            Thumbs.Children.Add(Thumbnail(item.Id, bitmap));
        }
    }

    /// <summary>A 72×48 thumbnail with a remove button (Mac <c>PendingThumbnail</c>).</summary>
    private FrameworkElement Thumbnail(Guid id, BitmapSource? bitmap)
    {
        var picture = new Border { Width = 72, Height = 48, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        picture.SetResourceReference(Border.BorderBrushProperty, "Line.Strong");
        if (bitmap is not null)
            picture.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        else
            picture.SetResourceReference(Border.BackgroundProperty, "Bg.Hover");

        var remove = new Button
        {
            Tag = Glyph.Close,
            Width = 20,
            Height = 20,
            FontSize = 9,
            Margin = new Thickness(0, 2, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
            Foreground = Brushes.White,
            ToolTip = "Remove this screenshot",
        };
        remove.SetResourceReference(StyleProperty, "Answers.IconButton");
        AutomationProperties.SetName(remove, "Remove this screenshot");
        remove.Click += (_, _) => _controller?.RemovePending(id);

        var grid = new Grid { Margin = new Thickness(0, 0, 6, 0) };
        grid.Children.Add(picture);
        grid.Children.Add(remove);
        return grid;
    }

    private void RefreshBar(InterviewController c)
    {
        var count = c.PendingImages.Count;
        var askKey = (count, AskHotkeyLabel);
        if (askKey != _askKey)
        {
            _askKey = askKey;
            AskFull.Content = AskContent(full: true, count);
            AskIcon.Content = count == 0 ? null : AskContent(full: false, count);
        }
        var tip = count == 0
            ? $"Send the interviewer's latest words ({AskHotkeyLabel})"
            : $"Send the interviewer's latest words and {count} screenshot(s) ({AskHotkeyLabel})";
        Ui.Tip(AskFull, tip);
        Ui.Tip(AskIcon, tip);

        CameraButton.IsEnabled = !c.Capturing;
        Ui.Tip(CameraButton, $"Select an area of the screen to add to this prompt ({ScreenshotHotkeyLabel}) — Esc cancels");
        _input.ImageCount = count;
    }

    /// <summary>"Ask  F8  [photo 2]" — the hotkey in the muted voice, the screenshot count as a badge.</summary>
    private StackPanel AskContent(bool full, int images)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (full)
        {
            panel.Children.Add(new TextBlock { Text = "Ask", VerticalAlignment = VerticalAlignment.Center });
            var key = new TextBlock
            {
                Text = AskHotkeyLabel,
                FontSize = 11,
                FontWeight = FontWeights.Normal,
                Margin = new Thickness(6, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            key.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
            panel.Children.Add(key);
        }
        if (images > 0)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(5, 0, 6, 0),
                Margin = new Thickness(full ? 8 : 0, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge.SetResourceReference(Border.BackgroundProperty, "Accent");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new TextBlock { Text = Glyph.Photo, FontSize = 10, Margin = new Thickness(0, 1, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Bg.Window");
            var number = new TextBlock
            {
                Text = images.ToString(System.Globalization.CultureInfo.CurrentCulture),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            number.SetResourceReference(TextBlock.ForegroundProperty, "Bg.Window");
            row.Children.Add(icon);
            row.Children.Add(number);
            badge.Child = row;
            panel.Children.Add(badge);
        }
        return panel;
    }

    // ── Densities (SwiftUI ViewThatFits, by measuring) ───────────────────────────────────

    private void UpdateDensities()
    {
        if (!IsLoaded || ActualWidth <= 0 || _controller is not { } c) return;
        // Measuring is cheap but not free: redo it only when the width or what the rows hold changed.
        var key = string.Join("|", HeaderGrid.ActualWidth, BottomBar.ActualWidth, c.ActiveProfile, c.IsStreaming,
                              c.PendingImages.Count, AskHotkeyLabel);
        if (key == _densityKey) return;
        _densityKey = key;

        // Header: full row (title, three profile buttons, labelled Live coding) or compact.
        var stop = StopButton.Visibility == Visibility.Visible ? Ui.NaturalWidth(StopButton) : 0;
        var headerNeed = Ui.NaturalWidth(TitleText) + Ui.NaturalWidth(ProfileButtons) + Ui.NaturalWidth(LiveCodingFull) + stop;
        var compactHeader = headerNeed > HeaderGrid.ActualWidth;
        TitleText.Visibility = Ui.Shown(!compactHeader);
        ProfileButtons.Visibility = Ui.Shown(!compactHeader);
        ProfileMenuButton.Visibility = Ui.Shown(compactHeader);
        LiveCodingFull.Visibility = Ui.Shown(!compactHeader);
        LiveCodingIcon.Visibility = Ui.Shown(compactHeader);

        // Bottom bar.
        var width = BottomBar.ActualWidth;
        var camera = Ui.NaturalWidth(CameraButton);
        var send = 46.0;   // the coach box's Send button and gap
        var density = Ui.NaturalWidth(AskFull) + camera + 8 + FullInputWidth + send <= width ? BarDensity.Full
            : Ui.NaturalWidth(AskIcon) + camera + 8 + CompactInputWidth + send <= width ? BarDensity.IconAsk
            : BarDensity.Popup;
        ApplyDensity(density);
    }

    private void ApplyDensity(BarDensity density)
    {
        AskFull.Visibility = Ui.Shown(density == BarDensity.Full);
        AskIcon.Visibility = Ui.Shown(density != BarDensity.Full);
        KeyboardButton.Visibility = Ui.Shown(density == BarDensity.Popup);
        if (density == _density) return;

        var hadFocus = _input.IsKeyboardFocusWithin;
        _density = density;
        if (density == BarDensity.Popup)
        {
            InputHost.Content = null;
            PopupInputHost.Content = _input;
        }
        else
        {
            TypePopup.IsOpen = false;
            PopupInputHost.Content = null;
            InputHost.Content = _input;
            if (hadFocus) Dispatcher.InvokeAsync(_input.FocusInput, DispatcherPriority.Input);
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────────────────────

    private void ApplyProfile(InterviewProfile profile)
    {
        if (_controller is not { } c) return;
        c.Draft = c.Draft with { Profile = profile };
        Ui.Fire(() => c.RunAsync(InterviewStep.ApplyInstruction, profile));
        _throttle.Request();   // the toggle's own click state is replaced by the controller's
    }

    private void OnProfileMenu(object sender, RoutedEventArgs e)
    {
        if (_controller is not { } c) return;
        var menu = new ContextMenu { PlacementTarget = ProfileMenuButton, Placement = PlacementMode.Bottom };
        foreach (var profile in InterviewSteps.Profiles)
        {
            var p = profile;
            var item = new MenuItem { Header = p.Label(), IsCheckable = false };
            if (c.ActiveProfile == p) item.FontWeight = FontWeights.SemiBold;
            item.Click += (_, _) => ApplyProfile(p);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnLiveCoding(object sender, RoutedEventArgs e)
    {
        if (_controller is not { } c) return;
        c.Draft = c.Draft with { LiveCoding = true };
        Ui.Fire(() => c.RunAsync(InterviewStep.LiveCoding));
        _throttle.Request();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (_controller is { } c) Ui.Fire(c.StopStreamingAsync);
    }

    private void OnAsk(object sender, RoutedEventArgs e)
    {
        if (_controller is { } c) Ui.Fire(c.AskAsync);
    }

    private void OnScreenshot(object sender, RoutedEventArgs e)
    {
        if (_controller is { } c) Ui.Fire(c.TakeScreenshotAsync);
    }

    private void OnClearTray(object sender, RoutedEventArgs e) => _controller?.ClearPending();

    private void OnKeyboard(object sender, RoutedEventArgs e)
    {
        // The press that closed the popup (StaysOpen=False) also clicks this button: don't reopen.
        if (TypePopup.IsOpen || DateTime.UtcNow - _typeClosedAt < TimeSpan.FromMilliseconds(250))
        {
            TypePopup.IsOpen = false;
            return;
        }
        TypePopup.IsOpen = true;
    }

    private void OnTypePopupOpened(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(_input.FocusInput, DispatcherPriority.Input);

    private void OnTypePopupClosed(object? sender, EventArgs e)
    {
        _typeClosedAt = DateTime.UtcNow;
        if (_density == BarDensity.Popup && IsKeyboardFocusWithin) KeyboardButton.Focus();
    }

    private void OnTypePopupKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        TypePopup.IsOpen = false;
        KeyboardButton.Focus();
        e.Handled = true;
    }
}
