using System.Runtime.InteropServices;

namespace LocalCaption.Asr;

/// <summary>
/// Decides which compute backend to load, and makes the answer visible.
/// </summary>
/// <remarks>
/// <para>SPEC-WINDOWS.md §5.2: CUDA is the primary backend on the RTX 3070 and CPU
/// (AVX2, Zen 3) is the fallback. On any other PC (no NVIDIA GPU, or ARM64) the CPU is
/// simply the backend, and nothing here may require a GPU (SPEC-16, Compatibility target). Both ship in the installer rather than one being fetched
/// on demand, so there is no first-run failure mode on a machine that is known to have a
/// GPU.</para>
/// <para><b>A silent fall back to CPU must be visible rather than merely felt.</b> On this
/// laptop the dGPU can genuinely disappear at runtime — the MUX switch, Eco mode and
/// Armoury Crate profiles all remove it (§5.8) — and the only symptom would be captions
/// quietly getting slower. The resolved backend is returned so it can be logged and shown
/// in Settings.</para>
/// <para>Vulkan (AMD, Intel, or NVIDIA without CUDA) is a third, opt-in backend: x64 only,
/// used only when <c>asr.backend</c> says <c>vulkan</c>, never by <c>auto</c>.</para>
/// </remarks>
public static class BackendProbe
{
    /// <summary>
    /// Resolve <see cref="AsrBackend.Auto"/> against what this machine actually has.
    /// An explicit <c>cuda</c> or <c>cpu</c> is honoured as written, so the CPU path can be
    /// forced for A/B testing without a rebuild (<c>asr.backend</c>, §5.2). An explicit
    /// <c>vulkan</c> is honoured when it can be tried, and <c>auto</c> never picks it — see
    /// <see cref="BackendChoice"/>, which holds the rules.
    /// </summary>
    public static AsrBackend Resolve(AsrBackend requested) => Plan(requested).Resolved;

    /// <summary>
    /// Plan an engine load on this PC, in this process: the backend, the library order to set
    /// before the first factory, and a note when an explicit <c>vulkan</c> cannot be honoured.
    /// </summary>
    public static BackendPlan Plan(AsrBackend requested) =>
        BackendChoice.Plan(requested, PlatformFacts.Current(), LoadedLibrary());

    /// <summary>
    /// The native library Whisper.net has already loaded in this process, or null before the
    /// first model load. Reading it loads nothing.
    /// </summary>
    public static Whisper.net.LibraryLoader.RuntimeLibrary? LoadedLibrary()
    {
        try { return Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary; }
        catch (Exception) { return null; }
    }

    /// <summary>Whether a resolved backend wants the GPU path enabled on the factory.</summary>
    public static bool UsesGpu(AsrBackend resolved) =>
        resolved is AsrBackend.Cuda or AsrBackend.Metal or AsrBackend.Vulkan;

    /// <summary>
    /// True when a GPU backend is worth attempting. Whisper.net falls back to CPU by itself
    /// if the runtime is absent, so this only has to avoid the obviously-pointless cases.
    /// </summary>
    public static bool HasUsableGpu() => UsesGpu(Resolve(AsrBackend.Auto));

    /// <summary>
    /// Look for the CUDA runtime the way the loader will. Checked by presence rather than
    /// by P/Invoking <c>cudaGetDeviceCount</c>, because a missing DLL would raise
    /// <see cref="DllNotFoundException"/> from a probe that is supposed to answer a
    /// question, not throw one.
    /// </summary>
    /// <remarks>
    /// <para>Three things have to hold, not one. The process must be x64: Whisper.net's CUDA
    /// natives are win-x64 only, and there is no CUDA for Windows on ARM. The NVIDIA display
    /// driver must be installed (<see cref="HasNvidiaDriver"/>). And the CUDA runtime DLLs must
    /// be findable.</para>
    /// <para>The driver check matters on every PC that is not the G15: an installer built with
    /// <c>-CudaDirectory</c> puts <c>cudart64_*.dll</c> beside the executable on <i>every</i>
    /// machine it is installed on. Presence alone then said "CUDA" on an Intel or AMD laptop,
    /// <c>auto</c> kept the GPU-sized models, Whisper.net quietly loaded its CPU library, and
    /// large-v3-turbo ran at 17 s per window (§5.8) on a PC that could never have done better.</para>
    /// </remarks>
    public static bool HasCudaRuntime()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return false;
        if (!HasNvidiaDriver()) return false;

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

