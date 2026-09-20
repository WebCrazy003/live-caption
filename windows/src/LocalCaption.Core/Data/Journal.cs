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
            // A journal someone still has open is a session that is still running, not one
            // that died. Offering it for recovery would invite the user to delete a live
            // recording's only durable record — which is what a second copy of the app did,
            // the first time one was launched while another was recording.
            if (IsInUse(file)) continue;

            IReadOnlyList<TranscriptSegment> segments;
            try { segments = Read(file); } catch (IOException) { continue; }

            var name = System.IO.Path.GetFileNameWithoutExtension(file);
            var id = Guid.TryParse(name, out var parsed) ? parsed : Guid.NewGuid();
            recovered.Add(new RecoveredSession(id, file, segments));
        }
        return recovered;
    }

    /// <summary>
    /// Whether another handle still holds this journal — i.e. its session is still running.
    /// </summary>
    /// <remarks>
    /// Asking for exclusive access is the question: if it is refused, someone else has the
    /// file. <see cref="Pending"/> deliberately reads with a permissive share set, so it
    /// cannot tell a live journal from an orphaned one without this.
    /// </remarks>
    private static bool IsInUse(string file)
    {
        try
        {
            using var _ = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Read a journal that another handle may still have open for writing.
    /// </summary>
    /// <remarks>
    /// <b>Windows-only hazard.</b> <see cref="File.ReadAllLines(string)"/> opens with
    /// <see cref="FileShare.Read"/>, which denies write sharing — so on Windows it throws
    /// against the live <see cref="Journal"/>'s own append handle, and the scan skips the
    /// file entirely and reports nothing to recover. Unix ignores share modes, so the macOS
    /// build cannot see this. Ask for the permissive share set explicitly.
    /// </remarks>
    private static string[] ReadSharedLines(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Files.Utf8NoBom);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return [.. lines];
    }

    /// <summary>
    /// Every segment in one journal, whether or not its session is still running.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Pending"/> on purpose: "what is in this file" and "what
    /// should be offered for recovery" are different questions, and only the second one
    /// cares whether the session that owns it is still alive.
    /// </remarks>
    public static IReadOnlyList<TranscriptSegment> Read(string path)
    {
        var segments = new List<TranscriptSegment>();
        foreach (var line in ReadSharedLines(path))
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
        return segments;
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
