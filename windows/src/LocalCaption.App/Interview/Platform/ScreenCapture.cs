using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LocalCaption.Interview;
using ShapePath = System.Windows.Shapes.Path;

namespace LocalCaption.App.Interview.Platform;

/// <summary>
/// The Screenshot hotkey / camera button (SPEC-16 §4.3): Windows' answer to the Mac's
/// <c>screencapture -i</c>. Hides the app, freezes the desktop, lets the user drag a rectangle
/// (Space: the window under the pointer; Esc or right-click: cancel), and returns it as PNG.
/// </summary>
/// <remarks>
/// <para><b>Pixels.</b> Everything is measured in physical pixels: the monitors and the capture
/// run inside <see cref="Win32.PhysicalPixelsScope"/>, so the frame is right on mixed-DPI
/// multi-monitor setups whatever DPI awareness the process manifest declares. The overlays are
/// WPF windows placed with <c>SetWindowPos</c> in the coordinate space their own awareness uses,
/// and the pointer is mapped to frame pixels by the ratio of the monitor's pixel size to the
/// overlay's size — never by a DPI value — so a drag lands on the same pixels it covers.</para>
/// <para><b>Focus.</b> The overlay takes the keyboard (Esc and Space must reach it). Before it
/// closes, the window that had the foreground — usually the meeting app — gets it back, and the
/// app's own windows reappear without activating (<c>SW_SHOWNA</c>).</para>
/// <para><b>Never throws.</b> Any failure is logged and returned as a cancel (<c>null</c>).</para>
/// </remarks>
public sealed class ScreenCapture : IScreenCapture
{
    /// <summary>How long a hidden window takes to leave the composed desktop: DWM drops it on the next frame; this is margin.</summary>
    private static readonly TimeSpan HideSettle = TimeSpan.FromMilliseconds(120);

    private readonly Dispatcher _dispatcher;
    private bool _busy;

    /// <summary>Create on the UI thread.</summary>
    public ScreenCapture() : this(Dispatcher.CurrentDispatcher) { }

    /// <param name="dispatcher">The UI thread's dispatcher: the overlays are its windows.</param>
    public ScreenCapture(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>
    /// False (default, like macOS): letting go of the mouse takes the shot. True: the selection
    /// stays up — drag again to redo — until Enter takes it.
    /// </summary>
    public bool ConfirmWithEnter { get; set; }

    /// <inheritdoc />
    public Task<byte[]?> SelectAreaAsync()
    {
        try
        {
            if (_dispatcher.CheckAccess()) return SelectOnUiThreadAsync();
            return _dispatcher.InvokeAsync(SelectOnUiThreadAsync).Task.Unwrap()
                .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default);
        }
        catch (Exception e)
        {
            Log($"could not start: {e.Message}");
            return Task.FromResult<byte[]?>(null);
        }
    }

    private async Task<byte[]?> SelectOnUiThreadAsync()
    {
        if (_busy) return null;
        _busy = true;
        var hidden = new HiddenWindows();
        var previous = IntPtr.Zero;

        // Our windows back without activation, and the foreground back to whoever had it — the
        // meeting app after a hotkey, our own window after the camera button. SetForegroundWindow
        // is allowed here because the overlay is still the foreground window; it is skipped when
        // the foreground never moved (a capture that failed before any overlay opened).
        void GiveBack()
        {
            hidden.RestoreAll();
            if (previous != IntPtr.Zero && Win32.IsWindow(previous) && Win32.GetForegroundWindow() != previous)
                Win32.SetForegroundWindow(previous);
        }

        try
        {
            // A modal dialog (Settings) owns the thread; hiding it would end or strand it.
            if (ComponentDispatcher.IsThreadModal)
            {
                Log("not started: a dialog is open");
                return null;
            }

            previous = Win32.GetForegroundWindow();
            hidden.HideAll();
            if (hidden.Any) await Task.Delay(HideSettle);
            FlushComposition();

            if (FrozenDesktop.Capture() is not { } frame) return null;

            var session = new SelectionSession(frame, ConfirmWithEnter, beforeClose: GiveBack);
            if (await session.RunAsync() is not { } selected) return null;

            var crop = selected.Intersect(frame.Bounds).Offset(-frame.Bounds.Left, -frame.Bounds.Top);
            if (crop.IsEmpty) return null;
            // Encoding a 4K crop takes a moment; the frame is frozen, so it can happen off the UI thread.
            byte[]? png = null;
            try { png = await Task.Run(() => Encode(frame.Image, crop)); }
            catch (Exception e) { Log($"background encode failed: {e.Message}"); }
            return png ?? Encode(frame.Image, crop);
        }
        catch (Exception e)
        {
            Log($"failed: {e.Message}");
            return null;
        }
        finally
        {
            if (hidden.Any)
            {
                try { GiveBack(); }     // a no-op when the session already did it
                catch (Exception e) { Log($"restoring the app failed: {e.Message}"); }
            }
            _busy = false;
        }
    }

