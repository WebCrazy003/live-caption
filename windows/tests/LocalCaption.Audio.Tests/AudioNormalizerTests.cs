using System.Runtime.InteropServices;
using LocalCaption.Audio;
using NAudio.Wave;

namespace LocalCaption.Audio.Tests;

/// <summary>
/// §4.4 format normalisation. Pure conversion — no device, so these run anywhere.
/// </summary>
/// <remarks>
/// The formats asserted here are the ones this machine and its peripherals actually
/// produce: B0 measured 48 kHz / 2 ch / float on both of the G15's endpoints, and §4.4
/// names 44.1 kHz and six channels as the realistic awkward cases.
/// </remarks>
public sealed class AudioNormalizerTests
{
    private static byte[] FloatBytes(params float[] samples) => MemoryMarshal.AsBytes<float>(samples).ToArray();

    private static float[] Run(WaveFormat format, byte[] bytes)
    {
        var normalizer = new AudioNormalizer(format);
        var first = normalizer.Process(bytes, bytes.Length);
        return [.. first, .. normalizer.Flush()];
    }

    /// <summary><paramref name="frames"/> interleaved frames of a constant value.</summary>
    private static byte[] Constant(int frames, int channels, float value)
    {
        var samples = new float[frames * channels];
        Array.Fill(samples, value);
        return FloatBytes(samples);
    }

    [Fact]
    public void StereoFloat48kBecomesMono16k()
    {
        var output = Run(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), Constant(48000, 2, 0.25f));

