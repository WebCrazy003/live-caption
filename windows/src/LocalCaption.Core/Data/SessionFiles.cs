using LocalCaption.Core.Transcripts;

namespace LocalCaption.Core.Data;

/// <summary>Filesystem operations on a session's saved transcript artifacts.</summary>
public static class SessionFiles
{
    /// <summary>
    /// Delete a transcript <c>.txt</c> and its sibling <c>.json</c> sidecar together
    /// (SPEC-06 delete). Returns the paths actually removed.
    /// </summary>
    public static IReadOnlyList<string> DeleteTranscript(string txtPath)
    {
        var jsonPath = Path.ChangeExtension(txtPath, ".json");
        var removed = new List<string>();
        foreach (var path in new[] { txtPath, jsonPath })
        {
            if (!File.Exists(path)) continue;
            try { File.Delete(path); removed.Add(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// <summary>True when the session's recorded <c>.txt</c> export is on disk.</summary>
    public static bool HasExport(SessionRecord record) =>
        !string.IsNullOrEmpty(record.TranscriptFile) && File.Exists(record.TranscriptFile);

    /// <summary>
    /// Write a saved session's <c>.txt</c> + <c>.json</c> export and record the path on its
    /// row. The captions are already in the database; this is only the export.
    /// </summary>
    public static string Export(Store store, long sessionId, Transcript transcript, string sessionName,
                                DateTimeOffset start, DateTimeOffset end, int durationSeconds,
                                string folder, bool showTimestamps)
    {
        var result = TranscriptWriter.Save(transcript, folder, sessionName, start, end, durationSeconds,
                                           showTimestamps);
        store.SetTranscriptFile(sessionId, result.TxtPath);
        return result.TxtPath;
    }

    /// <summary>
    /// The path of a session's <c>.txt</c> export, writing a fresh one from its saved captions
    /// when the recorded file is missing — deleted, on an unplugged drive, or a path from the
    /// other platform. Null when there is neither a file nor any captions to write one from.
    /// </summary>
    public static string? EnsureExport(Store store, SessionRecord record, string folder, bool showTimestamps)
    {
        if (HasExport(record)) return record.TranscriptFile;
        if (record.Id is not { } id) return null;

        var segments = store.Segments(id);
        if (segments.Count == 0) return null;

        var start = (TimeFormat.ParseIso(record.CreatedAt) ?? DateTimeOffset.Now).ToLocalTime();
        var end = (record.EndedAt is { } ended ? TimeFormat.ParseIso(ended) : null)?.ToLocalTime() ?? start;
        return Export(store, id, new Transcript(segments), record.SessionName, start, end,
                      record.DurationSeconds, folder, showTimestamps);
    }
}
