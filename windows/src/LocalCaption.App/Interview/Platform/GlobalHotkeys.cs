using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using LocalCaption.Core.Interview;
using LocalCaption.Interview;

namespace LocalCaption.App.Interview.Platform;

/// <summary>Where one global hotkey stands (Swift <c>GlobalHotkey.State</c>).</summary>
public abstract record GlobalHotkeyState
{
    private GlobalHotkeyState() { }

    /// <summary>Not registered: Interview mode is not active, or no hotkey is set.</summary>
    public sealed record Off : GlobalHotkeyState;

    /// <summary>Registered with Windows; pressing it anywhere raises the event.</summary>
    public sealed record Registered(Hotkey Hotkey) : GlobalHotkeyState;

    /// <summary>Windows refused it; <paramref name="Reason"/> is shown in Settings ("another app is using F8").</summary>
    public sealed record Unavailable(Hotkey Hotkey, string Reason) : GlobalHotkeyState;
}

/// <summary>
/// The interview's two system-wide hotkeys, Ask and Screenshot (SPEC-16 §4.2; Swift
/// <c>GlobalHotkey</c>). They fire while the meeting app has focus.
/// </summary>
/// <remarks>
/// <para><b>Window.</b> <c>RegisterHotKey</c> delivers <c>WM_HOTKEY</c> to a window, so this owns
/// a message-only window (parent <c>HWND_MESSAGE</c>: never shown, never in Alt+Tab, gets no
/// broadcasts) created through <see cref="HwndSource"/> on the constructing thread — which must be
/// the UI thread, since that thread's dispatcher is the message loop that receives the hotkey. It
/// is independent of the main window, so the hotkeys survive the main window being hidden (the
/// screenshot overlay hides it) and never collide with <see cref="ShortcutManager"/>'s ids.</para>
/// <para><b>Keys.</b> By virtual key (<see cref="HotkeyMapping"/>), so any keyboard layout works;
/// <c>Cmd</c> is the Windows key; <c>MOD_NOREPEAT</c> stops auto-repeat from asking twice.</para>
/// <para><b>Events</b> are raised on the UI thread, posted rather than called from inside the
/// window procedure: a handler that opens the screenshot overlay runs a nested message loop, and
/// an exception from a handler must not unwind through user32.</para>
/// </remarks>
public sealed class GlobalHotkeys : IDisposable
{
    private const int HotkeyMessage = 0x0312;                  // WM_HOTKEY: wParam = the id given to RegisterHotKey
    private const int AlreadyRegistered = 1409;                // ERROR_HOTKEY_ALREADY_REGISTERED

    private readonly Dispatcher _dispatcher;
    private readonly HwndSource _window;
    private readonly Slot _ask = new(1, "Ask");                // ids are per window; 0x0000–0xBFFF is the application range
    private readonly Slot _screenshot = new(2, "Screenshot");
    private bool _disposed;

