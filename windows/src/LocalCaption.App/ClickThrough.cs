using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace LocalCaption.App;

/// <summary>
/// Makes a window ignore the mouse, so clicks land on whatever is underneath it.
/// </summary>
/// <remarks>
/// <para>Pinned and see-through, the captions can sit on top of the call. This is the last
/// piece: with it on, the window is something you look through and click through, and the
/// meeting underneath behaves as if nothing were there.</para>
/// <para><b>A window that ignores the mouse cannot be clicked to make it stop.</b> So there
/// are three ways back, because any one of them can be unavailable: the shortcut (which a
/// virtual machine holding the keyboard will swallow), the small handle this class floats
/// over the title bar (which is its own window and does take clicks), and simply activating
/// the app — Alt+Tab or its taskbar button — which is taken to mean "I want to use this".</para>
/// </remarks>
public sealed class ClickThrough
{
    private const int ExStyle = -20;                 // GWL_EXSTYLE
    private const long Transparent = 0x20;           // WS_EX_TRANSPARENT
    private const long Layered = 0x80000;            // WS_EX_LAYERED
    private const uint ByAlpha = 0x2;                // LWA_ALPHA

    private const uint StyleChanging = 0x007C;        // WM_STYLECHANGING
    private const uint StyleChanged = 0x007D;         // WM_STYLECHANGED

    private readonly Window _window;
    private Handle? _handle;
    private DateTime _enabledAt;
    private WindowProcedure? _procedure;      // held in a field: the window calls it for as long as it lives
    private IntPtr _wpfProcedure;

    public ClickThrough(Window window)
    {
        _window = window;
        _window.Activated += (_, _) =>
        {
            // Turning it on from the title bar leaves the window active, and the click that
            // follows — through it, onto the call — deactivates it. Only an activation that
            // comes later is someone coming back.
            if (IsOn && (DateTime.UtcNow - _enabledAt).TotalMilliseconds > 600) Set(false);
        };
        _window.LocationChanged += (_, _) => _handle?.Follow(_window);
        _window.SizeChanged += (_, _) => _handle?.Follow(_window);
        _window.StateChanged += (_, _) => { if (_window.WindowState == WindowState.Minimized) Set(false); };
        _window.Closed += (_, _) => _handle?.Close();
    }

    public bool IsOn { get; private set; }

    /// <summary>Raised after every change, so buttons can show the state.</summary>
    public event Action? Changed;

    /// <summary>What the handle's tooltip says the way back is — the user's own shortcut.</summary>
    public string ShortcutHint { get; set; } = "";

    public void Toggle() => Set(!IsOn);

    public void Set(bool on)
    {
        if (IsOn == on) return;

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (_procedure is null)
        {
            _procedure = HoldStyles;
            _wpfProcedure = SetWindowLongPtr(hwnd, WindowProcedureIndex, Marshal.GetFunctionPointerForDelegate(_procedure));
        }

        // IsOn first: the hook below is what actually keeps the bits on, and it reads this.
        IsOn = on;
        _enabledAt = DateTime.UtcNow;

        var style = GetWindowLongPtr(hwnd, ExStyle).ToInt64();
        SetWindowLongPtr(hwnd, ExStyle, new IntPtr(on ? style | Transparent | Layered : style & ~Transparent & ~Layered));

        // Listeners first: one of them makes the window topmost, and a window that becomes
        // topmost goes in front of every other topmost window — including a handle shown a
        // moment earlier. That is how the handle came to be "only visible once clicked".
        Changed?.Invoke();

        if (on)
        {
            // Owned, not merely topmost: Windows keeps an owned window in front of its owner
            // whatever either of them does afterwards, and hides it when the owner minimises.
            _handle ??= new Handle(() => Set(false)) { Owner = _window };
            _handle.Hint = ShortcutHint;
            _handle.Show();
            _handle.Follow(_window);
        }
        else
        {
            _handle?.Hide();
        }
    }

