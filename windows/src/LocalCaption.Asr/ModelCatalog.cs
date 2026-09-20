using Whisper.net;

namespace LocalCaption.Asr;

/// <summary>Which compute backend a factory should use.</summary>
/// <remarks>
/// <c>asr.backend</c> in <c>config.json</c> accepts only <c>auto</c>, <c>cuda</c> and
/// <c>cpu</c> (§5.2). <see cref="Metal"/> is a <b>resolved-only</b> value that never appears
/// in config: it exists so a stage-A run on the Mac reports what it actually used instead of
/// claiming CUDA on a machine that has no NVIDIA GPU.
/// </remarks>
public enum AsrBackend
{
    /// <summary>Probe for a usable GPU and fall back to CPU (the <c>asr.backend</c> default).</summary>
    Auto,
    Cuda,
    Cpu,

    /// <summary>Apple Silicon GPU. Development only — never shipped, never written to config.</summary>
    Metal,
}

/// <summary>
/// One speech model: its config-friendly name, its weights file, and the DTW alignment-head
/// preset that gives it word timings.
/// </summary>
public sealed record ModelSpec(string Name, string FileName, WhisperAlignmentHeadsPreset Heads, long ApproxBytes)
{
    public const string HuggingFaceBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public Uri DownloadUri => new(HuggingFaceBase + FileName);
}

/// <summary>
/// Maps config-friendly model names to whisper.cpp weights — the direct analogue of the
/// macOS <c>WhisperEngine.variant(for:)</c>.
/// </summary>
/// <remarks>
/// <para>SPEC-WINDOWS.md §5.3 requires the <b>names</b> to stay identical to macOS
/// (<c>tiny.en</c>, <c>small.en</c>, <c>large-v3-turbo</c>) so a <c>config.json</c> carries
/// between the two machines unchanged. Only what a name resolves to differs: CoreML model
/// folders there, GGML <c>.bin</c> files here.</para>
/// <para>The alignment-head preset is not cosmetic. Without the right one, DTW produces no
/// usable word timings, and <see cref="LocalCaption.Core.Captions.RollingCaption"/> —
/// which merges hypotheses purely by word midpoint — has nothing to work with. §5.7.</para>
/// </remarks>
public static class ModelCatalog
{
    private static readonly Dictionary<string, ModelSpec> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tiny.en"] = new("tiny.en", "ggml-tiny.en.bin", WhisperAlignmentHeadsPreset.TinyEn, 75_000_000),
        ["base.en"] = new("base.en", "ggml-base.en.bin", WhisperAlignmentHeadsPreset.BaseEn, 142_000_000),
        ["small.en"] = new("small.en", "ggml-small.en.bin", WhisperAlignmentHeadsPreset.SmallEn, 466_000_000),
        ["large-v3-turbo"] = new("large-v3-turbo", "ggml-large-v3-turbo.bin",
                                 WhisperAlignmentHeadsPreset.LargeV3Turbo, 1_620_000_000),
        ["large-v3"] = new("large-v3", "ggml-large-v3.bin", WhisperAlignmentHeadsPreset.LargeV3, 3_100_000_000),
        ["medium.en"] = new("medium.en", "ggml-medium.en.bin", WhisperAlignmentHeadsPreset.MediumEn, 1_530_000_000),
    };

    /// <summary>Every model the app offers, in ascending size.</summary>
    public static IReadOnlyList<ModelSpec> All => Known.Values.OrderBy(m => m.ApproxBytes).ToList();

    /// <summary>
    /// Resolve a config name. An unknown name falls back to a <c>ggml-&lt;name&gt;.bin</c>
    /// guess with <see cref="WhisperAlignmentHeadsPreset.None"/> — it may still load, but it
    /// will have no word timings, so the caller should expect the §5.7.1 fallback path.
    /// </summary>
    public static ModelSpec Resolve(string name) =>
        Known.TryGetValue(name.Trim(), out var spec)
            ? spec
            : new ModelSpec(name, $"ggml-{name}.bin", WhisperAlignmentHeadsPreset.None, 0);

    public static bool IsKnown(string name) => Known.ContainsKey(name.Trim());

    /// <summary>Where a model's weights live once downloaded.</summary>
    public static string PathFor(ModelSpec spec, string modelsDirectory) =>
        Path.Combine(modelsDirectory, spec.FileName);
}
