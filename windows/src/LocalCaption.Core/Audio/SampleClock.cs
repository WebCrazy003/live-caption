namespace LocalCaption.Core.Audio;

/// <summary>
/// Keeps the capture clock true across gaps in the loopback stream.
/// </summary>
/// <remarks>
/// <para><b>SPEC-WINDOWS.md §4.3 — the one real trap in the audio layer.</b> The whole app
/// derives time from sample count: <c>SpeechSegmenter.TotalSamples</c> drives the elapsed
/// clock, the endpoint detector, every <c>t_start_ms</c> in the transcript and
/// <c>duration_seconds</c>. WASAPI loopback does not reliably deliver packets while nothing
/// is playing, so without a correction the clock stops during pauses and every timestamp
/// after the first silence is wrong — invisibly, until someone reads the transcript.</para>
/// <para>This class is pure arithmetic over three inputs (device position, frame count,
/// monotonic time) so that the logic which can silently corrupt a transcript is unit-tested
/// rather than only observed. It lives in <c>Core</c> for that reason, despite describing a
/// WASAPI behaviour: it has no dependency on WASAPI.</para>
/// <para><b>Two corrections, because B0 showed one is not enough</b>
/// (<c>windows/BENCH-RESULTS.md</c> §3):</para>
/// <list type="number">
///   <item><description><b>Device-position padding</b> — §4.3's mitigation 2. Each packet
///   reports <c>u64DevicePosition</c>; the distance from where we expected to be is audio
///   the engine rendered and never handed us, and it is inserted as zeros.</description></item>
///   <item><description><b>A wall-clock rule</b> — not in the spec, and required. Padding
///   only corrects a gap <i>when the next packet arrives carrying a position</i>. Measured
///   on the Jump Desktop Virtual Speaker with nothing rendering: <b>zero packets in twelve
///   seconds</b>, and therefore zero correction. <see cref="OnIdle"/> advances the clock
///   from elapsed time when the stream goes quiet, and <see cref="OnPacket"/> reconciles so
///   the same gap is never counted twice.</description></item>
/// </list>
/// <para>Neither replaces the silent-render keepalive (§4.3 mitigation 1), which B0 showed
/// is what keeps packets flowing at all on a virtual endpoint. This is the guarantee for
/// when the keepalive fails or the engine stalls anyway.</para>
/// </remarks>
public sealed class SampleClock(int sampleRate = 16000, double idleThresholdSeconds = 0.2)
{
    /// <summary>
    /// The rate everything here is counted in: <b>the device's</b>, not the app's 16 kHz.
    /// </summary>
    /// <remarks>
    /// Device positions and packet frame counts arrive at the endpoint's mix rate, so the
    /// wall-clock rule has to convert elapsed seconds at that same rate — and the endpoint
    /// decides it, which is why <see cref="Rebase"/> can change it. Getting this wrong is
    /// silent and proportional: at 48 kHz, a clock left at 16 kHz synthesises exactly a
    /// third of the silence a stall needs.
    /// </remarks>
    private int _sampleRate = sampleRate > 0 ? sampleRate : 16000;

    private long _expectedPosition;
    private long _synthesized;
    private double? _lastPacketAt;

    /// <summary>The session is running, so elapsed time owes samples.</summary>
    private bool _running;

    /// <summary>A packet has arrived, so the device's own numbering is known.</summary>
    private bool _hasOrigin;

    /// <summary>Frames inserted because a packet's device position ran ahead of us.</summary>
    public long PaddedFrames { get; private set; }

    /// <summary>Frames inserted because no packet arrived at all.</summary>
    public long SynthesizedFrames { get; private set; }

    /// <summary>
    /// How often <c>u64DevicePosition</c> moved backwards. Non-zero means the driver's
    /// position cannot be trusted and only the wall-clock rule is holding the clock up; B0
    /// saw none on either endpoint, but a virtual driver is where it would show up.
    /// </summary>
    public int Regressions { get; private set; }

    /// <summary>Total frames of correction this clock has inserted.</summary>
    public long InsertedFrames => PaddedFrames + SynthesizedFrames;

    /// <summary>Packets accounted for.</summary>
    public long Packets { get; private set; }

    /// <summary>
    /// Whether this stream reports a usable <c>u64DevicePosition</c> at all.
    /// </summary>
    /// <remarks>
    /// <b>Process loopback does not</b> — measured in B1 on Windows 11 26200: the virtual
    /// device returns <c>0</c> for every packet, so §4.3's mitigation 2 has nothing to work
    /// with. That matters because process loopback is the v1 <i>default</i> capture mode
    /// (§4.1), and §4.5 states the padding "stays in place regardless". It cannot. The
    /// wall-clock rule is the whole of the clock's protection there.
    /// <para>Detected rather than configured, because it is a property of the stream and the
    /// alternative is a flag that has to be right in two places.</para>
    /// </remarks>
    public bool PositionReported { get; private set; } = true;

    /// <summary>How many packets a position has to be absent from before we stop believing in it.</summary>
    private const int PositionPatience = 8;
    private int _positionless;

