using LocalCaption.Asr;
using Whisper.net;

namespace LocalCaption.Asr.Tests;

/// <summary>
/// The name→weights→alignment-heads mapping (SPEC-WINDOWS.md §5.3).
/// </summary>
public class ModelCatalogTests
{
    [Theory]
    [InlineData("tiny.en", "ggml-tiny.en.bin", WhisperAlignmentHeadsPreset.TinyEn)]
    [InlineData("base.en", "ggml-base.en.bin", WhisperAlignmentHeadsPreset.BaseEn)]
    [InlineData("small.en", "ggml-small.en.bin", WhisperAlignmentHeadsPreset.SmallEn)]
    [InlineData("large-v3-turbo", "ggml-large-v3-turbo.bin", WhisperAlignmentHeadsPreset.LargeV3Turbo)]
    public void KnownModelsMapToWeightsAndAlignmentHeads(string name, string file, WhisperAlignmentHeadsPreset heads)
    {
        var spec = ModelCatalog.Resolve(name);
        Assert.Equal(file, spec.FileName);
        // The preset is not cosmetic: the wrong one yields no usable word timings, and
        // RollingCaption has nothing else to merge sliding windows with (§5.7).
        Assert.Equal(heads, spec.Heads);
    }

    [Fact]
    public void ConfigNamesMatchTheMacOsBuild()
    {
        // §5.3 requires these names to stay identical across platforms so a config.json
        // carries between the Mac and the ASUS unchanged. Only what they resolve to differs.
        foreach (var name in new[] { "tiny.en", "base.en", "small.en", "large-v3-turbo" })
            Assert.True(ModelCatalog.IsKnown(name), $"'{name}' must stay a recognised config value");
    }

    [Fact]
    public void TheWindowsDefaultsResolve()
    {
        // §0.5 / §9.2: the RTX 3070 affords turbo as the default final model.
        var config = new LocalCaption.Core.Data.Config();
        Assert.True(ModelCatalog.IsKnown(config.Asr.InterimModel), config.Asr.InterimModel);
        Assert.True(ModelCatalog.IsKnown(config.Asr.FinalModel), config.Asr.FinalModel);
        Assert.Equal(WhisperAlignmentHeadsPreset.TinyEn, ModelCatalog.Resolve(config.Asr.InterimModel).Heads);
    }

    [Fact]
    public void UnknownNameDegradesWithoutWordTimings()
    {
        // An unknown model may still load, but it has no alignment heads — so the caller
        // gets no word timings and the §5.7.1 fallback applies. Guessing a preset here
        // would produce confidently wrong word times, which is worse than none.
        var spec = ModelCatalog.Resolve("some-future-model");
        Assert.Equal("ggml-some-future-model.bin", spec.FileName);
        Assert.Equal(WhisperAlignmentHeadsPreset.None, spec.Heads);
        Assert.False(ModelCatalog.IsKnown("some-future-model"));
    }

    [Fact]
    public void ResolveIsCaseAndWhitespaceTolerant()
    {
        // These come out of a hand-editable config file.
        Assert.Equal("ggml-tiny.en.bin", ModelCatalog.Resolve("  Tiny.EN  ").FileName);
    }

    [Fact]
    public void DownloadsComeFromTheWhisperCppRepository()
    {
        var uri = ModelCatalog.Resolve("tiny.en").DownloadUri;
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("huggingface.co", uri.Host);
        Assert.EndsWith("ggml-tiny.en.bin", uri.AbsolutePath);
    }

    [Fact]
    public void CatalogIsOrderedBySize()
    {
        var sizes = ModelCatalog.All.Select(m => m.ApproxBytes).ToList();
        Assert.Equal(sizes.OrderBy(s => s), sizes);
    }
}

/// <summary>Backend selection (§5.2, §5.8).</summary>
public class BackendProbeTests
{
    [Fact]
    public void ExplicitBackendsAreHonouredAsWritten()
    {
        // asr.backend exists so the CPU path can be forced for A/B testing without a
        // rebuild. A probe that overrode an explicit choice would defeat that.
        Assert.Equal(AsrBackend.Cpu, BackendProbe.Resolve(AsrBackend.Cpu));
        Assert.Equal(AsrBackend.Cuda, BackendProbe.Resolve(AsrBackend.Cuda));
    }

    [Fact]
    public void AutoResolvesToSomethingConcrete()
    {
        var resolved = BackendProbe.Resolve(AsrBackend.Auto);
        Assert.NotEqual(AsrBackend.Auto, resolved);
        Assert.Contains(resolved, new[] { AsrBackend.Cuda, AsrBackend.Cpu, AsrBackend.Metal });
    }

    [Fact]
    public void CudaIsNeverDetectedOffWindows()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.False(BackendProbe.HasCudaRuntime());
        // Reporting "cuda" on a machine with no NVIDIA GPU would make the Settings readout
        // and the bench output lie about what actually ran.
        Assert.NotEqual(AsrBackend.Cuda, BackendProbe.Resolve(AsrBackend.Auto));
    }

    [Fact]
    public void OnlyGpuBackendsEnableTheGpuPath()
    {
        Assert.True(BackendProbe.UsesGpu(AsrBackend.Cuda));
        Assert.True(BackendProbe.UsesGpu(AsrBackend.Metal));
        Assert.False(BackendProbe.UsesGpu(AsrBackend.Cpu));
    }

    [Fact]
    public void DescribeNamesTheResolvedBackend()
    {
        Assert.Contains("cpu", BackendProbe.Describe(AsrBackend.Cpu));
    }
}
