namespace LocalCaption.Core.Audio;

/// <summary>
/// Five-second handoff buffer between the audio callback and the CPU framing work.
/// </summary>
/// <remarks>
/// An overflow is <b>latched</b>, not absorbed: once full the buffer stops accepting audio
/// and keeps an exact count of what was lost, so the session can be stopped and the loss
/// reported rather than silently accumulating a growing backlog of stale speech.
/// </remarks>
public sealed class CaptureBuffer
{
    public readonly record struct Batch(
        IReadOnlyList<float> Samples, int DroppedSamples, int OldestAgeMs, int CallbackGapMs);

    private readonly Lock _lock = new();
    private readonly int _capacity;
    private List<float> _data = [];
    private int _dropped;
    private double? _oldestAt;
    private double? _lastAppendAt;
    private double _maxGap;
    private bool _overflowed;

    public CaptureBuffer(int capacity = 5 * 16000) => _capacity = Math.Max(1, capacity);

    /// <summary>Called from the audio callback. Must not block or allocate unboundedly.</summary>
    public void Append(IReadOnlyList<float> samples, double? now = null)
    {
        if (samples.Count == 0) return;
        var at = now ?? MonotonicClock.Now;

        lock (_lock)
        {
            if (_lastAppendAt is { } previous) _maxGap = Math.Max(_maxGap, at - previous);
            _lastAppendAt = at;

            if (_overflowed) { _dropped += samples.Count; return; }

            _oldestAt ??= at;
            var available = Math.Max(0, _capacity - _data.Count);
            var take = Math.Min(available, samples.Count);
            for (var i = 0; i < take; i++) _data.Add(samples[i]);
            if (samples.Count > available)
            {
                _overflowed = true;
                _dropped += samples.Count - available;
            }
        }
    }

    public Batch Drain(double? now = null)
    {
        var at = now ?? MonotonicClock.Now;
        lock (_lock)
        {
            var batch = new Batch(_data, _dropped,
                                  (int)(Math.Max(0, at - (_oldestAt ?? at)) * 1000),
                                  (int)(_maxGap * 1000));
            _data = [];
            _dropped = 0;
            _oldestAt = null;
            _maxGap = 0;
            return batch;
        }
    }
}

/// <summary>
/// Owns the framing and VAD work, keeping it off both the audio callback and the UI thread.
/// The Swift original is an <c>actor</c>; here a semaphore gives the same serial access.
/// </summary>
public sealed class CaptureProcessor(CaptureBuffer buffer, SpeechSegmenter segmenter)
{
    public readonly record struct Output(
        IReadOnlyList<SpeechRequest> Requests, int TotalSamples,
        CaptureBuffer.Batch Batch, IReadOnlyList<SpeechSegmenter.Transition> Transitions);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<Output> PollAsync(bool finish = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = MonotonicClock.Now;
            var batch = buffer.Drain(now);
            var requests = new List<SpeechRequest>(segmenter.Append(batch.Samples, now));
            if (finish) requests.AddRange(segmenter.Finish(now));
            return new Output(requests, segmenter.TotalSamples, batch, segmenter.DrainTransitions());
        }
        finally
        {
            _gate.Release();
        }
    }
}
