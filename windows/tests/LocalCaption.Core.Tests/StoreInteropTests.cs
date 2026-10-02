using LocalCaption.Core.Data;
using LocalCaption.Core.Transcripts;
using Microsoft.Data.Sqlite;

namespace LocalCaption.Core.Tests;

/// <summary>
/// One database file, both builds (specs/SPEC-16 §2.1–§2.3): a file written by the macOS app
/// opens here, a file written here carries the bookkeeping the macOS app expects, and the
/// caption text lives in <c>session_segments</c>.
/// </summary>
public sealed class StoreInteropTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lc-interop-{Guid.NewGuid():N}");
    private string DbPath => Path.Combine(_dir, "localcaption.db");

    public StoreInteropTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// <c>.schema</c> of a real macOS database after all four GRDB migrations (2026-10-02),
    /// verbatim — including GRDB's quoting and the columns v4 appended to <c>interviews</c>.
    /// </summary>
    private const string MacSchema = """
        CREATE TABLE grdb_migrations (identifier TEXT NOT NULL PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS "sessions" ("id" INTEGER PRIMARY KEY AUTOINCREMENT, "session_name" TEXT NOT NULL, "created_at" TEXT NOT NULL, "ended_at" TEXT, "duration_seconds" INTEGER NOT NULL DEFAULT 0, "transcript_file" TEXT, "mode" TEXT NOT NULL DEFAULT 'caption', "interview_dir" TEXT);
        CREATE INDEX "idx_sessions_created" ON "sessions"("created_at");
        CREATE TABLE interviews (
            id TEXT PRIMARY KEY,
            session_id INTEGER REFERENCES sessions(id) ON DELETE SET NULL,
            name TEXT NOT NULL,
            created_at TEXT NOT NULL,
            started_at TEXT,
            ended_at TEXT,
            capture_session_uuid TEXT,
            engine TEXT NOT NULL,
            model TEXT NOT NULL,
            reasoning_effort TEXT NOT NULL,
            thread_id TEXT,
            cv_document_id TEXT,
            cv_title TEXT,
            cv_text TEXT,
            jd_text TEXT,
            instructions TEXT NOT NULL DEFAULT '',
            answer_length TEXT NOT NULL DEFAULT 'medium',
            skill_ids TEXT NOT NULL DEFAULT '[]',
            legacy_briefing TEXT,
            summary_status TEXT NOT NULL DEFAULT 'pending',
            summary_text TEXT,
            summary_completed_at TEXT,
            transcript TEXT
        , candidate_name TEXT, company TEXT, interview_step INTEGER);
        CREATE INDEX idx_interviews_session ON interviews(session_id);
        CREATE INDEX idx_interviews_capture ON interviews(capture_session_uuid);
        CREATE TABLE interview_turns (
            interview_id TEXT NOT NULL REFERENCES interviews(id) ON DELETE CASCADE,
            n INTEGER NOT NULL,
            kind TEXT NOT NULL,
            question TEXT NOT NULL,
            answer TEXT NOT NULL DEFAULT '',
            status TEXT NOT NULL,
            error TEXT,
            audio_from_ms INTEGER,
            audio_to_ms INTEGER,
            images TEXT NOT NULL DEFAULT '[]',
            asked_at TEXT NOT NULL,
            ttft_ms INTEGER,
            total_ms INTEGER,
            PRIMARY KEY (interview_id, n)
        );
        CREATE TABLE interview_images (
            interview_id TEXT NOT NULL REFERENCES interviews(id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            turn_n INTEGER,
            png BLOB NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (interview_id, name)
        );
        CREATE TABLE session_segments (
            session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            n INTEGER NOT NULL,
            text TEXT NOT NULL,
            t_start_ms INTEGER NOT NULL,
            t_end_ms INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (session_id, n)
        );
        INSERT INTO grdb_migrations VALUES ('v1_sessions'), ('v2_interview'), ('v3_interview_store'),
                                           ('v4_segments_and_details');
        """;

    private static readonly string[] Tables =
        ["sessions", "interviews", "interview_turns", "interview_images", "session_segments"];

    private void Run(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<string> Column(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var values = new List<string>();
        while (r.Read()) values.Add(r.IsDBNull(0) ? "<null>" : Convert.ToString(r.GetValue(0))!);
        return values;
    }

    /// <summary>Every column of every shared table, as <c>table.name type notnull default pk</c>, sorted.</summary>
    private static List<string> Shape(string path) => Tables
        .SelectMany(t => Column(path, $"""
            SELECT '{t}.' || name || ' ' || upper(type) || ' ' || "notnull" || ' ' || ifnull(dflt_value, '-') || ' ' || pk
            FROM pragma_table_info('{t}')
            """))
        .Order(StringComparer.Ordinal)
        .ToList();

    private static SessionRecord Record(string name, string? file = null) => new()
    {
        SessionName = name, CreatedAt = "2026-10-01T09:00:00Z", EndedAt = "2026-10-01T09:30:00Z",
        DurationSeconds = 1800, TranscriptFile = file,
    };

    private static TranscriptSegment Seg(string text, int start) =>
        new() { Text = text, TStartMs = start, TEndMs = start + 900, CreatedAt = "2026-10-01T09:00:05Z" };

    [Fact]
    public void ANewDatabaseHasExactlyTheMacSchemaAndBookkeeping()
    {
        using (new Store(DbPath)) { }

        var mac = Path.Combine(_dir, "mac.db");
        Run(mac, MacSchema);

        Assert.Equal(Shape(mac), Shape(DbPath));
        Assert.Equal(Store.MigrationIds, Column(DbPath, "SELECT identifier FROM grdb_migrations ORDER BY identifier"));
    }

    [Fact]
    public void AMacDatabaseOpensWithItsSessionsAndCaptions()
    {
        Run(DbPath, MacSchema + """
            INSERT INTO sessions (session_name, created_at, duration_seconds, transcript_file)
            VALUES ('Mac call', '2026-10-01T08:00:00Z', 60, '/Users/someone/Library/x.txt');
            INSERT INTO session_segments VALUES (1, 1, 'hello', 0, 900, '2026-10-01T08:00:01Z'),
                                                (1, 2, 'world', 1000, 1900, '2026-10-01T08:00:02Z');
            """);

        using var store = new Store(DbPath);
        var mac = Assert.Single(store.All());
        Assert.Equal("Mac call", mac.SessionName);
        Assert.Equal(["hello", "world"], store.Segments(mac.Id!.Value).Select(s => s.Text));

        // A Mac path that does not exist here is an export we cannot see, not data to import.
        Assert.Equal(0, store.ImportTranscriptFiles());
        // Nothing was re-run or recorded twice.
        Assert.Equal(["4"], Column(DbPath, "SELECT COUNT(*) FROM grdb_migrations"));

        var added = store.Insert(Record("Windows call"), [Seg("hi", 0)]);
        Assert.Equal(1, store.SegmentCount(added.Id!.Value));
    }

    [Fact]
    public void AnOlderWindowsDatabaseIsCompletedAndGainsTheBookkeeping()
    {
        // What the previous Windows build wrote: v1–v3 by user_version only, no grdb table.
        Run(DbPath, """
            CREATE TABLE sessions (id INTEGER PRIMARY KEY AUTOINCREMENT, session_name TEXT NOT NULL,
              created_at TEXT NOT NULL, ended_at TEXT, duration_seconds INTEGER NOT NULL DEFAULT 0,
              transcript_file TEXT, mode TEXT NOT NULL DEFAULT 'caption', interview_dir TEXT);
            CREATE INDEX idx_sessions_created ON sessions(created_at);
            CREATE TABLE interviews (id TEXT PRIMARY KEY, session_id INTEGER REFERENCES sessions(id) ON DELETE SET NULL,
              name TEXT NOT NULL, created_at TEXT NOT NULL, started_at TEXT, ended_at TEXT, capture_session_uuid TEXT,
              engine TEXT NOT NULL, model TEXT NOT NULL, reasoning_effort TEXT NOT NULL, thread_id TEXT,
              cv_document_id TEXT, cv_title TEXT, cv_text TEXT, jd_text TEXT, instructions TEXT NOT NULL DEFAULT '',
              answer_length TEXT NOT NULL DEFAULT 'medium', skill_ids TEXT NOT NULL DEFAULT '[]', legacy_briefing TEXT,
              summary_status TEXT NOT NULL DEFAULT 'pending', summary_text TEXT, summary_completed_at TEXT, transcript TEXT);
            CREATE TABLE interview_turns (interview_id TEXT NOT NULL REFERENCES interviews(id) ON DELETE CASCADE,
              n INTEGER NOT NULL, kind TEXT NOT NULL, question TEXT NOT NULL, answer TEXT NOT NULL DEFAULT '',
              status TEXT NOT NULL, error TEXT, audio_from_ms INTEGER, audio_to_ms INTEGER,
              images TEXT NOT NULL DEFAULT '[]', asked_at TEXT NOT NULL, ttft_ms INTEGER, total_ms INTEGER,
              PRIMARY KEY (interview_id, n));
            CREATE TABLE interview_images (interview_id TEXT NOT NULL REFERENCES interviews(id) ON DELETE CASCADE,
              name TEXT NOT NULL, turn_n INTEGER, png BLOB NOT NULL, created_at TEXT NOT NULL,
              PRIMARY KEY (interview_id, name));
            INSERT INTO sessions (session_name, created_at) VALUES ('Old Windows call', '2026-09-20T10:00:00Z');
            PRAGMA user_version = 3;
            """);

        using (var store = new Store(DbPath))
            Assert.Equal("Old Windows call", Assert.Single(store.All()).SessionName);

        var mac = Path.Combine(_dir, "mac.db");
        Run(mac, MacSchema);
        Assert.Equal(Shape(mac), Shape(DbPath));
        Assert.Equal(Store.MigrationIds, Column(DbPath, "SELECT identifier FROM grdb_migrations ORDER BY identifier"));
        Assert.Equal(["4"], Column(DbPath, "PRAGMA user_version"));
    }

    [Fact]
    public void AnIdentifierFromANewerMacBuildIsLeftAlone()
    {
        Run(DbPath, MacSchema + "INSERT INTO grdb_migrations VALUES ('v5_something_newer');");
        using var store = new Store(DbPath);
        Assert.Equal(0, store.Count());
        Assert.Contains("v5_something_newer", Column(DbPath, "SELECT identifier FROM grdb_migrations"));
    }

    [Fact]
    public void SegmentsAreSavedInOrderAndDeletedWithTheirSession()
    {
        using var store = new Store(DbPath);
        var saved = store.Insert(Record("Call"), [Seg("one", 0), Seg("two", 1000), Seg("three", 2000)]);
        var id = saved.Id!.Value;

        var segments = store.Segments(id);
        Assert.Equal(["one", "two", "three"], segments.Select(s => s.Text));
        Assert.Equal([0, 1000, 2000], segments.Select(s => s.TStartMs));
        Assert.Equal(3, store.SegmentCount(id));

        store.Delete(id);
        Assert.Equal(0, store.SegmentCount(id));
    }

    [Fact]
    public void TranscriptFilesAreImportedOnceFromTheSidecarOrTheText()
    {
        var folder = Path.Combine(_dir, "transcripts");
        var start = DateTimeOffset.FromUnixTimeSeconds(1_759_309_200);
        var transcript = new Transcript([Seg("from sidecar", 4000)]);
        var withSidecar = TranscriptWriter.Save(transcript, folder, "A", start, start.AddMinutes(1), 60, false);

        var textOnly = Path.Combine(folder, "text-only.txt");
        File.WriteAllText(textOnly, "Session: B\nStart:   x\nEnd:     y\nDuration: 00:01:00\n\n[00:00:03] first line\nsecond line\n");

        using var store = new Store(DbPath);
        var a = store.Insert(Record("A", withSidecar.TxtPath));
        var b = store.Insert(Record("B", textOnly));
        store.Insert(Record("Gone", Path.Combine(folder, "missing.txt")));

        Assert.Equal(2, store.ImportTranscriptFiles());
        Assert.Equal(["from sidecar"], store.Segments(a.Id!.Value).Select(s => s.Text));
        Assert.Equal(4000, store.Segments(a.Id.Value)[0].TStartMs);

        var lines = store.Segments(b.Id!.Value);
        Assert.Equal(["first line", "second line"], lines.Select(s => s.Text));
        Assert.Equal([3000, 0], lines.Select(s => s.TStartMs));
        Assert.All(lines, s => Assert.Equal("2026-10-01T09:00:00Z", s.CreatedAt));

        Assert.Equal(0, store.ImportTranscriptFiles());
    }

    [Fact]
    public void AMissingExportIsRewrittenFromTheSavedCaptions()
    {
        var folder = Path.Combine(_dir, "transcripts");
        using var store = new Store(DbPath);
        var saved = store.Insert(Record("Mac call", "/Users/someone/Library/gone.txt"), [Seg("kept in the db", 2000)]);

        var path = SessionFiles.EnsureExport(store, saved, folder, showTimestamps: true);

        Assert.NotNull(path);
        Assert.StartsWith(folder, path);
        Assert.Contains("[00:00:02] kept in the db", File.ReadAllText(path));
        Assert.Equal(path, store.Fetch(saved.Id!.Value)!.TranscriptFile);
        // An existing export is returned as is, not rewritten.
        Assert.Equal(path, SessionFiles.EnsureExport(store, store.Fetch(saved.Id.Value)!, folder, true));

        var empty = store.Insert(Record("Nothing", null));
        Assert.Null(SessionFiles.EnsureExport(store, empty, folder, true));
    }
}
