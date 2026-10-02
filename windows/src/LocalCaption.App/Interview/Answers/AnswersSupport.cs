using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalCaption.Core.Interview;

namespace LocalCaption.App.Interview.Answers;

/// <summary>
/// Coalesces bursts of change notifications into one refresh. The interview controller raises
/// <c>Changed</c> once per streamed token; re-laying-out the Answers panel that often costs far
/// more than it shows, so views ask for a refresh here and get at most one per
/// <see cref="Interval"/> (50 ms ≈ 20 a second — smooth to the eye, cheap to lay out).
/// </summary>
/// <remarks>
/// The first request after a quiet spell is not held back a full interval: it runs on the next
/// dispatcher turn at <see cref="DispatcherPriority.Background"/>, after input and layout,
/// so a click that changes state (a profile, Clear) is answered at once.
/// </remarks>
internal sealed class ChangeThrottle
{
    private readonly Action _refresh;
    private readonly DispatcherTimer _timer;
    private bool _pending;
    private DateTime _last = DateTime.MinValue;

    public ChangeThrottle(Action refresh, Dispatcher dispatcher)
    {
        _refresh = refresh;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Interval };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Run();
        };
    }

    /// <summary>The shortest gap between two refreshes.</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Ask for a refresh. Safe from any thread.</summary>
    public void Request()
    {
        var dispatcher = _timer.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(Request, DispatcherPriority.Background);
            return;
        }
        if (_pending) return;
        _pending = true;
        var since = DateTime.UtcNow - _last;
        if (since >= Interval)
        {
            dispatcher.InvokeAsync(Run, DispatcherPriority.Background);
        }
        else
        {
            _timer.Interval = Interval - since;
            _timer.Start();
        }
    }

    /// <summary>Refresh now if one is waiting (e.g. before the view is hidden).</summary>
    public void Flush()
    {
        if (!_pending) return;
        _timer.Stop();
        Run();
    }

    /// <summary>Drop a waiting refresh.</summary>
    public void Cancel()
    {
        _timer.Stop();
        _pending = false;
    }

    private void Run()
    {
        if (!_pending) return;
        _pending = false;
        _last = DateTime.UtcNow;
        _refresh();
    }
}

/// <summary>
/// Segoe Fluent Icons / Segoe MDL2 Assets code points (the app's <c>Font.Icon</c>; both fonts
/// share them, so Windows 10 1809 renders the same glyphs as Windows 11).
/// </summary>
internal static class Glyph
{
    public const string ChevronRight = "";
    public const string ChevronDown = "";
    public const string Copy = "";
    public const string Check = "";
    public const string Refresh = "";
    public const string Message = "";
    public const string Stop = "";
    public const string Pause = "";
    public const string Warning = "";
    public const string Ask = "";
    public const string Camera = "";
    public const string Photo = "";
    public const string Keyboard = "";
    public const string Send = "";
    public const string Close = "";
    public const string Code = "";
    public const string Lock = "";
    public const string Flag = "";
    public const string View = "";
    public const string Hide = "";
    public const string FontDecrease = "";
    public const string FontIncrease = "";
    public const string Document = "";
    public const string List = "";
    public const string ChevronDownSmall = "";
}

/// <summary>The wording of a turn, exactly as the Mac's <c>AnswerCard</c> builds it.</summary>
internal static class TurnText
{
    /// <summary>
    /// The collapsed card's line: <c>Skill: &lt;cmd&gt;</c>; else the model's <c>**Q:**</c> line
    /// (the first non-empty line of the answer, <c>**Q:**</c> → <c>Q:</c>); else what was sent.
    /// </summary>
    public static string SummaryLine(InterviewTurn turn)
    {
        if (turn.Kind == InterviewTurnKind.Skill) return "Skill: " + turn.Question;
        // Swift split(separator:) drops empty pieces, so "first" is the first non-empty line.
        var first = turn.Answer.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is not null && first.StartsWith("**Q:**", StringComparison.Ordinal))
            return first.Replace("**Q:**", "Q:", StringComparison.Ordinal).Trim(' ', '\t');
        return turn.Question;
    }

    /// <summary>The prefix before the question on an expanded card ("" for Ask and Regenerate).</summary>
    public static string KindLabel(InterviewTurnKind kind) => kind switch
    {
        InterviewTurnKind.Typed => "You: ",
        InterviewTurnKind.Quick => "Quick: ",
        InterviewTurnKind.Skill => "Skill: ",
        _ => "",
    };

    /// <summary>Time to first words, <c>%.1f s</c> (always a full stop, as Swift's format).</summary>
    public static string Ttft(int ms) => (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>"1 screenshot" / "3 screenshots".</summary>
    public static string Screenshots(int count) => count == 1 ? "1 screenshot" : $"{count} screenshots";
}

/// <summary>Decodes stored PNGs for thumbnails and the "What was sent" popup.</summary>
internal static class PngImages
{
    /// <summary>
    /// A frozen bitmap no wider than <paramref name="decodeWidth"/> pixels, or null when the
    /// bytes are not an image. Decoding small keeps a tray of 10 full-screen captures cheap;
    /// pass enough pixels for 300 % scaling (3 × the DIP width).
    /// </summary>
    public static BitmapSource? Decode(byte[]? png, int decodeWidth)
    {
        if (png is null || png.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = decodeWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException
                                      or InvalidOperationException or ArgumentException)
        {
            Trace.WriteLine($"[answers] a screenshot could not be decoded: {e.Message}");
            return null;
        }
    }
}

/// <summary>Small WPF helpers shared by the Answers views.</summary>
internal static class Ui
{
    /// <summary>
    /// Run a controller action from a click or key without awaiting it. The controller reports
    /// its own failures in its state; anything it throws anyway is traced, never lost in an
    /// unobserved task and never allowed to reach the dispatcher.
    /// </summary>
    public static async void Fire(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            Trace.WriteLine($"[answers] {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// The width <paramref name="element"/> wants with no limit — the WPF counterpart of
    /// SwiftUI's <c>ViewThatFits</c> test. A collapsed element is measured as if shown. The
    /// parent is invalidated afterwards, so its next pass re-measures the element with the
    /// real constraint rather than keeping this unbounded one.
    /// </summary>
    public static double NaturalWidth(UIElement element)
    {
        var visibility = element.Visibility;
        if (visibility != Visibility.Visible) element.Visibility = Visibility.Visible;
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = element.DesiredSize.Width;
        if (visibility != Visibility.Visible) element.Visibility = visibility;
        (VisualTreeHelper.GetParent(element) as UIElement)?.InvalidateMeasure();
        return width;
    }

    /// <summary>Show or collapse.</summary>
    public static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A tooltip that stays readable when the element is disabled (blocked actions explain why).</summary>
    public static void Tip(FrameworkElement element, string? text)
    {
        element.ToolTip = string.IsNullOrEmpty(text) ? null : text;
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(element, true);
    }
}
