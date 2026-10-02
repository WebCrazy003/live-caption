using System.Text.Json;

namespace LocalCaption.Core.Transcripts;

/// <summary>
/// Reads a saved transcript back into segments — used to move sessions saved before captions
/// lived in the database into it. Prefers the <c>.json</c> sidecar (exact segments and
/// timings); falls back to the <c>.txt</c> body, one segment per line, with
/// <c>[HH:MM:SS]</c> prefixes read as start times. A port of macOS
/// <c>TranscriptFileReader.swift</c>.
/// </summary>
public static class TranscriptFileReader
{
    /// <summary>Null when neither file can be read.</summary>
    public static IReadOnlyList<TranscriptSegment>? Segments(string txtPath, string createdAt)
    {
        var jsonPath = Path.ChangeExtension(txtPath, ".json");
        try
        {
            if (File.Exists(jsonPath) &&
                JsonSerializer.Deserialize<TranscriptWriter.Sidecar>(File.ReadAllText(jsonPath)) is { Segments: { } segments })
                return segments;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                     or NotSupportedException or ArgumentException) { }

        try { return File.Exists(txtPath) ? FromText(File.ReadAllText(txtPath), createdAt) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                     or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>The <c>.txt</c> body (after the header's blank line), one segment per non-empty line.</summary>
    public static IReadOnlyList<TranscriptSegment> FromText(string text, string createdAt)
    {
        var lines = text.Split('\n');
        var blank = Array.FindIndex(lines, l => l.Trim(' ', '\t').Length == 0);
        var bodyStart = blank < 0 ? 0 : blank + 1;

        var segments = new List<TranscriptSegment>();
        foreach (var raw in lines.Skip(bodyStart))
        {
            var line = raw.Trim(' ', '\t');
            if (line.Length == 0) continue;
            var startMs = 0;
            var close = line.IndexOf(']');
            if (line.StartsWith('[') && close > 0 && TimeFormat.ParseClock(line[1..close]) is { } ms)
            {
                startMs = ms;
                line = line[(close + 1)..].Trim(' ', '\t');
            }
            segments.Add(new TranscriptSegment { Text = line, TStartMs = startMs, TEndMs = startMs, CreatedAt = createdAt });
        }
        return segments;
    }
}
