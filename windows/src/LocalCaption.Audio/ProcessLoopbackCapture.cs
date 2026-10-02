using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace LocalCaption.Audio;

/// <summary>
/// Mode A — capture one application's render stream, before it reaches any endpoint
/// (SPEC-WINDOWS.md §4.5). <b>The v1 default.</b>
/// </summary>
/// <remarks>
/// <para>v1.0 of the spec had endpoint loopback as the only path. The owner's confirmation
/// that interviews are taken over Jump Desktop inverted that (§4.1): in a remote session the
/// endpoint routing is precisely the fragile part, and process loopback is immune to it —
/// it captures what an app plays regardless of which device that audio lands on, whether the
/// default endpoint flips mid-call, or how a virtual driver behaves under loopback.</para>
/// <para>It also fixes a transcript-quality problem for free: Spotify, Slack pings and other
/// browser tabs never enter the transcript.</para>
/// <para><b>The process is remembered by executable name, not PID</b> (§4.5) and re-resolved
/// at every Start, because a PID is meaningless after the user restarts Teams.</para>
/// </remarks>
public sealed class ProcessLoopbackCapture : WasapiCapture
{
    private readonly string _executable;
    private Process? _target;

    /// <param name="executable">
    /// Executable name, with or without <c>.exe</c> — <c>Teams</c>, <c>chrome</c>,
    /// <c>Zoom</c>. Persisted as <c>audio.target_process</c> (§9.2).
    /// </param>
    public ProcessLoopbackCapture(string executable) =>
        _executable = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executable[..^4]
            : executable;

    /// <summary>
    /// Whether this Windows can capture one application at all.
    /// </summary>
    /// <remarks>
    /// <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c> arrived in build 20348 (Server
    /// 2022); every Windows 11 has it, and Windows 10 client releases (1809–22H2, builds
    /// 17763–19045) do not. There, activation fails with an HRESULT that says nothing useful,
    /// so callers ask first and capture the whole output device instead (SPEC-16,
    /// Compatibility target: "process loopback when the OS supports it, endpoint loopback
    /// otherwise").
    /// </remarks>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    /// <summary>What to tell someone whose Windows cannot capture one app.</summary>
    public const string UnsupportedNote =
        "This version of Windows cannot capture a single app (that needs Windows 11), so everything this PC plays is being captured.";

    /// <summary>The PID resolved at the last Start, for the log.</summary>
    public int? ProcessId { get; private set; }

    public override string ClockReport => $"{base.ClockReport} · target {_executable} ({ProcessId?.ToString() ?? "unresolved"})";

    protected override OpenedStream OpenStream()
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(
                "Capturing a single app needs Windows 11. Choose \"Everything this PC plays\" as the source.");

        var target = Resolve(_executable)
            ?? throw new InvalidOperationException(
                $"{_executable} is not running. Start it, or choose a different source.");

        ProcessId = target.Id;
        _target = target;

        // §4.5: if the target exits, the stream dies and nothing says so. Recording silence
        // for the rest of an interview is the failure this prevents — the session pauses and
        // asks for a new source instead.
        try
        {
            target.EnableRaisingEvents = true;
            target.Exited += OnTargetExited;
        }
        catch (Exception)
        {
            // Some processes refuse the handle. The capture still works; it just cannot
            // report the exit early, and the stalled clock will make it obvious.
        }

        var format = ProcessLoopback.CaptureFormat;
        var client = ProcessLoopback.Activate(target.Id, TimeSpan.FromSeconds(10));
        client.Initialize(AudioClientShareMode.Shared,
                          AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
                          bufferDuration: 2_000_000,       // 200 ms, in 100 ns units
                          periodicity: 0, format, Guid.Empty);

        return new OpenedStream(client, format, $"{Describe(target)} (process)");
    }

    protected override void CloseStream()
    {
        if (_target is null) return;
        try { _target.Exited -= OnTargetExited; } catch (Exception) { }
        _target.Dispose();
        _target = null;
    }

    private void OnTargetExited(object? sender, EventArgs e) =>
        Report(new CaptureFault($"{_executable} closed, so there is nothing left to capture.",
                                Recoverable: false));

    /// <summary>
    /// The process to tap. Prefers one that currently has an audio session, then the one
    /// with a main window, because a browser has many processes and only some make sound.
    /// </summary>
    private static Process? Resolve(string executable)
    {
        var candidates = Process.GetProcessesByName(executable);
        if (candidates.Length == 0) return null;

        var audible = AudioSessions.ProcessIds();
        return candidates.FirstOrDefault(p => audible.Contains(p.Id))
            ?? candidates.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero)
            ?? candidates[0];
    }

    private static string Describe(Process process)
    {
        try
        {
            return string.IsNullOrWhiteSpace(process.MainWindowTitle)
                ? process.ProcessName
                : process.ProcessName;
        }
        catch (Exception) { return process.ProcessName; }
    }
}
