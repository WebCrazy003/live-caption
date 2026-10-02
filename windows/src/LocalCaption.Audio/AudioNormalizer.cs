using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LocalCaption.Audio;

/// <summary>
/// Turns whatever an endpoint reports into the one format the rest of the app accepts:
/// <b>float32, 16 kHz, mono</b>.
/// </summary>
/// <remarks>
/// <para><b>SPEC-WINDOWS.md §4.4.</b> The mix format is the endpoint's choice, not ours.
/// B0 measured 48 kHz / 2 ch / 32-bit float on both endpoints of the G15, but 44.1 kHz and
/// six channels are both realistic on a laptop with an HDMI monitor or a Realtek surround
/// driver, and process loopback (§4.5) has no mix format at all — the format is whatever we
/// asked for.</para>
/// <para>Two details the spec calls out and this honours:</para>
/// <list type="bullet">
///   <item><description><b>Downmix by averaging, not summing.</b> Summing N channels of
///   correlated content clips.</description></item>
///   <item><description><b>Resample, never decimate.</b> 44.1 kHz → 16 kHz is a
///   non-integer ratio, and dropping samples aliases — audible as a noise floor the ASR
///   pays for in WER. <see cref="WdlResamplingSampleProvider"/> is named in §4.4 for this
///   reason.</description></item>
/// </list>
/// <para>The output contract is byte-identical to the macOS capture layer's, which is what
/// lets §6's logic port stand unchanged.</para>
/// </remarks>
public sealed class AudioNormalizer
{
    public const int TargetSampleRate = 16000;

    private readonly WaveFormat _source;
    private readonly int _channels;
    private readonly int _bytesPerSample;
    private readonly BufferedWaveProvider _mono;
    private readonly ISampleProvider _resampled;
    private readonly bool _passthrough;

    private byte[] _scratch = [];
    private float[] _out = [];

    // Exact rate accounting, in whole samples at each end.
    private long _framesIn;
    private long _samplesOut;

    public AudioNormalizer(WaveFormat source)
    {
        _source = Unwrap(source);
        _channels = Math.Max(1, _source.Channels);
        _bytesPerSample = Math.Max(1, _source.BitsPerSample / 8);

        // A source already at 16 kHz mono float skips both stages — process loopback can
        // ask for exactly that, and there is no reason to run it through a resampler.
        _passthrough = _source.SampleRate == TargetSampleRate && _channels == 1 &&
                       _source.Encoding == WaveFormatEncoding.IeeeFloat && _source.BitsPerSample == 32;

        var monoFormat = WaveFormat.CreateIeeeFloatWaveFormat(_source.SampleRate, 1);
        _mono = new BufferedWaveProvider(monoFormat)
        {
            // Without this the provider invents silence to satisfy a read, which would put
            // samples into the stream that no device ever produced — precisely the kind of
            // fabricated audio the §4.3 clock exists to account for honestly.
            ReadFully = false,
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = false,
        };

        _resampled = _source.SampleRate == TargetSampleRate
            ? _mono.ToSampleProvider()
            : new WdlResamplingSampleProvider(_mono.ToSampleProvider(), TargetSampleRate);
    }

    /// <summary>A one-line description for the session log and the Settings readout.</summary>
    public string Describe() =>
        $"{_source.SampleRate} Hz · {_channels} ch · {_source.BitsPerSample}-bit {_source.Encoding} → 16 kHz mono" +
        (_passthrough ? " (passthrough)" : "");

    /// <summary>Convert one WASAPI packet, read straight from the capture buffer.</summary>
    public float[] Process(IntPtr buffer, int frames)
    {
        if (buffer == IntPtr.Zero || frames <= 0) return [];
        var bytes = frames * _channels * _bytesPerSample;
        if (_scratch.Length < bytes) _scratch = new byte[bytes];
        Marshal.Copy(buffer, _scratch, 0, bytes);
        return Process(_scratch, bytes);
    }

