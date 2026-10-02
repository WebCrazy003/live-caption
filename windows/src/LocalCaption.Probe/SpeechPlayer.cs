using NAudio.Wave;

namespace LocalCaption.Probe;

/// <summary>
/// Plays a 16 kHz mono WAV to an endpoint at that endpoint's format, on repeat.
/// </summary>
/// <remarks>
/// For the end-to-end session check only: a 440 Hz tone proves the clock but transcribes to
/// nothing, and the question "does a session produce a transcript" needs real speech going
/// into a real loopback capture. Linear interpolation is plenty — the sample is already
/// 16 kHz and the capture layer will resample it straight back down.
/// </remarks>
public sealed class SpeechPlayer(WaveFormat format, float[] source) : IWaveProvider
{
    private readonly double _step = 16000.0 / format.SampleRate;
    private double _position;

    public WaveFormat WaveFormat { get; } = format;

    public int Read(byte[] buffer, int offset, int count)
    {
        var channels = WaveFormat.Channels;
        var frames = count / (4 * channels);

        for (var i = 0; i < frames; i++)
        {
            var sample = source.Length == 0 ? 0f : Interpolate();
            for (var channel = 0; channel < channels; channel++)
                BitConverter.TryWriteBytes(buffer.AsSpan(offset + (i * channels + channel) * 4, 4), sample);

            _position += _step;
            if (_position >= source.Length) _position -= source.Length;    // loop
        }

        return frames * channels * 4;
    }

    private float Interpolate()
    {
        var index = (int)_position;
        var next = (index + 1) % source.Length;
        var fraction = (float)(_position - index);
        return source[index] + (source[next] - source[index]) * fraction;
    }
}
