using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LocalCaption.Core.Data;

namespace LocalCaption.App;

/// <summary>
/// Remembers where the window was, and refuses to restore it somewhere invisible.
/// </summary>
/// <remarks>
/// <para><b>SPEC-WINDOWS.md §7.3.</b> The always-on-top and opacity features are cut, so this is
/// all that is left of window behaviour — but the validation matters: laptops get docked and
/// undocked constantly, and a frame saved on a second monitor would otherwise restore
/// off-screen with no way to drag it back.</para>
/// <para><b>Position is kept in physical pixels, size in DIPs</b> (SPEC-16, Compatibility
/// target: several monitors at different scales). The app is per-monitor DPI aware
/// (app.manifest), so a DIP means a different number of pixels on each monitor and WPF
/// converts a window's Left/Top with the DPI of whichever monitor the window happens to be
/// created on — which, before it is shown, is not the one it is being restored to. A saved
/// DIP position from a 150 % second screen therefore came back on the wrong screen, or off
/// every screen. Physical screen coordinates name one spot whatever the scales, so the
/// window is moved there with <c>SetWindowPos</c> before it is first shown, and Windows
/// rescales it for that monitor. Size stays in DIPs, so it looks the same size anywhere.</para>
/// <para>A <c>window.x</c>/<c>y</c> written by an older build (DIPs at the system scale) reads
/// as physical pixels once: identical at 100 %, nearer the top-left otherwise, and still
/// checked against the monitors like any other value.</para>
/// </remarks>
public static class WindowPlacement
{
    // Small enough to sit beside a video call. The layout is built to survive it: the
    // sidebar steps aside, the toolbar scrolls, the controls wrap, and Settings lives in the
    // title bar — which is the point of allowing it, where 720 px used to be the floor.
    //
    // 360 wide is the Mac's minimum (specs/SPEC-16 C11); below 480 the title bar sheds its
    // icon, the theme button and the divider and narrows its buttons (MainWindow.FitTitleBar),
    // or the window controls would not fit. The height stays 320, not the Mac's 240: Windows
    // wraps the transport bar rather than shedding its labels (a decided difference), and at
    // 360–400 wide it wraps to three rows, so header, toolbar, three rows of buttons and the
    // status bar already take about 300 of 320 — at 240 the captions would get nothing.
    private const double MinimumWidth = 360;
    private const double MinimumHeight = 320;

    public static void Restore(Window window, Config config)
    {
        window.MinWidth = MinimumWidth;
        window.MinHeight = MinimumHeight;
        window.Width = Math.Max(MinimumWidth, config.Window.Width);
        window.Height = Math.Max(MinimumHeight, config.Window.Height);

        if (config.Window.X is not { } x || config.Window.Y is not { } y ||
            double.IsNaN(x) || double.IsNaN(y) || !TitleBarOnAMonitor(x, y))
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        // A first guess, right on a single 100 % monitor; corrected below before anything is drawn.
        window.Left = x;
        window.Top = y;

        void Place(object? sender, EventArgs e)
        {
            window.SourceInitialized -= Place;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), 0, 0,
                         NoSize | NoZOrder | NoActivate);
        }
        window.SourceInitialized += Place;
    }

    public static void Save(Window window, Config config)
    {
        // Restore bounds, not the maximised frame — reopening maximised at the size of a
        // monitor that is no longer attached is the bug this avoids.
        var normal = window.WindowState == WindowState.Normal;
        var bounds = normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        if (double.IsNaN(bounds.Width) || bounds.Width <= 0) return;

        config.Window.Width = bounds.Width;
        config.Window.Height = bounds.Height;

        // The position in physical pixels: exact from the window itself while it is normal;
        // while maximised or minimised, the restore position in this window's DIPs scaled by
        // its current DPI — the restore spot is on the monitor it is maximised on.
        var hwnd = new WindowInteropHelper(window).Handle;
        if (normal && hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
        {
            config.Window.X = rect.Left;
            config.Window.Y = rect.Top;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        config.Window.X = Math.Round(bounds.X * dpi.DpiScaleX);
        config.Window.Y = Math.Round(bounds.Y * dpi.DpiScaleY);
    }

    /// <summary>
    /// Whether the left part of the title bar at physical (x, y) lands on a connected display —
    /// the part that has to be there for the window to be dragged back.
    /// </summary>
    /// <remarks>
    /// Asked of the OS rather than of the virtual-screen rectangle: that is a bounding box
    /// over every monitor, so an L-shaped or mixed-height arrangement leaves gaps inside it
    /// that no display covers. "The nearest monitor" is deliberately not good enough.
    /// </remarks>
    private static bool TitleBarOnAMonitor(double x, double y)
    {
        var strip = new NativeRect
        {
            Left = (int)Math.Round(x),
            Top = (int)Math.Round(y),
            Right = (int)Math.Round(x) + 160,
            Bottom = (int)Math.Round(y) + 32,
        };
        try { return MonitorFromRect(ref strip, MonitorDefaultToNull) != IntPtr.Zero; }
        catch (Exception) { return false; }
    }

    private const uint MonitorDefaultToNull = 0;
    private const uint NoSize = 0x0001, NoZOrder = 0x0004, NoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
