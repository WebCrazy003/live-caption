using System.Runtime.InteropServices;
using System.Windows;
using LocalCaption.Core.Data;

namespace LocalCaption.App;

/// <summary>
/// Remembers where the window was, and refuses to restore it somewhere invisible.
/// </summary>
/// <remarks>
/// <b>SPEC-WINDOWS.md §7.3.</b> The always-on-top and opacity features are cut, so this is
/// all that is left of window behaviour — but the validation matters: laptops get docked and
/// undocked constantly, and a frame saved on a second monitor would otherwise restore
/// off-screen with no way to drag it back. On this machine, with an HDMI monitor and a
/// remote session that comes and goes, it will fire.
/// </remarks>
public static class WindowPlacement
{
    // Small enough to sit beside a video call. The layout is built to survive it: the
    // sidebar steps aside, the toolbar scrolls, the controls wrap, and Settings lives in the
    // title bar — which is the point of allowing it, where 720 px used to be the floor.
    private const double MinimumWidth = 400;
    private const double MinimumHeight = 320;

    public static void Restore(Window window, Config config)
    {
        window.MinWidth = MinimumWidth;
        window.MinHeight = MinimumHeight;
        window.Width = Math.Max(MinimumWidth, config.Window.Width);
        window.Height = Math.Max(MinimumHeight, config.Window.Height);

        if (config.Window.X is not { } x || config.Window.Y is not { } y)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        if (!IntersectsAMonitor(x, y, window.Width, window.Height))
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = x;
        window.Top = y;
    }

    public static void Save(Window window, Config config)
    {
        // Restore bounds, not the maximised frame — reopening maximised at the size of a
        // monitor that is no longer attached is the bug this avoids.
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        if (double.IsNaN(bounds.Width) || bounds.Width <= 0) return;

        config.Window.Width = bounds.Width;
        config.Window.Height = bounds.Height;
        config.Window.X = bounds.X;
        config.Window.Y = bounds.Y;
    }

    /// <summary>At least a corner of the frame has to land on a connected display.</summary>
    private static bool IntersectsAMonitor(double x, double y, double width, double height)
    {
        var rect = new Rect(x, y, Math.Max(1, width), Math.Max(1, height));
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                     SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!virtualScreen.IntersectsWith(rect)) return false;

        // The virtual screen is a bounding box over every monitor, so an L-shaped
        // arrangement leaves gaps inside it that no display covers. Ask the OS which
        // monitor the frame lands on, and treat "the nearest one" as not good enough.
        var native = new NativeRect
        {
            Left = (int)rect.Left,
            Top = (int)rect.Top,
            Right = (int)rect.Right,
            Bottom = (int)rect.Bottom,
        };
        return MonitorFromRect(ref native, MonitorDefaultToNull) != IntPtr.Zero;
    }

    private const uint MonitorDefaultToNull = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
}
