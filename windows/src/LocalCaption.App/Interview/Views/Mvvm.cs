using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LocalCaption.App.Interview.Views.Prep;

// The few MVVM pieces the preparation screens need. The App had none (its windows are
// code-behind), and other Interview screens are being drafted in parallel, so these stay
// internal and in their own namespace: nothing here can collide with a sibling's helpers.

/// <summary><see cref="INotifyPropertyChanged"/> for the preparation view models.</summary>
internal abstract class PrepObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raise for one property, or for every property when <paramref name="name"/> is empty.</summary>
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));

    /// <summary>Re-read every binding on this object (WPF treats an empty name as "all").</summary>
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// Coalesces "something changed" notices that may arrive on any thread into one call of
/// <c>refresh</c> on the dispatcher thread, at background priority: a streaming answer raises
/// the controller's <c>Changed</c> per delta, and the form only needs to catch up once per frame.
/// </summary>
internal sealed class PrepRefresher(Dispatcher dispatcher, Action refresh)
{
    private int _queued;

    public void Request()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Volatile.Write(ref _queued, 0);
            refresh();
        });
    }
}

/// <summary>A synchronous command.</summary>
internal sealed class PrepCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) execute();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// An async command: disabled while it runs (no double clicks starting two preparations), and
/// a failure goes to <c>onError</c> instead of the dispatcher's unhandled-exception path.
/// </summary>
internal sealed class PrepAsyncCommand(Func<Task> execute, Func<bool>? canExecute = null,
                                       Action<Exception>? onError = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _running;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        catch (Exception e)
        {
            if (onError is null) System.Diagnostics.Trace.WriteLine("[prep] " + e);
            else onError(e);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Visible when the value is "present" — <c>true</c>, a non-empty string, any other non-null
/// object — else Collapsed. <c>ConverterParameter=not</c> inverts it.
/// </summary>
public sealed class PrepVisibility : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var present = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            _ => true,
        };
        if (parameter as string == "not") present = !present;
        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A label and the value it stands for, for the pickers.</summary>
internal sealed record PrepChoice(string Value, string Label);

/// <summary>
/// One part's mark in the preparation panel: number, title, and running / stopped / done.
/// </summary>
internal sealed record PrepPartStatus(int Number, string Title, bool IsRunning, bool IsFailed, bool IsDone)
{
    /// <summary>What a screen reader says for the part's header.</summary>
    public string AccessibleName => $"{Number}. {Title}" + (IsRunning ? ", running" : IsFailed ? ", stopped here" : IsDone ? ", done" : "");
}

/// <summary>
/// A small spinning arc — WPF has no themed indeterminate spinner, and the stock ProgressBar
/// is Aero-grey in the dark theme. Spins only while visible, and not at all when Windows
/// animations are turned off (the arc alone still reads as "working").
/// </summary>
public sealed class PrepSpinner : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(PrepSpinner),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly RotateTransform _rotation = new();

    public PrepSpinner()
    {
        Width = 14;
        Height = 14;
        Focusable = false;
        IsHitTestVisible = false;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _rotation;
        SetResourceReference(ForegroundProperty, "Text.Muted");
        IsVisibleChanged += (_, _) => Animate(IsVisible);
        System.Windows.Automation.AutomationProperties.SetName(this, "Working");
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private void Animate(bool on)
    {
        if (on && SystemParameters.ClientAreaAnimation)
        {
            var spin = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            _rotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 2) return;
        var thickness = Math.Max(1.5, size / 7);
        var r = (size - thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var start = new Point(c.X, c.Y - r);
        // 270° clockwise, ending at 9 o'clock.
        var end = new Point(c.X - r, c.Y);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, isFilled: false, isClosed: false);
            g.ArcTo(end, new Size(r, r), 0, isLargeArc: true, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        var pen = new Pen(Foreground, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, geometry);
    }
}
