using LocalCaption.Core.Audio;

namespace LocalCaption.Core.Captions;

/// <summary>
/// Merges model word timings in <b>session coordinates</b>, never by the length of an old
/// prefix. The interim track decodes a sliding 6 s tail, so successive hypotheses start at
/// different points in the audio and share no common prefix — absolute word times are the
/// only thing that can line them up. The overlapping suffix is provisional and may be
/// corrected by a later decode.
/// </summary>
/// <remarks>
/// SPEC-WINDOWS.md §6 calls this "the subtlest code in the app — port literally, do not
/// improve". It is a literal port; <c>testdata/rolling</c> is what proves that.
/// </remarks>
public sealed class RollingCaption
{
    private const double AnchorWindowSeconds = 0.35;
    private const double SampleRate = 16000;

    private readonly List<CaptionWord> _words = [];
    private int _lastStartSample = -1;

    public IReadOnlyList<CaptionWord> Words => _words;
    public int LastEndSample { get; private set; } = -1;
    public string Text => string.Join(" ", _words.Select(w => w.Text));

    /// <summary>
    /// Fold one hypothesis in. Returns false when the hypothesis is rejected — it is stale
    /// (its window ends at or behind the high-water mark) or it contributes no usable word.
    /// A rejected hypothesis never clears text that is already on screen.
    /// </summary>
    public bool Update(IReadOnlyList<CaptionWord> incoming, int startSample, int endSample)
    {
        if (endSample <= LastEndSample || incoming.Count == 0) return false;

        var offset = startSample / SampleRate;
        var duration = (endSample - startSample) / SampleRate;

        var valid = incoming
            .Where(w => !string.IsNullOrWhiteSpace(w.Text)
                        && double.IsFinite(w.Start) && double.IsFinite(w.End)
                        && w.Start >= 0 && w.End >= w.Start && w.Start <= duration)
            .Select(w => new CaptionWord(w.Text.Trim(),
                                         offset + w.Start,
                                         offset + Math.Min(duration, w.End)))
            .ToList();
        if (valid.Count == 0) return false;

        if (_words.Count == 0 || startSample == _lastStartSample)
        {
            // Same audio origin: the model is re-decoding the same window, so permit
            // corrections — including a hypothesis shorter than the one it replaces.
            var prefix = _words.TakeWhile(w => w.End <= offset).ToList();
            _words.Clear();
            _words.AddRange(prefix);
            _words.AddRange(valid);
        }
        else
        {
            // Anchor on the first matching word within 350 ms. Time proximity is what
            // distinguishes a genuinely repeated phrase from duplicated window overlap.
            (int Old, int New)? anchor = null;
            for (var j = 0; j < valid.Count && anchor is null; j++)
            {
                var candidate = valid[j];
                if (candidate.Normalized.Length == 0) continue;

                var best = -1;
                var bestDistance = double.MaxValue;
                for (var i = 0; i < _words.Count; i++)
                {
                    if (_words[i].Normalized != candidate.Normalized) continue;
                    var distance = Math.Abs(_words[i].Midpoint - candidate.Midpoint);
                    if (distance > AnchorWindowSeconds) continue;
                    // Strictly-less keeps the earliest of equally close matches, which is
                    // what Swift's `min(by:)` does. Ties must not drift between platforms.
                    if (distance < bestDistance) { bestDistance = distance; best = i; }
                }
                if (best >= 0) anchor = (best, j);
            }

            if (anchor is { } found)
            {
                // Keep the newer model's prefix where it reaches back before the anchor,
                // preserving only the old words outside the range the new window covers.
                var prefixCount = found.New == 0
                    ? found.Old
                    : Math.Min(found.Old, _words.TakeWhile(w => w.Midpoint < valid[0].Start).Count());
                var kept = _words.Take(prefixCount).ToList();
                _words.Clear();
                _words.AddRange(kept);
                _words.AddRange(valid);
            }
            else
            {
                // No lexical anchor: the new window owns its time range. An explicit
                // provisional correction, not an invented textual overlap.
                var kept = _words.TakeWhile(w => w.Midpoint < valid[0].Start).ToList();
                _words.Clear();
                _words.AddRange(kept);
                _words.AddRange(valid);
            }
        }

        _lastStartSample = startSample;
        LastEndSample = endSample;
        return true;
    }
}
