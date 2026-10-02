using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LocalCaption.Core.Data;

namespace LocalCaption.App;

/// <summary>One open window that could be pasted into.</summary>
public sealed record WindowChoice(IntPtr Handle, string Title, string Process)
{
    public string Label => $"{Title}  ·  {Process}";
}

/// <summary>How a send ended, in words fit for the status bar.</summary>
public sealed record SendResult(bool Ok, string Message);

/// <summary>
/// Gets the last question out of this app and into the tool that answers it.
/// </summary>
/// <remarks>
/// <para>Three ways, because "the tool" is three different things. A chat in a browser tab can
/// be <b>pasted into</b> — this app does the alt-tab, Ctrl+V, Enter and alt-tab-back that the
/// user was doing by hand. A browser tab with the companion extension, or a program of the
/// user's own, can <b>ask a loopback address</b> for what is new. And anything at all can
/// <b>watch a file</b>.</para>
/// <para>All three stay on this machine. The address listens on 127.0.0.1 only and sends no
/// CORS header, so a web page cannot read it; only the extension (which holds a host
/// permission) or a local program can. What the answer tool then does with the text is the
/// user's business — §1.2's "nothing leaves the device" is about what <i>this</i> app sends.</para>
/// </remarks>
public sealed class AnswerSender : IDisposable
{
    private readonly Func<Config> _config;
    private LoopbackFeed? _feed;
    private IntPtr _pickedWindow;

    public AnswerSender(Func<Config> config) => _config = config;

    /// <summary>Start or stop the loopback feed to match config. Call after Settings saves.</summary>
    public string? Apply()
    {
        var send = _config().Send;
        var wanted = send.Target == "address";

        if (!wanted || _feed?.Port != send.Port)
        {
            _feed?.Dispose();
            _feed = null;
        }

        if (wanted && _feed is null)
        {
            try { _feed = new LoopbackFeed(send.Port); }
            catch (Exception e) { return $"Could not listen on port {send.Port}: {e.Message}"; }
        }
        return null;
    }

    /// <summary>Remember the exact window the user pointed at, for as long as it lives.</summary>
    public void Pick(IntPtr window) => _pickedWindow = window;

    /// <summary>Whether a turn that just ended should go out by itself.</summary>
    public bool SendsAutomatically
    {
        get
        {
            var send = _config().Send;
            // Never for "window": that one takes the foreground to paste, and doing that
            // unasked — mid-sentence, mid-click — is how a helper becomes a hazard.
            return send.Auto && send.Target is "address" or "file";
        }
    }

    public SendResult Send(string text)
    {
        var send = _config().Send;
        text = text.Trim();
        if (text.Length == 0) return new(false, "Nothing to send yet");

        var body = send.Template.Contains("{text}", StringComparison.Ordinal)
            ? send.Template.Replace("{text}", text, StringComparison.Ordinal)
            : text;

        try
        {
            switch (send.Target)
            {
                case "window": return Paste(body, send);
                case "file":
                    Directory.CreateDirectory(Path.GetDirectoryName(send.File) ?? ".");
                    File.AppendAllText(send.File, body + Environment.NewLine + Environment.NewLine, new UTF8Encoding(false));
                    return new(true, "Sent to file");
                case "address":
                    if (_feed is null && Apply() is { } problem) return new(false, problem);
                    _feed!.Publish(body, send.Submit);
                    return new(true, _feed.HasListener ? "Sent" : "Queued — nothing is listening yet");
                default:
                    return ClipboardWriter.Copy(body) ? new(true, "Copied — no send target is set") : new(false, "Clipboard is busy");
            }
        }
        catch (Exception e)
        {
            return new(false, $"Could not send: {e.Message}");
        }
    }

    // ── paste into a window ──────────────────────────────────────────────────────────────

    /// <summary>Every visible, titled top-level window that is not this app's.</summary>
    public static List<WindowChoice> OpenWindows()
    {
        var found = new List<WindowChoice>();
        var mine = (uint)Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == mine) return true;

            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            string process;
            try { using var p = Process.GetProcessById((int)pid); process = p.ProcessName; }
            catch (Exception) { process = "?"; }

