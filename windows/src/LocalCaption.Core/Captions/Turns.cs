using LocalCaption.Core.Transcripts;

namespace LocalCaption.Core.Captions;

/// <summary>
/// Finds "what they just asked": the last unbroken stretch of speech in a transcript.
/// </summary>
/// <remarks>
/// <para>"Copy last N sentences" answers the wrong question for an interview. An interviewer
/// asks something in one sentence or in six, and what is wanted afterwards is <i>that</i>,
/// whole, with nothing from the question before it. Sentences do not carry that boundary;
/// silence does. A turn is therefore the run of segments, walking back from the end, that
/// are separated by less than <c>gapMs</c> of quiet.</para>
/// <para>The timings are the sample-based ones, which freeze while paused — so a pause in the
/// middle of someone's question does not split it in two.</para>
/// </remarks>
public static class Turns
{
    /// <summary>What a bookmark's segment text starts with. Never part of anything copied or sent.</summary>
    public const string BookmarkLead = "[★";

    public static bool IsBookmark(TranscriptSegment segment) =>
        segment.Text.StartsWith(BookmarkLead, StringComparison.Ordinal);

    /// <summary>The segments of the last turn, oldest first. Empty when nothing has been said.</summary>
    public static IReadOnlyList<TranscriptSegment> Last(IReadOnlyList<TranscriptSegment> segments, int gapMs)
    {
        var spoken = segments.Where(s => !IsBookmark(s)).ToList();
        if (spoken.Count == 0) return [];

        var first = spoken.Count - 1;
        while (first > 0 && spoken[first].TStartMs - spoken[first - 1].TEndMs < gapMs) first--;
        return spoken.GetRange(first, spoken.Count - first);
    }

    /// <summary>
    /// The last turn as text, with whatever is still being spoken on the end of it.
    /// </summary>
    /// <param name="live">
    /// The provisional caption, if any. Someone who presses the key the instant the question
    /// ends is ahead of the final model by about a second; without this they would get the
    /// question minus its last clause.
    /// </param>
    /// <param name="nowMs">
    /// The session clock now, when known. The provisional caption has no timing of its own,
    /// so whether it continues the last turn or opens a new one is judged from how long it
    /// would have taken to say: if even that leaves a full gap of silence before it, it is a
    /// new turn, and the old one is left out.
    /// </param>
    public static string LastText(IReadOnlyList<TranscriptSegment> segments, int gapMs, string live = "",
                                  int? nowMs = null)
    {
        var turn = Last(segments, gapMs);
        var committed = string.Join(" ", turn.Select(s => s.Text.Trim()).Where(t => t.Length > 0));
        live = live.Trim();
        if (live.Length == 0) return committed;
        if (committed.Length == 0) return live;

        if (nowMs is { } now)
        {
            const int msPerWord = 400;      // unhurried speech; erring long keeps turns together
            var spokenFor = Filters.WordCount(live) * msPerWord;
            if (now - turn[^1].TEndMs - spokenFor >= gapMs) return live;
        }

        return committed + " " + live;
    }
}
