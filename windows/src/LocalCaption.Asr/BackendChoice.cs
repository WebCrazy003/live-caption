using System.Runtime.InteropServices;
using Whisper.net.LibraryLoader;

namespace LocalCaption.Asr;

/// <summary>
/// The facts about this PC that the backend choice depends on. Probed by
/// <see cref="Current"/>; built by hand in tests, which is the point of it being a record.
/// </summary>
/// <param name="IsWindows">Running on Windows.</param>
/// <param name="ProcessArchitecture">The process's architecture — not the OS's: an x64 build
/// emulated on ARM64 loads x64 libraries.</param>
/// <param name="AppleSilicon">The stage-A development Mac (Metal).</param>
/// <param name="HasCudaRuntime">What <see cref="BackendProbe.HasCudaRuntime"/> says.</param>
/// <param name="HasVulkanLoader">What <see cref="BackendProbe.HasVulkanLoader"/> says.</param>
/// <param name="VulkanBundled">What <see cref="BackendProbe.HasVulkanRuntime"/> says: false in
/// a build made with <c>-NoVulkan</c>.</param>
public sealed record PlatformFacts(
    bool IsWindows, Architecture ProcessArchitecture, bool AppleSilicon, bool HasCudaRuntime, bool HasVulkanLoader,
    bool VulkanBundled = true)
{
    /// <summary>This PC, now. Cheap: a few file-existence checks.</summary>
    public static PlatformFacts Current() => new(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
        RuntimeInformation.ProcessArchitecture,
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.ProcessArchitecture is Architecture.Arm64,
        BackendProbe.HasCudaRuntime(),
        BackendProbe.HasVulkanLoader(),
        BackendProbe.HasVulkanRuntime());
}

/// <summary>What to load for one engine: the backend, the library order, and why if it differs.</summary>
/// <param name="Requested">What <c>asr.backend</c> asked for.</param>
/// <param name="Resolved">What will be asked of Whisper.net (<see cref="BackendProbe.UsesGpu"/>).</param>
/// <param name="LibraryOrder">
/// The <see cref="RuntimeOptions.RuntimeLibraryOrder"/> to set before the first factory, or
/// null when this process has already loaded its library and the order no longer matters.
/// </param>
/// <param name="Note">
/// A sentence for the status line when the request could not be honoured as asked: an explicit
/// <c>vulkan</c> that fell back, or a switch away from Vulkan that needs a restart. Null otherwise.
/// </param>
public sealed record BackendPlan(
    AsrBackend Requested, AsrBackend Resolved, IReadOnlyList<RuntimeLibrary>? LibraryOrder, string? Note);

/// <summary>
/// Which backend and which Whisper.net native library to use, from <c>asr.backend</c> and the
/// facts about this PC. Pure, so the rules have tests.
/// </summary>
/// <remarks>
/// <para><b>Vulkan is opt-in, and <c>auto</c> must not drift into it.</b> Whisper.net's own
/// default order is Cuda → Cuda12 → <i>Vulkan</i> → CoreML → OpenVino → Cpu → CpuNoAvx
/// (<see cref="RuntimeOptions.RuntimeLibraryOrder"/>), and the loader walks it by which
/// <c>runtimes/&lt;variant&gt;/&lt;rid&gt;</c> folders exist. Bundling the Vulkan runtime would
/// therefore put every non-NVIDIA PC on the Vulkan library the moment it was installed. So the
/// order is always set explicitly: without Vulkan for <c>auto</c>, <c>cuda</c> and <c>cpu</c>
/// — which is exactly what they loaded before the package existed — and Vulkan → CPU, never
/// CUDA, for <c>vulkan</c>.</para>
/// <para><b>One library per process.</b> Whisper.net loads its native library once, on the
/// first factory, and records it in <see cref="RuntimeOptions.LoadedLibrary"/>; nothing
/// reloads it. Switching into or out of Vulkan therefore needs a restart, and until then the
/// honest answer is the CPU — not CUDA requests running on a Vulkan library, and not a Vulkan
/// request running on CUDA.</para>
/// </remarks>
public static class BackendChoice
{
    /// <summary>Every <c>asr.backend</c> value the pickers offer, in their order.</summary>
    public static IReadOnlyList<string> ConfigValues { get; } = ["auto", "cuda", "cpu", "vulkan"];

    /// <summary>The picker label for <c>vulkan</c>.</summary>
    public const string VulkanLabel = "Vulkan (AMD, Intel, other GPUs)";

    /// <summary>
    /// Read <c>asr.backend</c>. Anything unrecognised is <see cref="AsrBackend.Auto"/>, as it
    /// always has been.
    /// </summary>
    public static AsrBackend Parse(string? value) =>
        Enum.TryParse<AsrBackend>(value, ignoreCase: true, out var parsed) ? parsed : AsrBackend.Auto;

