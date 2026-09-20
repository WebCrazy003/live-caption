using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LocalCaption.Probe;

/// <summary>
/// What one loopback capture run observed, and the W9 / §4.3 verdict it supports.
/// </summary>
/// <remarks>
/// The measurement that matters is not "did audio arrive" but <b>did the sample clock keep
/// time</b>. <c>SpeechSegmenter.totalSamples</c> drives the elapsed clock, the endpoint
/// detector and every <c>t_start_ms</c> in the transcript, so a driver that delivers no
/// packets during silence does not merely lose silence — it makes every timestamp after the
/// first pause wrong, invisibly. <see cref="PaddingFrames"/> is the size of that error over
/// this run, and it is exactly what §4.3's mitigation 2 has to insert.
/// </remarks>
public sealed class CaptureResult
{
    private const double GapThresholdMs = 100;

    public int Packets { get; private set; }
    public long TotalFrames { get; private set; }
    public long PaddingFrames { get; private set; }
    public int Regressions { get; private set; }
    public int SilentFlags { get; private set; }
    public int Discontinuities { get; private set; }
    public int TimestampErrors { get; private set; }
    public int LongGaps { get; private set; }
    public double LongestGapMs { get; private set; }
    public double Peak { get; private set; }
    public double WallClockSeconds { get; set; }
    public string? Error { get; set; }

    private long? _firstPosition;
    private long _expectedPosition;

    public void Observe(int frames, AudioClientBufferFlags flags, long devicePosition, double msSinceLastPacket,
                        IntPtr buffer, int bytesPerFrame, WaveFormat mix)
    {
        Packets++;
        TotalFrames += frames;
        RecordGap(msSinceLastPacket);

        if (flags.HasFlag(AudioClientBufferFlags.Silent)) SilentFlags++;
        if (flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity)) Discontinuities++;
        if (flags.HasFlag(AudioClientBufferFlags.TimestampError)) TimestampErrors++;

        // §4.3 mitigation 2. Device position counts frames the engine has rendered, including
        // the ones it never handed us. The distance between where it says we are and where
        // our own frame count says we are is the zero-fill the capture layer owes the clock.
        if (_firstPosition is null)
        {
            _firstPosition = devicePosition;
            _expectedPosition = devicePosition;
        }
        else if (devicePosition > _expectedPosition)
        {
            PaddingFrames += devicePosition - _expectedPosition;
        }
        else if (devicePosition < _expectedPosition)
        {
            // Monotonicity is the W9 question a virtual driver is most likely to fail. If the
            // position ever goes backwards the padding arithmetic is unusable and the capture
            // layer must fall back to wall-clock timing.
            Regressions++;
        }

        _expectedPosition = Math.Max(devicePosition, _expectedPosition) + frames;

