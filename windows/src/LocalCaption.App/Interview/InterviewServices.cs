using System.Media;
using System.Windows.Threading;
using LocalCaption.App.Interview.Platform;
using LocalCaption.Core.Interview;
using LocalCaption.Interview;
using LocalCaption.Session;

namespace LocalCaption.App.Interview;

/// <summary>
/// Interview mode's app-wide pieces, built once at startup on the UI thread (specs/SPEC-16
/// §4–§5): the Codex engine and its service, the CV / skill library, the one
/// <see cref="InterviewController"/> with the Windows screenshot and clipboard services, and the
/// two global hotkeys.
/// </summary>
/// <remarks>
/// <para><b>Caption only mode never starts Codex</b> (owner, 2026-10-02; SPEC-16 §4.1, §9.1).
/// Nothing here launches it: constructing the engine, the service, the library and the
/// controller only reads files. Codex starts on an interview action, or a Codex button in
/// Settings — see <see cref="CodexAppServerEngine.IsRunning"/> for the one place the host asks
/// before archiving a thread.</para>
/// <para><b>Shutdown.</b> <see cref="Dispose"/> unregisters the hotkeys, drops the clipboard's
/// owner window and shuts the engine down; the engine's Job Object kills <c>codex</c> with the
/// app even if this never runs.</para>
/// </remarks>
public sealed class InterviewServices : IDisposable
{
    private readonly AppEnvironment _env;
    private bool _disposed;

