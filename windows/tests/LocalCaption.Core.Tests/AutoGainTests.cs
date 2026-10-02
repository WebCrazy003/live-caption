using LocalCaption.Core.Audio;
using Xunit;

namespace LocalCaption.Core.Tests;

/// <summary>
/// The gain stage in front of the speech gate. The first test is the one that matters most to
/// anyone whose volume was never the problem: for them, nothing changes at all.
/// </summary>
public sealed class AutoGainTests
{
    private const int SampleRate = 16000;

    /// <summary>
    /// Something shaped like talking: bursts of noise (syllables) with short gaps, in
    /// sentences a few seconds long with a pause between them. Deterministic.
    /// </summary>
    private static float[] Speech(double seconds, float peak, int seed = 7)
    {
        var random = new Random(seed);
        var samples = new float[(int)(seconds * SampleRate)];
        var i = 0;
        while (i < samples.Length)
        {
            var sentenceEnd = Math.Min(samples.Length, i + (int)((2.5 + random.NextDouble() * 2.5) * SampleRate));
            while (i < sentenceEnd)
            {
                var syllable = (int)((0.12 + random.NextDouble() * 0.2) * SampleRate);
                var loudness = peak * (0.35f + 0.65f * (float)random.NextDouble());     // stressed and unstressed
                for (var n = 0; n < syllable && i < sentenceEnd; n++, i++)
                {
                    var envelope = MathF.Sin(MathF.PI * n / syllable);
                    samples[i] = loudness * envelope * ((float)random.NextDouble() * 2 - 1);
                }
                i += (int)((0.04 + random.NextDouble() * 0.07) * SampleRate);           // gap between syllables
            }
            i = sentenceEnd + (int)(0.9 * SampleRate);                                  // pause between sentences
        }
        return samples;
    }

    private static float[] Scaled(float[] samples, double decibels)
    {
        var factor = (float)Math.Pow(10, decibels / 20);
        return samples.Select(s => s * factor).ToArray();
    }

    /// <summary>How much of the audio the segmenter turned into utterances.</summary>
    private static double Coverage(float[] samples)
    {
        // Spelled out: `new Tuning()` on a struct is all zeros — a threshold of 0, which calls
        // everything speech — not the constructor's defaults.
        var segmenter = new SpeechSegmenter(Guid.NewGuid(), new SpeechSegmenter.Tuning(600, 500, 20, 0.015f));
        var finals = new List<SpeechRequest>();
        for (var at = 0; at < samples.Length; at += 480)
            finals.AddRange(segmenter.Append(samples.AsSpan(at, Math.Min(480, samples.Length - at)).ToArray(), 0).Where(r => r.IsFinal));
        finals.AddRange(segmenter.Finish(0).Where(r => r.IsFinal));
        return (double)finals.Sum(r => r.Audio.Count) / samples.Length;
    }

    private static float[] Through(AutoGain gain, float[] samples, int chunk = 480)
    {
        var output = new List<float>(samples.Length);
        for (var at = 0; at < samples.Length; at += chunk)
            output.AddRange(gain.Process(samples.AsSpan(at, Math.Min(chunk, samples.Length - at)).ToArray()));
        return [.. output];
    }

    [Fact]
    public void Audio_at_normal_volume_passes_through_bit_for_bit()
    {
        var original = Speech(30, peak: 0.5f);
        var processed = Through(new AutoGain(), (float[])original.Clone());

        Assert.Equal(original, processed);
    }

    [Theory]
    [InlineData(-10)]    // speakers at about half
    [InlineData(-16)]    // 34% — the session this was written for
    [InlineData(-24)]    // nearly off
    [InlineData(-29)]    // the last notch before mute
    public void Turned_down_audio_is_detected_as_well_as_the_original(double decibels)
    {
        var original = Speech(60, peak: 0.45f);
        var quiet = Scaled(original, decibels);

        var reference = Coverage(original);
        var without = Coverage(quiet);
        var with = Coverage(Through(new AutoGain(), (float[])quiet.Clone()));

        Assert.True(with >= reference - 0.05,
            $"at {decibels} dB: {with:P0} detected with gain, {reference:P0} at full volume, {without:P0} with no gain");
    }

    [Fact]
    public void Without_it_the_same_quiet_audio_really_is_mostly_lost()
    {
        // The problem, reproduced — so the test above is known to be testing something.
        var original = Speech(60, peak: 0.45f);
        // -24 dB rather than the -16 dB of the real session: these synthetic syllables are
        // noise bursts, far denser than a voice, so they survive a cut that speech does not.
        Assert.True(Coverage(Scaled(original, -24)) < Coverage(original) * 0.2);
    }

    [Fact]
    public void The_result_does_not_depend_on_how_the_audio_was_chunked()
    {
        var quiet = Scaled(Speech(20, peak: 0.45f), -16);

        var small = Through(new AutoGain(), (float[])quiet.Clone(), chunk: 160);
        var ragged = Through(new AutoGain(), (float[])quiet.Clone(), chunk: 1031);

        Assert.Equal(small.Length, quiet.Length);      // the sample clock depends on this
        Assert.Equal(small, ragged);
    }

    [Fact]
    public void Silence_stays_silent_however_long_it_lasts()
    {
        var gain = new AutoGain();
        Through(gain, Scaled(Speech(10, peak: 0.45f), -20));
        var silence = Through(gain, new float[SampleRate * 120]);

        Assert.All(silence, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void A_long_silence_does_not_wind_the_gain_up()
    {
        var gain = new AutoGain();
        Through(gain, Scaled(Speech(10, peak: 0.45f), -10));
        Through(gain, new float[SampleRate * 2]);          // let it finish settling on its target
        var settled = gain.Gain;
        Through(gain, new float[SampleRate * 300]);

        Assert.Equal(settled, gain.Gain, precision: 3);
    }

    [Fact]
    public void Nothing_is_ever_pushed_past_full_scale()
    {
        var gain = new AutoGain();
        Through(gain, Scaled(Speech(15, peak: 0.45f), -28));              // gain is now high
        var loud = Through(gain, Speech(5, peak: 0.95f, seed: 11));        // and something loud arrives

        Assert.All(loud, s => Assert.InRange(s, -1f, 1f));
    }

    [Fact]
    public void Gain_is_capped_so_faint_noise_is_not_promoted_to_speech()
    {
        var gain = new AutoGain();
        var hiss = Scaled(Speech(20, peak: 0.45f), -60);                  // far below anything spoken
        var processed = Through(gain, hiss);

        Assert.True(gain.Gain <= AutoGain.MaxGain + 0.01f);
        Assert.Equal(0, Coverage(processed));
    }

    [Fact]
    public void Reset_forgets_the_last_session()
    {
        var gain = new AutoGain();
        Through(gain, Scaled(Speech(10, peak: 0.45f), -20));
        Assert.True(gain.Gain > 2);

        gain.Reset();
        Assert.Equal(1f, gain.Gain);
    }
}
