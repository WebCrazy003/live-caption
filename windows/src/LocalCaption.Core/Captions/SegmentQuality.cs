using System.IO.Compression;
using System.Text;

namespace LocalCaption.Core.Captions;

/// <summary>
/// The two quality signals whisper.cpp does not report, derived here so
/// <see cref="Filters.IsLowQuality"/> can apply the same thresholds as macOS.
/// </summary>
/// <remarks>
/// <para>SPEC-WINDOWS.md §5.6 calls this "a real porting gap". WhisperKit hands the macOS
/// build all three signals; whisper.cpp reports only <c>no_speech_prob</c>:</para>
/// <list type="bullet">
/// <item><c>avg_logprob</c> — not exposed, but per-token log probabilities are, so take
/// their mean.</item>
/// <item><c>compression_ratio</c> — not exposed at all, so compute it in-app with the same
/// definition OpenAI uses: <c>len(utf8) / len(gzip(utf8))</c>.</item>
/// </list>
/// <para>Both are unit-tested against fixtures because a drift here would silently change
/// what the gate rejects, and a filter that quietly stops filtering is invisible until
/// transcripts fill with repetition loops.</para>
/// </remarks>
public static class SegmentQuality
{
    /// <summary>
    /// Mean of the per-token log probabilities. Returns 0 for an empty sequence — a segment
    /// with no tokens carries no evidence of being bad, and the caller's other two signals
    /// still apply.
    /// </summary>
    public static double AverageLogprob(IEnumerable<double> tokenLogProbabilities)
    {
        var count = 0;
        var sum = 0.0;
        foreach (var value in tokenLogProbabilities)
        {
            // whisper.cpp reports 0 for tokens it did not score; NaN would poison the mean.
            if (double.IsNaN(value) || double.IsInfinity(value)) continue;
            sum += value;
            count++;
        }
        return count == 0 ? 0 : sum / count;
    }

    /// <summary>
    /// <c>len(utf8) / len(gzip(utf8))</c> — OpenAI's definition. A repetition loop such as
    /// "come come come…" compresses far better than speech, so a high ratio means the
    /// decoder is looping rather than transcribing.
    /// </summary>
    public static double CompressionRatio(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var raw = Encoding.UTF8.GetBytes(text);
        using var compressed = new MemoryStream();
        // The gzip header and trailer are a fixed ~20 bytes that Python's zlib also emits,
        // so the ratio stays comparable with the reference implementation.
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(raw, 0, raw.Length);

        var packed = compressed.Length;
        return packed == 0 ? 0 : (double)raw.Length / packed;
    }
}
