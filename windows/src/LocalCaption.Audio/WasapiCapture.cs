using System.Runtime.InteropServices;
using LocalCaption.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LocalCaption.Audio;

/// <summary>
/// Everything the two capture modes share: the capture thread, the §4.3 sample clock, format
/// normalisation and fault classification.
/// </summary>
/// <remarks>
/// <para>Mode A and mode B (SPEC-WINDOWS.md §4.1) differ only in <i>how the
/// <see cref="AudioClient"/> is obtained</i> — process loopback activates a virtual device
/// asynchronously, endpoint loopback opens a real one — and in whether a silent-render
/// keepalive applies. Everything after that is identical, and it is the part that has to be
/// right: the clock, the resampler and the packet loop are where a transcript's timestamps
/// are won or lost.</para>
/// <para>The loop is deliberately <b>event-driven with a timeout</b> rather than either a
/// blocking wait or a polling sleep. §4.1 requires event-driven capture, but a loopback
/// stream's event is raised by the render engine — the very thing that stops during
/// silence — so a blocking wait would hang exactly when the clock most needs advancing.</para>
/// </remarks>
public abstract class WasapiCapture : IAudioCapture
{
    /// <summary>How long to wait on the render event before treating the stream as stalled.</summary>
    private const int WaitTimeoutMs = 100;

    /// <summary>What a mode produced when it opened its stream.</summary>
    /// <param name="Client">Initialised, but not started — the base class starts it.</param>
    protected sealed record OpenedStream(AudioClient Client, WaveFormat Format, string Source);

    private readonly SampleClock _clock = new();

    private AudioClient? _client;
    private AudioCaptureClient? _capture;
    private AudioNormalizer? _normalizer;
    private EventWaitHandle? _frameReady;
    private Thread? _thread;
    private CancellationTokenSource? _cancellation;
    private WaveFormat? _format;
    private volatile float _level;
    private long _emitted;
    private double? _streamOpenedAt;
    private byte[] _zeros = [];

    public string Source { get; private set; } = "not started";
    public string Format { get; private set; } = "not started";
    public float Level => _level;

    public event Action<float[]>? Samples;
    public event Action<CaptureFault>? Fault;

    /// <summary>16 kHz samples handed on so far, corrections included.</summary>
    public long EmittedSamples => Interlocked.Read(ref _emitted);

    /// <summary>
    /// How far the audio produced has drifted from elapsed time, in milliseconds. Positive
    /// means more audio than time.
    /// </summary>
    /// <remarks>
    /// Measured from the moment the stream opened, which is where the session's clock
    /// starts — not from the first sample, which on a silent source can lag by a whole idle
    /// threshold and would make a working clock look fast. §4.3's acceptance is ±100 ms, and
    /// this is the number that has to satisfy it.
    /// </remarks>
    public double DriftMs => _streamOpenedAt is { } opened
        ? (EmittedSamples / (double)AudioNormalizer.TargetSampleRate - (MonotonicClock.Now - opened)) * 1000
        : 0;

    /// <summary>What the §4.3 corrections have had to do this session.</summary>
    /// <remarks>
    /// Worth logging at Stop. A session that needed seconds of synthesised silence is one
    /// where the source stopped rendering, and that is the difference between a transcript
    /// whose timestamps are right and one whose are quietly wrong.
    /// </remarks>
    public virtual string ClockReport =>
        $"padded {_clock.PaddedFrames} · synthesised {_clock.SynthesizedFrames} · " +
        (_clock.PositionReported ? $"regressions {_clock.Regressions}" : "device position not reported");

    /// <summary>Open and initialise the client. Called on the caller's thread, from Start.</summary>
    protected abstract OpenedStream OpenStream();

    /// <summary>Release anything the mode opened alongside the client.</summary>
    /// <remarks>Called on every stop, including the one inside <see cref="Restart"/>.</remarks>
    protected virtual void CloseStream() { }