    /// <summary>
    /// Account for one captured packet, and report how many zero frames belong <i>before</i>
    /// it.
    /// </summary>
    /// <param name="devicePosition">The packet's <c>u64DevicePosition</c>, in frames.</param>
    /// <param name="frames">Frames in this packet.</param>
    /// <param name="now">Monotonic seconds, from <see cref="MonotonicClock"/>.</param>
    public int OnPacket(long devicePosition, int frames, double now)
    {
        Packets++;

        // A stream that keeps answering zero is not reporting a position, and treating each
        // packet as a backwards jump would bury the genuine signal under thousands of
        // false ones.
        if (PositionReported && devicePosition == 0 && ++_positionless >= PositionPatience)
        {
            PositionReported = false;

            // Whatever those first packets looked like, they were not regressions — there
            // was never a position to regress. Keeping the count would report a fault in
            // every mode-A session that ever runs.
            Regressions = 0;
        }

        if (!PositionReported)
        {
            _lastPacketAt = now;
            _synthesized = 0;
            return 0;
        }

        if (!_hasOrigin)
        {
            // The first packet defines the origin. Whatever the engine had rendered before
            // the session started is not ours to account for.
            //
            // But the wait for it is. Between opening a stream and its first packet — or,
            // after a device change, between the old endpoint's last packet and the new
            // one's first — real time passed with no audio in it, and the transcript's
            // timestamps are wrong by exactly that much if it is dropped. Measured on a
            // default-endpoint switch mid-capture: 331 ms, silently lost (§4.2).
            var owed = 0L;
            if (_running && _lastPacketAt is { } last)
            {
                owed = Math.Max(0, (long)((now - last) * _sampleRate) - _synthesized);
                SynthesizedFrames += owed;
            }

            _hasOrigin = true;
            _running = true;
            _expectedPosition = devicePosition + frames;
            _lastPacketAt = now;
            _synthesized = 0;
            return (int)owed;
        }

        var padding = 0L;

        if (devicePosition > _expectedPosition)
        {
            // The gap the device reports, minus whatever the wall-clock rule already
            // inserted for the same stretch of time. Whichever source saw more time pass
            // wins, and neither is allowed to count it twice.
            padding = Math.Max(0, devicePosition - _expectedPosition - _synthesized);
            PaddedFrames += padding;
        }
        else if (devicePosition < _expectedPosition)
        {
            Regressions++;
        }

        // Track the device's own numbering: synthesized frames are samples we invented, and
        // the engine's counter knows nothing about them.
        _expectedPosition = Math.Max(devicePosition, _expectedPosition) + frames;
        _synthesized = 0;
        _lastPacketAt = now;
        return (int)padding;
    }

    /// <summary>
    /// Called while waiting for packets. Reports how many zero frames to emit to keep the
    /// clock running through a stall.
    /// </summary>
    /// <remarks>
    /// Returns 0 until the stream has been quiet for longer than the idle threshold, so
    /// ordinary packet jitter does not manufacture silence. Once it does fire it emits the
    /// <i>whole</i> elapsed shortfall, so crossing the threshold late costs nothing —
    /// the time is recovered rather than lost.
    /// </remarks>
    public int OnIdle(double now)
    {
        if (!_running || _lastPacketAt is not { } last) return 0;

        var elapsed = now - last;
        if (elapsed < idleThresholdSeconds) return 0;

        var wanted = (long)(elapsed * _sampleRate);
        var emit = wanted - _synthesized;
        if (emit <= 0) return 0;

        _synthesized += emit;
        SynthesizedFrames += emit;
        return (int)emit;
    }

    /// <summary>
    /// Begin, or re-point at a new stream after a device change (§4.2).
    /// </summary>
    /// <remarks>
    /// <para>Only the <i>device's</i> numbering is discarded — the new endpoint counts from
    /// its own origin — while the session's elapsed clock carries on, so the time spent
    /// switching devices is still owed and <see cref="OnIdle"/> will fill it.</para>
    /// <para><b>Calling this is what starts the wall-clock rule</b>, and it has to happen at
    /// Start rather than on the first packet. B1 found this the hard way: with the keepalive
    /// deliberately disabled, an endpoint that is silent from the very beginning delivers no
    /// first packet, so a clock that waits for one never starts and the session captures
    /// nothing while believing itself idle. That is the measured B0 case, not a hypothetical.</para>
    /// </remarks>
    /// <param name="sampleRate">
    /// The new stream's rate, when it differs — a device change can move between 48 kHz and
    /// 44.1 kHz endpoints mid-session (§4.2, §4.4).
    /// </param>
    public void Rebase(double now, int? sampleRate = null)
    {
        if (sampleRate is > 0) _sampleRate = sampleRate.Value;
        _hasOrigin = false;
        _expectedPosition = 0;

        if (_running) return;      // mid-session device change: the clock keeps its history
        _running = true;
        _synthesized = 0;
        _lastPacketAt = now;
    }

    /// <summary>End the session. Idle time after this is nobody's audio.</summary>
    public void Halt() => _running = false;
}