    /// <summary>Why Vulkan cannot be used on this PC, or null when it can be tried.</summary>
    public static string? VulkanUnavailableReason(PlatformFacts facts)
    {
        if (!facts.IsWindows) return "Vulkan is only offered on Windows.";
        if (facts.ProcessArchitecture is Architecture.Arm64)
            return "Vulkan is not available on ARM64 Windows — the speech engine's Vulkan build is x64 only.";
        if (facts.ProcessArchitecture is not Architecture.X64)
            return $"Vulkan needs the x64 build of Local Caption; this one is {facts.ProcessArchitecture.ToString().ToLowerInvariant()}.";
        if (!facts.VulkanBundled)
            return "This build of Local Caption was made without the Vulkan backend.";
        if (!facts.HasVulkanLoader)
            return "No Vulkan driver was found on this PC (vulkan-1.dll is not in System32). " +
                   "Installing or updating the graphics driver usually adds it.";
        return null;
    }

    /// <summary>
    /// Resolve <paramref name="requested"/> against this PC. <c>auto</c> is CUDA when a CUDA
    /// runtime and NVIDIA driver are present on x64, else the CPU (Metal on the development
    /// Mac) — never Vulkan. An explicit <c>vulkan</c> is Vulkan when it can be tried, else CPU.
    /// </summary>
    public static AsrBackend Resolve(AsrBackend requested, PlatformFacts facts) => requested switch
    {
        AsrBackend.Cpu => AsrBackend.Cpu,
        AsrBackend.Cuda => AsrBackend.Cuda,
        AsrBackend.Metal => AsrBackend.Metal,
        AsrBackend.Vulkan => VulkanUnavailableReason(facts) is null ? AsrBackend.Vulkan : AsrBackend.Cpu,
        _ when facts.AppleSilicon => AsrBackend.Metal,
        _ when facts.HasCudaRuntime => AsrBackend.Cuda,
        _ => AsrBackend.Cpu,
    };

