using System.Text.Json;
using LocalCaption.Core.Transcripts;

namespace LocalCaption.Core.Data;

/// <summary>A session recovered from a leftover journal after a crash or forced quit.</summary>
public sealed record RecoveredSession(Guid SessionId, string Path, IReadOnlyList<TranscriptSegment> Segments)
{
    public DateTimeOffset? StartedAt =>
        Segments.Count > 0 ? TimeFormat.ParseIso(Segments[0].CreatedAt) : null;
}

/// <summary>
/// Append-only crash-recovery journal: every finalized segment is written as one JSON line
/// to <c>journal\&lt;session_id&gt;.jsonl</c> and flushed to the physical disk, so a crash
/// mid-session loses nothing. Deleted on a clean Stop.
/// </summary>
/// <remarks>
/// <b>SPEC-WINDOWS.md §9.4.</b> <see cref="FileStream.Flush()"/> does <i>not</i> reach the
/// disk — it only pushes into the OS cache. <c>Flush(flushToDisk: true)</c> calls
/// <c>FlushFileBuffers</c>, and without it this file's entire reason for existing is void on
/// a hard power loss.
/// </remarks>
public sealed class Journal : IDisposable
{
    public Guid SessionId { get; }
    public string Path { get; }
    private FileStream? _stream;

    public Journal(Guid sessionId, string? directory = null)
    {
        directory ??= AppPaths.Journal;
        Directory.CreateDirectory(directory);
        SessionId = sessionId;
        Path = System.IO.Path.Combine(directory, $"{sessionId.ToString("D").ToUpperInvariant()}.jsonl");
        _stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    /// <summary>Append one segment as a JSON line and flush it all the way to the platter.</summary>
    public void Append(TranscriptSegment segment)
    {
        var stream = _stream ?? throw new ObjectDisposedException(nameof(Journal));
        var line = JsonSerializer.Serialize(segment) + "\n";
        stream.Write(Files.Utf8NoBom.GetBytes(line));
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Delete the journal — called on a clean Stop, after the transcript is saved.</summary>
    public void DeleteFile()
    {
        _stream?.Dispose();
        _stream = null;
        try { File.Delete(Path); } catch (IOException) { /* nothing left to recover from */ }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }

    /// <summary>Scan for leftover journals to offer for recovery on launch.</summary>
    public static IReadOnlyList<RecoveredSession> Pending(string? directory = null)
    {
        directory ??= AppPaths.Journal;
        if (!Directory.Exists(directory)) return [];

        var recovered = new List<RecoveredSession>();
        foreach (var file in Directory.GetFiles(directory, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch (IOException) { continue; }

            var segments = new List<TranscriptSegment>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                // A crash can leave the final line half-written. Skip it and keep the rest —
                // recovering all but the last segment beats recovering nothing.
                try
                {
                    if (JsonSerializer.Deserialize<TranscriptSegment>(line) is { } segment)
                        segments.Add(segment);
                }
                catch (JsonException) { }
            }

            var name = System.IO.Path.GetFileNameWithoutExtension(file);
            var id = Guid.TryParse(name, out var parsed) ? parsed : Guid.NewGuid();
            recovered.Add(new RecoveredSession(id, file, segments));
        }
        return recovered;
    }

    /// <summary>Remove a specific journal file, used when a recovery is discarded.</summary>
    public static void Remove(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}

/// <summary>
/// Serialises fsync onto one worker. Completion means the segment is durable, so callers may
/// then publish it as final. The Swift original is an <c>actor</c>; a
/// <see cref="SemaphoreSlim"/> of one gives the same guarantee without blocking the UI.
/// </summary>
/// <remarks>
/// <c>onFinal</c> awaiting durability before publishing is the invariant behind the macOS
/// fixes in <c>9e237fc</c> / <c>b1743a7</c> — see SPEC-WINDOWS.md §6.2.
/// </remarks>
public sealed class JournalWriter(Guid sessionId, string? directory = null) : IDisposable
{
    private readonly Journal _journal = new(sessionId, directory);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task AppendAsync(TranscriptSegment segment, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await Task.Run(() => _journal.Append(segment), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public void DeleteFile() => _journal.DeleteFile();

    public void Dispose()
    {
        _journal.Dispose();
        _gate.Dispose();
    }
}
