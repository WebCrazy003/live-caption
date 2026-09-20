namespace LocalCaption.Bench;

/// <summary>
/// Minimal RIFF/WAVE reader for the benchmark's input fixtures.
/// </summary>
/// <remarks>
/// Deliberately not a general WAV library — it accepts exactly what whisper.cpp wants
/// (16 kHz mono float) and converts 16/24/32-bit PCM and 32-bit float to that, refusing
/// anything else loudly. The shipping app never parses WAV at all; its audio arrives from
/// WASAPI already normalised (§4.4).
/// </remarks>
public static class WavReader
{
    public const int RequiredSampleRate = 16000;

    public static float[] ReadMono16k(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException($"{path}: not a RIFF file");
        reader.ReadUInt32(); // riff size
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException($"{path}: not a WAVE file");

        int channels = 0, bitsPerSample = 0, sampleRate = 0, formatTag = 0;
        byte[]? data = null;

        while (stream.Position < stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            var next = stream.Position + size + (size % 2);   // chunks are word-aligned

            switch (id)
            {
                case "fmt ":
                    formatTag = reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    sampleRate = (int)reader.ReadUInt32();
                    reader.ReadUInt32();  // byte rate
                    reader.ReadUInt16();  // block align
                    bitsPerSample = reader.ReadUInt16();
                    break;
                case "data":
                    data = reader.ReadBytes((int)size);
                    break;
            }
            stream.Position = Math.Min(next, stream.Length);
        }

        if (data is null) throw new InvalidDataException($"{path}: no data chunk");
        if (sampleRate != RequiredSampleRate)
            throw new InvalidDataException(
                $"{path}: {sampleRate} Hz — the benchmark needs {RequiredSampleRate} Hz mono " +
                $"(convert with: ffmpeg -i in.wav -ar 16000 -ac 1 out.wav)");

        var interleaved = Decode(data, formatTag, bitsPerSample, path);
        return channels <= 1 ? interleaved : Downmix(interleaved, channels);
    }

    private static float[] Decode(byte[] data, int formatTag, int bitsPerSample, string path)
    {
        const int Pcm = 1, IeeeFloat = 3, Extensible = 0xFFFE;

        if (formatTag is IeeeFloat && bitsPerSample == 32)
        {
            var samples = new float[data.Length / 4];
            Buffer.BlockCopy(data, 0, samples, 0, samples.Length * 4);
            return samples;
        }

        if (formatTag is Pcm or Extensible)
        {
            switch (bitsPerSample)
            {
                case 16:
                {
                    var samples = new float[data.Length / 2];
                    for (var i = 0; i < samples.Length; i++)
                        samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
                    return samples;
                }
                case 24:
                {
                    var samples = new float[data.Length / 3];
                    for (var i = 0; i < samples.Length; i++)
                    {
                        var o = i * 3;
                        var value = data[o] | (data[o + 1] << 8) | ((sbyte)data[o + 2] << 16);
                        samples[i] = value / 8388608f;
                    }
                    return samples;
                }
                case 32:
                {
                    var samples = new float[data.Length / 4];
                    for (var i = 0; i < samples.Length; i++)
                        samples[i] = BitConverter.ToInt32(data, i * 4) / 2147483648f;
                    return samples;
                }
            }
        }

        throw new InvalidDataException($"{path}: unsupported format tag {formatTag} at {bitsPerSample}-bit");
    }

    private static float[] Downmix(float[] interleaved, int channels)
    {
        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++) sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }

    /// <summary>
    /// A synthetic speech-like signal, so the benchmark can run before anyone has recorded a
    /// fixture. Useless for judging accuracy — the transcript will be nonsense — but a valid
    /// workload for timing, which is what the latency gates measure.
    /// </summary>
    public static float[] SyntheticSpeech(double seconds)
    {
        var samples = new float[(int)(seconds * RequiredSampleRate)];
        var random = new Random(20260920);
        double phase = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / RequiredSampleRate;
            // A wandering formant with syllable-rate amplitude modulation, which keeps the
            // encoder doing roughly as much work as it would on real speech.
            var f0 = 120 + 40 * Math.Sin(2 * Math.PI * 0.7 * t);
            phase += 2 * Math.PI * f0 / RequiredSampleRate;
            var envelope = 0.5 * (1 + Math.Sin(2 * Math.PI * 3.5 * t));
            samples[i] = (float)(0.3 * envelope * (Math.Sin(phase) + 0.3 * Math.Sin(3 * phase))
                                 + 0.01 * (random.NextDouble() - 0.5));
        }
        return samples;
    }
}
