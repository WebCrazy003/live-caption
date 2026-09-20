using System.Text.Json;
using System.Text.Json.Serialization;
using LocalCaption.Core.Data;

namespace LocalCaption.Core.Transcripts;

public readonly record struct TranscriptSaveResult(string TxtPath, string JsonPath);

/// <summary>
/// Writes a finished transcript to disk: the human <c>.txt</c> plus a machine-readable
/// <c>.json</c> sidecar. Filenames come from the start time, with <c> (2)</c>, <c> (3)</c>…
/// collision suffixes (SPEC.md §12.1).
/// </summary>
/// <remarks>
/// SPEC-WINDOWS.md §9.1 requires these files to be <b>byte-identical</b> to the macOS
/// output: UTF-8 without a BOM, <c>\n</c> endings, the same header, the same sidecar keys.
/// That is what lets §17 verify parity with a plain diff, so nothing here may use
/// <see cref="Environment.NewLine"/> or a default UTF-8 encoder.
/// </remarks>
public static class TranscriptWriter
{
    /// <summary>Sidecar shape — session metadata plus the full segment list, keys sorted.</summary>
    private sealed record Sidecar(
        [property: JsonPropertyName("duration_seconds")] int DurationSeconds,
        [property: JsonPropertyName("ended_at")] string EndedAt,
        [property: JsonPropertyName("segments")] IReadOnlyList<TranscriptSegment> Segments,
        [property: JsonPropertyName("session_name")] string SessionName,
        [property: JsonPropertyName("started_at")] string StartedAt);

    private static readonly JsonSerializerOptions SidecarOptions = new() { WriteIndented = true };

    public static TranscriptSaveResult Save(Transcript transcript, string folder, string sessionName,
                                            DateTimeOffset start, DateTimeOffset end,
                                            int durationSeconds, bool showTimestamps)
    {
        Directory.CreateDirectory(folder);

        var baseName = ResolveBase(folder, TimeFormat.FileStamp(start));
        var txtPath = Path.Combine(folder, baseName + ".txt");
        var jsonPath = Path.Combine(folder, baseName + ".json");

        var text = transcript.FileText(sessionName, start, end, durationSeconds, showTimestamps);
        Files.WriteAllTextAtomic(txtPath, Files.Lf(text));

        var sidecar = new Sidecar(durationSeconds, TimeFormat.Iso(end), transcript.Segments,
                                  sessionName, TimeFormat.Iso(start));
        Files.WriteAllTextAtomic(jsonPath, Files.Lf(JsonSerializer.Serialize(sidecar, SidecarOptions)));

        return new TranscriptSaveResult(txtPath, jsonPath);
    }

    /// <summary>Pick a base filename colliding with neither an existing .txt nor .json.</summary>
    internal static string ResolveBase(string folder, string stamp)
    {
        bool Free(string name) =>
            !File.Exists(Path.Combine(folder, name + ".txt")) &&
            !File.Exists(Path.Combine(folder, name + ".json"));

        if (Free(stamp)) return stamp;
        var n = 2;
        while (!Free($"{stamp} ({n})")) n++;
        return $"{stamp} ({n})";
    }
}