    /// <summary>
    /// Keep the two style bits on for as long as click-through is.
    /// </summary>
    /// <remarks>
    /// <para>WS_EX_TRANSPARENT only falls through to <i>other applications</i> on a layered
    /// window. WPF will not have it: its render target answers every WM_STYLECHANGING by
    /// clearing WS_EX_LAYERED on any window that is not one of its own per-pixel-alpha ones,
    /// and it answers after any hook added through <c>HwndSource.AddHook</c>. Found the slow
    /// way — the style read back as transparent-but-not-layered, from inside and from another
    /// process, on this window and on a plain one, and every click still landed here.</para>
    /// <para>So this sits <i>above</i> WPF's window procedure instead of beside it: it lets
    /// WPF answer first and then puts the two bits back, which makes this the last word. It
    /// is installed the first time click-through is used and left for the life of the
    /// window — unpicking one link from a subclass chain is where that technique goes wrong.
    /// With click-through off it passes everything straight through.</para>
    /// <para>The layer gets full alpha the moment it appears; until a layered window has
    /// attributes it is not drawn at all. Full alpha changes nothing on screen: see-through
    /// is still per-pixel, by DWM.</para>
    /// </remarks>
    private IntPtr HoldStyles(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var result = CallWindowProc(_wpfProcedure, hwnd, message, wParam, lParam);

        try
        {
            // WPARAM is unsigned, so -20 can arrive sign-extended or as 0x00000000FFFFFFEC;
            // truncating to 32 bits reads both as what they mean.
            if (IsOn && unchecked((int)wParam.ToInt64()) == ExStyle)
            {
                if (message == StyleChanging)
                {
                    // STYLESTRUCT { DWORD styleOld; DWORD styleNew; }
                    Marshal.WriteInt32(lParam, 4, Marshal.ReadInt32(lParam, 4) | (int)(Transparent | Layered));
                }
                else if (message == StyleChanged)
                {
                    SetLayeredWindowAttributes(hwnd, 0, 255, ByAlpha);
                }
            }
        }
        catch (Exception)
        {
            // An exception must never cross back into user32.
        }
        return result;
    }

    private const int WindowProcedureIndex = -4;     // GWLP_WNDPROC

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// The way back: a small pill over the title bar that is a window of its own, and so
    /// still takes the click the main window now lets through.
    /// </summary>
    private sealed class Handle : Window
    {
        private readonly TextBlock _label;

        public Handle(Action exit)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;          // a 150-pixel pill; the cost is nothing
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            Cursor = Cursors.Hand;

            _label = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            _label.SetResourceReference(TextBlock.ForegroundProperty, "Accent.Text");

            var glyph = new TextBlock { Text = "\uE8B0", FontSize = 12, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(glyph);
            row.Children.Add(_label);

            var pill = new Border { CornerRadius = new CornerRadius(13), Padding = new Thickness(11, 5, 12, 5), BorderThickness = new Thickness(1), Child = row };
            pill.SetResourceReference(Border.BackgroundProperty, "Bg.Raised");
            pill.SetResourceReference(Border.BorderBrushProperty, "Accent.Border");
            Content = pill;

            MouseLeftButtonUp += (_, _) => exit();
        }

        public string Hint
        {
            set
            {
                _label.Text = "Click-through on · click to exit";
                ToolTip = value.Length > 0 ? $"Clicks pass through Local Caption. Click here, press {value}, or switch to the app to stop." :
                                             "Clicks pass through Local Caption. Click here, or switch to the app, to stop.";
            }
        }

        /// <summary>Sit over the middle of the owner's title bar, clear of its buttons.</summary>
        public void Follow(Window owner)
        {
            if (owner.WindowState == WindowState.Minimized) return;
            UpdateLayout();
            var width = ActualWidth > 0 ? ActualWidth : 230;

            // From where the owner really is on screen, not from Left/Top: those hold the
            // restore position while it is maximised or snapped.
            var origin = owner.PointToScreen(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(owner);
            Left = origin.X / dpi.DpiScaleX + Math.Max(0, (owner.ActualWidth - width) / 2);
            Top = origin.Y / dpi.DpiScaleY + 6;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // Never take focus: clicking the pill must not pull the keyboard off the call.
            var hwnd = new WindowInteropHelper(this).Handle;
            const long noActivate = 0x08000000, toolWindow = 0x80;
            SetWindowLongPtr(hwnd, ExStyle, new IntPtr(GetWindowLongPtr(hwnd, ExStyle).ToInt64() | noActivate | toolWindow));
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);
}
