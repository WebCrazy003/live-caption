using System.Runtime.InteropServices;
using LocalCaption.Asr;
using Whisper.net.LibraryLoader;

namespace LocalCaption.Asr.Tests;

/// <summary>
/// The opt-in Vulkan backend (SPEC-16, Compatibility target): who gets it, what loads, and
/// what is said when it cannot run. The one rule everything else serves — <c>auto</c> never
/// drifts onto Vulkan — is checked from several sides.
/// </summary>
public sealed class BackendChoiceTests
{
    private static PlatformFacts Pc(Architecture arch = Architecture.X64, bool cuda = false, bool vulkan = true,
                                    bool windows = true) =>
        new(windows, arch, AppleSilicon: false, HasCudaRuntime: cuda, HasVulkanLoader: vulkan);

    private static readonly PlatformFacts AmdLaptop = Pc(vulkan: true);
    private static readonly PlatformFacts G15 = Pc(cuda: true, vulkan: true);
    private static readonly PlatformFacts Snapdragon = Pc(Architecture.Arm64, vulkan: true);
    private static readonly PlatformFacts NoDriver = Pc(vulkan: false);

    // ── config ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("vulkan", AsrBackend.Vulkan)]
    [InlineData("Vulkan", AsrBackend.Vulkan)]
    [InlineData("auto", AsrBackend.Auto)]
    [InlineData("cuda", AsrBackend.Cuda)]
    [InlineData("cpu", AsrBackend.Cpu)]
    [InlineData("opencl", AsrBackend.Auto)]
    [InlineData("", AsrBackend.Auto)]
    [InlineData(null, AsrBackend.Auto)]
    public void ConfigValuesParseAndUnknownOnesAreAuto(string? value, AsrBackend expected) =>
        Assert.Equal(expected, BackendChoice.Parse(value));

    [Fact]
    public void ExistingEnumNumbersKeepTheirMeaning()
    {
        // Enum.TryParse accepts digits, so an append-only enum keeps an odd "2" meaning cpu.
        Assert.Equal(0, (int)AsrBackend.Auto);
        Assert.Equal(1, (int)AsrBackend.Cuda);
        Assert.Equal(2, (int)AsrBackend.Cpu);
        Assert.Equal(3, (int)AsrBackend.Metal);
    }

    // ── auto never picks Vulkan ──────────────────────────────────────────────────────────

    [Fact]
    public void AutoIsCudaWithCudaAndCpuWithout_NeverVulkan()
    {
        Assert.Equal(AsrBackend.Cuda, BackendChoice.Resolve(AsrBackend.Auto, G15));
        Assert.Equal(AsrBackend.Cpu, BackendChoice.Resolve(AsrBackend.Auto, AmdLaptop));
        Assert.Equal(AsrBackend.Cpu, BackendChoice.Resolve(AsrBackend.Auto, Snapdragon));
        Assert.Equal(AsrBackend.Cpu, BackendChoice.Resolve(AsrBackend.Auto, NoDriver));
    }

    [Theory]
    [InlineData(AsrBackend.Auto)]
    [InlineData(AsrBackend.Cuda)]
    [InlineData(AsrBackend.Cpu)]
    public void OnlyAVulkanRequestPutsVulkanInTheLoadOrder(AsrBackend requested)
    {
        foreach (var pc in new[] { G15, AmdLaptop, Snapdragon, NoDriver })
        {
            var plan = BackendChoice.Plan(requested, pc, loaded: null);
            Assert.NotNull(plan.LibraryOrder);
            Assert.DoesNotContain(RuntimeLibrary.Vulkan, plan.LibraryOrder!);
            Assert.Null(plan.Note);
        }
    }