        // A silent buffer's contents are undefined — §4.3 says write zeros rather than copy —
        // so only non-silent buffers are worth measuring for level.
        if (!flags.HasFlag(AudioClientBufferFlags.Silent) && buffer != IntPtr.Zero && frames > 0)
            Peak = Math.Max(Peak, PeakOf(buffer, frames, bytesPerFrame, mix));
    }

    /// <summary>
    /// The encoding the buffer actually carries. An endpoint reports
    /// <see cref="WaveFormatEncoding.Extensible"/> — a WAVEFORMATEXTENSIBLE whose subtype is
    /// the real answer (§4.4). Reading <c>Encoding</c> directly sees only the wrapper, which
    /// matches no case and silently reports a level of zero.
    /// </summary>
    private static WaveFormat Unwrap(WaveFormat mix)
    {
        if (mix is not WaveFormatExtensible extensible) return mix;
        try { return extensible.ToStandardWaveFormat(); }
        catch (InvalidOperationException) { return mix; }
    }

    private void RecordGap(double milliseconds)
    {
        if (milliseconds <= GapThresholdMs) return;
        LongGaps++;
        LongestGapMs = Math.Max(LongestGapMs, milliseconds);
    }

    /// <summary>
    /// Close the run, accounting for the stretch after the last packet.
    /// </summary>
    /// <remarks>
    /// Without this, silence at the end of a run is invisible — and a run with <i>no</i>
    /// packets at all reports zero gaps, which reads as "the clock never stalled" when in
    /// fact it never started. That is the §4.3 failure in its purest form, so it is the one
    /// case the report must not get wrong.
    /// </remarks>
    public void Finish(double msSinceLastPacket) => RecordGap(msSinceLastPacket);

    private static double PeakOf(IntPtr buffer, int frames, int bytesPerFrame, WaveFormat format)
    {
        var mix = Unwrap(format);
        var samples = frames * mix.Channels;
        switch (mix.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat when mix.BitsPerSample == 32:
            {
                var data = new float[samples];
                Marshal.Copy(buffer, data, 0, samples);
                var peak = 0.0;
                foreach (var value in data) peak = Math.Max(peak, Math.Abs(value));
                return peak;
            }
            case WaveFormatEncoding.Pcm when mix.BitsPerSample == 16:
            {
                var bytes = new byte[frames * bytesPerFrame];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                var peak = 0.0;
                for (var i = 0; i + 1 < bytes.Length; i += 2)
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(bytes, i) / 32768.0));
                return peak;
            }
            // Extensible 24/32-bit PCM is realistic (§4.4) but the level readout is a
            // convenience, not a measurement — skip rather than guess at the packing.
            default:
                return 0;
        }
    }

    /// <summary>Whether this run rendered audio to the endpoint, which changes what silence means.</summary>
    public bool Played { get; set; }

    /// <summary>
    /// What this run says about §4.3 — and, when the gap swallowed the whole run, about the
    /// limit of the mitigation the spec calls a guarantee.
    /// </summary>
    private string SilenceVerdict(ProbeOptions options)
    {
        if (LongGaps == 0 && options.GapSeconds > 0)
            return $"  §4.3: {options.GapSeconds:0.#}s of digital silence inside an active render stream produced NO stall —\n" +
                   $"        the driver kept delivering zero-filled packets throughout ({SilentFlags} flagged SILENT,\n" +
                   $"        {PaddingFrames} frames of padding needed). What keeps loopback alive here is a stream\n" +
                   "        existing, not the samples being non-zero.";

        if (LongGaps == 0 && Peak > 0.0001)
            return "  §4.3: audio flowed the whole time and no packet was late. Says nothing about silence — re-run with --gap.";

        if (LongGaps == 0)
            return "  §4.3: packets kept arriving through digital silence on this driver — the clock did not stall.";

        var lines = new List<string>
        {
            $"  §4.3: the silence gap is REAL on this endpoint — {LongGaps} stall(s), worst {LongestGapMs:0} ms.",
        };

        // Mitigation 2 corrects a gap when the next packet arrives carrying its device
        // position. If no packet ever arrives — a silent endpoint, or silence that runs to
        // the end of the session — there is nothing to correct against, and the clock is
        // simply short. §4.3 calls padding "the correctness guarantee"; this is the edge it
        // does not cover, and B1 has to close it from the wall clock.
        if (Packets == 0)
            lines.Add("        Padding cannot rescue this run: no packet arrived, so no device position ever did either.");
        else if (PaddingFrames == 0)
            lines.Add("        Device position did NOT account for the gap — padding alone will not fix the clock here.");
        else
            lines.Add($"        Padding accounts for it: {PaddingFrames} frames of correction recover the missing time.");

        return string.Join('\n', lines);
    }

    public string Report(WaveFormat mix, ProbeOptions options)
    {
        var capturedSeconds = mix.SampleRate > 0 ? TotalFrames / (double)mix.SampleRate : 0;
        var paddedSeconds = mix.SampleRate > 0 ? (TotalFrames + PaddingFrames) / (double)mix.SampleRate : 0;
        var driftMs = (capturedSeconds - WallClockSeconds) * 1000;
        var correctedDriftMs = (paddedSeconds - WallClockSeconds) * 1000;

        var lines = new List<string>
        {
            "── results ─────────────────────────────────────────────────────────",
            $"  packets            {Packets}",
            $"  frames captured    {TotalFrames}  ({capturedSeconds:0.00}s of audio in {WallClockSeconds:0.00}s of wall clock)",
            $"  clock drift        {driftMs,+8:0} ms   ← the error §4.3 warns about, uncorrected",
            $"  padding implied    {PaddingFrames} frames ({PaddingFrames / (double)Math.Max(mix.SampleRate, 1):0.00}s)",
            $"  drift after pad    {correctedDriftMs,+8:0} ms   ← what mitigation 2 leaves behind",
            $"  position monotonic {(Regressions == 0 ? "yes" : $"NO — {Regressions} regressions")}",
            $"  longest gap        {LongestGapMs:0} ms ({LongGaps} over {GapThresholdMs:0} ms)",
            $"  flags              silent={SilentFlags} discontinuity={Discontinuities} timestamp-error={TimestampErrors}",
            $"  peak level         {Peak:0.000}{(Peak <= 0.0001 ? "   (nothing was playing, or nothing reached this endpoint)" : "")}",
        };

        if (Error is not null) lines.Add($"  capture error      {Error}");

        lines.Add("");
        lines.Add("── verdict ─────────────────────────────────────────────────────────");

        // Reaching this point at all means IAudioClient.Initialize returned S_OK with
        // AUDCLNT_STREAMFLAGS_LOOPBACK — the first of W9's four questions, and a separate
        // fact from whether any data followed.
        lines.Add("  W9 init:     Initialize(SHARED | LOOPBACK) succeeded.");
        lines.Add($"  W9 format:   {mix.SampleRate} Hz · {mix.Channels} ch · {mix.BitsPerSample}-bit — §4.4 handles this.");

        lines.Add(Packets switch
        {
            0 when Played => "  W9 data:     NO packets while audio was playing. Loopback is unusable on this endpoint — mode A is the mitigation.",
            0 => "  W9 data:     no packets — but nothing was rendering, so this is the §4.3 silence gap,\n" +
                 "               not a dead endpoint. Re-run with --play to exercise the endpoint itself.",
            _ => $"  W9 data:     {Packets} packets, {TotalFrames} frames.",
        });

        lines.Add(Packets < 2
            ? "  W9 position: not measurable — fewer than two packets."
            : Regressions == 0
                ? "  W9 position: u64DevicePosition advances monotonically — the §4.3 padding arithmetic is sound."
                : $"  W9 position: u64DevicePosition went BACKWARDS {Regressions}×. Padding cannot be trusted here; time from the wall clock instead.");

        lines.Add("");
        lines.Add(SilenceVerdict(options));

        if (!options.Keepalive && LongGaps > 0)
            lines.Add("  next: re-run with --keepalive to see whether the silent-render trick closes the gaps.");

        lines.Add("");
        lines.Add("  Mode A (process loopback, §4.5) is NOT exercised here — it needs");
        lines.Add("  ActivateAudioInterfaceAsync, which NAudio does not wrap. That is B1.");
        return string.Join('\n', lines);
    }
}
