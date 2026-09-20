using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalCaption.Core.Transcripts;

/// <summary>
/// Writes a <see cref="Guid"/> in the uppercase form Swift's <c>UUID</c> encodes to.
/// </summary>
/// <remarks>
/// Without this the two platforms' <c>.json</c> sidecars and journals would differ in case
/// for every id — a needless divergence in a format SPEC-WINDOWS.md §9.1 wants identical.
/// </remarks>
public sealed class UppercaseGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        Guid.Parse(reader.GetString() ?? throw new JsonException("expected a UUID string"));

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
}

/// <summary>
/// One finalized caption segment (SPEC.md §9.1). Timing is session-relative, in ms.
/// Interim/provisional captions are never stored.
/// </summary>
/// <remarks>Properties are declared in the sorted-key order the sidecar is written in.</remarks>
public sealed record TranscriptSegment
{
    [JsonPropertyName("created_at")] public string CreatedAt { get; init; } = "";
    [JsonPropertyName("id"), JsonConverter(typeof(UppercaseGuidConverter))]
    public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("t_end_ms")] public int TEndMs { get; init; }
    [JsonPropertyName("t_start_ms")] public int TStartMs { get; init; }
    [JsonPropertyName("text")] public string Text { get; init; } = "";
}

/// <summary>
/// Ordered final segments, plus rendering to the on-disk <c>.txt</c> format (SPEC.md §12.1).
/// </summary>
public sealed class Transcript
{
    private readonly List<TranscriptSegment> _segments;

    public Transcript(IEnumerable<TranscriptSegment>? segments = null) =>
        _segments = segments?.ToList() ?? [];

    public IReadOnlyList<TranscriptSegment> Segments => _segments;
    public bool IsEmpty => _segments.Count == 0;
    public void Append(TranscriptSegment segment) => _segments.Add(segment);

    /// <summary>Body lines, optionally prefixed with <c>[HH:MM:SS]</c> from each start.</summary>
    public string Body(bool showTimestamps) => string.Join("\n", _segments.Select(
        s => showTimestamps ? $"{TimeFormat.Stamp(s.TStartMs)} {s.Text}" : s.Text));

    /// <summary>Full file content: header block, blank line, body (SPEC.md §12.1).</summary>
    public string FileText(string sessionName, DateTimeOffset start, DateTimeOffset end,
                           int durationSeconds, bool showTimestamps)
    {
        var header =
            $"Session: {sessionName}\n" +
            $"Start:   {TimeFormat.Human(start)}\n" +
            $"End:     {TimeFormat.Human(end)}\n" +
            $"Duration: {TimeFormat.Clock(durationSeconds)}";
        return header + "\n\n" + Body(showTimestamps) + "\n";
    }
}