    /// <summary>
    /// The native libraries Whisper.net may try, in order, for a request and its resolution.
    /// </summary>
    public static IReadOnlyList<RuntimeLibrary> LibraryOrder(AsrBackend requested, AsrBackend resolved)
    {
        if (requested is AsrBackend.Vulkan)
        {
            // Vulkan, else the CPU — never CUDA, which was not asked for. The no-AVX build is
            // kept behind the CPU one, as in the default order, for processors without AVX2.
            return resolved is AsrBackend.Vulkan
                ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
                : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
        }

        // Whisper.net's default order without Vulkan: what these choices loaded before the
        // Vulkan runtime was bundled, when its folder simply did not exist.
        return [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.CoreML,
                RuntimeLibrary.OpenVino, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
    }

    /// <summary>
    /// Plan one engine load.
    /// </summary>
    /// <param name="requested">From <c>asr.backend</c>.</param>
    /// <param name="facts">This PC.</param>
    /// <param name="loaded">The library this process already loaded, or null before the first load.</param>
    public static BackendPlan Plan(AsrBackend requested, PlatformFacts facts, RuntimeLibrary? loaded)
    {
        var resolved = Resolve(requested, facts);
        var note = requested is AsrBackend.Vulkan && resolved is not AsrBackend.Vulkan
            ? $"Vulkan was chosen, but it cannot be used here: {VulkanUnavailableReason(facts)}"
            : null;

        if (loaded is null) return new(requested, resolved, LibraryOrder(requested, resolved), note);

        // Already loaded, and Whisper.net will not load another (see remarks).
        if (resolved is AsrBackend.Vulkan && loaded is not RuntimeLibrary.Vulkan)
            return new(requested, AsrBackend.Cpu, null,
                       "Vulkan was chosen, and takes effect when Local Caption is restarted.");

        if (resolved is not AsrBackend.Vulkan && loaded is RuntimeLibrary.Vulkan && BackendProbe.UsesGpu(resolved))
            return new(requested, AsrBackend.Cpu, null,
                       $"Switching from Vulkan to {requested.ToString().ToLowerInvariant()} takes effect when " +
                       "Local Caption is restarted.");

        return new(requested, resolved, null, note);
    }

    /// <summary>
    /// Whether an engine that asked for <paramref name="resolved"/> is really on that GPU.
    /// </summary>
    /// <param name="resolved">The backend asked of Whisper.net.</param>
    /// <param name="library">What <see cref="RuntimeOptions.LoadedLibrary"/> says was loaded.</param>
    /// <param name="noGpuDevice">whisper.cpp logged that it found no GPU device to use.</param>
    /// <remarks>
    /// The second way a Vulkan request lands on the CPU, and the quieter one: the Vulkan
    /// library loads (the loader is present) but the driver offers no usable device, so
    /// whisper.cpp says "no GPU found" and carries on with its CPU backend inside the Vulkan
    /// build. Only Vulkan is judged on that log line; CUDA's answer is unchanged.
    /// </remarks>
    public static bool FellBack(AsrBackend resolved, string library, bool noGpuDevice) => resolved switch
    {
        AsrBackend.Cuda => !library.Equals(nameof(RuntimeLibrary.Cuda), StringComparison.OrdinalIgnoreCase),
        AsrBackend.Vulkan => noGpuDevice ||
                             !library.Equals(nameof(RuntimeLibrary.Vulkan), StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// The status-line sentence for a Vulkan load that ended up on the CPU, or null when it did not.
    /// </summary>
    public static string? VulkanFallbackNote(AsrBackend resolved, string library, NativeLoadLog log)
    {
        if (resolved is not AsrBackend.Vulkan || !FellBack(resolved, library, log.NoGpuFound)) return null;
        return log.NoGpuFound && library.Equals(nameof(RuntimeLibrary.Vulkan), StringComparison.OrdinalIgnoreCase)
            ? "Vulkan was chosen, but the graphics driver offered no Vulkan GPU to run on."
            : "Vulkan was chosen, but its speech library did not load" +
              (log.VulkanLoadError is { } error ? $" ({error})." : ".");
    }

    /// <summary>A picker entry: config value, label, whether it can be chosen, and why.</summary>
    public sealed record Option(string Value, string Label, bool Enabled, string ToolTip);

    /// <summary>
    /// The backend picker's entries, for Settings and the quick bar alike.
    /// </summary>
    /// <remarks>
    /// Vulkan is listed even where it cannot run, disabled with the reason, so someone looking
    /// for GPU support on an AMD laptop or an ARM64 PC learns why it is not there. A config
    /// that already says <c>vulkan</c> still shows it selected; the engine then runs on the CPU
    /// and the status line says why.
    /// </remarks>
    public static IReadOnlyList<Option> Options(PlatformFacts facts)
    {
        var vulkanBlocked = VulkanUnavailableReason(facts);
        const string experimental =
            "Experimental. Uses the GPU through its Vulkan driver — for AMD and Intel graphics, or an NVIDIA " +
            "GPU without the CUDA files. Never chosen by 'auto'. If it cannot start, speech recognition runs " +
            "on the CPU and the status line says why. Takes effect after a restart if another backend has " +
            "already loaded.";
        return
        [
            new("auto", "auto", true, "CUDA on an NVIDIA GPU when its runtime is present, otherwise the CPU."),
            new("cuda", "cuda", true, "NVIDIA GPUs, through the bundled CUDA build."),
            new("cpu", "cpu", true, "The processor only. Works on every PC; slower with large models."),
            new("vulkan", VulkanLabel, vulkanBlocked is null,
                vulkanBlocked is null ? experimental : $"{vulkanBlocked}\n\n{experimental}"),
        ];
    }
}

/// <summary>
/// Reads Whisper.net's log during a model load for the two things a Vulkan load can go
/// wrong with that nothing else reports: the library not loading, and no GPU behind it.
/// </summary>
/// <remarks>
/// <para>Both lines exist verbatim in the 1.9.1 binaries. The loader's own
/// "Failed to load whisper library from &lt;path&gt;. Error: …" and "Couldn't load dependency
/// at &lt;path&gt;. Received: …" are Whisper.net's managed <c>NativeLibraryLoader</c>, at Debug
/// level; "whisper_backend_init_gpu: no GPU found" is whisper.cpp's, forwarded through
/// <c>LogProvider</c> once the library is up.</para>
/// <para>Only lines about the <c>vulkan</c> runtime folder count as a load error, so a CUDA
/// or CPU attempt in the same log cannot be mistaken for one.</para>
/// </remarks>
public sealed class NativeLoadLog
{
    /// <summary>whisper.cpp found no GPU device and is running on its CPU backend.</summary>
    public bool NoGpuFound { get; private set; }

    /// <summary>Why the Vulkan library or one of its dependencies would not load, if it did not.</summary>
    public string? VulkanLoadError { get; private set; }

    /// <summary>Feed one log message. Thread-safe enough for a log: last writer wins.</summary>
    public void Observe(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;

        if (message.Contains("no GPU found", StringComparison.OrdinalIgnoreCase))
        {
            NoGpuFound = true;
            return;
        }

        var inVulkan = message.Contains("\\vulkan\\", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("/vulkan/", StringComparison.OrdinalIgnoreCase);
        if (!inVulkan) return;

        foreach (var marker in new[] { "PInvokeError: ", "Error: ", "Received: " })
        {
            var at = message.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;
            var detail = message[(at + marker.Length)..].Trim().TrimEnd('.');
            if (detail.Length > 0) VulkanLoadError = detail;
            return;
        }
    }
}