    /// <summary>Create on the UI thread. Nothing is registered until <see cref="Update"/>.</summary>
    public GlobalHotkeys()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _window = new HwndSource(new HwndSourceParameters("LocalCaption interview hotkeys")
        {
            ParentWindow = Win32.MessageOnlyParent,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        _window.AddHook(OnMessage);
    }

    /// <summary>The Ask hotkey's state.</summary>
    public GlobalHotkeyState AskState => _ask.State;

    /// <summary>The Screenshot hotkey's state.</summary>
    public GlobalHotkeyState ScreenshotState => _screenshot.State;

    /// <summary>The Ask hotkey was pressed. UI thread.</summary>
    public event Action? AskPressed;

    /// <summary>The Screenshot hotkey was pressed. UI thread.</summary>
    public event Action? ScreenshotPressed;

    /// <summary>Either state changed. UI thread.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Make the registrations match: with <paramref name="active"/> false (not Interview mode,
    /// preparation showing, or the interview saved — SPEC-16 §4.2) both are off; otherwise each
    /// non-null hotkey is registered. Only a hotkey whose wanted value changed is touched, so
    /// calling this on every state change is cheap and does not flicker. A hotkey Windows refused
    /// is tried again only when it, or <paramref name="active"/>, changes. Never throws.
    /// </summary>
    public void Update(Hotkey? askHotkey, Hotkey? screenshotHotkey, bool active)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => Update(askHotkey, screenshotHotkey, active)));
            return;
        }
        if (_disposed) return;

        var ask = active ? askHotkey : null;
        var shot = active ? screenshotHotkey : null;
        // The same combination twice: Windows would refuse the second with "already registered",
        // which would blame another app. Say what is really wrong instead.
        var clash = shot is not null && shot == ask;
        var askWant = new Want(ask, false);
        var shotWant = new Want(shot, clash);

        var changed = new List<(Slot Slot, Want Want)>(2);
        if (_ask.Wanted != askWant) changed.Add((_ask, askWant));
        if (_screenshot.Wanted != shotWant) changed.Add((_screenshot, shotWant));
        if (changed.Count == 0) return;

        // Release first, then register: swapping Ask F8 ↔ Screenshot F9 must not trip over itself.
        foreach (var (slot, _) in changed) Unregister(slot);
        foreach (var (slot, want) in changed) Register(slot, want);
        StateChanged?.Invoke();
    }

    private void Register(Slot slot, Want want)
    {
        slot.Wanted = want;
        if (want.Hotkey is not { } hotkey)
        {
            slot.State = new GlobalHotkeyState.Off();
            return;
        }
        var name = HotkeyMapping.DisplayName(hotkey);
        if (want.ClashesWithAsk)
        {
            slot.State = new GlobalHotkeyState.Unavailable(hotkey, $"{name} is already the Ask hotkey");
            return;
        }
        if (HotkeyMapping.ToNative(hotkey) is not { } native)
        {
            slot.State = new GlobalHotkeyState.Unavailable(hotkey, $"Windows has no {hotkey.Key} key");
            return;
        }
        try
        {
            if (RegisterHotKey(_window.Handle, slot.Id, native.Modifiers, native.VirtualKey))
            {
                slot.IsRegistered = true;
                slot.State = new GlobalHotkeyState.Registered(hotkey);
                return;
            }
            var error = Marshal.GetLastPInvokeError();
            slot.State = new GlobalHotkeyState.Unavailable(hotkey, error == AlreadyRegistered
                ? $"another app is using {name}"
                : $"Windows refused {name} (error {error})");
        }
        catch (Exception e)
        {
            slot.State = new GlobalHotkeyState.Unavailable(hotkey, $"Windows refused {name} ({e.Message})");
        }
        InterviewPlatformLog.Write("hotkey", $"{slot.Name} {name}: {((GlobalHotkeyState.Unavailable)slot.State).Reason}");
    }

    private void Unregister(Slot slot)
    {
        if (slot.IsRegistered)
        {
            try { UnregisterHotKey(_window.Handle, slot.Id); }
            catch (Exception e) { InterviewPlatformLog.Write("hotkey", $"unregistering {slot.Name} failed: {e.Message}"); }
            slot.IsRegistered = false;
        }
        slot.Wanted = default;
        slot.State = new GlobalHotkeyState.Off();
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != HotkeyMessage) return IntPtr.Zero;
        var id = unchecked((int)wParam.ToInt64());
        Action? target = id == _ask.Id ? AskPressed : id == _screenshot.Id ? ScreenshotPressed : null;
        if (id != _ask.Id && id != _screenshot.Id) return IntPtr.Zero;
        handled = true;
        if (target is not null)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed) target();
            }));
        }
        return IntPtr.Zero;
    }

    /// <summary>Unregister both and destroy the window (SPEC-16 §4.2: unregistered on quit). Call on the UI thread.</summary>
    public void Dispose()
    {
        if (!_dispatcher.CheckAccess())
        {
            try { _dispatcher.Invoke(new Action(Dispose)); }
            catch (Exception) { /* the dispatcher has shut down; Windows frees hotkeys with the window */ }
            return;
        }
        if (_disposed) return;
        _disposed = true;
        Unregister(_ask);
        Unregister(_screenshot);
        _window.RemoveHook(OnMessage);
        _window.Dispose();
    }

    /// <summary>What a slot should be: the hotkey (null = none) and whether it duplicates Ask.</summary>
    private readonly record struct Want(Hotkey? Hotkey, bool ClashesWithAsk);

    private sealed class Slot(int id, string name)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public Want Wanted { get; set; }
        public bool IsRegistered { get; set; }
        public GlobalHotkeyState State { get; set; } = new GlobalHotkeyState.Off();
    }

    /// <summary>Windows Vista+ for <c>MOD_NOREPEAT</c> (Windows 7+); <c>GetLastError</c> 1409 when taken.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
