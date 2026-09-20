using System.Diagnostics;
using System.Globalization;

namespace LocalCaption.Core.Audio;

/// <summary>
/// Monotonic seconds, the C# equivalent of <c>ProcessInfo.processInfo.systemUptime</c>.
/// Wall-clock time must never be used for scheduling: the ASUS resyncs its clock over the
/// network and a backward jump would make decode budgets and backlog maths nonsense.
/// </summary>
public static class MonotonicClock
{
    public static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
}

/// <summary>One word with its timing in session coordinates (seconds).</summary>
public readonly record struct CaptionWord(string Text, double Start, double End)
{
    public double Midpoint => (Start + End) / 2;

    /// <summary>
    /// Lowercase, whitespace-trimmed, then punctuation-trimmed — in that order, so
    /// <c>"hello, "</c> and <c>"Hello"</c> compare equal. Used only to anchor overlapping
    /// hypotheses against each other, never for display.
    /// </summary>
    public string Normalized
    {
        get
        {
            var t = Text.ToLowerInvariant().Trim();
            var start = 0;
            var end = t.Length;
            while (start < end && char.IsPunctuation(t[start])) start++;
            while (end > start && char.IsPunctuation(t[end - 1])) end--;
            return t[start..end];
        }
    }
}

/// <summary>One decode request: a window of audio plus where it sits in the session.</summary>
public sealed class SpeechRequest
{
    public Guid Session { get; }
    public int Utterance { get; }
    public int StartSample { get; }
    public int EndSample { get; }
    public IReadOnlyList<float> Audio { get; }
    public bool IsFinal { get; }
    public double SubmittedAt { get; }

    public SpeechRequest(Guid session, int utterance, int startSample, IReadOnlyList<float> audio,
                         bool isFinal, double? submittedAt = null)
    {
        Session = session;
        Utterance = utterance;
        StartSample = startSample;
        EndSample = startSample + audio.Count;
        Audio = audio;
        IsFinal = isFinal;
        SubmittedAt = submittedAt ?? MonotonicClock.Now;
    }
}

/// <summary>What a decode produced. <see cref="Label"/> is what lands in the metrics log.</summary>
public abstract record SpeechOutcome
{
    private SpeechOutcome() { }

    public sealed record Success(string Text, IReadOnlyList<CaptionWord> Words, int Fallbacks) : SpeechOutcome;
    public sealed record Empty : SpeechOutcome;
    public sealed record Filtered : SpeechOutcome;
    public sealed record Cancelled : SpeechOutcome;
    public sealed record TimedOut : SpeechOutcome;
    public sealed record Failure(string Message) : SpeechOutcome;

    public string Label => this switch
    {
        Success => "success",
        Empty => "empty",
        Filtered => "filtered",
        Cancelled => "cancelled",
        TimedOut => "timeout",
        Failure => "failure",
        _ => "unknown",
    };
}

/// <summary>
/// Pure frame/VAD logic — no inference, no UI, no audio device. Retains 200 ms of pre-roll
/// during silence so an utterance is never clipped at its onset, but never buffers a whole
/// silent stretch: five minutes of silence must enqueue no work at all.
/// </summary>
public sealed class SpeechSegmenter
{
    public readonly record struct Transition(int Utterance, int Sample, bool Started);

    public readonly record struct Tuning
    {
        public int EndpointMs { get; }
        public int IntervalMs { get; }
        public int MaxUtteranceS { get; }
        public float Threshold { get; }

        public Tuning(int endpointMs = 600, int intervalMs = 500,
                      int maxUtteranceS = 20, float threshold = 0.015f)
        {
            EndpointMs = Math.Max(100, endpointMs);
            IntervalMs = Math.Max(100, intervalMs);
            MaxUtteranceS = Math.Min(60, Math.Max(5, maxUtteranceS));
            Threshold = threshold;
        }
    }

    private const int SampleRate = 16000;
    private const int FrameSamples = 1600;      // 100 ms
    private const int PreRollSamples = 3200;    // 200 ms
    private const int InterimTailSamples = 96000; // 6 s

