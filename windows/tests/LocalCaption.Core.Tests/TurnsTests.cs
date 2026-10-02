using LocalCaption.Core.Captions;
using LocalCaption.Core.Transcripts;
using Xunit;

namespace LocalCaption.Core.Tests;

/// <summary>
/// "The last question": the final unbroken stretch of speech, however many sentences it is.
/// </summary>
public sealed class TurnsTests
{
    private const int Gap = 2500;

    private static TranscriptSegment Said(string text, int startMs, int endMs) =>
        new() { Text = text, TStartMs = startMs, TEndMs = endMs };

    [Fact]
    public void Nothing_said_is_an_empty_turn()
    {
        Assert.Empty(Turns.Last([], Gap));
        Assert.Equal("", Turns.LastText([], Gap));
    }

    [Fact]
    public void A_question_asked_in_several_breaths_is_one_turn()
    {
        var segments = new[]
        {
            Said("Tell me about a hard project.", 0, 3000),
            Said("What made it hard?", 3800, 5500),          // 0.8 s pause: still the same question
            Said("And what would you change?", 6900, 9000),  // 1.4 s pause: still the same question
        };

        Assert.Equal("Tell me about a hard project. What made it hard? And what would you change?",
                     Turns.LastText(segments, Gap));
    }

    [Fact]
    public void A_long_silence_starts_a_new_turn_and_the_old_one_is_left_behind()
    {
        var segments = new[]
        {
            Said("Where did you work before?", 0, 2500),
            Said("Thanks.", 3000, 3600),
            Said("Now, why this company?", 9000, 11000),     // 5.4 s of the candidate answering
        };

        Assert.Equal("Now, why this company?", Turns.LastText(segments, Gap));
    }

    [Fact]
    public void A_gap_of_exactly_the_threshold_is_a_new_turn()
    {
        var segments = new[] { Said("One.", 0, 1000), Said("Two.", 1000 + Gap, 5000) };
        Assert.Equal("Two.", Turns.LastText(segments, Gap));
    }

    [Fact]
    public void Bookmarks_are_never_part_of_a_turn_and_never_split_one()
    {
        var segments = new[]
        {
            Said("How do you prioritise", 0, 2000),
            Said($"{Turns.BookmarkLead} bookmark 00:00:02]", 2100, 2100),
            Said("when everything is urgent?", 2400, 4500),
        };

        Assert.Equal("How do you prioritise when everything is urgent?", Turns.LastText(segments, Gap));
    }

    [Fact]
    public void Words_still_being_spoken_are_the_end_of_the_turn()
    {
        var segments = new[] { Said("What is your biggest", 0, 2000) };

        // Pressed mid-sentence, 2.6 s in: "weakness and why" took about 1.2 s to say, so it
        // began right where the last segment ended. Same turn.
        Assert.Equal("What is your biggest weakness and why",
                     Turns.LastText(segments, Gap, live: "weakness and why", nowMs: 3300));
    }

    [Fact]
    public void Words_being_spoken_after_a_long_silence_are_a_turn_of_their_own()
    {
        var segments = new[] { Said("Thanks, that is clear.", 0, 2000) };

        // 10 s later someone starts a new question. The old remark is not part of it.
        Assert.Equal("So tell me about", Turns.LastText(segments, Gap, live: "So tell me about", nowMs: 12000));
    }

    [Fact]
    public void Without_a_clock_the_live_words_are_simply_appended()
    {
        var segments = new[] { Said("What is your", 0, 1500) };
        Assert.Equal("What is your notice period", Turns.LastText(segments, Gap, live: "notice period"));
    }
}
