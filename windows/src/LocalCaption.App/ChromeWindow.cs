using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace LocalCaption.App;

/// <summary>
/// A window that paints its own title bar, and leaves everything else to Windows.
/// </summary>
/// <remarks>
/// <para>The look comes from <c>ChromeWindowStyle</c> in <c>Themes/Controls.xaml</c>; this
/// class is the little that a style cannot do. <c>WindowChrome</c> keeps the real frame —
/// resize borders, Snap, the system menu, Windows 11's rounded corners — so nothing here
/// reimplements window management. It only answers the caption buttons, corrects the one
/// thing <c>WindowChrome</c> gets wrong (a maximised window overhangs the screen by the
/// width of its resize border), and tells DWM which theme the frame should match.</para>
/// <para>Two slots carry a window's own controls into the bar: <see cref="TitleBarLeading"/>
/// beside the icon, <see cref="TitleBarContent"/> beside the caption buttons. That is what
/// keeps Settings reachable however narrow the window gets — the title bar is the one strip
/// that is never scrolled away or squeezed out.</para>
/// </remarks>
public class ChromeWindow : Window
{
    public static readonly DependencyProperty TitleBarContentProperty = DependencyProperty.Register(
        nameof(TitleBarContent), typeof(object), typeof(ChromeWindow));

    public static readonly DependencyProperty TitleBarLeadingProperty = DependencyProperty.Register(
        nameof(TitleBarLeading), typeof(object), typeof(ChromeWindow));

    private FrameworkElement? _root;

    public ChromeWindow()
    {
        SetResourceReference(StyleProperty, "ChromeWindowStyle");

        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand,
            (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand,
            (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand,
            (_, _) => SystemCommands.RestoreWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand,
            (_, _) => SystemCommands.CloseWindow(this)));

        SourceInitialized += (_, _) =>
        {
            ApplyFrameTheme();
            if (AllowSeeThrough) ClearCompositionBackground();
        };
        ThemeManager.Changed += ApplyFrameTheme;
        Closed += (_, _) => ThemeManager.Changed -= ApplyFrameTheme;
    }

    /// <summary>
    /// Let translucent brushes show the desktop through them. Set before the window is shown.
    /// </summary>
    /// <remarks>
    /// <para>Not <c>AllowsTransparency</c>. That makes a layered window: WPF then renders in
    /// software and copies every frame across, the system frame, shadow and Snap go with
    /// <c>WindowStyle.None</c>, and none of it can be switched off again while the window
    /// lives. This keeps the ordinary hardware-composited window and only tells the render
    /// target to clear to transparent instead of black — the frame is already extended
    /// across the client area (<c>GlassFrameThickness = -1</c>), so wherever WPF paints
    /// less than opaque, DWM blends the desktop in. With opaque brushes it is invisible,
    /// which is why it costs nothing at the default.</para>
    /// </remarks>
    public bool AllowSeeThrough { get; set; }