    [Fact]
    public void WithoutVulkanTheOrderIsWhisperNetsDefaultMinusVulkan()
    {
        // What these choices loaded before the Vulkan runtime was bundled: Whisper.net's own
        // order, in which the absent vulkan folder was simply skipped.
        Assert.Equal(
            [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.CoreML, RuntimeLibrary.OpenVino,
             RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            BackendChoice.LibraryOrder(AsrBackend.Auto, AsrBackend.Cpu));
    }

    // ── an explicit vulkan ───────────────────────────────────────────────────────────────

    [Fact]
    public void VulkanOnAnX64PcWithADriverLoadsVulkanThenCpu_NeverCuda()
    {
        foreach (var pc in new[] { AmdLaptop, G15 })
        {
            var plan = BackendChoice.Plan(AsrBackend.Vulkan, pc, loaded: null);
            Assert.Equal(AsrBackend.Vulkan, plan.Resolved);
            Assert.True(BackendProbe.UsesGpu(plan.Resolved));
            Assert.Equal([RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx], plan.LibraryOrder);
            Assert.Null(plan.Note);
        }
    }

    [Fact]
    public void VulkanOnArm64IsTheCpuWithAReason()
    {
        var plan = BackendChoice.Plan(AsrBackend.Vulkan, Snapdragon, loaded: null);

        Assert.Equal(AsrBackend.Cpu, plan.Resolved);
        Assert.Equal([RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx], plan.LibraryOrder);
        Assert.Contains("ARM64", plan.Note);
    }

    [Fact]
    public void VulkanWithoutALoaderIsTheCpuWithAReason()
    {
        var plan = BackendChoice.Plan(AsrBackend.Vulkan, NoDriver, loaded: null);

        Assert.Equal(AsrBackend.Cpu, plan.Resolved);
        Assert.DoesNotContain(RuntimeLibrary.Vulkan, plan.LibraryOrder!);
        Assert.DoesNotContain(RuntimeLibrary.Cuda, plan.LibraryOrder!);
        Assert.Contains("vulkan-1.dll", plan.Note);
    }

    [Fact]
    public void VulkanInABuildWithoutItIsTheCpuWithAReason()
    {
        var noVulkanBuild = AmdLaptop with { VulkanBundled = false };
        var plan = BackendChoice.Plan(AsrBackend.Vulkan, noVulkanBuild, loaded: null);

        Assert.Equal(AsrBackend.Cpu, plan.Resolved);
        Assert.Contains("without the Vulkan backend", plan.Note);
        Assert.False(BackendChoice.Options(noVulkanBuild)[^1].Enabled);
    }

    [Fact]
    public void VulkanOffWindowsIsNeverTried()
    {
        Assert.NotNull(BackendChoice.VulkanUnavailableReason(Pc(windows: false)));
        Assert.Equal(AsrBackend.Cpu, BackendChoice.Resolve(AsrBackend.Vulkan, Pc(windows: false)));
    }

    [Fact]
    public void AnUnavailableVulkanGetsCpuSizedModels()
    {
        var plan = BackendChoice.Plan(AsrBackend.Vulkan, NoDriver, loaded: null);
        var (interim, final, banner) = AsrFallback.Choose(BackendProbe.UsesGpu(plan.Resolved),
                                                          "tiny.en", "large-v3-turbo", plan.Note);

        Assert.Equal(AsrFallback.CpuModels, (interim, final));
        Assert.NotNull(banner);
        Assert.Contains("Vulkan", banner);
        Assert.DoesNotContain("NVIDIA", banner);      // the generic hint would be beside the point
    }

    // ── one library per process ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RuntimeLibrary.Cuda)]
    [InlineData(RuntimeLibrary.Cpu)]
    [InlineData(RuntimeLibrary.CpuNoAvx)]
    public void ChoosingVulkanAfterAnotherLibraryLoadedIsTheCpuUntilRestart(RuntimeLibrary loaded)
    {
        var plan = BackendChoice.Plan(AsrBackend.Vulkan, AmdLaptop, loaded);

        Assert.Equal(AsrBackend.Cpu, plan.Resolved);      // never a Vulkan ask on a CUDA library
        Assert.Null(plan.LibraryOrder);
        Assert.Contains("restart", plan.Note);
    }

    [Fact]
    public void LeavingVulkanForAGpuBackendIsTheCpuUntilRestart()
    {
        var plan = BackendChoice.Plan(AsrBackend.Cuda, G15, RuntimeLibrary.Vulkan);

        Assert.Equal(AsrBackend.Cpu, plan.Resolved);      // never a CUDA ask on the Vulkan library
        Assert.Contains("restart", plan.Note);
    }

    [Fact]
    public void CpuOnTheVulkanLibraryIsJustTheCpu()
    {
        var plan = BackendChoice.Plan(AsrBackend.Cpu, AmdLaptop, RuntimeLibrary.Vulkan);

        Assert.Equal(AsrBackend.Cpu, plan.Resolved);
        Assert.Null(plan.Note);
    }

    [Fact]
    public void NothingChangesForOtherChoicesOnceLoaded()
    {
        Assert.Equal(new BackendPlan(AsrBackend.Auto, AsrBackend.Cuda, null, null),
                     BackendChoice.Plan(AsrBackend.Auto, G15, RuntimeLibrary.Cuda));
        Assert.Equal(new BackendPlan(AsrBackend.Vulkan, AsrBackend.Vulkan, null, null),
                     BackendChoice.Plan(AsrBackend.Vulkan, AmdLaptop, RuntimeLibrary.Vulkan));
    }

    // ── did it really run on the GPU ─────────────────────────────────────────────────────

    [Fact]
    public void VulkanFellBackWhenTheCpuLibraryLoadedOrNoDeviceWasFound()
    {
        Assert.False(BackendChoice.FellBack(AsrBackend.Vulkan, "Vulkan", noGpuDevice: false));
        Assert.True(BackendChoice.FellBack(AsrBackend.Vulkan, "Cpu", noGpuDevice: false));
        Assert.True(BackendChoice.FellBack(AsrBackend.Vulkan, "CpuNoAvx", noGpuDevice: false));
        Assert.True(BackendChoice.FellBack(AsrBackend.Vulkan, "Vulkan", noGpuDevice: true));
    }

    [Fact]
    public void CudaFallBackIsJudgedAsBefore()
    {
        Assert.False(BackendChoice.FellBack(AsrBackend.Cuda, "Cuda", noGpuDevice: false));
        Assert.True(BackendChoice.FellBack(AsrBackend.Cuda, "Cpu", noGpuDevice: false));
        // The no-device log line is only consulted for Vulkan.
        Assert.False(BackendChoice.FellBack(AsrBackend.Cuda, "Cuda", noGpuDevice: true));
        Assert.False(BackendChoice.FellBack(AsrBackend.Cpu, "Cpu", noGpuDevice: true));
        Assert.False(BackendChoice.FellBack(AsrBackend.Metal, "Cpu", noGpuDevice: false));
    }

    [Fact]
    public void EngineInfoReportsAVulkanFallBack()
    {
        var info = new EngineInfo("tiny.en", "small.en", AsrBackend.Vulkan, true, 8, "", Library: "Cpu");
        Assert.True(info.FellBack);
        Assert.Contains("vulkan→cpu", info.ToString());
    }

    [Fact]
    public void TheLoadLogExplainsAMissingLibrary()
    {
        // Whisper.net 1.9.1's NativeLibraryLoader, verbatim apart from the path.
        var log = new NativeLoadLog();
        log.Observe(@"Trying to load whisper library from C:\App\runtimes\vulkan\win-x64\whisper.dll");
        log.Observe(@"Failed to load whisper library from C:\App\runtimes\vulkan\win-x64\whisper.dll. Error: " +
                    "Cannot load the library on this platform using NativeLibrary. PInvokeError: The specified module could not be found.");
        log.Observe(@"Successfully loaded whisper library from C:\App\runtimes\win-x64\whisper.dll");

        Assert.False(log.NoGpuFound);
        Assert.Equal("The specified module could not be found", log.VulkanLoadError);

        var note = BackendChoice.VulkanFallbackNote(AsrBackend.Vulkan, "Cpu", log);
        Assert.Contains("did not load", note);
        Assert.Contains("The specified module could not be found", note);
    }

    [Fact]
    public void TheLoadLogNoticesNoGpu()
    {
        var log = new NativeLoadLog();
        log.Observe("whisper_backend_init_gpu: no GPU found\n");

        Assert.True(log.NoGpuFound);
        Assert.Contains("no Vulkan GPU", BackendChoice.VulkanFallbackNote(AsrBackend.Vulkan, "Vulkan", log));
    }

    [Fact]
    public void TheLoadLogIgnoresOtherRuntimesAndHealthyLoads()
    {
        var log = new NativeLoadLog();
        log.Observe(@"Failed to load whisper library from C:\App\runtimes\cuda\win-x64\whisper.dll. Error: nope");
        log.Observe("whisper_backend_init_gpu: using Vulkan0 backend");
        log.Observe(null);

        Assert.Null(log.VulkanLoadError);
        Assert.False(log.NoGpuFound);
        Assert.Null(BackendChoice.VulkanFallbackNote(AsrBackend.Vulkan, "Vulkan", log));
        Assert.Null(BackendChoice.VulkanFallbackNote(AsrBackend.Cuda, "Cpu", log));
    }

    // ── the pickers ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void PickersOfferVulkanLast_DisabledWithAReasonWhereItCannotRun()
    {
        var options = BackendChoice.Options(Snapdragon);
        Assert.Equal(BackendChoice.ConfigValues, options.Select(o => o.Value));

        var vulkan = options[^1];
        Assert.Equal("Vulkan (AMD, Intel, other GPUs)", vulkan.Label);
        Assert.False(vulkan.Enabled);
        Assert.Contains("ARM64", vulkan.ToolTip);
        Assert.Contains("Experimental", vulkan.ToolTip);

        Assert.False(BackendChoice.Options(NoDriver)[^1].Enabled);
        Assert.True(BackendChoice.Options(AmdLaptop)[^1].Enabled);
        Assert.All(BackendChoice.Options(Snapdragon).Take(3), o => Assert.True(o.Enabled));
    }
}