        // One second in, one second out — the rate changed, the duration did not.
        Assert.InRange(output.Length, 16000 - 64, 16000 + 64);
        Assert.All(output[100..^100], sample => Assert.Equal(0.25f, sample, 0.01f));
    }

    [Fact]
    public void ChannelsAreAveragedNotSummed()
    {
        // §4.4 is explicit about this: two channels of correlated content at 0.8 sum to 1.6
        // and clip. The averaged result is what the ASR should see.
        var frames = new float[2 * 16000];
        for (var i = 0; i < frames.Length; i++) frames[i] = 0.8f;

        var output = Run(WaveFormat.CreateIeeeFloatWaveFormat(16000, 2), FloatBytes(frames));

        Assert.NotEmpty(output);
        Assert.All(output, sample => Assert.True(sample <= 1.0f, $"clipped: {sample}"));
        Assert.Equal(0.8f, output[0], 0.001f);
    }

    [Fact]
    public void SixChannelsDownmixToOne()
    {
        // A Realtek surround driver is enough to produce this.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 6);
        var frames = new float[6 * 4800];
        for (var frame = 0; frame < 4800; frame++)
            for (var channel = 0; channel < 6; channel++)
                frames[frame * 6 + channel] = channel * 0.1f;      // mean 0.25

        var output = Run(format, FloatBytes(frames));

        Assert.NotEmpty(output);
        Assert.All(output[20..^20], sample => Assert.Equal(0.25f, sample, 0.01f));
    }

    [Fact]
    public void FortyFourPointOneResamplesWithoutDecimating()
    {
        // The case §4.4 warns about: 44100 → 16000 is 2.75625, not an integer. The output
        // must still come out at the right length rather than being sample-dropped.
        var output = Run(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), Constant(44100, 2, 0.5f));

        Assert.InRange(output.Length, 16000 - 128, 16000 + 128);
        Assert.All(output[200..^200], sample => Assert.Equal(0.5f, sample, 0.02f));
    }

    [Fact]
    public void SixteenBitPcmScalesToUnitFloat()
    {
        var format = new WaveFormat(16000, 16, 1);
        var samples = new short[16000];
        Array.Fill(samples, (short)16384);                          // exactly half scale

        var output = Run(format, MemoryMarshal.AsBytes<short>(samples).ToArray());

        Assert.NotEmpty(output);
        Assert.Equal(0.5f, output[0], 0.001f);
    }

    [Fact]
    public void TwentyFourBitPcmKeepsItsSign()
    {
        // 24-bit is three bytes little-endian with the sign in the top byte; getting this
        // wrong turns quiet negative audio into very loud positive audio.
        var format = new WaveFormat(16000, 24, 1);
        var bytes = new byte[3 * 3];
        //  +half scale, -half scale, zero
        bytes[0] = 0x00; bytes[1] = 0x00; bytes[2] = 0x40;
        bytes[3] = 0x00; bytes[4] = 0x00; bytes[5] = 0xC0;
        bytes[6] = 0x00; bytes[7] = 0x00; bytes[8] = 0x00;

        var output = Run(format, bytes);

        Assert.Equal(3, output.Length);
        Assert.Equal(0.5f, output[0], 0.001f);
        Assert.Equal(-0.5f, output[1], 0.001f);
        Assert.Equal(0f, output[2], 0.001f);
    }

    [Fact]
    public void ExtensibleFormatsAreUnwrapped()
    {
        // Both of the G15's endpoints report Extensible rather than IeeeFloat. Matching on
        // the wrapper instead of the subtype converts every sample to zero — silence that
        // looks exactly like a muted meeting.
        var extensible = WaveFormatExtensible.CreateIeeeFloatWaveFormat(48000, 2);
        var output = Run(extensible, Constant(48000, 2, 0.75f));

        Assert.NotEmpty(output);
        Assert.All(output[100..^100], sample => Assert.Equal(0.75f, sample, 0.01f));
    }

    [Fact]
    public void AlreadyTargetFormatPassesStraightThrough()
    {
        // What process loopback (§4.5) asks for. Nothing to convert, and no resampler
        // latency to pay for.
        var normalizer = new AudioNormalizer(WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        var output = normalizer.Process(FloatBytes(0.1f, 0.2f, 0.3f), 12);

        Assert.Equal([0.1f, 0.2f, 0.3f], output);
        Assert.Empty(normalizer.Flush());
        Assert.Contains("passthrough", normalizer.Describe());
    }

    [Fact]
    public void PartialAndEmptyPacketsAreSafe()
    {
        var normalizer = new AudioNormalizer(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));

        Assert.Empty(normalizer.Process([], 0));
        Assert.Empty(normalizer.Process(IntPtr.Zero, 480));
        Assert.Empty(normalizer.Process(new byte[4], 4));           // less than one frame
    }

    [Fact]
    public void StreamedPacketsTotalTheSameAsOneBigOne()
    {
        // The real call pattern: a hundred 10 ms packets, not one contiguous second. The
        // resampler carries state across them, so the totals have to agree.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var normalizer = new AudioNormalizer(format);
        var packet = Constant(48000 / 100, 2, 0.4f);

        var total = 0;
        for (var i = 0; i < 100; i++) total += normalizer.Process(packet, packet.Length).Length;
        total += normalizer.Flush().Length;

        Assert.InRange(total, 16000 - 64, 16000 + 64);
    }

    [Fact]
    public void SixtySecondsInIsSixtySecondsOut()
    {
        // A rate error is invisible in a short run and ruinous in a long one. A 20-minute
        // soak measured the capture clock running 2.3 s fast — 0.19%, which is ten seconds
        // across a ninety-minute interview, and every timestamp after it is wrong by that
        // much. The existing tests used a one-second clip and a ±64-sample tolerance, which
        // is ±0.4%: far too loose to see it.
        // 512 frames, which is what this machine's endpoint actually delivers — and which
        // 3 does not divide. A test using 480 misses the bug entirely.
        const int packetFrames = 512;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var normalizer = new AudioNormalizer(format);
        var packet = Constant(packetFrames, 2, 0.4f);

        var packets = 60 * 48000 / packetFrames;
        var total = 0L;
        for (var i = 0; i < packets; i++) total += normalizer.Process(packet, packet.Length).Length;
        total += normalizer.Flush().Length;

        // Within a millisecond of 60 s at 16 kHz. Rounding up per packet would land 117 ms
        // long, which passes a ±0.4% tolerance and ruins a ninety-minute transcript.
        var expected = (long)packets * packetFrames / 3;
        Assert.Equal(expected, total, tolerance: 16);
    }

    [Theory]
    [InlineData(441)]      // 44.1 kHz endpoints land on awkward sizes too
    [InlineData(480)]
    [InlineData(512)]
    [InlineData(1024)]
    public void OutputRateIsExactWhateverThePacketSize(int packetFrames)
    {
        // The rate must not depend on how the driver happens to chunk its audio.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var normalizer = new AudioNormalizer(format);
        var packet = Constant(packetFrames, 2, 0.2f);

        var packets = 30 * 48000 / packetFrames;
        var total = 0L;
        for (var i = 0; i < packets; i++) total += normalizer.Process(packet, packet.Length).Length;
        total += normalizer.Flush().Length;

        Assert.Equal((long)packets * packetFrames / 3, total, tolerance: 16);
    }

    [Fact]
    public void SilenceIsExactAtTheTargetRate()
    {
        // The §4.3 corrections are expressed in output samples and must arrive intact —
        // this is the clock, not audio.
        Assert.Equal(1600, AudioNormalizer.Silence(1600).Length);
        Assert.All(AudioNormalizer.Silence(64), sample => Assert.Equal(0f, sample));
        Assert.Empty(AudioNormalizer.Silence(0));
        Assert.Empty(AudioNormalizer.Silence(-5));
    }
}