    /// <summary>Build everything. Call on the UI thread (the controller and the platform services belong to it).</summary>
    public InterviewServices(AppEnvironment env)
    {
        _env = env;

        // The controller posts its continuations to the creating thread's context. WPF installs
        // the dispatcher's once Application.Run is under way; make sure of it rather than throw.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        // The app treats a throw from here as "Interview mode unavailable" and starts in Caption
        // only mode (App.OnStartup), so let go of whatever was already built before rethrowing:
        // the clipboard listener's window and the hotkey window are real Win32 resources.
        try
        {
            // interview.codex_path is read at each launch, so Settings changes apply at the next one.
            Engine = new CodexAppServerEngine(() => _env.Config.Interview.CodexPath);
            Codex = new CodexService(Engine);
            Library = new InterviewLibrary();
            Screen = new ScreenCapture();
            Clipboard = new ClipboardImages();
            Hotkeys = new GlobalHotkeys();
            Controller = new InterviewController(env.Store, Library, Codex, () => _env.Config.Interview,
                                                 Screen, Clipboard,
                                                 new InterviewControllerOptions { Beep = SystemSounds.Beep.Play });
            Controller.Changed += (_, _) => RetryPendingStop();
        }
        catch
        {
            if (Hotkeys is not null) Try(Hotkeys.Dispose, "unregistering the hotkeys");
            if (Clipboard is not null) Try(Clipboard.Dispose, "releasing the clipboard");
            if (Codex is not null) Try(Codex.Dispose, "stopping the Codex service");
            if (Engine is not null) Try(() => Engine.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)), "shutting Codex down");
            throw;
        }
    }

    /// <summary>The <c>codex app-server</c> engine (default options: the app's own paths).</summary>
    public CodexAppServerEngine Engine { get; }

    /// <summary>Sign-in, usage and models, shared by the interview screens and Settings.</summary>
    public CodexService Codex { get; }

    /// <summary>CVs and the four skills.</summary>
    public InterviewLibrary Library { get; }

    /// <summary>The region screenshot overlay (Screenshot hotkey / camera button).</summary>
    public ScreenCapture Screen { get; }

    /// <summary>Clipboard images for the screenshot tray (read only in Interview mode).</summary>
    public ClipboardImages Clipboard { get; }

    /// <summary>The Ask and Screenshot hotkeys; registered only while an interview is live.</summary>
    public GlobalHotkeys Hotkeys { get; }

    /// <summary>The interview shown in the main window.</summary>
    public InterviewController Controller { get; }

    /// <summary>The Ask hotkey as configured (F8 when unset or unreadable).</summary>
    public Hotkey AskHotkey => Hotkey.Resolve(_env.Config.Interview.Hotkey);

    /// <summary>The Screenshot hotkey as configured (F9 when unset or unreadable).</summary>
    public Hotkey ScreenshotHotkey => Hotkey.Resolve(_env.Config.Interview.ScreenshotHotkey, Hotkey.DefaultScreenshot);

    /// <summary>Why the Ask hotkey could not be registered, or null.</summary>
    public string? AskUnavailableReason => (Hotkeys.AskState as GlobalHotkeyState.Unavailable)?.Reason;

    /// <summary>Why the Screenshot hotkey could not be registered, or null.</summary>
    public string? ScreenshotUnavailableReason => (Hotkeys.ScreenshotState as GlobalHotkeyState.Unavailable)?.Reason;

    /// <summary>
    /// A read-only controller for a saved interview (the Sessions window's replay). Its summary
    /// resumes the thread on first use, so it is only offered in Interview mode.
    /// </summary>
    /// <remarks>
    /// Each one is tracked weakly, so <see cref="StopCodexForCaptionOnly"/> can see a summary
    /// still streaming in a replay and wait for it. A replay that was closed and collected has
    /// nothing in flight (an awaited turn keeps its controller alive until it ends).
    /// </remarks>
    public InterviewController OpenSaved(InterviewRecord record)
    {
        var controller = new InterviewController(_env.Store, Library, Codex, () => _env.Config.Interview, existing: record);
        _saved.RemoveAll(w => !w.TryGetTarget(out _));
        _saved.Add(new WeakReference<InterviewController>(controller));
        // The handler references this object, not the other way round: the weak list stays weak.
        controller.Changed += (_, _) => RetryPendingStop();
        return controller;
    }

    private readonly List<WeakReference<InterviewController>> _saved = [];
    private bool _stopPending;

    /// <summary>
    /// An answer, a preparation or a summary is in flight in the main window's interview or in
    /// any replay the Sessions window opened (<see cref="OpenSaved"/>).
    /// </summary>
    public bool AnyBusy
    {
        get
        {
            if (Controller.IsBusy) return true;
            foreach (var weak in _saved)
                if (weak.TryGetTarget(out var saved) && saved.IsBusy) return true;
            return false;
        }
    }

    /// <summary>
    /// Archive a deleted interview's Codex thread — unless that would start Codex in Caption only
    /// mode (SPEC-16 §9.1): then the thread is left where it is and that is logged. Never throws.
    /// </summary>
    public async Task ArchiveThreadAsync(string threadId)
    {
        if (!_env.Config.Interview.IsInterviewMode && !Engine.IsRunning)
        {
            InterviewPlatformLog.Write("sessions", $"left thread {threadId} unarchived: Caption only mode does not start Codex");
            return;
        }
        try { await Engine.ArchiveThreadAsync(threadId); }
        catch (Exception e) { InterviewPlatformLog.Write("sessions", $"archiving {threadId} failed: {e.Message}"); }
    }

    /// <summary>Check Codex once, if nobody has yet — when Interview mode is entered (the Mac's status row does on appear).</summary>
    public void CheckCodexOnce()
    {
        if (Codex.Status is not null || Codex.Checking) return;
        _ = CheckAsync();

        async Task CheckAsync()
        {
            try { await Codex.RefreshAsync(); }
            catch (Exception e) { InterviewPlatformLog.Write("codex", $"checking Codex failed: {e.Message}"); }
        }
    }

    /// <summary>
    /// Caption only mode runs no <c>codex</c> (SPEC-16 §11.2): stop one left running by Interview
    /// mode — unless an answer, a preparation or a summary is still in flight (here or in a
    /// Sessions-window replay, <see cref="AnyBusy"/>), which it would cut off. Then the stop is
    /// remembered and retried when that work changes state, for as long as <c>interview.mode</c>
    /// still says Caption only. The main window also calls this on each refresh in Caption only
    /// mode. Known threads are resumed when Interview mode next needs Codex.
    /// </summary>
    public void StopCodexForCaptionOnly()
    {
        if (!Engine.IsRunning)
        {
            _stopPending = false;
            return;
        }
        if (AnyBusy)
        {
            if (!_stopPending)
                InterviewPlatformLog.Write("codex", "left running into Caption only mode until interview work in flight finishes");
            _stopPending = true;
            return;
        }
        _stopPending = false;
        InterviewPlatformLog.Write("codex", "stopping: Caption only mode runs no codex");
        _ = Engine.ShutdownAsync();
    }

    /// <summary>A deferred <see cref="StopCodexForCaptionOnly"/>, once the work it waited for has finished.</summary>
    private void RetryPendingStop()
    {
        if (!_stopPending || _disposed) return;
        if (_env.Config.Interview.IsInterviewMode)
        {
            _stopPending = false;      // Interview mode came back: codex is wanted again
            return;
        }
        if (!AnyBusy) StopCodexForCaptionOnly();
    }

    /// <summary>Unregister the hotkeys, drop the clipboard owner, shut Codex down. Call on the UI thread, once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Try(Hotkeys.Dispose, "unregistering the hotkeys");
        Try(Clipboard.Dispose, "releasing the clipboard");
        Try(Codex.Dispose, "stopping the Codex service");
        // ShutdownAsync terminates the process synchronously and awaits nothing on this thread,
        // so the short wait cannot deadlock; the Job Object covers a hard exit anyway.
        Try(() => Engine.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)), "shutting Codex down");
    }

    private static void Try(Action work, string what)
    {
        try { work(); }
        catch (Exception e) { InterviewPlatformLog.Write("app", $"{what} failed: {e.Message}"); }
    }
}
