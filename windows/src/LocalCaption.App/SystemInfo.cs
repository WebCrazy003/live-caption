using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using LocalCaption.Asr;
using LocalCaption.Core;
using LocalCaption.Session;
using Microsoft.Win32;

namespace LocalCaption.App;

/// <summary>
/// What this machine is and what the speech engine is doing on it, for Settings ▸ System.
/// </summary>
/// <remarks>
/// <para>Read from the registry and one Win32 call rather than WMI. WMI would answer the same
/// questions, but it costs a package, a COM round-trip and a noticeable pause the first time
/// — a lot to pay for four lines of text in a settings page.</para>
/// <para>Everything here is best-effort and says "unknown" rather than throwing: this page is
/// for diagnosing a slow machine, which is exactly when something else is already wrong.</para>
/// </remarks>
public static class SystemInfo
{
    /// <summary>One labelled fact. <see cref="Flag"/> marks a value worth a second look.</summary>
    public sealed record Row(string Label, string Value, bool Flag = false);

    /// <summary>A titled group of facts.</summary>
    public sealed record Section(string Title, IReadOnlyList<Row> Rows);

    public static IReadOnlyList<Section> Collect(AppEnvironment env, StreamingOrchestrator? orchestrator)
    {
        var cuda = BackendProbe.HasCudaRuntime();
        var engine = orchestrator?.EngineInfo;
        var speed = orchestrator?.Speed;

        var engineRows = new List<Row>();
        if (engine is null)
        {
            engineRows.Add(new("Status", orchestrator?.Status ?? "Not loaded"));
        }
        else
        {
            engineRows.Add(new("Models", $"{engine.InterimModel} (live) · {engine.FinalModel} (final)"));
            engineRows.Add(new("Backend", engine.FellBack
                ? $"asked for {Lower(engine.Backend)}, running on {engine.Library.ToLowerInvariant()}"
                : $"{Lower(engine.Backend)} · {engine.Library.ToLowerInvariant()} library", engine.FellBack));
            engineRows.Add(new("Threads", engine.Threads.ToString()));
            engineRows.Add(new("Word timings", engine.WordTimings ? "DTW" : "none", !engine.WordTimings));
        }
        engineRows.Add(new("CUDA runtime", cuda ? "found" : "not found — 'auto' runs on the CPU", !cuda));

        var speedRows = speed is { Decodes: > 0 }
            ? new List<Row>
            {
                new("Live model", $"{speed.InterimMs} ms per update", speed.InterimMs > 1000),
                new("Final model", speed.FinalMs > 0 ? $"{speed.FinalMs} ms per phrase" : "—"),
                new("Throughput", speed.RealtimeFactor > 0 ? $"{speed.RealtimeFactor:0.0}× realtime" : "—",
                    speed.RealtimeFactor is > 0 and < 1.5),
                new("Caption lag", $"{speed.LagMs / 1000.0:0.0} s behind live audio", speed.LagMs > 3000),
                new("Decodes measured", speed.Decodes.ToString()),
            }
            : [new Row("This session", "No speech decoded yet — figures appear once captions start.")];

        return
        [
            new("Speech engine", engineRows),
            new("Speed", speedRows),
            new("This machine",
            [
                new("Processor", Processor()),
                new("Cores", $"{Math.Max(1, Environment.ProcessorCount / 2)} physical (est.) · {Environment.ProcessorCount} logical"),
                new("Memory", Memory()),
                new("Graphics", string.Join(" · ", Graphics())),
                new("Windows", RuntimeInformation.OSDescription),
            ]),
            new("Models on disk", Models()),
            new("Application",
            [
                new("Version", Version()),
                new(".NET", RuntimeInformation.FrameworkDescription),
                new("Data folder", AppPaths.Root),
                new("Transcripts", env.Config.General.TranscriptFolder),
            ]),
        ];
    }

    /// <summary>The whole page as plain text, for pasting into a bug report.</summary>
    public static string AsText(IEnumerable<Section> sections) => string.Join(Environment.NewLine,
        sections.Select(s => $"[{s.Title}]{Environment.NewLine}" +
                             string.Join(Environment.NewLine, s.Rows.Select(r => $"  {r.Label}: {r.Value}")) +
                             Environment.NewLine));

    private static string Lower(AsrBackend backend) => backend.ToString().ToLowerInvariant();

    public static string Version() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "unknown";

    private static string Processor()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown";
        }
        catch (Exception) { return "unknown"; }
    }

    /// <summary>Display adapters, from the display class key — the same list Device Manager shows.</summary>
    private static IReadOnlyList<string> Graphics()
    {
        var found = new List<string>();
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (adapters is not null)
            {
                foreach (var name in adapters.GetSubKeyNames())
                {
                    if (name.Length != 4 || !int.TryParse(name, out _)) continue;   // "0000", "0001"…
                    try
                    {
                        using var adapter = adapters.OpenSubKey(name);
                        if (adapter?.GetValue("DriverDesc") is string description &&
                            !found.Contains(description)) found.Add(description);
                    }
                    catch (Exception) { /* one unreadable adapter should not hide the rest */ }
                }
            }
        }
        catch (Exception) { }

        return found.Count > 0 ? found : ["unknown"];
    }

    private static string Memory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        try
        {
            if (!GlobalMemoryStatusEx(ref status)) return "unknown";
            const double gb = 1024.0 * 1024 * 1024;
            return $"{status.TotalPhysical / gb:0.0} GB · {status.AvailablePhysical / gb:0.0} GB free";
        }
        catch (Exception) { return "unknown"; }
    }

    private static IReadOnlyList<Row> Models()
    {
        var rows = new List<Row>();
        foreach (var model in ModelCatalog.All)
        {
            var path = ModelCatalog.PathFor(model, AppPaths.Models);
            string state;
            try
            {
                state = File.Exists(path)
                    ? $"downloaded · {new FileInfo(path).Length / 1_000_000.0:N0} MB"
                    : $"not downloaded · about {model.ApproxBytes / 1_000_000.0:N0} MB";
            }
            catch (Exception) { state = "unknown"; }
            rows.Add(new(model.Name, state));
        }
        return rows;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
