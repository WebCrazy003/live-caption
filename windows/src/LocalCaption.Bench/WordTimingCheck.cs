using System.Globalization;
using LocalCaption.Asr;
using LocalCaption.Core.Audio;

namespace LocalCaption.Bench;

/// <summary>
/// W2: are whisper.cpp's DTW word timings stable enough for <c>RollingCaption</c>?
/// </summary>
/// <remarks>
/// <para><b>The question is stability, not accuracy.</b> <c>RollingCaption</c> merges each new
/// interim hypothesis into the last one by finding a word that matches lexically <i>and</i>
/// lands within <b>350 ms</b> of its previous position (<c>AnchorWindowSeconds</c>). It has no
/// other way to align them: every interim decode is a different 6-second tail with no shared
/// prefix. If the same spoken word drifts by more than 350 ms between overlapping windows,
/// the anchor is missed and the caption does not degrade gracefully — it scrambles.</para>
/// <para>So this decodes overlapping windows exactly as the interim lane does, converts each
/// word's midpoint into session time, and measures how far the same word moves from one
/// window to the next. Ground truth is not needed and not available; what matters is whether
/// the timings agree with themselves.</para>
/// </remarks>
internal static class WordTimingCheck
{
    /// <summary>RollingCaption's anchor window — the number this whole check is about.</summary>
    private const double AnchorSeconds = 0.35;

    private const int SampleRate = 16000;

    public static async Task<int> RunAsync(float[] audio, BenchOptions options)
    {
        var duration = audio.Length / (double)SampleRate;
        var window = Math.Min(6.0, duration);
        var hop = 0.5;                                    // asr.interim_interval_ms

        Console.WriteLine("── W2: DTW word-timing stability ───────────────────────────────────");
        Console.WriteLine($"  audio      {duration:0.00}s");
        Console.WriteLine($"  windows    {window:0.#}s tail every {hop * 1000:0} ms, as the interim lane decodes");
        Console.WriteLine($"  anchor     RollingCaption matches within {AnchorSeconds * 1000:0} ms");
        Console.WriteLine();

        var spec = ModelCatalog.Resolve(options.Models.Count > 0 ? options.Models[0] : "tiny.en");
        await using var engine = new WhisperEngine(spec.Name, spec.Name, options.Backends[0], options.Threads);
        await engine.PrepareAsync(options.ModelsDirectory);

        // Each window is the last `window` seconds of audio available at that moment, which is
        // what SpeechSegmenter hands the interim lane.
        var starts = new List<double>();
        for (var end = window; end <= duration + 1e-9; end += hop) starts.Add(end - window);
        if (starts.Count < 2)
        {
            Console.WriteLine("  audio is too short to overlap two windows.");
            return 1;
        }

        var perWindow = new List<(double Start, IReadOnlyList<CaptionWord> Words)>();
        foreach (var start in starts)
        {
            var from = (int)(start * SampleRate);
            var count = Math.Min((int)(window * SampleRate), audio.Length - from);
            var outcome = await engine.TranscribeInterimAsync(audio[from..(from + count)]);
            var words = outcome is SpeechOutcome.Success success ? success.Words : [];
            perWindow.Add((start, words));
        }

        var produced = perWindow.Count(w => w.Words.Count > 0);
        Console.WriteLine($"  decoded    {perWindow.Count} windows, {produced} produced word timings");
        if (produced < 2)
        {
            Console.WriteLine("  FAIL — not enough windows carried word timings to compare. See §5.7.1.");
            return 1;
        }

        // Compare each window against the previous one, matching words by normalised text in
        // the overlapping region and measuring how far the midpoint moved.
        var deltas = new List<double>();
        var unmatched = 0;
        var pairs = 0;
        var anchored = 0;
        for (var i = 1; i < perWindow.Count; i++)
        {
            var (previousStart, previous) = perWindow[i - 1];
            var (currentStart, current) = perWindow[i];
            if (previous.Count == 0 || current.Count == 0) continue;

            pairs++;
            var anchorFound = false;

            foreach (var word in current)
            {
                var mid = currentStart + word.Midpoint;
                // CaptionWord is a value type, so "no match" is an empty sequence rather
                // than a null.
                var candidates = previous
                    .Where(p => p.Normalized == word.Normalized)
                    .Select(p => Math.Abs(previousStart + p.Midpoint - mid))
                    .ToList();

                if (candidates.Count == 0) continue;

                var delta = candidates.Min();
                // Beyond a second the two are almost certainly different utterances of the
                // same word, not the same one having moved.
                if (delta > 1.0) { unmatched++; continue; }
                deltas.Add(delta);
                if (delta <= AnchorSeconds) anchorFound = true;
            }

            if (anchorFound) anchored++;
        }

        if (deltas.Count == 0)
        {
            Console.WriteLine("  FAIL — no word appeared in two consecutive windows; nothing to anchor on.");
            return 1;
        }

        deltas.Sort();
        var p50 = Percentile(deltas, 50);
        var p90 = Percentile(deltas, 90);
        var worst = deltas[^1];
        var within = deltas.Count(d => d <= AnchorSeconds) / (double)deltas.Count;

        Console.WriteLine($"  comparisons {deltas.Count} matched word pairs ({unmatched} rejected as different utterances)");
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  drift      p50 {0,6:0} ms · p90 {1,6:0} ms · worst {2,6:0} ms", p50 * 1000, p90 * 1000, worst * 1000));
        Console.WriteLine($"  per word   {within:P1} of pairs land within {AnchorSeconds * 1000:0} ms");
        Console.WriteLine($"  per merge  {anchored}/{pairs} window pairs offered at least one anchor");
        Console.WriteLine();

        // The operative criterion. RollingCaption anchors on the *first* word that matches
        // lexically and within 350 ms — it does not need every word to agree, and one that
        // drifts is simply not chosen. So what matters is whether each merge finds an anchor
        // at all; the per-word spread is context for how much margin there is.
        var ok = pairs > 0 && anchored == pairs;
        Console.WriteLine(ok
            ? $"  PASS — every overlapping window pair offered a word within {AnchorSeconds * 1000:0} ms,\n" +
              "         which is what RollingCaption anchors on. W2's default holds: keep the\n" +
              "         word-timed merge."
            : $"  FAIL — {pairs - anchored} of {pairs} merges had no word within {AnchorSeconds * 1000:0} ms.\n" +
              "         §5.7.1's fallback applies: fixed-origin windows + LocalAgreement.");
        return ok ? 0 : 1;
    }

    private static double Percentile(List<double> sorted, int percentile)
    {
        if (sorted.Count == 0) return 0;
        var rank = percentile / 100.0 * (sorted.Count - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        return low == high ? sorted[low] : sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }
}
