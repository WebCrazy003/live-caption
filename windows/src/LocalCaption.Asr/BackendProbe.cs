using System.Runtime.InteropServices;

namespace LocalCaption.Asr;

/// <summary>
/// Decides which compute backend to load, and makes the answer visible.
/// </summary>
/// <remarks>
/// <para>SPEC-WINDOWS.md §5.2: CUDA 12 is the primary backend on the RTX 3070 and CPU
/// (AVX2, Zen 3) is the fallback. Both ship in the installer rather than one being fetched
/// on demand, so there is no first-run failure mode on a machine that is known to have a
/// GPU.</para>
/// <para><b>A silent fall back to CPU must be visible rather than merely felt.</b> On this
/// laptop the dGPU can genuinely disappear at runtime — the MUX switch, Eco mode and
/// Armoury Crate profiles all remove it (§5.8) — and the only symptom would be captions
/// quietly getting slower. The resolved backend is returned so it can be logged and shown
/// in Settings.</para>
/// </remarks>
public static class BackendProbe
{
    /// <summary>
    /// Resolve <see cref="AsrBackend.Auto"/> against what this machine actually has.
    /// An explicit <c>cuda</c> or <c>cpu</c> is honoured as written, so the CPU path can be
    /// forced for A/B testing without a rebuild (<c>asr.backend</c>, §5.2).
    /// </summary>
    public static AsrBackend Resolve(AsrBackend requested) => requested switch
    {
        AsrBackend.Cpu => AsrBackend.Cpu,
        AsrBackend.Cuda => AsrBackend.Cuda,
        AsrBackend.Metal => AsrBackend.Metal,
        _ when HasMetal() => AsrBackend.Metal,
        _ when HasCudaRuntime() => AsrBackend.Cuda,
        _ => AsrBackend.Cpu,
    };

    /// <summary>Whether a resolved backend wants the GPU path enabled on the factory.</summary>
    public static bool UsesGpu(AsrBackend resolved) => resolved is AsrBackend.Cuda or AsrBackend.Metal;

    /// <summary>
    /// True when a GPU backend is worth attempting. Whisper.net falls back to CPU by itself
    /// if the runtime is absent, so this only has to avoid the obviously-pointless cases.
    /// </summary>
    public static bool HasUsableGpu() => UsesGpu(Resolve(AsrBackend.Auto));

    /// <summary>
    /// Apple Silicon's Metal runtime ships with Whisper.net and always works on arm64. This
    /// is the stage-A development case only — it is never a shipping configuration.
    /// </summary>
    private static bool HasMetal() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
        RuntimeInformation.ProcessArchitecture is Architecture.Arm64;

    /// <summary>
    /// Look for the CUDA runtime the way the loader will. Checked by presence rather than
    /// by P/Invoking <c>cudaGetDeviceCount</c>, because a missing DLL would raise
    /// <see cref="DllNotFoundException"/> from a probe that is supposed to answer a
    /// question, not throw one.
    /// </summary>
    public static bool HasCudaRuntime()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;

        foreach (var directory in ProbeDirectories())
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                if (Directory.EnumerateFiles(directory, "cudart64*.dll").Any()) return true;
                if (Directory.EnumerateFiles(directory, "cublas64*.dll").Any()) return true;
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return false;
    }

    private static IEnumerable<string> ProbeDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");

        // The CUDA toolkit / driver install, for a machine that has it system-wide.
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(system)) yield return system;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (entry.Contains("CUDA", StringComparison.OrdinalIgnoreCase))
                yield return entry;
    }

    /// <summary>A one-line description of the machine, for the session log.</summary>
    public static string Describe(AsrBackend resolved) =>
        $"{resolved.ToString().ToLowerInvariant()} · {RuntimeInformation.OSDescription} · " +
        $"{RuntimeInformation.ProcessArchitecture} · {Environment.ProcessorCount} logical cores";
}
