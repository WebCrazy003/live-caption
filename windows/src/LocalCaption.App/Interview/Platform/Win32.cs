using System.Runtime.InteropServices;
using LocalCaption.Interview;

namespace LocalCaption.App.Interview.Platform;

/// <summary>
/// Win32 declarations shared by the interview platform services. Every one exists on Windows 10
/// 1809 (build 17763), the oldest supported release (SPEC-16 "Compatibility target"); the
/// minimum version is noted beside each. Structs use only fixed-size fields and
/// <see cref="IntPtr"/>, so they lay out the same on x64 and ARM64.
/// </summary>
internal static class Win32
{
    /// <summary><c>RECT</c>: left/top inclusive, right/bottom exclusive.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;

        public readonly PixelRect ToPixelRect() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X, Y;
    }

    /// <summary><c>HWND_MESSAGE</c>: the parent that makes a window message-only (Windows 2000+).</summary>
    public static readonly IntPtr MessageOnlyParent = new(-3);

    /// <summary><c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</c> (Windows 10 1703+).</summary>
    public static readonly IntPtr PerMonitorAwareV2 = new(-4);

    /// <summary>
    /// Run Win32 geometry calls as a per-monitor-aware thread, so every coordinate they take or
    /// return is a physical pixel whatever the process manifest says, then put the thread back.
    /// </summary>
    /// <remarks>
    /// <c>SetThreadDpiAwarenessContext</c> (Windows 10 1607+) changes how <i>this thread's</i> calls
    /// are virtualised; it does not touch existing windows. Windows created while it is in force
    /// would take its awareness, which WPF does not expect — so no window is created inside a
    /// scope, and the scope never spans an <c>await</c>.
    /// </remarks>
    public readonly struct PhysicalPixelsScope : IDisposable
    {
        private readonly IntPtr _previous;

        private PhysicalPixelsScope(IntPtr previous) => _previous = previous;

        public static PhysicalPixelsScope Enter()
        {
            try
            {
                return new PhysicalPixelsScope(SetThreadDpiAwarenessContext(PerMonitorAwareV2));
            }
            catch (EntryPointNotFoundException)
            {
                return default;     // older than 1607: the process awareness stands (not a supported OS anyway)
            }
        }

        public void Dispose()
        {
            if (_previous != IntPtr.Zero) SetThreadDpiAwarenessContext(_previous);
        }
    }

    /// <summary>Windows 10 1607+. Returns the previous context, or <c>NULL</c> if the argument was invalid.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    /// <summary>Windows 2000+. With a <c>PhysicalPixelsScope</c> in force: physical pixels.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>Windows 2000+. Allowed while this process owns the foreground (or received the last input, e.g. a hotkey).</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr window);

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr window);

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr window, int command);

    public const int SwHide = 0;            // SW_HIDE
    public const int SwShowNoActivate = 8;  // SW_SHOWNA: show in its current size, position and state, without activating

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    public static readonly IntPtr HwndTopmost = new(-1);
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpNoZOrder = 0x0004;

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    /// <summary>Windows 2000+.</summary>
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>Windows Vista+. Waits for the next desktop composition pass.</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    /// <summary>Windows Vista+. Attributes used: 3 (transitions, Vista+), 33 (corner preference, Windows 11 22000+; fails harmlessly before).</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>Windows Vista+. Attributes used: 9 (extended frame bounds, Vista+), 14 (cloaked, Windows 8+).</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);

    /// <summary>Windows Vista+ (the <c>DWMWA_CLOAKED</c> overload).</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    public const int DwmaTransitionsForceDisabled = 3;   // DWMWA_TRANSITIONS_FORCEDISABLED
    public const int DwmaExtendedFrameBounds = 9;        // DWMWA_EXTENDED_FRAME_BOUNDS: physical pixels, whatever the caller's DPI awareness
    public const int DwmaCloaked = 14;                   // DWMWA_CLOAKED
    public const int DwmaWindowCornerPreference = 33;    // DWMWA_WINDOW_CORNER_PREFERENCE
    public const int DwmcpDoNotRound = 1;                // DWMWCP_DONOTROUND

    /// <summary>Turn off DWM's show/hide animation, so a hidden window is gone in the next frame. Best effort.</summary>
    public static void DisableTransitions(IntPtr window, bool disabled)
    {
        try
        {
            var value = disabled ? 1 : 0;
            DwmSetWindowAttribute(window, DwmaTransitionsForceDisabled, ref value, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