    private static byte[]? Encode(BitmapSource frame, PixelRect crop)
    {
        try
        {
            var image = new FormatConvertedBitmap(
                new CroppedBitmap(frame, new Int32Rect(crop.Left, crop.Top, crop.Width, crop.Height)),
                PixelFormats.Bgr24, null, 0);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception e)
        {
            Log($"encoding failed: {e.Message}");
            return null;
        }
    }

    private static void FlushComposition()
    {
        try { Win32.DwmFlush(); }
        catch (Exception) { /* no DWM: nothing to wait for */ }
    }

    private static void Log(string line) => InterviewPlatformLog.Write("screenshot", line);

    // ── The app's own windows ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every visible top-level window of the UI thread — main window, panels, tooltips, the
    /// click-through handle — hidden natively and shown again natively.
    /// </summary>
    /// <remarks>
    /// <c>ShowWindow</c>, not <c>Window.Hide()</c>/<c>Show()</c>: WPF's <c>Show</c> activates a
    /// maximised window whatever <c>ShowActivated</c> says, and changing <c>Visibility</c> would
    /// run the app's visibility handlers. <c>SW_HIDE</c> then <c>SW_SHOWNA</c> keeps size, state
    /// and z-order and never activates; WPF's <c>Visibility</c> is untouched throughout (it only
    /// follows <c>WM_SHOWWINDOW</c> when an owner minimises) and its renderer pauses and resumes on
    /// that message. DWM transitions are switched off meanwhile so a window vanishes in one frame
    /// instead of fading out into the capture.
    /// </remarks>
    private sealed class HiddenWindows
    {
        private readonly List<IntPtr> _windows = [];

        public bool Any => _windows.Count > 0;

        public void HideAll()
        {
            var found = new List<IntPtr>();
            EnumThreadWindowsProc collect = (hwnd, _) =>
            {
                if (Win32.IsWindowVisible(hwnd) && !Win32.IsIconic(hwnd)) found.Add(hwnd);
                return true;
            };
            EnumThreadWindows(Win32.GetCurrentThreadId(), collect, IntPtr.Zero);
            GC.KeepAlive(collect);

            foreach (var hwnd in found)
            {
                Win32.DisableTransitions(hwnd, true);
                Win32.ShowWindow(hwnd, Win32.SwHide);
                _windows.Add(hwnd);
            }
        }

        public void RestoreAll()
        {
            // Back to front, so each lands where it was relative to the others.
            for (var i = _windows.Count - 1; i >= 0; i--)
            {
                var hwnd = _windows[i];
                try
                {
                    if (!Win32.IsWindow(hwnd)) continue;
                    Win32.ShowWindow(hwnd, Win32.SwShowNoActivate);
                    Win32.DisableTransitions(hwnd, false);
                }
                catch (Exception e)
                {
                    Log($"restoring a window failed: {e.Message}");
                }
            }
            _windows.Clear();
        }
    }

    // ── The frozen desktop ───────────────────────────────────────────────────────────────

    /// <summary>One monitor: its physical rectangle, and the same rectangle in the coordinates our windows use.</summary>
    private sealed record Display(PixelRect Physical, PixelRect WindowSpace);

