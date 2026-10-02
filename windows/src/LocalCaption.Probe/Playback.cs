using NAudio.Wave;

namespace LocalCaption.Probe;

/// <summary>
/// A tone with a silent gap in the middle, rendered to the endpoint being probed.
/// </summary>
/// <remarks>
/// <para>This is §4.3's acceptance test made self-contained: <i>"play a 60-second clip with a
/// 20-second silent gap in the middle; the resulting segment timestamps must match wall-clock
/// within ±100 ms."</i> A tone rather than a speech file because nothing here transcribes —
/// the experiment is entirely about whether the sample clock survives the gap, and a
/// generated signal needs no fixture, no resampler and no format negotiation.</para>
/// <para>The gap is the whole point. Audio either side of it proves the endpoint delivers
/// packets at all; the middle proves what it does when nothing is playing, which is the one
/// thing that cannot be inferred from a run with sound.</para>
/// </remarks>
public sealed class ToneWithGap(WaveFormat format, double totalSeconds, double gapSeconds) : IWaveProvider
{
    private const double Frequency = 440;
    private const double Amplitude = 0.1;   // about -20 dBFS: audible, never clipping

    private readonly double _gapStart = Math.Max(0, (totalSeconds - gapSeconds) / 2);
    private long _frame;

    public WaveFormat WaveFormat { get; } = format;

    /// <summary>When the silent stretch begins and ends, in seconds from Start.</summary>
    public (double Start, double End) Gap => (_gapStart, _gapStart + gapSeconds);

    public int Read(byte[] buffer, int offset, int count)
    {
        var channels = WaveFormat.Channels;
        var frames = count / (4 * channels);

        for (var i = 0; i < frames; i++)
        {
            var seconds = _frame / (double)WaveFormat.SampleRate;
            var silent = gapSeconds > 0 && seconds >= _gapStart && seconds < _gapStart + gapSeconds;

            // Digital silence, not a fade: zero samples are what a paused meeting app renders,
            // and it is zero samples that stop some drivers delivering loopback packets.
            var sample = silent ? 0f : (float)(Amplitude * Math.Sin(2 * Math.PI * Frequency * seconds));

            for (var channel = 0; channel < channels; channel++)
            {
                var index = offset + (i * channels + channel) * 4;
                BitConverter.TryWriteBytes(buffer.AsSpan(index, 4), sample);
            }
            _frame++;
        }

        return frames * channels * 4;
    }
}
