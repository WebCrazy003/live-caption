using LocalCaption.Core.Captions;
using LocalCaption.Core.Transcripts;

namespace LocalCaption.Core.Interview;

/// <summary>
/// Decides which transcript text an Ask sends (SPEC-14 §What gets sent). Pure; shared with the
/// macOS build through <c>testdata/ask/</c>. Port of <c>AskSelection.swift</c>.
/// </summary>
/// <remarks>
/// The live interim line is always included: the final decode lags ~2 s behind speech and the
/// user presses right as the interviewer stops, so waiting for finals would cost the answer.
/// </remarks>
public static class AskSelection
{
    /// <summary>A committed final, in session audio time.</summary>
    public sealed record Segment(string Text, int TStartMs, int TEndMs)
    {
        public Segment(TranscriptSegment segment) : this(segment.Text, segment.TStartMs, segment.TEndMs) { }
    }

    /// <summary>Where the previous Ask ended.</summary>
    /// <param name="AudioMs">Session audio time of that press.</param>
    /// <param name="InterimWasSent">
    /// The utterance in flight at that press was sent as interim text, so its final — which
    /// straddles <paramref name="AudioMs"/> — counts as already asked.
    /// </param>
    public sealed record Mark(int AudioMs, bool InterimWasSent);

    /// <summary><c>interview.send_mode</c>, resolved: <see cref="SinceLastAsk"/> or
    /// <see cref="LastSentences"/> with its clamped count.</summary>
    public abstract record Mode
    {
        private Mode() { }

        /// <summary>Every final that is new since the previous Ask's <see cref="Mark"/>, plus the interim.</summary>
        public sealed record SinceLastAsk : Mode;

        /// <summary>The last <paramref name="N"/> sentences of the whole transcript plus the
        /// interim, whatever the mark.</summary>
        public sealed record LastSentences(int N) : Mode;
    }

    /// <param name="Text">Empty when there is nothing new to send.</param>
    /// <param name="Mark">The mark the next Ask starts from.</param>
    /// <param name="FromMs">Start of the audio span the ask covers, for the interview record.</param>
    /// <param name="ToMs">End of that span: the press.</param>
    public sealed record Result(string Text, Mark Mark, int FromMs, int ToMs);

    public static Result Select(IReadOnlyList<Segment> segments, string interim, Mode mode, Mark? mark,
                                int pressAudioMs, int maxWords)
    {
        var provisional = interim.Trim();
        string text;
        switch (mode)
        {
            case Mode.LastSentences(var n):
                text = Sentences.LastN(Join(segments.Select(s => s.Text)), provisional, n);
                break;
            case Mode.SinceLastAsk:
                var fresh = segments.Where(seg =>
                {
                    if (mark is null) return true;
                    if (seg.TStartMs >= mark.AudioMs) return true;
                    var straddles = seg.TEndMs > mark.AudioMs;
                    return straddles && !mark.InterimWasSent;
                });
                text = Join(fresh.Select(s => s.Text).Append(provisional));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
        return new Result(LastWords(text, maxWords),
                          new Mark(pressAudioMs, InterimWasSent: provisional.Length > 0),
                          mark?.AudioMs ?? 0,
                          pressAudioMs);
    }

    private static string Join(IEnumerable<string> parts) =>
        string.Join(" ", parts.Select(p => p.Trim()).Where(p => p.Length > 0));

    /// <summary>
    /// Keep the last <paramref name="n"/> words — the question is at the end of what was said.
    /// Words are re-joined with single spaces; <c>n &lt;= 0</c> gives <c>""</c>.
    /// </summary>
    public static string LastWords(string text, int n)
    {
        if (n <= 0) return "";
        var words = SwiftText.SplitOnWhitespace(text);
        return string.Join(" ", words.Skip(Math.Max(0, words.Count - n)));
    }
}
