using LocalCaption.Core.Audio;
using LocalCaption.Core.Captions;
using Whisper.net;

namespace LocalCaption.Asr;

/// <summary>
/// Turns whisper.cpp's per-<b>token</b> timings into per-<b>word</b> timings.
/// </summary>
/// <remarks>
/// <para>Whisper.net exposes no word-level API — <see cref="SegmentData.Tokens"/> is the
/// finest granularity available — so the grouping happens here, on the rule SPEC-WINDOWS.md
/// §5.7 specifies: a token beginning with a space starts a new word, and tokens without one
/// are continuations of the current word ("un", "believ", "able").</para>
/// <para><b>Why this matters.</b>
/// <see cref="LocalCaption.Core.Captions.RollingCaption"/> merges overlapping hypotheses by
/// word midpoint in session coordinates and has no other way to align them, because each
/// interim decode is a different 6 s tail with no shared prefix. Bad word times do not
/// degrade the live caption gracefully — they scramble it. When timings are unavailable
/// entirely, returning an empty list is correct: the pipeline detects it and falls back to
/// "waiting for final transcription" (§5.7.1) rather than showing nonsense.</para>
/// <para>Special tokens (<c>&lt;|…|&gt;</c>) carry no text and must not become words; they
/// are dropped before grouping.</para>
/// </remarks>
public static class WordTimings
{
    /// <summary>whisper.cpp reports token times in centiseconds (10 ms units).</summary>
    private const double CentisecondsToSeconds = 0.01;

    /// <summary>
    /// Whether a token is whisper's own markup rather than speech.
    /// </summary>
    /// <remarks>
    /// <b>Two shapes, and only one of them was obvious.</b> The model's special tokens print
    /// as <c>&lt;|…|&gt;</c>, but whisper.cpp renders its <i>internal</i> ones in square
    /// brackets — <c>[_BEG_]</c>, <c>[_TT_100]</c>, <c>[_SOT_]</c> — and the interim lane
    /// asks for token timestamps, which is exactly what makes it emit them. They reached the
    /// screen: "for your country. [_BEG_] [_BEG_] [_BEG_] And so my fellow … mirror[_TT_100]".
    /// <para>WhisperKit on macOS never produced either shape, so the ported filter had no
    /// reason to know about them.</para>
    /// </remarks>
    private static bool IsMarkup(string raw) =>
        (raw.StartsWith("<|", StringComparison.Ordinal) && raw.EndsWith("|>", StringComparison.Ordinal)) ||
        (raw.StartsWith("[_", StringComparison.Ordinal) && raw.EndsWith("]", StringComparison.Ordinal));

    /// <summary>
    /// Group a segment's tokens into words, timed relative to the start of the decoded
    /// window. Returns an empty list when the segment carries no usable timings.
    /// </summary>
    public static IReadOnlyList<CaptionWord> FromTokens(SegmentData segment)
    {
        var tokens = segment.Tokens;
        if (tokens is null || tokens.Length == 0) return [];

        var words = new List<CaptionWord>();
        var text = "";
        var start = 0.0;
        var end = 0.0;

        foreach (var token in tokens)
        {
            var raw = token.Text;
            if (string.IsNullOrEmpty(raw)) continue;
            // Special/timestamp tokens are markup, not speech.
            if (IsMarkup(raw)) continue;

            var (tokenStart, tokenEnd) = TimesOf(token);
            var startsWord = raw[0] is ' ' or '\n' || text.Length == 0;

            if (startsWord && text.Trim().Length > 0)
            {
                Emit(words, text, start, end);
                text = "";
            }

            if (text.Length == 0) start = tokenStart;
            end = Math.Max(end, tokenEnd);
            text += raw;
        }

        if (text.Trim().Length > 0) Emit(words, text, start, end);
        return words;
    }

    private static void Emit(List<CaptionWord> words, string text, double start, double end)
    {
        var trimmed = Filters.Clean(text);
        if (trimmed.Length == 0) return;
        // A zero-length word would have an undefined midpoint, which is the one thing
        // RollingCaption's anchor search cannot tolerate.
        words.Add(new CaptionWord(trimmed, start, Math.Max(end, start)));
    }

    /// <summary>
    /// Prefer the DTW timestamp when the factory was built with alignment heads — it is the
    /// accurate one — and fall back to the token's own start/end otherwise.
    /// </summary>
    private static (double Start, double End) TimesOf(WhisperToken token)
    {
        var start = token.Start * CentisecondsToSeconds;
        var end = token.End * CentisecondsToSeconds;

        if (token.DtwTimestamp >= 0)
        {
            var dtw = token.DtwTimestamp * CentisecondsToSeconds;
            // DTW gives one aligned instant per token, not a span; pair it with the token's
            // own end so the word still has a width to take a midpoint from.
            start = dtw;
            end = Math.Max(dtw, end);
        }

        if (double.IsNaN(start) || start < 0) start = 0;
        if (double.IsNaN(end) || end < start) end = start;
        return (start, end);
    }
}