    /// <summary>The desktop as it was when the overlay opened, and what Space can pick on it.</summary>
    private sealed record FrozenDesktop(BitmapSource Image, PixelRect Bounds, IReadOnlyList<Display> Displays, IReadOnlyList<WindowTarget> Windows)
    {
        public IReadOnlyList<PixelRect> PhysicalMonitors { get; } = Displays.Select(d => d.Physical).ToList();

        public static FrozenDesktop? Capture()
        {
            List<(IntPtr Handle, PixelRect Physical)> monitors;
            List<WindowTarget> windows;
            PixelRect bounds;
            BitmapSource? image;
            using (Win32.PhysicalPixelsScope.Enter())
            {
                monitors = EnumerateMonitors();
                if (monitors.Count == 0)
                {
                    Log("no monitors");
                    return null;
                }
                bounds = CaptureGeometry.UnionOf(monitors.Select(m => m.Physical));
                windows = SnapshotWindows();
                image = Grab(bounds, monitors.Select(m => m.Physical));
            }
            if (image is null) return null;

            // The same monitors as this process's windows see them (identical under a per-monitor
            // manifest; scaled under a system-aware one) — for placing the overlays.
            var displays = monitors.Select(m => new Display(m.Physical, MonitorRect(m.Handle) ?? m.Physical)).ToList();
            return new FrozenDesktop(image, bounds, displays, windows);
        }

        private static List<(IntPtr, PixelRect)> EnumerateMonitors()
        {
            var list = new List<(IntPtr, PixelRect)>();
            MonitorEnumProc collect = (monitor, _, _, _) =>
            {
                if (MonitorRect(monitor) is { } r && !r.IsEmpty) list.Add((monitor, r));
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero);
            GC.KeepAlive(collect);
            return list;
        }

        /// <summary><c>rcMonitor</c>, in the coordinate space of the calling thread's DPI awareness.</summary>
        private static PixelRect? MonitorRect(IntPtr monitor)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            return GetMonitorInfo(monitor, ref info) ? info.Monitor.ToPixelRect() : null;
        }

        /// <summary>
        /// Copy every monitor out of the screen DC into one top-down 32-bit DIB section the size
        /// of the virtual desktop's bounding box (gaps between monitors stay black), then into a
        /// frozen <see cref="BitmapSource"/>. Must run inside a <see cref="Win32.PhysicalPixelsScope"/>:
        /// the screen DC's coordinates follow the calling thread's awareness.
        /// </summary>
        private static BitmapSource? Grab(PixelRect bounds, IEnumerable<PixelRect> monitors)
        {
            long size = (long)bounds.Width * bounds.Height * 4;
            if (bounds.IsEmpty || size > int.MaxValue)
            {
                Log($"desktop too large to freeze ({bounds.Width}×{bounds.Height})");
                return null;
            }

            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            IntPtr memory = IntPtr.Zero, section = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                memory = CreateCompatibleDC(screen);
                if (memory == IntPtr.Zero) return null;
                var header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = bounds.Width,
                    Height = -bounds.Height,        // negative: top-down rows, as BitmapSource wants
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,                // BI_RGB: no colour table follows, so the header alone is a valid BITMAPINFO
                };
                section = CreateDIBSection(screen, ref header, 0 /* DIB_RGB_COLORS */, out var bits, IntPtr.Zero, 0);
                if (section == IntPtr.Zero || bits == IntPtr.Zero) return null;
                previous = SelectObject(memory, section);

                foreach (var m in monitors)
                {
                    // CAPTUREBLT: include layered windows (tooltips, translucent overlays) as seen on screen.
                    if (!BitBlt(memory, m.Left - bounds.Left, m.Top - bounds.Top, m.Width, m.Height,
                                screen, m.Left, m.Top, SourceCopy | CaptureBlt))
                    {
                        // Fails while the secure desktop (UAC, lock screen) is up.
                        Log($"BitBlt failed ({Marshal.GetLastPInvokeError()})");
                        return null;
                    }
                }
                GdiFlush();     // GDI may batch; the bits must be complete before they are read directly

