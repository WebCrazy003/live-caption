using LocalCaption.Core.Captions;

namespace LocalCaption.Core.Tests;

/// <summary>
/// Fixtures for the two quality signals Windows has to derive (SPEC-WINDOWS.md §5.6).
/// </summary>
/// <remarks>
/// These are <b>not</b> shared vectors, deliberately. macOS gets all three signals handed
/// to it by WhisperKit and has no counterpart implementation, so there is no cross-platform
/// contract to assert — only that the Windows values land on the same side of the same
/// thresholds. A filter that quietly stops filtering is invisible until transcripts fill
/// with repetition loops, so the gate decisions are asserted, not just the arithmetic.
/// </remarks>
public class SegmentQualityTests
{
    // ── avg_logprob: mean of per-token log probabilities ─────────────────────────────────

    [Fact]
    public void AverageLogprobIsTheMean()
    {
        Assert.Equal(-0.5, SegmentQuality.AverageLogprob([-0.25, -0.75]), precision: 10);
        Assert.Equal(-1.0, SegmentQuality.AverageLogprob([-1.0]), precision: 10);
    }

    [Fact]
    public void AverageLogprobIgnoresUnscoredTokens()
    {
        // whisper.cpp leaves some tokens unscored. A NaN would poison the mean and turn the
        // comparison against the threshold into a silent false — NaN < -1.0 is false — so
        // every low-confidence garble would sail through the gate.
        Assert.Equal(-0.5, SegmentQuality.AverageLogprob([-0.25, double.NaN, -0.75]), precision: 10);
        Assert.Equal(-0.5, SegmentQuality.AverageLogprob([-0.5, double.PositiveInfinity]), precision: 10);
    }

    [Fact]
    public void AverageLogprobOfNothingDoesNotTripTheGate()
    {
        // A segment with no scored tokens carries no evidence of being bad; the other two
        // signals still apply. Returning something < -1.0 here would reject valid speech.
        Assert.Equal(0, SegmentQuality.AverageLogprob([]));
        Assert.False(Filters.IsLowQuality(SegmentQuality.AverageLogprob([]), 0.05, 1.4));
    }

    // ── compression_ratio: len(utf8) / len(gzip(utf8)) ───────────────────────────────────

    [Fact]
    public void RepetitionCompressesFarBetterThanSpeech()
    {
        const string loop = "come come come come come come come come come come come come come come come come";
        const string speech = "Thanks for joining today, I wanted to start by walking through the architecture.";

        var loopRatio = SegmentQuality.CompressionRatio(loop);
        var speechRatio = SegmentQuality.CompressionRatio(speech);

        Assert.True(loopRatio > speechRatio,
            $"a repetition loop must compress better than speech: loop={loopRatio:0.00}, speech={speechRatio:0.00}");
    }

    [Fact]
    public void GateRejectsRepetitionAndPassesSpeech()
    {
        // The decision, not the number, is what the product depends on.
        const string loop = "come come come come come come come come come come come come come come come come " +
                            "come come come come come come come come come come come come come come come come";
        const string speech = "Thanks for joining today, I wanted to start by walking through the architecture " +
                              "before we get into the migration timeline and what it means for the team.";

        Assert.True(Filters.IsLowQuality(-0.3, 0.05, SegmentQuality.CompressionRatio(loop)),
            "a long repetition loop must trip the compression threshold");
        Assert.False(Filters.IsLowQuality(-0.3, 0.05, SegmentQuality.CompressionRatio(speech)),
            "ordinary speech must not trip the compression threshold");
    }

    [Fact]
    public void CompressionRatioGrowsWithRepetition()
    {
        var once = SegmentQuality.CompressionRatio("the quick brown fox jumps over the lazy dog");
        var many = SegmentQuality.CompressionRatio(
            string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog ", 20)));
        Assert.True(many > once, $"ratio should rise with repetition: once={once:0.00}, many={many:0.00}");
    }

    [Fact]
    public void EmptyTextHasNoRatioAndDoesNotTripTheGate()
    {
        Assert.Equal(0, SegmentQuality.CompressionRatio(""));
        Assert.False(Filters.IsLowQuality(-0.3, 0.05, SegmentQuality.CompressionRatio("")));
    }

    [Fact]
    public void NonAsciiTextIsMeasuredInUtf8Bytes()
    {
        // Must not throw or return something wild on the characters a real transcript has.
        var ratio = SegmentQuality.CompressionRatio("Über — naïve “quotes” and emoji 🎧 in one line.");
        Assert.True(ratio is > 0 and < 10, $"unexpected ratio {ratio:0.00}");
    }
}
