using LocalCaption.Asr;

namespace LocalCaption.Asr.Tests;

/// <summary>
/// §5.8 — the rule that decides what runs when the RTX 3070 is not there.
/// </summary>
/// <remarks>
/// On this laptop the GPU is user-switchable: Eco mode, the MUX switch and Armoury Crate's
/// profiles all remove it, usually without the user thinking of it as changing anything. The
/// decision is arithmetic, so it is tested; the engine around it is not.
/// </remarks>
public sealed class AsrFallbackTests
{
    [Fact]
    public void OnTheGpuNothingIsChanged()
    {
        var (interim, final, banner) = AsrFallback.Choose(onGpu: true, "tiny.en", "large-v3-turbo");

        Assert.Equal("tiny.en", interim);
        Assert.Equal("large-v3-turbo", final);
        Assert.Null(banner);
    }

    [Fact]
    public void TurboIsReplacedOnTheCpuBecauseB0MeasuredItAsUnusable()
    {
        // 17 seconds per 6-second window is not "slower", it is a session that never catches
        // up. §5.8 names tiny.en + small.en as the fallback pair.
        var (interim, final, banner) = AsrFallback.Choose(onGpu: false, "tiny.en", "large-v3-turbo");

        Assert.Equal("tiny.en", interim);
        Assert.Equal("small.en", final);
        Assert.Contains("Running on CPU", banner);
        Assert.Contains("17 seconds", banner);
    }

    [Fact]
    public void ACpuSizedChoiceIsLeftAlone()
    {
        // The user picked something that works without a GPU. Overriding it would be the app
        // disagreeing with its own Settings screen for no reason.
        var (interim, final, banner) = AsrFallback.Choose(onGpu: false, "tiny.en", "small.en");

        Assert.Equal("tiny.en", interim);
        Assert.Equal("small.en", final);
        Assert.Contains("Running on CPU", banner);
        Assert.DoesNotContain("instead", banner);
    }

    [Fact]
    public void ATurboInterimIsDowngradedToo()
    {
        var (interim, final, _) = AsrFallback.Choose(onGpu: false, "large-v3-turbo", "large-v3-turbo");

        Assert.Equal("tiny.en", interim);
        Assert.Equal("small.en", final);
    }

    [Fact]
    public void OneBadWindowIsNotALostGpu()
    {
        // A clipped buffer or a cancelled decode fails once. Pausing an interview over that
        // would be its own bug — but three in a row, with audio still arriving, is the device
        // going away.
        Assert.True(AsrFallback.FailuresBeforeGpuPresumedLost > 1);
        Assert.True(AsrFallback.FailuresBeforeGpuPresumedLost <= 5);
    }
}
