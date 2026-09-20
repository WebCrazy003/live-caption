using System.Text.RegularExpressions;

namespace LocalCaption.Core.Captions;

/// <summary>Text cleanup and hallucination suppression for ASR output.</summary>
public static partial class Filters
{
    [GeneratedRegex(@"<\|[^|]*\|>")] private static partial Regex SpecialTokens();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    /// <summary>
    /// Strip Whisper special/timestamp tokens (<c>&lt;|…|&gt;</c>) and <c>[BLANK_AUDIO]</c>,
    /// then collapse whitespace. Runs on every decode before anything else sees it.
    /// </summary>
    public static string Clean(string s)
    {
        var t = SpecialTokens().Replace(s, "");
        t = t.Replace("[BLANK_AUDIO]", "");
        t = Whitespace().Replace(t, " ");
        return t.Trim();
    }

    private static readonly HashSet<string> Blocklist = new(StringComparer.Ordinal)
    {
        "you", "thank you", "thanks", "thanks for watching", "bye", "bye bye",
        "so", "the", "uh", "um", "you know", "thank you so much", "okay", "mm", "hmm", "yeah",
    };

    /// <summary>
    /// Filter Whisper's silence hallucinations ("you you you", "Thank you.", …). Matching is
    /// on a stem that is lowercased, space-trimmed, then stripped of leading/trailing
    /// <c>.!?,-</c>, so "Thank you." and "thank you" are the same stem.
    /// </summary>
    public static bool IsHallucination(string text)
    {
        // Swift trims CharacterSet.whitespaces here — spaces and tabs, NOT newlines.
        var stem = text.ToLowerInvariant().Trim(' ', '\t').Trim('.', '!', '?', ',', '-');
        if (stem.Length == 0) return true;
        if (Blocklist.Contains(stem)) return true;

        var words = stem.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 3)
        {
            var unique = new HashSet<string>(words, StringComparer.Ordinal).Count;
            if (unique == 1) return true;                             // "you you you"
            if ((double)unique / words.Length < 0.34) return true;    // heavy repetition
        }
        return false;
    }

    /// <summary>
    /// Metadata junk/hallucination gate (SPEC.md §8.3). On Windows two of the three inputs
    /// are derived rather than reported — see SPEC-WINDOWS.md §5.6 — but the thresholds are
    /// identical to macOS and must not drift.
    /// </summary>
    public static bool IsLowQuality(double avgLogprob, double noSpeechProb, double compressionRatio)
    {
        if (noSpeechProb > 0.6) return true;        // silence mistaken for speech
        if (avgLogprob < -1.0) return true;         // low-confidence garble
        if (compressionRatio > 2.4) return true;    // repetition loop
        return false;
    }

    public static int WordCount(string s) => s.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

    public static int SentenceCount(string s) => s.Count(c => c is '.' or '!' or '?');
}