    public Guid Session { get; }
    public Tuning Settings { get; }
    public int TotalSamples { get; private set; }

    private int _nextUtterance;
    private readonly List<float> _remainder = [];
    private readonly List<float> _preRoll = [];
    private readonly List<float> _utterance = [];
    private int _startSample;
    private int _silenceSamples;
    private int _sinceInterim;
    private readonly List<Transition> _transitions = [];

    public SpeechSegmenter(Guid session, Tuning? tuning = null, int startSample = 0)
    {
        Session = session;
        Settings = tuning ?? new Tuning();
        TotalSamples = startSample;
    }

    public IReadOnlyList<SpeechRequest> Append(IReadOnlyList<float> samples, double now)
    {
        _remainder.AddRange(samples);
        var requests = new List<SpeechRequest>();
        var offset = 0;
        while (_remainder.Count - offset >= FrameSamples)
        {
            Consume(_remainder.GetRange(offset, FrameSamples), now, requests);
            offset += FrameSamples;
        }
        _remainder.RemoveRange(0, offset);
        return requests;
    }

    /// <summary>
    /// Capture must be stopped before this is called. Includes the last sub-100 ms frame, so
    /// no speech sample is ever dropped at Stop.
    /// </summary>
    public IReadOnlyList<SpeechRequest> Finish(double now)
    {
        var requests = new List<SpeechRequest>();
        if (_remainder.Count > 0)
        {
            var tail = new List<float>(_remainder);
            _remainder.Clear();
            Consume(tail, now, requests);
        }
        if (_utterance.Count > 0) Finalize(now, requests);
        _preRoll.Clear();
        return requests;
    }

    public IReadOnlyList<Transition> DrainTransitions()
    {
        var events = _transitions.ToArray();
        _transitions.Clear();
        return events;
    }

    private void Consume(List<float> frame, double now, List<SpeechRequest> requests)
    {
        var speaking = Rms(frame) >= Settings.Threshold;
        var frameStart = TotalSamples;
        TotalSamples += frame.Count;

        if (_utterance.Count == 0)
        {
            if (!speaking)
            {
                _preRoll.AddRange(frame);
                if (_preRoll.Count > PreRollSamples) _preRoll.RemoveRange(0, _preRoll.Count - PreRollSamples);
                return;
            }
            _startSample = frameStart - _preRoll.Count;
            _utterance.AddRange(_preRoll);
            _preRoll.Clear();
            _transitions.Add(new Transition(_nextUtterance, frameStart, Started: true));
        }

        _utterance.AddRange(frame);
        _silenceSamples = speaking ? 0 : _silenceSamples + frame.Count;
        _sinceInterim += frame.Count;

        if (speaking && _sinceInterim >= Settings.IntervalMs * 16)
        {
            _sinceInterim = 0;
            var take = Math.Min(InterimTailSamples, _utterance.Count);
            var tail = _utterance.GetRange(_utterance.Count - take, take);
            requests.Add(new SpeechRequest(Session, _nextUtterance, TotalSamples - tail.Count,
                                           tail, isFinal: false, submittedAt: now));
        }

        if (_silenceSamples >= Settings.EndpointMs * 16 ||
            _utterance.Count >= Settings.MaxUtteranceS * SampleRate)
        {
            Finalize(now, requests);
        }
    }

    private void Finalize(double now, List<SpeechRequest> requests)
    {
        _transitions.Add(new Transition(_nextUtterance, TotalSamples, Started: false));
        requests.Add(new SpeechRequest(Session, _nextUtterance, _startSample,
                                       _utterance.ToArray(), isFinal: true, submittedAt: now));
        _nextUtterance++;
        _utterance.Clear();
        _silenceSamples = 0;
        _sinceInterim = 0;
    }

    public static float Rms(IReadOnlyList<float> samples)
    {
        if (samples.Count == 0) return 0;
        float sum = 0;
        for (var i = 0; i < samples.Count; i++) sum += samples[i] * samples[i];
        return MathF.Sqrt(sum / samples.Count);
    }
}
