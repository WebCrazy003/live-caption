namespace LocalCaption.Asr;

/// <summary>
/// Which models to load when the GPU is not there — and how to tell that it has gone.
/// </summary>
/// <remarks>
/// <para><b>SPEC-WINDOWS.md §5.8.</b> On this laptop the GPU the app depends on is
/// user-switchable: the MUX switch, Armoury Crate's power profiles and Eco mode can all
/// remove the RTX 3070, often on battery and without the user thinking of it as a change to
/// anything. So CUDA is probed at every model load rather than once, and the app has to have
/// an answer for "it is gone now".</para>
/// <para>Kept separate from the engine because the decision is arithmetic and the engine is
/// not: this way the rule that protects a 40-minute interview has tests.</para>
/// </remarks>
public static class AsrFallback
{
    /// <summary>
    /// How many decodes in a row must fail before the GPU is presumed gone.
    /// </summary>
    /// <remarks>
    /// One failure is a bad window — a clipped buffer, a cancelled decode. Three in a row,
    /// with audio still arriving, is not. The threshold is deliberately small: §5.8's worst
    /// case is a transcript dropped at minute 40, so noticing late costs more than an
    /// occasional unnecessary pause, which the user simply resumes from.
    /// </remarks>
    public const int FailuresBeforeGpuPresumedLost = 3;

    /// <summary>The models to run when only the CPU is available.</summary>
    /// <remarks>
    /// §5.8 names these: <c>tiny.en</c> and <c>small.en</c>, <b>not</b> turbo. B0 measured why
    /// — turbo needs 17 s per 6-second window on this CPU, which is not "slower", it is
    /// unusable. small.en's 4 s is slow but produces a transcript.
    /// </remarks>
    public static (string Interim, string Final) CpuModels => ("tiny.en", "small.en");

    /// <summary>
    /// Pick the models for a backend, downgrading only when the choice would be hopeless.
    /// </summary>
    /// <param name="onGpu">Whether a GPU backend actually loaded — not whether one was asked for.</param>
    /// <returns>The models to load, and the banner to show, or null when nothing is wrong.</returns>
    public static (string Interim, string Final, string? Banner) Choose(bool onGpu, string interim, string final)
    {
        if (onGpu) return (interim, final, null);

        // A CPU-sized choice the user already made is left alone; only the ones B0 measured
        // as unusable are overridden, and the banner says so rather than silently differing
        // from what Settings shows.
        if (!IsHopelessOnCpu(final)) return (interim, final, CpuBanner(null));

        var (cpuInterim, cpuFinal) = CpuModels;
        return (IsHopelessOnCpu(interim) ? cpuInterim : interim, cpuFinal, CpuBanner(final));
    }

    /// <summary>Models whose CPU decode time exceeds the audio they are decoding (B0 §2a).</summary>
    private static bool IsHopelessOnCpu(string model) =>
        model.Contains("large", StringComparison.OrdinalIgnoreCase);

    private static string CpuBanner(string? replaced)
    {
        const string core = "Running on CPU — captions will be slower. Enable the NVIDIA GPU for best results.";
        return replaced is null
            ? core
            : $"{core} ({replaced} needs about 17 seconds per window without a GPU, so {CpuModels.Final} " +
              "is being used instead.)";
    }
}