    private void ClearCompositionBackground()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource { CompositionTarget: { } target } source) return;

        // Clear to transparent instead of black…
        target.BackgroundColor = Colors.Transparent;

        // …and ask DWM to honour the alpha WPF hands it. "Blur behind" has not blurred
        // anything since Windows 8; what is left of it is exactly this, per-pixel
        // composition of an ordinary hardware-rendered window. The region is deliberately
        // empty, which is the long-standing way of saying "no blur, just the alpha".
        var region = CreateRectRgn(0, 0, -1, -1);
        try
        {
            var blur = new BlurBehind { Flags = 0x1 | 0x2, Enable = true, Region = region };
            DwmEnableBlurBehindWindow(source.Handle, ref blur);
        }
        catch (Exception)
        {
            // No DWM, no see-through: the brushes simply composite over black.
        }
        finally
        {
            if (region != IntPtr.Zero) DeleteObject(region);
        }

        ApplySeeThroughFrame();
    }

    private bool _seeThrough;

    /// <summary>
    /// Whether any of the window is currently less than solid. Swaps the frame to suit.
    /// </summary>
    /// <remarks>
    /// <para>Found by looking, not from documentation. With the frame extended across the
    /// client area — which is what gives a solid window its shadow and its drawn border —
    /// a translucent brush does not reveal the desktop. It reveals DWM's own idea of the
    /// window: a black sheet with the system's ghost caption buttons on it. So the frame
    /// comes out of the client area only while something is actually see-through, and goes
    /// back the moment the slider returns to solid. The default look loses nothing.</para>
    /// </remarks>
    public bool SeeThrough
    {
        get => _seeThrough;
        set
        {
            if (_seeThrough == value) return;
            _seeThrough = value;
            ApplySeeThroughFrame();
        }
    }

    private void ApplySeeThroughFrame()
    {
        if (!AllowSeeThrough) return;

        if (_seeThrough)
        {
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 38,
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = false,
            });
        }
        else
        {
            ClearValue(WindowChrome.WindowChromeProperty);      // back to the style's full frame
        }

        // Without an extended frame Windows 11 squares the corners off; ask for them back.
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            var round = 2;                                      // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, CornerPreference, ref round, sizeof(int));
        }
        catch (Exception) { }
    }

    private const int CornerPreference = 33;       // DWMWA_WINDOW_CORNER_PREFERENCE (Windows 11)

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind
    {
        public uint Flags;                                   // DWM_BB_ENABLE | DWM_BB_BLURREGION
        [MarshalAs(UnmanagedType.Bool)] public bool Enable;
        public IntPtr Region;
        [MarshalAs(UnmanagedType.Bool)] public bool TransitionOnMaximized;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr window, ref BlurBehind blur);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    /// <summary>Controls shown at the right of the title bar, before the caption buttons.</summary>
    public object? TitleBarContent
    {
        get => GetValue(TitleBarContentProperty);
        set => SetValue(TitleBarContentProperty, value);
    }

    /// <summary>Controls shown at the left of the title bar, between the icon and the title.</summary>
    public object? TitleBarLeading
    {
        get => GetValue(TitleBarLeadingProperty);
        set => SetValue(TitleBarLeadingProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _root = GetTemplateChild("PART_Root") as FrameworkElement;
        FitToScreen();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        FitToScreen();
    }

    /// <summary>
    /// Pull the content back inside the monitor while maximised.
    /// </summary>
    /// <remarks>
    /// Windows sizes a maximised window so its resize border hangs off every edge, where it
    /// cannot be grabbed. With a system title bar nobody notices; with a custom one, the top
    /// of the bar and the last few pixels of every edge are simply cut off.
    /// </remarks>
    private void FitToScreen()
    {
        if (_root is null) return;

        if (WindowState != WindowState.Maximized)
        {
            _root.Margin = new Thickness(0);
            return;
        }

        var frame = SystemParameters.WindowResizeBorderThickness;
        const double padded = 4;      // SM_CXPADDEDBORDER, in device-independent pixels
        _root.Margin = new Thickness(frame.Left + padded, frame.Top + padded,
                                     frame.Right + padded, frame.Bottom + padded);
    }

    // ── frame theme ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ask DWM to draw the frame's one-pixel border and its shadow for our theme, not the
    /// system's. Best-effort: older builds ignore attributes they do not know.
    /// </summary>
    private void ApplyFrameTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            var dark = ThemeManager.IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int));

            // COLORREF is 0x00BBGGRR. The panel colour, so the frame's edge meets the bar.
            var border = ThemeManager.IsDark ? 0x00332822 : 0x00C2D3D9;
            DwmSetWindowAttribute(handle, BorderColor, ref border, sizeof(int));
        }
        catch (Exception)
        {
            // dwmapi is absent or refuses: the window is still a window.
        }
    }

    private const int UseImmersiveDarkMode = 20;   // DWMWA_USE_IMMERSIVE_DARK_MODE
    private const int BorderColor = 34;            // DWMWA_BORDER_COLOR (Windows 11)

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
