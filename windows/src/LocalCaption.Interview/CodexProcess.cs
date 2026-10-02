using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using LocalCaption.Core.Data;

namespace LocalCaption.Interview;

/// <summary>
/// Newline-delimited text over a child process's stdio (SPEC-12 §Process &amp; transport). An
/// interface so engine tests can script the server without a real process.
/// </summary>
public interface ILineTransport
{
    /// <summary>Lines from stdout, in order, without the <c>\n</c>. Ends when the process exits.</summary>
    IAsyncEnumerable<string> Lines { get; }

    /// <summary>Write one line (a <c>\n</c> is appended).</summary>
    /// <exception cref="EngineException"><see cref="EngineError.Crashed"/> when the process is gone.</exception>
    void Send(string line);

    /// <summary>Close stdin, then kill the process if it is still running 2 s later.</summary>
    void Terminate();
}

/// <summary>
/// <c>codex app-server</c> as a child process: UTF-8 without BOM both ways, <c>\n</c> line
/// terminator, stderr to a rotated log (<see cref="CodexProcessLog"/>). On Windows the child is
/// put in a kill-on-close Job Object so it never outlives the app (SPEC-16 §4.1).
/// </summary>
public sealed class ProcessLineTransport : ILineTransport
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Lock _gate = new();
    private readonly Task _stdoutPump;
    private int _terminated;

    /// <summary>Start <paramref name="fileName"/> with <paramref name="arguments"/> (no shell).</summary>
    /// <param name="environment">The child's whole environment (not added to the app's).</param>
    /// <param name="stderrLog">Where stderr goes; <c>null</c> discards it.</param>
    /// <remarks>
    /// The child is put in the Job Object just after <see cref="Process.Start()"/>, so for that
    /// moment it runs outside the job: if the app dies in between, or codex spawns a child of its
    /// own before the assignment, that process is not killed with the app. Closing the window
    /// needs <c>CREATE_SUSPENDED</c> + <c>ResumeThread</c>, which <see cref="Process"/> cannot do;
    /// the window is microseconds and codex reads stdin before it starts anything, so it is
    /// accepted rather than replacing <see cref="Process"/> with raw <c>CreateProcess</c>.
    /// </remarks>
    public ProcessLineTransport(string fileName, IReadOnlyList<string> arguments, string cwd,
                                IReadOnlyDictionary<string, string> environment, string? stderrLog)
    {
        _process = new Process { StartInfo = ChildProcess.StartInfo(fileName, arguments, environment, cwd) };
        _process.Start();
        if (OperatingSystem.IsWindows() && !ChildProcessJob.Assign(_process))
            InterviewLog.Write("could not put codex in the kill-on-close job; it may outlive the app");

        // Raw streams: no StreamWriter preamble, no \r\n, no ReadLine splitting on \r.
        _stdin = _process.StandardInput.BaseStream;
        _stdoutPump = PumpStdoutAsync(_process.StandardOutput.BaseStream);
        _ = PumpStderrAsync(_process.StandardError.BaseStream, stderrLog);
        _ = CompleteAfterExitAsync();
    }

    /// <inheritdoc />
    public IAsyncEnumerable<string> Lines => _lines.Reader.ReadAllAsync();

    /// <inheritdoc />
    public void Send(string line)
    {
        var bytes = Files.Utf8NoBom.GetBytes(line + "\n");
        lock (_gate)
        {
            try
            {
                if (_process.HasExited) throw new EngineException(new EngineError.Crashed());
                _stdin.Write(bytes);
                _stdin.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                throw new EngineException(new EngineError.Crashed());
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Idempotent. The <see cref="Process"/> is disposed once the child is gone.</remarks>
    public void Terminate()
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0) return;
        lock (_gate)
        {
            try { _stdin.Dispose(); }
            catch (IOException) { }
        }
        _ = KillLaterAsync();
    }

    private async Task KillLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        ChildProcess.TryKillTree(_process);
        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException) { }
        lock (_gate) _process.Dispose(); // Send reads HasExited under the lock; afterwards it reports Crashed
        _lines.Writer.TryComplete();
    }

    /// <summary>Split stdout on <c>0x0A</c>, decode each line as UTF-8. A partial last line is dropped, as on the Mac.</summary>
    private async Task PumpStdoutAsync(Stream stdout)
    {
        var buffer = new byte[64 * 1024];
        var pending = new MemoryStream();
        try
        {
            int n;
            while ((n = await stdout.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] != 0x0A) continue;
                    pending.Write(buffer, start, i - start);
                    _lines.Writer.TryWrite(Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length));
                    pending.SetLength(0);
                    start = i + 1;
                }
                pending.Write(buffer, start, n - start);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        finally
        {
            _lines.Writer.TryComplete();
        }
    }

    /// <summary>
    /// stdout normally reaches EOF when the process exits; if something else still holds the
    /// pipe (a grandchild), end the line stream anyway shortly after the exit, as the Mac's
    /// termination handler does.
    /// </summary>
    private async Task CompleteAfterExitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAny(_stdoutPump, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        _lines.Writer.TryComplete();
    }

    private static async Task PumpStderrAsync(Stream stderr, string? logPath)
    {
        var log = logPath is null ? null : CodexProcessLog.Open(logPath);
        try
        {
            var buffer = new byte[8 * 1024];
            int n;
            while ((n = await stderr.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (log is null) continue;
                await log.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                await log.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        finally
        {
            if (log is not null) await log.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Starting and stopping the codex children (the server and <c>--version</c> probes).</summary>
internal static class ChildProcess
{
    /// <summary>
    /// No shell, no window, all three streams redirected as UTF-8 without a BOM, and — when
    /// <paramref name="environment"/> is given — exactly that environment (not added to the app's).
    /// </summary>
    public static ProcessStartInfo StartInfo(string fileName, IEnumerable<string> arguments,
                                             IReadOnlyDictionary<string, string>? environment, string? cwd = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Files.Utf8NoBom,
            StandardOutputEncoding = Files.Utf8NoBom,
            StandardErrorEncoding = Files.Utf8NoBom,
        };
        if (cwd is not null) info.WorkingDirectory = cwd;
        foreach (var a in arguments) info.ArgumentList.Add(a);
        if (environment is not null)
        {
            info.Environment.Clear();
            foreach (var (k, v) in environment) info.Environment[k] = v;
        }
        return info;
    }

    /// <summary>Kill <paramref name="process"/> and its descendants if it is still running; never throws.</summary>
    public static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }
}

/// <summary>Codex's stderr goes to <c>interview\codex.log</c>, rotated at 5 MB (SPEC-12).</summary>
public static class CodexProcessLog
{
    /// <summary>Rotate when the log is larger than this at launch.</summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Open <paramref name="path"/> for appending, first moving it to <c>&lt;path&gt;.1</c> when it
    /// is over <see cref="MaxBytes"/>. <c>null</c> if it cannot be opened (logging is best effort).
    /// </summary>
    public static FileStream? Open(string path)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            var file = new FileInfo(path);
            if (file.Exists && file.Length > MaxBytes)
            {
                var old = path + ".1";
                File.Delete(old);
                File.Move(path, old);
            }
            return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// One Job Object per app run with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>. Its handle is never
/// closed by the app, so Windows closes it when the app exits — however it exits — and kills
/// every process in it (SPEC-16 §4.1; fixes the Mac's never-called <c>shutdown()</c>).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<nint> Job = new(Create);

    /// <summary>Put <paramref name="process"/> in the job. <c>false</c> if that failed.</summary>
    public static bool Assign(Process process)
    {
        var job = Job.Value;
        return job != 0 && AssignProcessToJobObject(job, process.Handle);
    }

    private static nint Create()
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0) return 0;
        var info = new JobObjectExtendedLimitInformationStruct
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>()))
        {
            CloseHandle(job);
            return 0;
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint hJob, int jobObjectInfoClass,
        ref JobObjectExtendedLimitInformationStruct lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