    /// <summary>
    /// Whether an NVIDIA display driver is installed: it is what puts <c>nvcuda.dll</c>, the
    /// CUDA driver API, in System32. Absent on every PC without an NVIDIA GPU.
    /// </summary>
    public static bool HasNvidiaDriver()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        try
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return system.Length > 0 && File.Exists(Path.Combine(system, "nvcuda.dll"));
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Whether the Vulkan loader, <c>vulkan-1.dll</c>, is in System32 — what Whisper.net's
    /// Vulkan build links against (<c>ggml-vulkan-whisper.dll</c> imports it).
    /// </summary>
    /// <remarks>
    /// <para>Checked by presence, for the same reason as <see cref="HasCudaRuntime"/>: a probe
    /// should answer, not throw. The loader is installed by every current AMD, Intel and NVIDIA
    /// display driver; a PC with only the Microsoft Basic Display Adapter, or a driver from
    /// before Vulkan, lacks it.</para>
    /// <para>Presence is necessary, not sufficient. A loader with no Vulkan-capable device
    /// behind it still loads, and whisper.cpp then reports "no GPU found" and runs on its CPU
    /// backend — which <see cref="WhisperEngine"/> watches for in the native log.</para>
    /// </remarks>
    public static bool HasVulkanLoader()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        try
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return system.Length > 0 && File.Exists(Path.Combine(system, "vulkan-1.dll"));
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Whether this build carries Whisper.net's Vulkan library, where its loader looks for it
    /// (<c>runtimes/vulkan/win-x64</c> beside the app). Absent from ARM64 builds and from
    /// x64 builds made with <c>build/publish.ps1 -NoVulkan</c>.
    /// </summary>
    public static bool HasVulkanRuntime()
    {
        try
        {
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", "vulkan", "win-x64", "whisper.dll"));
        }
        catch (Exception) { return false; }
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

    /// <summary>
    /// How many threads each whisper.cpp lane gets when <c>asr.threads</c> is 0 (§9.2:
    /// "physical cores").
    /// </summary>
    /// <remarks>
    /// <para><see cref="Environment.ProcessorCount"/> counts logical processors. On x64 that is
    /// usually two per core (SMT), and oversubscribing whisper.cpp with SMT siblings costs
    /// throughput, hence the halving the G15 (8 cores, 16 threads) was tuned with. ARM64
    /// Windows chips have no SMT, so halving there would leave half the CPU idle; and an x64
    /// part with fewer than four logical processors most likely has no SMT either, where
    /// halving would be the slowest possible choice.</para>
    /// <para>Capped at 8: whisper.cpp's decode stops scaling well around there, and the
    /// interim and final lanes run side by side, so more threads per lane only make them
    /// fight. The G15 gets the 8 it always had.</para>
    /// </remarks>
    public static int DefaultThreads()
    {
        var logical = Math.Max(1, Environment.ProcessorCount);
        var physical = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm || logical < 4
            ? logical
            : logical / 2;
        return Math.Clamp(physical, 1, 8);
    }

    /// <summary>
    /// The Visual C++ runtime DLLs whisper.cpp's natives need that this PC does not have, or
    /// an empty list. Windows only; elsewhere always empty.
    /// </summary>
    /// <remarks>
    /// Whisper.net's native libraries link the VC++ 2015–2022 runtime dynamically (MSVCP140,
    /// VCRUNTIME140, and on x64 also VCRUNTIME140_1 and the OpenMP VCOMP140). A self-contained
    /// .NET publish does not carry them. The installer installs the redistributable; a copied
    /// folder on a clean Windows does not have it, and then every model load fails with a
    /// <see cref="DllNotFoundException"/> that names whisper.dll, not the real culprit.
    /// </remarks>
    public static IReadOnlyList<string> MissingNativeRuntime()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return [];

        string[] needed = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? ["msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll"]
            : ["msvcp140.dll", "vcruntime140.dll"];

        try
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return [.. needed.Where(dll => !File.Exists(Path.Combine(AppContext.BaseDirectory, dll)) &&
                                           !(system.Length > 0 && File.Exists(Path.Combine(system, dll))))];
        }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// A sentence someone can act on, for a model-load failure whose real cause is this PC;
    /// null when nothing better than the exception's own message is known.
    /// </summary>
    /// <remarks>
    /// Only consulted after a load has actually failed: a presence check of System32 is a good
    /// explanation for a failure, but not certain enough to refuse a load that might work (an
    /// x64 process emulated on ARM64, say, finds its runtime by rules this does not model).
    /// </remarks>
    public static string? ExplainLoadFailure(Exception error)
    {
        var missing = MissingNativeRuntime();
        if (missing.Count > 0)
        {
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            return $"Speech recognition needs the Microsoft Visual C++ 2015–2022 Redistributable ({arch}), " +
                   $"which this PC does not have ({string.Join(", ", missing)} missing). Install it from " +
                   $"https://aka.ms/vs/17/release/vc_redist.{arch}.exe and restart Local Caption.";
        }

        // Whisper.net's own words when the CPU lacks AVX/AVX2/FMA and the noavx build is absent.
        if (error.Message.Contains("AVX", StringComparison.OrdinalIgnoreCase))
            return "This processor lacks the AVX2 instructions the speech engine's standard build " +
                   "needs, and the build for older processors is missing from this install. Reinstall Local Caption.";

        return null;
    }

    /// <summary>A one-line description of the machine, for the session log.</summary>
    public static string Describe(AsrBackend resolved) =>
        $"{resolved.ToString().ToLowerInvariant()} · {RuntimeInformation.OSDescription} · " +
        $"{RuntimeInformation.ProcessArchitecture} · {Environment.ProcessorCount} logical cores";
}