    /// <summary>Release what outlives a single stream. Called once, from Dispose.</summary>
    protected virtual void Released() { }

    public void Start()
    {
        if (_thread is not null) return;

        Open();
        _cancellation = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cancellation.Token))
        {
            IsBackground = true,
            Name = "LocalCaption capture",
            // Raised as well as MMCSS-registered: the priority survives if MMCSS is
            // unavailable, and the registration is what actually protects us under load.
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        Close();
    }

    /// <summary>
    /// End the session's clock, as opposed to just this stream.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Stop"/> because §4.2's restart stops a stream without ending
    /// the session — the seconds spent switching endpoints are still the session's, and the
    /// clock must go on owing them.
    /// </remarks>
    public void Finish()
    {
        Stop();
        _clock.Halt();
        _streamOpenedAt = null;
        Interlocked.Exchange(ref _emitted, 0);
    }

    /// <summary>
    /// Rebuild the stream, preserving the session and the sample clock (§4.2).
    /// </summary>
    public void Restart()
    {
        var running = _thread is not null;
        Stop();
        if (running) Start();
    }

    public void Dispose()
    {
        Stop();
        Released();
        _cancellation?.Dispose();
        _cancellation = null;
        GC.SuppressFinalize(this);
    }

    // ── stream lifetime ──────────────────────────────────────────────────────────────────

    private void Open()
    {
        var opened = OpenStream();
        _client = opened.Client;
        _format = opened.Format;
        Source = opened.Source;
        _normalizer = new AudioNormalizer(opened.Format);
        Format = _normalizer.Describe();

        _frameReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        _client.SetEventHandle(_frameReady.SafeWaitHandle.DangerousGetHandle());
        _capture = _client.AudioCaptureClient;

        // Start the wall-clock rule now, not on the first packet: a source that is silent
        // from the beginning never sends one (§4.3, measured in B0).
        var openedAt = MonotonicClock.Now;
        _streamOpenedAt ??= openedAt;       // the session's origin survives a restart (§4.2)
        _clock.Rebase(openedAt, opened.Format.SampleRate);
        _client.Start();
    }

    private void Close()
    {
        try { _client?.Stop(); } catch (Exception) { }
        try { _client?.Dispose(); } catch (Exception) { }
        _client = null;
        _capture = null;

        CloseStream();

        // Whatever the resampler still holds belongs to this session.
        if (_normalizer?.Flush() is { Length: > 0 } tail) Emit(tail, measure: false);
        _normalizer = null;

        _frameReady?.Dispose();
        _frameReady = null;
        _level = 0;
    }

    // ── the capture thread ───────────────────────────────────────────────────────────────

    private void Run(CancellationToken cancellationToken)
    {
        using var mmcss = MmcssThread.Join("Pro Audio");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var signalled = _frameReady?.WaitOne(WaitTimeoutMs) ?? false;
                if (cancellationToken.IsCancellationRequested) break;

                var drained = Drain();

                // Nothing arrived and the event did not fire. The stream is stalled, and
                // §4.3's padding cannot help: it needs a packet to carry a device position.
                // Measured on this machine: twelve seconds of nothing.
                if (!drained && !signalled) Synthesize();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Fault?.Invoke(Classify(e));
        }
    }

    /// <summary>Take every packet currently queued. True if any arrived.</summary>
    private bool Drain()
    {
        var capture = _capture;
        var normalizer = _normalizer;
        if (capture is null || normalizer is null || _format is null) return false;

        var any = false;
        while (capture.GetNextPacketSize() != 0)
        {
            var buffer = capture.GetBuffer(out var frames, out var flags, out var devicePosition, out _);
            try
            {
                if (frames <= 0) continue;
                any = true;

                var padding = _clock.OnPacket(devicePosition, frames, MonotonicClock.Now);
                if (padding > 0) Emit(Convert(normalizer, padding), measure: false);

                // §4.3: a SILENT buffer's contents are undefined. Write zeros, do not copy —
                // the frame count is still real and the clock still owes those samples.
                var samples = flags.HasFlag(AudioClientBufferFlags.Silent)
                    ? Convert(normalizer, frames)
                    : normalizer.Process(buffer, frames);

                Emit(samples, measure: true);
            }
            finally
            {
                capture.ReleaseBuffer(frames);
            }
        }

        return any;
    }

    /// <summary>Advance the clock from elapsed time while the stream is stalled.</summary>
    private void Synthesize()
    {
        var normalizer = _normalizer;
        if (normalizer is null) return;

        var frames = _clock.OnIdle(MonotonicClock.Now);
        if (frames <= 0) return;

        // Chunked, because a long stall is measured in seconds while the buffer feeding the
        // resampler holds five. Twelve seconds of silence is a real case, not a limit case.
        var rate = Math.Max(1, _format?.SampleRate ?? AudioNormalizer.TargetSampleRate);
        var chunk = rate / 2;
        while (frames > 0)
        {
            var take = Math.Min(chunk, frames);
            Emit(Convert(normalizer, take), measure: false);
            frames -= take;
        }
    }

    private void Emit(float[] samples, bool measure)
    {
        if (samples.Length == 0) return;
        Interlocked.Add(ref _emitted, samples.Length);
        if (measure) _level = Peak(samples);
        Samples?.Invoke(samples);
    }

    /// <summary>
    /// Push silence through the normaliser at the <i>source</i> rate.
    /// </summary>
    /// <remarks>
    /// The corrections go in at the source rate rather than being converted arithmetically
    /// to 16 kHz, so the resampler's timeline stays continuous and no rounding accumulates
    /// across a session's worth of gaps.
    /// </remarks>
    private float[] Convert(AudioNormalizer normalizer, int frames)
    {
        var bytes = frames * Math.Max(1, _format?.Channels ?? 1) * Math.Max(1, (_format?.BitsPerSample ?? 32) / 8);
        if (_zeros.Length < bytes) _zeros = new byte[bytes];
        else Array.Clear(_zeros, 0, bytes);
        return normalizer.Process(_zeros, bytes);
    }

    private static float Peak(float[] samples)
    {
        var peak = 0f;
        foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }

    /// <summary>Raise a fault from a derived mode, on its own terms.</summary>
    protected void Report(CaptureFault fault) => Fault?.Invoke(fault);

    private static CaptureFault Classify(Exception e)
    {
        // AUDCLNT_E_DEVICE_INVALIDATED — the endpoint went away, was reconfigured, or its
        // format changed. §4.2 says rebuild against the current default and retry; on this
        // machine that is the routine case, not the exceptional one.
        const int DeviceInvalidated = unchecked((int)0x88890004);
        const int ServiceNotRunning = unchecked((int)0x88890010);

        return e is COMException com && com.HResult is DeviceInvalidated or ServiceNotRunning
            ? new CaptureFault("The audio device changed. Reconnecting…", Recoverable: true, e)
            : new CaptureFault($"Audio capture stopped: {e.Message}", Recoverable: false, e);
    }
}

/// <summary>
/// Registers the calling thread with the Multimedia Class Scheduler.
/// </summary>
/// <remarks>
/// §4.1: the capture thread must not be descheduled under load, and "Pro Audio" is the task
/// name Windows reserves for this. Best-effort — MMCSS can be unavailable, and a capture
/// thread at high priority without it still beats not capturing.
/// </remarks>
internal sealed class MmcssThread : IDisposable
{
    [DllImport("avrt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    private IntPtr _handle;

    private MmcssThread(IntPtr handle) => _handle = handle;

    public static MmcssThread Join(string task)
    {
        try
        {
            uint index = 0;
            return new MmcssThread(AvSetMmThreadCharacteristicsW(task, ref index));
        }
        catch (Exception) { return new MmcssThread(IntPtr.Zero); }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        try { AvRevertMmThreadCharacteristics(_handle); } catch (Exception) { }
        _handle = IntPtr.Zero;
    }
}
