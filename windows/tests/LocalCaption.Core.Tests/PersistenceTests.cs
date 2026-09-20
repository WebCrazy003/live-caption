using System.Text.Json;
using LocalCaption.Core.Data;
using LocalCaption.Core.Transcripts;

namespace LocalCaption.Core.Tests;

/// <summary>
/// Ports of the macOS <c>StoreTests</c>, <c>JournalTests</c>, <c>TranscriptTests</c> and
/// <c>SessionFilesTests</c>. These have no shared vectors: they assert on real filesystem
/// and SQLite behaviour, which cannot be expressed as data.
/// </summary>
public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lc-core-{Guid.NewGuid():N}");

    public PersistenceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private static TranscriptSegment Seg(string text, int start, int end) =>
        new() { Text = text, TStartMs = start, TEndMs = end, CreatedAt = "2026-08-07T10:00:00Z" };

    /// <summary>A fixed instant, so the formatted output is stable across runs and machines.</summary>
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_754_560_338);

    // ── Store ────────────────────────────────────────────────────────────────────────────

    private string DbPath => Path.Combine(_dir, "sessions.db");

    private static SessionRecord Record(string name, string created, int duration = 0) => new()
    {
        SessionName = name, CreatedAt = created, EndedAt = created,
        DurationSeconds = duration, TranscriptFile = $"{name}.txt",
    };

    [Fact]
    public void InsertAssignsIdAndCounts()
    {
        using var store = new Store(DbPath);
        Assert.Equal(0, store.Count());
        var inserted = store.Insert(Record("A", "2026-08-07T10:00:00Z"));
        Assert.NotNull(inserted.Id);
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void ListSortedByCreated()
    {
        using var store = new Store(DbPath);
        store.Insert(Record("Old", "2026-08-01T09:00:00Z"));
        store.Insert(Record("New", "2026-08-07T09:00:00Z"));
        Assert.Equal(["New", "Old"], store.All(SessionSort.CreatedDesc).Select(r => r.SessionName));
        Assert.Equal(["Old", "New"], store.All(SessionSort.CreatedAsc).Select(r => r.SessionName));
    }

    [Fact]
    public void SortByDurationAndName()
    {
        using var store = new Store(DbPath);
        store.Insert(Record("Bravo", "2026-08-01T09:00:00Z", duration: 30));
        store.Insert(Record("Alpha", "2026-08-02T09:00:00Z", duration: 120));
        Assert.Equal("Alpha", store.All(SessionSort.DurationDesc).First().SessionName);
        Assert.Equal(["Alpha", "Bravo"], store.All(SessionSort.NameAsc).Select(r => r.SessionName));
    }

    [Fact]
    public void SearchByName()
    {
        using var store = new Store(DbPath);
        store.Insert(Record("Interview Rajat", "2026-08-07T09:00:00Z"));
        store.Insert(Record("Standup", "2026-08-07T10:00:00Z"));
        Assert.Equal(["Interview Rajat"], store.All(search: "inter").Select(r => r.SessionName));
        Assert.Equal(2, store.All(search: "  ").Count);   // a blank search returns everything
    }

    [Fact]
    public void RenameEditsMetadataOnly()
    {
        using var store = new Store(DbPath);
        var record = store.Insert(Record("Before", "2026-08-07T09:00:00Z"));
        store.Rename(record.Id!.Value, "After");

        var fetched = store.Fetch(record.Id!.Value);
        Assert.Equal("After", fetched!.SessionName);
        Assert.Equal("Before.txt", fetched.TranscriptFile);   // rename must not touch the file field
    }

    [Fact]
    public void Delete()
    {
        using var store = new Store(DbPath);
        var record = store.Insert(Record("Doomed", "2026-08-07T09:00:00Z"));
        store.Delete(record.Id!.Value);
        Assert.Equal(0, store.Count());
        Assert.Null(store.Fetch(record.Id!.Value));
    }

    [Fact]
    public void PersistsAcrossReopen()
    {
        using (var store = new Store(DbPath))
            store.Insert(Record("Persisted", "2026-08-07T09:00:00Z"));

        // Reopening the same file must find the data and re-run migrations idempotently.
        using var reopened = new Store(DbPath);
        Assert.Equal(1, reopened.Count());
        Assert.Equal("Persisted", reopened.All().First().SessionName);
    }

    // ── Journal ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AppendThenRecover()
    {
        var id = Guid.NewGuid();
        using (var journal = new Journal(id, _dir))
        {
            journal.Append(Seg("first", 1000, 2000));
            journal.Append(Seg("second", 3000, 4000));
        }

        // Simulate a crash: the journal file is still present, never deleted.
        var pending = Journal.Pending(_dir);
        Assert.Single(pending);
        Assert.Equal(id, pending[0].SessionId);
        Assert.Equal(["first", "second"], pending[0].Segments.Select(s => s.Text));
    }

    [Fact]
    public void ALiveJournalIsNotOfferedForRecovery()
    {
        // A second copy of the app, launched while the first is recording, must not offer to
        // "recover" the running session — accepting would delete the only durable record of a
        // recording in progress. Found by launching a packaged build during a soak.
        using var live = new Journal(Guid.NewGuid(), _dir);
        live.Append(Seg("still recording", 0, 1000));

        Assert.Empty(Journal.Pending(_dir));

        // Once its owner lets go, it is an orphan like any other.
        live.Dispose();
        Assert.Single(Journal.Pending(_dir));
    }

    [Fact]
    public void CleanStopDeletesJournal()
    {
        using var journal = new Journal(Guid.NewGuid(), _dir);
        journal.Append(Seg("x", 0, 1000));
        journal.DeleteFile();
        Assert.Empty(Journal.Pending(_dir));
    }

    [Fact]
    public void RecoverSkipsMalformedTrailingLine()
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(_dir, $"{id.ToString("D").ToUpperInvariant()}.jsonl");
        // A crash can cut the last line in half. The earlier segments must still come back.
        File.WriteAllText(path, JsonSerializer.Serialize(Seg("ok", 0, 1000)) + "\n{ not json\n",
                          Files.Utf8NoBom);

        var pending = Journal.Pending(_dir);
        Assert.Equal(["ok"], pending[0].Segments.Select(s => s.Text));
        Assert.Equal(id, pending[0].SessionId);
    }

    [Fact]
    public async Task WriterAcknowledgesRecoverableSegmentsAndFailsAfterClose()
    {
        var id = Guid.NewGuid();
        using var writer = new JournalWriter(id, _dir);
        await writer.AppendAsync(Seg("first", 0, 1000));
        await writer.AppendAsync(Seg("second", 1000, 2000));

        // Read directly rather than through Pending: the session is still running, so it is
        // deliberately not offered for recovery. What is being asserted here is that the
        // appends reached the disk and can be read back while the writer still holds the
        // file — which on Windows needs the share set spelled out.
        var path = Path.Combine(_dir, $"{id.ToString("D").ToUpperInvariant()}.jsonl");
        Assert.Equal(["first", "second"], Journal.Read(path).Select(s => s.Text));

        writer.DeleteFile();
        Assert.Empty(Journal.Pending(_dir));

        // A closed journal must not silently acknowledge a write — the caller has to learn
        // the segment is not durable, because it publishes finals on that acknowledgement.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.AppendAsync(Seg("after close", 2000, 3000)));
    }

    [Fact]
    public void JournalUsesLfEndingsAndNoBom()
    {
        var id = Guid.NewGuid();
        using (var journal = new Journal(id, _dir)) journal.Append(Seg("only", 0, 1000));

        var bytes = File.ReadAllBytes(Path.Combine(_dir, $"{id.ToString("D").ToUpperInvariant()}.jsonl"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM written");
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    // ── Transcript ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void FileTextHasHeaderAndBody()
    {
        var transcript = new Transcript();
        transcript.Append(Seg("Thanks for joining today.", 3000, 5000));
        transcript.Append(Seg("Happy to be here.", 7000, 8000));

        var text = transcript.FileText("Interview A", Start, Start.AddSeconds(1904), 1904, showTimestamps: false);
        Assert.Contains("Session: Interview A", text);
        Assert.Contains("Duration: 00:31:44", text);
        Assert.Contains("Thanks for joining today.\nHappy to be here.", text);
        Assert.DoesNotContain("[00:00:03]", text);
    }

    [Fact]
    public void TimestampsWhenEnabled()
    {
        var transcript = new Transcript();
        transcript.Append(Seg("Hello there.", 3000, 5000));
        Assert.Equal("[00:00:03] Hello there.", transcript.Body(showTimestamps: true));
    }

    [Fact]
    public void SaveWritesTxtAndJsonSidecar()
    {
        var transcript = new Transcript();
        transcript.Append(Seg("One.", 1000, 2000));

        var result = TranscriptWriter.Save(transcript, _dir, "S", Start, Start.AddSeconds(60), 60, false);
        Assert.True(File.Exists(result.TxtPath));
        Assert.True(File.Exists(result.JsonPath));
        Assert.Equal(".txt", Path.GetExtension(result.TxtPath));

        using var sidecar = JsonDocument.Parse(File.ReadAllText(result.JsonPath));
        Assert.Equal(60, sidecar.RootElement.GetProperty("duration_seconds").GetInt32());
        Assert.Equal(1, sidecar.RootElement.GetProperty("segments").GetArrayLength());
        Assert.Equal("S", sidecar.RootElement.GetProperty("session_name").GetString());
    }

    [Fact]
    public void FilenameCollisionSuffix()
    {
        var transcript = new Transcript();
        transcript.Append(Seg("x", 0, 1));

        var first = TranscriptWriter.Save(transcript, _dir, "S", Start, Start, 0, false);
        var second = TranscriptWriter.Save(transcript, _dir, "S", Start, Start, 0, false);
        Assert.NotEqual(first.TxtPath, second.TxtPath);
        Assert.Contains(" (2)", Path.GetFileName(second.TxtPath));
    }

    [Fact]
    public void TranscriptFilesAreUtf8LfWithoutBom()
    {
        var transcript = new Transcript();
        transcript.Append(Seg("Über — naïve “quotes”.", 0, 1000));
        transcript.Append(Seg("Second line.", 1000, 2000));

        var result = TranscriptWriter.Save(transcript, _dir, "S", Start, Start.AddSeconds(2), 2, false);
        foreach (var path in new[] { result.TxtPath, result.JsonPath })
        {
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                         $"{Path.GetFileName(path)}: a BOM would break byte-parity on the first byte");
            Assert.DoesNotContain((byte)'\r', bytes);
        }
        // Non-ASCII must survive the round trip intact.
        Assert.Contains("Über — naïve “quotes”.", File.ReadAllText(result.TxtPath, Files.Utf8NoBom));
    }

    // ── SessionFiles ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void DeletesTxtAndJsonSidecar()
    {
        var txt = Path.Combine(_dir, "s.txt");
        var json = Path.Combine(_dir, "s.json");
        File.WriteAllText(txt, "t");
        File.WriteAllText(json, "{}");

        Assert.Equal(2, SessionFiles.DeleteTranscript(txt).Count);
        Assert.False(File.Exists(txt));
        Assert.False(File.Exists(json));
    }

    [Fact]
    public void MissingSidecarIsFine()
    {
        var txt = Path.Combine(_dir, "s.txt");
        File.WriteAllText(txt, "t");

        Assert.Single(SessionFiles.DeleteTranscript(txt));
        Assert.False(File.Exists(txt));
    }
}
