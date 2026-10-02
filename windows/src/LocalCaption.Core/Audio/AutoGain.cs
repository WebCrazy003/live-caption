namespace LocalCaption.Core.Audio;

/// <summary>
/// Makes captured audio as loud as speech normally is, however quietly it was played.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The segmenter's speech gate is an absolute level
/// (<see cref="SpeechSegmenter.Tuning.Threshold"/>), and on a great many sound devices a
/// loopback capture is taken <i>after</i> the volume control. Turn the speakers down to a
/// third and the interviewer arrives 16 dB quieter: only their loudest syllables clear the
/// gate, the transcript comes out as one-second crumbs with most of the speech missing, and
/// the live captions all but stop — because live decoding only runs while speech is
/// "detected". Found from a real session that kept 32% of a talking-head video. Nothing was
/// wrong with the pipeline. The volume was at 34%.</para>
/// <para><b>What it does.</b> It follows the recent peak of the signal and scales it up to
/// where ordinary full-volume speech sits. Three rules keep it honest:</para>
/// <list type="bullet">
///   <item><description><b>It never turns anything down, and it leaves normal audio
///   alone.</b> Gain is clamped to at least 1, and at unity the samples pass through
///   untouched — bit for bit — so a machine whose volume was never the problem behaves
///   exactly as it did before this class existed.</description></item>
///   <item><description><b>Silence stays silence.</b> Digital silence times any gain is
///   still zero, and the follower only relaxes while there is signal, so an hour of nothing
///   does not wind the gain up to pounce on the first click.</description></item>
///   <item><description><b>It is slow to rise and instant to fall.</b> Rising slowly means a
///   pause between sentences is not "corrected" into noise; falling at once means a sudden
///   loud sound is never amplified into clipping.</description></item>
/// </list>
/// <para>Works sample by sample, so the result does not depend on how the capture layer
/// happens to chunk its packets, and the output is always exactly as long as the input —
/// which matters here more than usual, because the session clock <i>is</i> the sample count.</para>
/// </remarks>
public sealed class AutoGain
{
    private const int SampleRate = 16000;

    /// <summary>
    /// Where quiet audio is brought up to. Deliberately <i>under</i> where full-volume speech
    /// peaks (0.4–0.7), so that ordinary audio — including its softer passages — never asks
    /// for any gain at all.
    /// </summary>
    public const float TargetPeak = 0.25f;

    /// <summary>
    /// Less than this much wanted gain (+3 dB) counts as none. Without a dead zone, audio
    /// that was fine all along would drift in and out of a few percent of gain and stop
    /// being bit-for-bit what was captured, for no benefit anyone could hear or measure.
    /// </summary>
    private const float DeadZone = 1.41f;

    /// <summary>+30 dB. Enough for a volume slider near the bottom; not enough to turn dither into speech.</summary>
    public const float MaxGain = 31.6f;

    /// <summary>Below this a sample is "nothing playing", and the follower holds still.</summary>
    private const float Floor = 0.0008f;

    // Half-lives, as per-sample coefficients.
    private static readonly float Release = Coefficient(seconds: 5.0);     // how fast the remembered peak fades
    private static readonly float Rise = Coefficient(seconds: 0.35);       // how fast gain is allowed to grow
    private static readonly float FirstRise = Coefficient(seconds: 0.02);  // …except the first time, when it is simply wrong
    private static readonly float Fall = Coefficient(seconds: 0.004);      // how fast it must shrink

    /// <summary>
    /// How much signal must have been heard before the remembered peak is believed.
    /// </summary>
    /// <remarks>
    /// Every sound begins quietly — the first milliseconds of a word are a ramp up from
    /// nothing — and a follower that has heard only those would conclude the audio is faint
    /// and start raising the gain on perfectly normal speech. An eighth of a second of
    /// signal is enough to have seen what the level really is, and is still inside the
    /// segmenter's 200 ms pre-roll, so nothing said in that time is lost.
    /// </remarks>
    private const int SettleSamples = SampleRate / 8;

    private float _peak;
    private float _gain = 1f;
    private float _level;
    private int _heard;
    private bool _locked;

    /// <summary>The gain being applied right now, as a multiplier (1 = untouched).</summary>
    public float Gain => _gain;

    /// <summary>The same in decibels, for a readout.</summary>
    public double GainDb => 20 * Math.Log10(Math.Max(1e-6, _gain));

    /// <summary>Recent output peak, 0–1 — what a level meter should show once gain is in play.</summary>
    public float Level => _level;

    /// <summary>Scale <paramref name="samples"/> in place. Returns the same array.</summary>
    public float[] Process(float[] samples)
    {
        var level = _level * 0.6f;      // the meter falls back between packets

        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i];
            var magnitude = MathF.Abs(x);

            if (magnitude > _peak) _peak = magnitude;
            else if (magnitude > Floor) _peak *= Release;
            if (magnitude > Floor && _heard < SettleSamples) _heard++;

            var wanted = _peak <= Floor || _heard < SettleSamples
                ? _gain
                : Math.Clamp(TargetPeak / _peak, 1f, MaxGain);
            if (wanted < DeadZone) wanted = 1f;
            // The slow rise exists so that a pause mid-conversation is not "corrected". At the
            // very start there is no conversation yet, only a gain of 1 that is known to be
            // wrong — and crawling up to the right one cost a real session its first second
            // ("So when I first…" never made it). So the first approach is fast, and only
            // once it has arrived does the gain become slow to move.
            var rate = wanted < _gain ? Fall : _locked ? Rise : FirstRise;
            _gain = wanted + (_gain - wanted) * rate;
            if (!_locked && _heard >= SettleSamples && _gain >= wanted * 0.9f) _locked = true;

            // Exactly unity is the common case, and it must be exact: no multiply, no
            // rounding, nothing for a bit-for-bit comparison with the input to find.
            if (_gain > 1.0005f)
            {
                x = Math.Clamp(x * _gain, -1f, 1f);
                samples[i] = x;
                magnitude = MathF.Abs(x);
            }
            else
            {
                _gain = Math.Max(1f, _gain);
            }

            if (magnitude > level) level = magnitude;
        }

        _level = level;
        return samples;
    }

    /// <summary>Forget everything: a new session should not inherit the last one's gain.</summary>
    public void Reset()
    {
        _peak = 0;
        _gain = 1f;
        _level = 0;
        _heard = 0;
        _locked = false;
    }

    private static float Coefficient(double seconds) => (float)Math.Pow(0.5, 1.0 / (seconds * SampleRate));
}