                // Bgr32: the fourth byte BitBlt leaves is undefined, so it is ignored. Create copies the bits.
                var image = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgr32, null,
                                                bits, (int)size, bounds.Width * 4);
                image.Freeze();
                return image;
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memory, previous);
                if (section != IntPtr.Zero) DeleteObject(section);
                if (memory != IntPtr.Zero) DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        /// <summary>
        /// Visible top-level windows of other processes, front to back (<c>EnumWindows</c> order),
        /// with their visible frames in physical pixels. Taken with the frame, so Space picks what
        /// the frozen picture shows — and because the overlay itself would be under the pointer for
        /// <c>WindowFromPoint</c>. Skipped like <c>WindowFromPoint</c> skips them: hidden, minimised,
        /// cloaked (other virtual desktops, suspended Store apps) and click-through windows.
        /// </summary>
        private static List<WindowTarget> SnapshotWindows()
        {
            var list = new List<WindowTarget>();
            var self = (uint)Environment.ProcessId;
            var className = new char[64];
            EnumWindowsProc collect = (hwnd, _) =>
            {
                try
                {
                    if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return true;
                    GetWindowThreadProcessId(hwnd, out var process);
                    if (process == self) return true;
                    if ((GetWindowLongPtr(hwnd, ExStyleIndex).ToInt64() & ExTransparent) != 0) return true;
                    if (Win32.DwmGetWindowAttribute(hwnd, Win32.DwmaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                    // The visible frame: GetWindowRect would include the invisible resize borders of Windows 10/11.
                    if (Win32.DwmGetWindowAttribute(hwnd, Win32.DwmaExtendedFrameBounds, out Win32.Rect rect, Marshal.SizeOf<Win32.Rect>()) != 0
                        && !Win32.GetWindowRect(hwnd, out rect))
                        return true;
                    var bounds = rect.ToPixelRect();
                    if (bounds.IsEmpty) return true;
                    var length = GetClassName(hwnd, className, className.Length);
                    var cls = length > 0 ? new string(className, 0, length) : "";
                    list.Add(new WindowTarget(bounds, cls is "Progman" or "WorkerW"));
                }
                catch (Exception)
                {
                    // Never let an exception unwind into user32.
                }
                return true;
            };
            EnumWindows(collect, IntPtr.Zero);
            GC.KeepAlive(collect);
            return list;
        }
    }

    // ── Selecting ────────────────────────────────────────────────────────────────────────

    /// <summary>One overlay per monitor, the shared outcome, and the hand-back of focus.</summary>
    private sealed class SelectionSession
    {
        private readonly FrozenDesktop _frame;
        private readonly Action _beforeClose;
        private readonly List<Overlay> _overlays = [];
        private readonly TaskCompletionSource<PixelRect?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private (Overlay Overlay, PixelRect Rect)? _pending;

        public SelectionSession(FrozenDesktop frame, bool confirmWithEnter, Action beforeClose)
        {
            _frame = frame;
            ConfirmWithEnter = confirmWithEnter;
            _beforeClose = beforeClose;
        }

        public bool ConfirmWithEnter { get; }
        public bool Finished { get; private set; }

        public Task<PixelRect?> RunAsync()
        {
            try
            {
                foreach (var display in _frame.Displays) _overlays.Add(new Overlay(this, _frame, display));
                foreach (var overlay in _overlays) overlay.Show();
                _overlays[PointerDisplay()].TakeKeyboard();
            }
            catch (Exception e)
            {
                Log($"the overlay failed: {e.Message}");
                Finish(null);
            }
            return _result.Task;
        }

        /// <summary>End the session: give the app and the focus back, then close every overlay.</summary>
        public void Finish(PixelRect? selection)
        {
            if (Finished) return;
            Finished = true;
            try { _beforeClose(); }
            catch (Exception e) { Log($"handing focus back failed: {e.Message}"); }
            foreach (var overlay in _overlays)
            {
                try { overlay.CloseQuietly(); }
                catch (Exception e) { Log($"closing an overlay failed: {e.Message}"); }
            }
            _result.TrySetResult(selection);
        }

        /// <summary>A drag began on <paramref name="overlay"/>: any selection left waiting elsewhere goes.</summary>
        public void BeginDrag(Overlay overlay)
        {
            if (_pending is { } p && p.Overlay != overlay) p.Overlay.ClearSelection();
            _pending = null;
        }

        /// <summary>A drag ended with a selection.</summary>
        public void Selected(Overlay overlay, PixelRect rect)
        {
            if (ConfirmWithEnter) _pending = (overlay, rect);
            else Finish(rect);
        }

        /// <summary>Enter: take the waiting selection, if there is one.</summary>
        public void Confirm()
        {
            if (_pending is { } p) Finish(p.Rect);
        }

        /// <summary>Space: the window under the pointer, as the frozen frame shows it.</summary>
        public void CaptureWindowUnderPointer()
        {
            if (PointerPosition() is not { } p) return;
            var monitor = _frame.PhysicalMonitors[CaptureGeometry.MonitorAt(_frame.PhysicalMonitors, p.X, p.Y)];
            if (CaptureGeometry.WindowAt(_frame.Windows, p.X, p.Y, _frame.Bounds, monitor) is { } rect) Finish(rect);
        }

        /// <summary>
        /// An overlay lost activation. Moving between overlays is fine; anything else — Alt+Tab,
        /// Win+L, a notification taking focus — cancels, as clicking away from macOS's selector does.
        /// Checked after the activation messages settle, since the next overlay activates just after.
        /// </summary>
        public void OverlayDeactivated(Dispatcher dispatcher)
        {
            dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (Finished) return;
                var foreground = Win32.GetForegroundWindow();
                if (_overlays.Any(o => o.Handle == foreground)) return;
                Finish(null);
            }));
        }

        private int PointerDisplay() =>
            PointerPosition() is { } p ? CaptureGeometry.MonitorAt(_frame.PhysicalMonitors, p.X, p.Y) : 0;

        private static Win32.Point? PointerPosition()
        {
            using (Win32.PhysicalPixelsScope.Enter())
                return Win32.GetCursorPos(out var p) ? p : null;
        }
    }

    /// <summary>
    /// A borderless top-most window over one monitor showing that monitor's part of the frozen
    /// frame, dimmed except for the selection.
    /// </summary>
    private sealed class Overlay : Window
    {
        private const int DpiChangedMessage = 0x02E0;       // WM_DPICHANGED: lParam → RECT, the suggested new window rect
        private const int DisplayChangeMessage = 0x007E;    // WM_DISPLAYCHANGE

        private static readonly Brush Shade = Frozen(new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0)));
        private static readonly Brush HintBackground = Frozen(new SolidColorBrush(Color.FromArgb(0xD9, 0x20, 0x20, 0x20)));

        private readonly SelectionSession _session;
        private readonly Display _display;
        private readonly Grid _root;
        private readonly ShapePath _shade;
        private readonly Rectangle _outline;
        private readonly Border _hint;
        private Point? _dragStart;
        private PixelRect _selection;
        private bool _closingQuietly;

        public Overlay(SelectionSession session, FrozenDesktop frame, Display display)
        {
            _session = session;
            _display = display;

            Title = "Local Caption screenshot";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;       // no WS_THICKFRAME: the client area is the whole window
            ShowInTaskbar = false;
            ShowActivated = false;                  // the one under the pointer is activated explicitly
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            // A first guess in DIPs; OnSourceInitialized places it exactly before it is ever visible.
            Left = display.WindowSpace.Left;
            Top = display.WindowSpace.Top;
            Width = Math.Max(1, display.WindowSpace.Width);
            Height = Math.Max(1, display.WindowSpace.Height);
            Background = Brushes.Black;
            Cursor = Cursors.Cross;
            ForceCursor = true;
            Focusable = true;
            UseLayoutRounding = true;

            var source = new CroppedBitmap(frame.Image, new Int32Rect(
                display.Physical.Left - frame.Bounds.Left, display.Physical.Top - frame.Bounds.Top,
                display.Physical.Width, display.Physical.Height));
            source.Freeze();
            var picture = new Image { Source = source, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);

            _shade = new ShapePath { Fill = Shade, IsHitTestVisible = false };
            _outline = new Rectangle
            {
                Stroke = Brushes.White, StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            };
            var layer = new Canvas { IsHitTestVisible = false };
            layer.Children.Add(_outline);

            _hint = new Border
            {
                Background = HintBackground,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 7, 14, 8),
                Margin = new Thickness(0, 24, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Foreground = Brushes.White,
                    FontSize = 13,
                    Text = session.ConfirmWithEnter
                        ? "Drag to select, Enter to take it   ·   Space: the window under the pointer   ·   Esc: cancel"
                        : "Drag to select an area   ·   Space: the window under the pointer   ·   Esc: cancel",
                },
            };

            _root = new Grid();
            _root.Children.Add(picture);
            _root.Children.Add(_shade);
            _root.Children.Add(layer);
            _root.Children.Add(_hint);
            _root.SizeChanged += (_, _) => UpdateShade();
            Content = _root;

            SourceInitialized += (_, _) => OnSourceInitialized();
            Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(EnsurePlaced));
            MouseLeftButtonDown += OnLeftDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnLeftUp;
            LostMouseCapture += (_, _) => AbandonDrag();
            MouseRightButtonDown += (_, e) =>
            {
                e.Handled = true;
                _session.Finish(null);
            };
            PreviewKeyDown += OnKey;
            Deactivated += (_, _) => _session.OverlayDeactivated(Dispatcher);
            Closed += (_, _) =>
            {
                if (!_closingQuietly) _session.Finish(null);    // Alt+F4, or Windows closing it
            };
        }

        public IntPtr Handle { get; private set; }

        public void TakeKeyboard()
        {
            if (!Activate()) Log("the overlay could not take the foreground; click it to use the keyboard");
            Focus();
            Keyboard.Focus(this);
        }

        public void CloseQuietly()
        {
            _closingQuietly = true;
            Close();
        }

        public void ClearSelection()
        {
            _selection = default;
            _outline.Visibility = Visibility.Collapsed;
            UpdateShade();
        }

        private void OnSourceInitialized()
        {
            Handle = new WindowInteropHelper(this).Handle;
            Win32.DisableTransitions(Handle, true);
            try
            {
                var square = Win32.DwmcpDoNotRound;     // Windows 11 would round a full-screen popup's corners
                Win32.DwmSetWindowAttribute(Handle, Win32.DwmaWindowCornerPreference, ref square, sizeof(int));
            }
            catch (Exception) { /* Windows 10: no such attribute */ }
            HwndSource.FromHwnd(Handle)?.AddHook(OnMessage);
            Place();
        }

        /// <summary>
        /// Cover the monitor exactly. In the window's own coordinate space
        /// (<see cref="Display.WindowSpace"/>), which is physical pixels for a per-monitor-aware
        /// window. Moving onto a monitor of another scale raises <c>WM_DPICHANGED</c>; see
        /// <see cref="OnMessage"/>.
        /// </summary>
        private void Place()
        {
            var r = _display.WindowSpace;
            if (!Win32.SetWindowPos(Handle, Win32.HwndTopmost, r.Left, r.Top, r.Width, r.Height, Win32.SwpNoActivate))
                Log($"placing the overlay failed ({Marshal.GetLastPInvokeError()})");
        }

        /// <summary>Once laid out: if anything moved the window (a DPI change applied late), put it back.</summary>
        private void EnsurePlaced()
        {
            if (Handle == IntPtr.Zero || !Win32.GetWindowRect(Handle, out var now)) return;
            if (now.ToPixelRect() != _display.WindowSpace) Place();
        }

        private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                if (message == DpiChangedMessage && lParam != IntPtr.Zero)
                {
                    // WPF answers WM_DPICHANGED by rescaling its content and then moving the window
                    // to the suggested rect — the old size scaled by the DPI ratio, which no longer
                    // covers the monitor. This hook runs before WPF's handler (HwndSource calls
                    // hooks added by AddHook first), so replacing the suggestion lets WPF update its
                    // DPI and apply our rect. Not marked handled: WPF must still see the new DPI.
                    var r = _display.WindowSpace;
                    Marshal.StructureToPtr(new Win32.Rect { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom }, lParam, false);
                }
                else if (message == DisplayChangeMessage)
                {
                    // Monitors were added, removed or rearranged: the frozen frame no longer matches.
                    Dispatcher.BeginInvoke(new Action(() => _session.Finish(null)));
                }
            }
            catch (Exception)
            {
                // Never let an exception unwind into user32.
            }
            return IntPtr.Zero;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (key)
            {
                case Key.Escape:
                    _session.Finish(null);
                    break;
                case Key.Space:
                    if (_dragStart is null) _session.CaptureWindowUnderPointer();
                    break;
                case Key.Enter:
                    if (_dragStart is not null && CaptureGeometry.IsSelection(_selection)) _session.Finish(_selection);
                    else _session.Confirm();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        private void OnLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (_session.Finished) return;
            e.Handled = true;
            _session.BeginDrag(this);
            var at = e.GetPosition(_root);
            _dragStart = at;
            _hint.Visibility = Visibility.Collapsed;
            CaptureMouse();
            Drag(at);
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (_dragStart is null || !IsMouseCaptured) return;
            Drag(e.GetPosition(_root));
        }

        private void OnLeftUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragStart is null) return;
            e.Handled = true;
            Drag(e.GetPosition(_root));
            _dragStart = null;                      // before releasing: LostMouseCapture must not abandon it
            ReleaseMouseCapture();
            if (CaptureGeometry.IsSelection(_selection)) _session.Selected(this, _selection);
            else ClearSelection();                  // a click, not a drag: keep waiting
        }

        private void AbandonDrag()
        {
            if (_dragStart is null) return;
            _dragStart = null;
            ClearSelection();
        }

        private void Drag(Point to)
        {
            if (_dragStart is not { } from || _root.ActualWidth <= 0 || _root.ActualHeight <= 0) return;
            var scaleX = _display.Physical.Width / _root.ActualWidth;
            var scaleY = _display.Physical.Height / _root.ActualHeight;
            _selection = CaptureGeometry.FromDrag(from.X, from.Y, to.X, to.Y, scaleX, scaleY, _display.Physical);

            if (_selection.IsEmpty)
            {
                _outline.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Drawn from the pixel rectangle itself, so what is outlined is exactly what is taken.
                var local = _selection.Offset(-_display.Physical.Left, -_display.Physical.Top);
                Canvas.SetLeft(_outline, local.Left / scaleX);
                Canvas.SetTop(_outline, local.Top / scaleY);
                _outline.Width = local.Width / scaleX;
                _outline.Height = local.Height / scaleY;
                _outline.Visibility = Visibility.Visible;
            }
            UpdateShade();
        }

        private void UpdateShade()
        {
            var all = new RectangleGeometry(new Rect(0, 0, Math.Max(0, _root.ActualWidth), Math.Max(0, _root.ActualHeight)));
            if (_outline.Visibility != Visibility.Visible)
            {
                _shade.Data = all;
                return;
            }
            var hole = new RectangleGeometry(new Rect(Canvas.GetLeft(_outline), Canvas.GetTop(_outline), _outline.Width, _outline.Height));
            _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, all, hole);
        }

        private static Brush Frozen(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }

    // ── Win32 ────────────────────────────────────────────────────────────────────────────

    private const uint SourceCopy = 0x00CC0020;     // SRCCOPY
    private const uint CaptureBlt = 0x40000000;     // CAPTUREBLT
    private const int ExStyleIndex = -20;           // GWL_EXSTYLE
    private const long ExTransparent = 0x20;        // WS_EX_TRANSPARENT

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Win32.Rect Monitor;
        public Win32.Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);
    private delegate bool EnumThreadWindowsProc(IntPtr window, IntPtr data);

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    /// <summary>Windows 2000+. Coordinates follow the calling thread's DPI awareness.</summary>
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumThreadWindowsProc callback, IntPtr data);

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    /// <summary>64-bit user32 only (x64, ARM64 — the app's targets).</summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr window, [Out] char[] name, int maxCount);

    /// <summary>Windows 2000+. <c>NULL</c>: the whole screen (every monitor).</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    /// <summary>Windows 2000+.</summary>
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    /// <summary>Windows 2000+.</summary>
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    /// <summary>Windows 2000+. Takes a <c>BITMAPINFO*</c>; for 32-bit <c>BI_RGB</c> the header alone suffices.</summary>
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    /// <summary>Windows 2000+.</summary>
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    /// <summary>Windows 2000+.</summary>
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    /// <summary>Windows 2000+.</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint rop);

    /// <summary>Windows 2000+.</summary>
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}