            // The shell's own surfaces have titles and are nothing anyone pastes into.
            if (process is "explorer" or "TextInputHost" or "ApplicationFrameHost" or "SystemSettings" &&
                title.ToString() is "Program Manager" or "Windows Input Experience" or "Settings") return true;

            found.Add(new WindowChoice(hwnd, title.ToString(), process));
            return true;
        }, IntPtr.Zero);

        return found;
    }

    private IntPtr ResolveWindow(Config.SendGroup send)
    {
        if (_pickedWindow != IntPtr.Zero && IsWindow(_pickedWindow)) return _pickedWindow;
        if (send.WindowTitle.Trim() is not { Length: > 0 } wanted) return IntPtr.Zero;

        return OpenWindows().FirstOrDefault(w => w.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase))?.Handle
               ?? IntPtr.Zero;
    }

    /// <summary>
    /// Do by machine what was being done by hand: switch to the chat, paste, send, switch back.
    /// </summary>
    /// <remarks>
    /// It pastes wherever that window's keyboard focus is. Chat pages keep it in their message
    /// box — they put it there on load and return it after every send — so this works as long
    /// as the chat is the tab showing in that window and nobody has clicked elsewhere in it.
    /// </remarks>
    private SendResult Paste(string text, Config.SendGroup send)
    {
        var target = ResolveWindow(send);
        if (target == IntPtr.Zero)
        {
            return ClipboardWriter.Copy(text)
                ? new(false, "Copied — the answer window was not found (Settings ▸ Send)")
                : new(false, "The answer window was not found");
        }

        if (!ClipboardWriter.Copy(text)) return new(false, "Clipboard is busy — not sent");

        var before = GetForegroundWindow();

        // A shortcut's own modifiers are still held when this runs. Ctrl+V sent over a held
        // Alt is Ctrl+Alt+V, which pastes nothing.
        for (var i = 0; i < 40 && AnyModifierDown(); i++) Thread.Sleep(15);

        if (IsIconic(target)) ShowWindow(target, 9);         // SW_RESTORE
        if (!BringForward(target)) return new(false, "Copied — Windows would not switch to the answer window");

        Thread.Sleep(90);
        Chord(0x11, 0x56);                                   // Ctrl+V
        if (send.Submit)
        {
            Thread.Sleep(140);                               // let the page take the paste first
            Tap(0x0D);                                       // Enter
        }

        if (before != IntPtr.Zero && before != target)
        {
            Thread.Sleep(160);
            BringForward(before);
        }
        return new(true, send.Submit ? "Sent" : "Pasted");
    }

    private static bool BringForward(IntPtr window)
    {
        if (GetForegroundWindow() == window) return true;

        // Windows only lets the foreground process hand the foreground on. Attaching to the
        // current foreground thread's input queue is the sanctioned way to be "it" briefly.
        var foreground = GetForegroundWindow();
        var theirs = GetWindowThreadProcessId(foreground, out _);
        var ours = GetCurrentThreadId();
        var attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached) AttachThreadInput(ours, theirs, false);
        }

        for (var i = 0; i < 30; i++)
        {
            if (GetForegroundWindow() == window) return true;
            Thread.Sleep(15);
        }
        return false;
    }

    private static bool AnyModifierDown() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    private static void Chord(ushort modifier, ushort key) =>
        Inject([Key(modifier, false), Key(key, false), Key(key, true), Key(modifier, true)]);

    private static void Tap(ushort key) => Inject([Key(key, false), Key(key, true)]);

    private static void Inject(Input[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

    private static Input Key(ushort virtualKey, bool up) => new()
    {
        Type = 1,                                            // INPUT_KEYBOARD
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = up ? 0x2u : 0u } },
    };

    public void Dispose() => _feed?.Dispose();

    // ── loopback feed ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A few dozen lines of HTTP on 127.0.0.1, for the extension and for home-made tools.
    /// </summary>
    /// <remarks>
    /// <para><c>GET /next?since=N</c> waits up to 25 s for an item newer than N and answers
    /// <c>{"last": id, "items": [{id, text, submit}]}</c> — a long poll, so a listener hears
    /// within milliseconds without hammering. <c>GET /latest</c> answers at once with the
    /// newest item. A raw <see cref="TcpListener"/> rather than <c>HttpListener</c>: no
    /// http.sys, so no URL reservation and no administrator, on anyone's machine.</para>
    /// </remarks>
    private sealed class LoopbackFeed : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<(long Id, string Text, bool Submit)> _items = [];
        private readonly object _gate = new();
        private TaskCompletionSource _arrived = NewSignal();
        private long _nextId = Stopwatch.GetTimestamp() & 0xFFFFFFF;     // so a restart is not mistaken for old news
        private DateTime _lastAsked = DateTime.MinValue;

        public LoopbackFeed(int port)
        {
            Port = port;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port { get; }

        /// <summary>Someone asked recently enough that a publish will be heard.</summary>
        public bool HasListener => (DateTime.UtcNow - _lastAsked).TotalSeconds < 40;

        public void Publish(string text, bool submit)
        {
            TaskCompletionSource signal;
            lock (_gate)
            {
                _items.Add((++_nextId, text, submit));
                if (_items.Count > 50) _items.RemoveRange(0, _items.Count - 50);
                signal = _arrived;
                _arrived = NewSignal();
            }
            signal.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                    _ = ServeAsync(client);
                }
            }
            catch (Exception) { /* stopped */ }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 2048, leaveOpen: true);
                    var request = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) ?? "";
                    while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { Length: > 0 }) { }

                    var parts = request.Split(' ');
                    var target = parts.Length > 1 ? parts[1] : "/";
                    var path = target.Split('?')[0];
                    _lastAsked = DateTime.UtcNow;

                    string body;
                    if (path == "/next")
                    {
                        var since = long.TryParse(Query(target, "since"), out var n) ? n : -1;
                        body = await NextAsync(since).ConfigureAwait(false);
                    }
                    else if (path == "/latest")
                    {
                        lock (_gate) body = Render(_items.Count > 0 ? [_items[^1]] : []);
                    }
                    else
                    {
                        body = "{\"app\":\"Local Caption\",\"endpoints\":[\"/next?since=N\",\"/latest\"]}";
                    }

                    var payload = Encoding.UTF8.GetBytes(body);
                    var head = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\n" +
                        $"Content-Length: {payload.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, _stop.Token).ConfigureAwait(false);
                    await stream.WriteAsync(payload, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception) { /* a dropped connection is the listener's problem */ }
            }
        }

        private async Task<string> NextAsync(long since)
        {
            Task wait;
            lock (_gate)
            {
                // since = -1 is a listener's first call: tell it where "now" is, send nothing
                // old. It would otherwise paste the last fifty questions into the chat.
                if (since < 0) return Render([]);
                var fresh = _items.Where(i => i.Id > since).ToList();
                if (fresh.Count > 0) return Render(fresh);
                wait = _arrived.Task;
            }

            await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(25), _stop.Token)).ConfigureAwait(false);
            lock (_gate) return Render(_items.Where(i => i.Id > since).ToList());
        }

        private string Render(IReadOnlyList<(long Id, string Text, bool Submit)> items) => JsonSerializer.Serialize(new
        {
            last = _nextId,
            items = items.Select(i => new { id = i.Id, text = i.Text, submit = i.Submit }),
        });

        private static string Query(string target, string key)
        {
            var at = target.IndexOf('?');
            if (at < 0) return "";
            foreach (var pair in target[(at + 1)..].Split('&'))
            {
                var bits = pair.Split('=', 2);
                if (bits[0] == key) return bits.Length > 1 ? Uri.UnescapeDataString(bits[1]) : "";
            }
            return "";
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch (Exception) { }
        }
    }

    // ── Win32 ────────────────────────────────────────────────────────────────────────────

    private delegate bool EnumProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;      // present so the union has its true size
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey; public ushort Scan; public uint Flags; public uint Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X; public int Y; public uint Data; public uint Flags; public uint Time; public IntPtr Extra; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