    /// <summary>
    /// Convert <paramref name="count"/> bytes of source-format audio.
    /// </summary>
    /// <remarks>
    /// Kept separate from the pointer overload so the conversion can be tested without a
    /// device — this is the half of the audio layer that <i>can</i> be unit-tested, and the
    /// half a silent bug would hide in.
    /// </remarks>
    public float[] Process(byte[] bytes, int count)
    {
        if (count <= 0) return [];

        var frames = count / (_channels * _bytesPerSample);
        if (frames == 0) return [];

        var mono = Downmix(bytes, frames);
        if (_passthrough) return mono;

        _framesIn += frames;
        _mono.AddSamples(MemoryMarshal.AsBytes<float>(mono).ToArray(), 0, mono.Length * 4);
        return Drain(Due);
    }

    /// <summary>
    /// Zero frames, for the §4.3 clock corrections. They enter at the <i>target</i> rate:
    /// the correction is expressed in output samples, and pushing silence through the
    /// resampler would only add its latency to a value that is already exact.
    /// </summary>
    public static float[] Silence(int frames) => frames > 0 ? new float[frames] : [];

    /// <summary>Whatever the resampler still holds, at the end of a session.</summary>
    public float[] Flush() => _passthrough ? [] : Drain(Due);

    /// <summary>
    /// How many output samples the audio fed in so far entitles us to, and no more.
    /// </summary>
    /// <remarks>
    /// <para><b>This is what keeps a long session's clock true.</b> The resampler is asked
    /// for a bounded number of samples rather than "whatever you have", because otherwise a
    /// packet size that does not divide evenly by the ratio rounds up every single time and
    /// the error compounds.</para>
    /// <para>Measured: this endpoint delivers <b>512-frame</b> packets, and 512 ÷ 3 is
    /// 170.667. A third of a sample per packet is 0.195% — 2.3 seconds across a 20-minute
    /// soak, ten across a 90-minute interview, and every timestamp after it wrong by that
    /// much. It hid for a long time because the unit tests used 480-frame packets, which
    /// divide exactly.</para>
    /// </remarks>
    private long Due => _framesIn * TargetSampleRate / Math.Max(1, _source.SampleRate) - _samplesOut;

    private float[] Drain(long due)
    {
        if (due <= 0) return [];

        var capacity = (int)Math.Min(due, (long)(_mono.BufferedDuration.TotalSeconds * TargetSampleRate) + 64);
        if (capacity <= 0) return [];
        if (_out.Length < capacity) _out = new float[capacity];

        var read = _resampled.Read(_out, 0, capacity);
        if (read <= 0) return [];

        _samplesOut += read;
        return _out[..read];
    }

    /// <summary>
    /// N channels to one, by averaging, converting whatever integer or float packing the
    /// endpoint uses on the way.
    /// </summary>
    private float[] Downmix(byte[] bytes, int frames)
    {
        var mono = new float[frames];
        var span = bytes.AsSpan();

        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0.0;
            for (var channel = 0; channel < _channels; channel++)
                sum += Sample(span, (frame * _channels + channel) * _bytesPerSample);
            mono[frame] = (float)(sum / _channels);
        }

        return mono;
    }

    private double Sample(ReadOnlySpan<byte> bytes, int offset) => _source.Encoding switch
    {
        WaveFormatEncoding.IeeeFloat => BitConverter.ToSingle(bytes[offset..]),
        _ => _source.BitsPerSample switch
        {
            16 => BitConverter.ToInt16(bytes[offset..]) / 32768.0,
            // 24-bit is packed three bytes little-endian, sign carried by the top byte.
            24 => ((bytes[offset] | (bytes[offset + 1] << 8) | ((sbyte)bytes[offset + 2] << 16))) / 8388608.0,
            32 => BitConverter.ToInt32(bytes[offset..]) / 2147483648.0,
            8 => (bytes[offset] - 128) / 128.0,
            _ => 0,
        },
    };

    /// <summary>
    /// The subtype behind a WAVEFORMATEXTENSIBLE. Endpoints report
    /// <see cref="WaveFormatEncoding.Extensible"/> — B0 saw it on both of the G15's — and
    /// reading <c>Encoding</c> without unwrapping it matches no case, which would silently
    /// convert every sample to zero.
    /// </summary>
    private static WaveFormat Unwrap(WaveFormat format)
    {
        if (format is not WaveFormatExtensible extensible) return format;
        try { return extensible.ToStandardWaveFormat(); }
        catch (InvalidOperationException) { return format; }
    }
}
