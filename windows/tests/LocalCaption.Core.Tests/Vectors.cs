using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalCaption.Core.Tests;

/// <summary>
/// Loads the shared cross-platform vectors from <c>testdata/</c> at the repo root
/// (SPEC-WINDOWS.md §6.1). The macOS suite reads the very same files from
/// <c>app/Tests/LocalCaptionKitTests/ConformanceTests.swift</c>.
/// </summary>
internal static class Vectors
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Resolved from the compile-time path of this file rather than the test binary's
    /// location, so the vectors are found whatever the build configuration or working
    /// directory — exactly as <c>#filePath</c> does on the macOS side.
    /// </summary>
    public static string Root { get; } = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(ThisFile())!, "..", "..", "..", "testdata"));

    private static string ThisFile([CallerFilePath] string path = "") => path;

    /// <summary>Every vector in a suite, ordered by filename so both platforms agree on order.</summary>
    public static IEnumerable<(string Name, T Value)> Load<T>(string suite)
    {
        var dir = Path.Combine(Root, suite);
        Assert.True(Directory.Exists(dir), $"no vector directory at {dir}");
        var files = Directory.GetFiles(dir, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
        Assert.True(files.Count > 0, $"no vectors found in testdata/{suite}");
        foreach (var file in files)
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(file), Options)
                        ?? throw new InvalidOperationException($"vector {file} decoded to null");
            yield return (Path.GetFileName(file), value);
        }
    }
}

// ── Vector shapes ────────────────────────────────────────────────────────────────────────
// Property names map to the JSON via SnakeCaseLower; anything that needs a different name
// is annotated. These mirror the Decodable structs in ConformanceTests.swift.

internal sealed record RollingVector(List<RollingStep> Steps, string ExpectText);
internal sealed record RollingStep(List<VectorWord> Words, int StartSample, int EndSample, bool ExpectAccepted);
internal sealed record VectorWord(string Text, double Start, double End);

internal sealed record SegmenterVector(
    SegmenterTuning Tuning, List<SegmenterStep> Steps, int? ExpectTotalFinalSamples);
internal sealed record SegmenterTuning(int EndpointMs, int IntervalMs, int MaxUtteranceS, float Threshold);
internal sealed record SampleRun(float Amplitude, int Count);
internal sealed record SegmenterStep(
    string Op, double? Now, List<SampleRun>? Runs,
    List<VectorRequest>? ExpectRequests, List<VectorRequest>? ExpectFinalRequests,
    int? ExpectFinalCount, int? ExpectInterimMaxSamples,
    List<VectorTransition>? ExpectTransitions);
internal sealed record VectorRequest(int Utterance, int StartSample, int SampleCount, int EndSample, bool IsFinal)
{
    public override string ToString() => $"#{Utterance} [{StartSample}+{SampleCount}={EndSample}]{(IsFinal ? "F" : "i")}";
}
internal sealed record VectorTransition(int Utterance, int Sample, bool Started)
{
    public override string ToString() => $"#{Utterance}@{Sample}{(Started ? "↑" : "↓")}";
}

internal sealed record AgreementVector(List<AgreementStep> Steps);
internal sealed record AgreementStep(
    string Op, List<string>? Hypothesis, List<string>? ExpectCommitted, List<string>? ExpectProvisional);

internal sealed record FilterVector(string Kind, List<FilterCase> Cases);
internal sealed record FilterCase(
    string? Input, JsonElement Expect,
    double? AvgLogprob, double? NoSpeechProb, double? CompressionRatio,
    int? ExpectWords, int? ExpectSentences);

internal sealed record SentenceVector(string Kind, List<SentenceCase> Cases);
internal sealed record SentenceCase(string Text, int? N, string? Provisional, JsonElement Expect);

internal sealed record ConfigVector(
    string Name, string? Input,
    [property: JsonPropertyName("round_trip_overrides")] Dictionary<string, JsonElement>? RoundTripOverrides,
    bool? ExpectRepaired, int? ExpectBackupCount, bool? ExpectFileExists,
    bool? ExpectReloadClean, bool? ExpectRewritten,
    Dictionary<string, JsonElement>? Expect,
    List<string>? ExpectEncodedContains, List<string>? ExpectEncodedOmits);
