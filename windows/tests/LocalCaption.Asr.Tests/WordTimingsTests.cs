using LocalCaption.Asr;
using LocalCaption.Core.Captions;
using Whisper.net;

namespace LocalCaption.Asr.Tests;

/// <summary>
/// Token-to-word grouping (SPEC-WINDOWS.md §5.7).
/// </summary>
/// <remarks>
/// <para>These build <see cref="SegmentData"/> by hand rather than decoding audio, which
/// means they run <b>without the native whisper library</b> — the part of the ASR layer
/// that can be verified on the Mac in stage A. It is also the part most worth verifying:
/// <see cref="RollingCaption"/> aligns overlapping hypotheses purely by word midpoint, so
/// bad word times do not degrade the live caption gracefully, they scramble it.</para>
/// </remarks>
public class WordTimingsTests
{
    /// <summary>whisper.cpp reports token times in centiseconds.</summary>
    private static WhisperToken Token(string text, long startCs, long endCs, long dtwCs = -1) =>
        new() { Text = text, Start = startCs, End = endCs, DtwTimestamp = dtwCs, ProbabilityLog = -0.2f };

    private static SegmentData Segment(params WhisperToken[] tokens) =>
        new(string.Concat(tokens.Select(t => t.Text)), TimeSpan.Zero, TimeSpan.FromSeconds(6),
            0.5f, 0.9f, 0.8f, 0.01f, "en", tokens);

    [Fact]
    public void LeadingSpaceStartsANewWord()
    {
        var words = WordTimings.FromTokens(Segment(
            Token(" hello", 0, 40),
            Token(" world", 50, 90)));

        Assert.Equal(["hello", "world"], words.Select(w => w.Text));
        Assert.Equal(0.0, words[0].Start, precision: 6);
        Assert.Equal(0.4, words[0].End, precision: 6);
        Assert.Equal(0.5, words[1].Start, precision: 6);
    }

    [Fact]
    public void SubwordTokensJoinIntoOneWord()
    {
        // "unbelievable" arrives as three tokens; only the first carries a leading space.
        var words = WordTimings.FromTokens(Segment(
            Token(" un", 100, 120),
            Token("believ", 120, 160),
            Token("able", 160, 200)));

        var word = Assert.Single(words);
        Assert.Equal("unbelievable", word.Text);
        Assert.Equal(1.0, word.Start, precision: 6);
        Assert.Equal(2.0, word.End, precision: 6);
    }

    [Fact]
    public void SpecialTokensAreNotWords()
    {
        var words = WordTimings.FromTokens(Segment(
            Token("<|startoftranscript|>", 0, 0),
            Token(" real", 10, 30),
            Token("<|endoftext|>", 30, 30)));

        Assert.Equal(["real"], words.Select(w => w.Text));
    }

    [Fact]
    public void DtwTimestampsArePreferredOverTokenTimes()
    {
        // When alignment heads are active the DTW instant is the accurate one; the token's
        // own start is the coarse fallback. Preferring the wrong one would shift every
        // midpoint and quietly break RollingCaption's 350 ms anchor window.
        var words = WordTimings.FromTokens(Segment(
            Token(" shifted", startCs: 300, endCs: 340, dtwCs: 120)));

        var word = Assert.Single(words);
        Assert.Equal(1.2, word.Start, precision: 6);
        Assert.Equal(3.4, word.End, precision: 6);
    }

    [Fact]
    public void MissingDtwFallsBackToTokenTimes()
    {
        var words = WordTimings.FromTokens(Segment(
            Token(" plain", startCs: 300, endCs: 340, dtwCs: -1)));

        var word = Assert.Single(words);
        Assert.Equal(3.0, word.Start, precision: 6);
        Assert.Equal(3.4, word.End, precision: 6);
    }

    [Fact]
    public void NoTokensYieldsNoWords()
    {
        // The pipeline detects an empty word list and falls back to "waiting for final
        // transcription" (§5.7.1). Inventing words here would defeat that.
        Assert.Empty(WordTimings.FromTokens(Segment()));
        Assert.Empty(WordTimings.FromTokens(
            new SegmentData("text", TimeSpan.Zero, TimeSpan.FromSeconds(1), 0, 0, 0, 0, "en", null!)));
    }

    [Fact]
    public void WhitespaceOnlyTokensDoNotBecomeWords()
    {
        var words = WordTimings.FromTokens(Segment(
            Token(" ", 0, 10),
            Token(" word", 10, 40),
            Token("  ", 40, 50)));

        Assert.Equal(["word"], words.Select(w => w.Text));
    }

    [Fact]
    public void EveryWordHasAUsableMidpoint()
    {
        // RollingCaption's anchor search cannot tolerate NaN or a reversed span.
        var words = WordTimings.FromTokens(Segment(
            Token(" a", 100, 50),       // end before start — whisper.cpp does emit these
            Token(" b", -10, 20),       // negative start
            Token(" c", 200, 200)));    // zero width

        Assert.Equal(3, words.Count);
        foreach (var word in words)
        {
            Assert.True(double.IsFinite(word.Midpoint), $"'{word.Text}' has a non-finite midpoint");
            Assert.True(word.End >= word.Start, $"'{word.Text}' ends before it starts");
            Assert.True(word.Start >= 0, $"'{word.Text}' starts before the window");
        }
    }

    [Fact]
    public void WordsComeOutInTimeOrderForATypicalSegment()
    {
        var words = WordTimings.FromTokens(Segment(
            Token(" Thanks", 0, 30),
            Token(" for", 30, 45),
            Token(" joining", 45, 90),
            Token(" today", 90, 130),
            Token(".", 130, 132)));

        Assert.Equal(["Thanks", "for", "joining", "today."], words.Select(w => w.Text));
        for (var i = 1; i < words.Count; i++)
            Assert.True(words[i].Midpoint >= words[i - 1].Midpoint, "words must not go backwards in time");
    }

    [Fact]
    public void TrailingPunctuationStaysAttachedToItsWord()
    {
        // A bare "." as its own word would be meaningless to the anchor match, whose
        // normalisation strips punctuation and would leave an empty string.
        var words = WordTimings.FromTokens(Segment(
            Token(" done", 0, 30),
            Token(".", 30, 32)));

        var word = Assert.Single(words);
        Assert.Equal("done.", word.Text);
        Assert.Equal("done", word.Normalized);
    }
}
