using LocalCaption.Core.Transcripts;
using Microsoft.Data.Sqlite;

namespace LocalCaption.Core.Data;

/// <summary>A row in the <c>sessions</c> table (SPEC.md §12.3).</summary>
/// <remarks>
/// The caption text lives in <c>session_segments</c> (macOS <c>v4_segments_and_details</c>,
/// specs/SPEC-16 §2.3). <see cref="TranscriptFile"/> points at the <c>.txt</c> export, which
/// may be missing — or a path on the other platform — without anything being lost.
/// </remarks>
public sealed record SessionRecord
{
    public const string CaptionMode = "caption";
    public const string InterviewMode = "interview";

    public long? Id { get; set; }
    public string SessionName { get; set; } = "";
    /// <summary>ISO-8601 UTC string.</summary>
    public string CreatedAt { get; set; } = "";
    public string? EndedAt { get; set; }
    public int DurationSeconds { get; set; }
    public string? TranscriptFile { get; set; }
    /// <summary><c>caption</c> | <c>interview</c> (specs/SPEC-11 §SQLite).</summary>
    public string Mode { get; set; } = CaptionMode;
    public string? InterviewDir { get; set; }

    public bool IsInterview => Mode == InterviewMode;
}

/// <summary>Sort options for the session list (SPEC.md §10 / SPEC-06).</summary>
public enum SessionSort
{
    CreatedDesc, CreatedAsc,
    NameAsc, NameDesc,
    DurationDesc, DurationAsc,
}

/// <summary>
/// SQLite session store (SPEC.md §12.3, SPEC-WINDOWS.md §9.3). WAL mode, foreign keys on,
/// and the DDL identical to the macOS <c>Store.swift</c>.
/// </summary>
/// <remarks>
/// <para><b>One file, both builds (specs/SPEC-16 §2.1).</b> macOS migrates with GRDB's
/// <c>DatabaseMigrator</c>, which records each applied migration by name in
/// <c>grdb_migrations</c> and never touches <c>PRAGMA user_version</c>. This store keeps the
/// same bookkeeping under the same names, so a database written by either build opens in
/// the other: a migration runs here only if its identifier is missing, and every step probes
/// the schema first, so files from older Windows builds (which recorded progress in
/// <c>user_version</c> alone) are completed rather than re-created.</para>
/// <para><c>user_version</c> is still set, to the number of migrations, for anything that
/// reads it; it is no longer consulted.</para>
/// </remarks>
public sealed partial class Store : IDisposable
{
    /// <summary>The macOS migration identifiers, in order. Never rename one.</summary>
    public static readonly IReadOnlyList<string> MigrationIds =
        ["v1_sessions", "v2_interview", "v3_interview_store", "v4_segments_and_details"];

    private readonly SqliteConnection _connection;

    public Store(string? path = null)
    {
        path ??= AppPaths.DatabaseFile;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _connection.Open();
        Execute("PRAGMA foreign_keys = ON");

        Execute("PRAGMA journal_mode = WAL");
        Migrate();
    }

    private void Migrate()
    {
        Execute("CREATE TABLE IF NOT EXISTS grdb_migrations (identifier TEXT NOT NULL PRIMARY KEY)");
        var applied = new HashSet<string>(Strings("SELECT identifier FROM grdb_migrations"));

        // Identifiers from a newer macOS build are left alone: its tables are not ours to touch.
        foreach (var id in MigrationIds.Where(id => !applied.Contains(id)))
        {
            InTransaction(() =>
            {
                Apply(id);
                using var record = _connection.CreateCommand();
                record.CommandText = "INSERT INTO grdb_migrations (identifier) VALUES ($id)";
                record.Parameters.AddWithValue("$id", id);
                record.ExecuteNonQuery();
                return 0;
            });
        }

        Execute($"PRAGMA user_version = {MigrationIds.Count}");
    }

    private void Apply(string id)
    {
        switch (id)
        {
            case "v1_sessions":
                // GRDB's `autoIncrementedPrimaryKey` + the same column set.
                Execute("""
                    CREATE TABLE IF NOT EXISTS sessions (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_name TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        ended_at TEXT,
                        duration_seconds INTEGER NOT NULL DEFAULT 0,
                        transcript_file TEXT
                    )
                    """);
                Execute("CREATE INDEX IF NOT EXISTS idx_sessions_created ON sessions(created_at)");
                break;

            case "v2_interview":
                // specs/SPEC-11 §SQLite: existing rows become `caption`.
                AddColumn("sessions", "mode", "TEXT NOT NULL DEFAULT 'caption'");
                AddColumn("sessions", "interview_dir", "TEXT");
                break;

            case "v3_interview_store":
                // All interview data — record, turns, CV/JD/summary/transcript text, screenshots.
                Execute("""
                    CREATE TABLE IF NOT EXISTS interviews (
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
                    );
                    CREATE INDEX IF NOT EXISTS idx_interviews_session ON interviews(session_id);
                    CREATE INDEX IF NOT EXISTS idx_interviews_capture ON interviews(capture_session_uuid);
                    CREATE TABLE IF NOT EXISTS interview_turns (
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
                    CREATE TABLE IF NOT EXISTS interview_images (
                        interview_id TEXT NOT NULL REFERENCES interviews(id) ON DELETE CASCADE,
                        name TEXT NOT NULL,
                        turn_n INTEGER,
                        png BLOB NOT NULL,
                        created_at TEXT NOT NULL,
                        PRIMARY KEY (interview_id, name)
                    );
                    """);
                break;

            case "v4_segments_and_details":
                // Everything in the database: each session's caption segments, and the
                // interviewee, company and interview step. The .txt/.json stay as an export.
                Execute("""
                    CREATE TABLE IF NOT EXISTS session_segments (
                        session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                        n INTEGER NOT NULL,
                        text TEXT NOT NULL,
                        t_start_ms INTEGER NOT NULL,
                        t_end_ms INTEGER NOT NULL,
                        created_at TEXT NOT NULL,
                        PRIMARY KEY (session_id, n)
                    )
                    """);
                AddColumn("interviews", "candidate_name", "TEXT");
                AddColumn("interviews", "company", "TEXT");
                AddColumn("interviews", "interview_step", "INTEGER");
                break;
        }
    }

    private void AddColumn(string table, string column, string definition)
    {
        if (Strings($"SELECT name FROM pragma_table_info('{table}')").Contains(column)) return;
        Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Insert a session (called on Stop). Returns the record with its assigned id.</summary>
    public SessionRecord Insert(SessionRecord record)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (session_name, created_at, ended_at, duration_seconds, transcript_file,
                                  mode, interview_dir)
            VALUES ($name, $created, $ended, $duration, $file, $mode, $interviewDir)
            RETURNING id
            """;
        command.Parameters.AddWithValue("$name", record.SessionName);
        command.Parameters.AddWithValue("$created", record.CreatedAt);
        command.Parameters.AddWithValue("$ended", (object?)record.EndedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", record.DurationSeconds);
        command.Parameters.AddWithValue("$file", (object?)record.TranscriptFile ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", record.Mode);
        command.Parameters.AddWithValue("$interviewDir", (object?)record.InterviewDir ?? DBNull.Value);

        return record with { Id = Convert.ToInt64(command.ExecuteScalar()) };
    }

    /// <summary>
    /// Insert a session and its caption segments in one transaction — the save itself; the
    /// <c>.txt</c>/<c>.json</c> export comes after and is only an export.
    /// </summary>
    public SessionRecord Insert(SessionRecord record, IReadOnlyList<TranscriptSegment> segments) =>
        InTransaction(() =>
        {
            var saved = Insert(record);
            WriteSegments(segments, saved.Id!.Value);
            return saved;
        });

    /// <summary>A session's caption segments, in order.</summary>
    public IReadOnlyList<TranscriptSegment> Segments(long sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT text, t_start_ms, t_end_ms, created_at FROM session_segments
            WHERE session_id = $id ORDER BY n
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var segments = new List<TranscriptSegment>();
        while (reader.Read())
            segments.Add(new TranscriptSegment
            {
                Text = reader.GetString(0),
                TStartMs = reader.GetInt32(1),
                TEndMs = reader.GetInt32(2),
                CreatedAt = reader.GetString(3),
            });
        return segments;
    }

    /// <summary>The ids of every session that has at least one caption segment.</summary>
    public HashSet<long> SessionIdsWithSegments()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT session_id FROM session_segments";
        using var reader = command.ExecuteReader();
        var ids = new HashSet<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    public int SegmentCount(long sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session_segments WHERE session_id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Sessions saved before captions moved into the database keep them only in their
    /// transcript files: copy them in — the <c>.json</c> sidecar's segments, else the
    /// <c>.txt</c> body line by line. Files are left as they are. A row whose file is missing
    /// here (deleted, or a path from the other platform) or unreadable is skipped and tried
    /// again next time, as on macOS. Returns how many were imported.
    /// </summary>
    public int ImportTranscriptFiles()
    {
        var missing = new List<SessionRecord>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {Columns} FROM sessions WHERE transcript_file IS NOT NULL
                AND id NOT IN (SELECT DISTINCT session_id FROM session_segments)
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) missing.Add(Read(reader));
        }

        var imported = 0;
        foreach (var record in missing)
        {
            var segments = TranscriptFileReader.Segments(record.TranscriptFile!, record.CreatedAt);
            if (segments is not { Count: > 0 }) continue;
            try
            {
                InTransaction(() => { WriteSegments(segments, record.Id!.Value); return 0; });
                imported++;
            }
            catch (SqliteException) { }   // one bad row must not stop the rest
        }
        return imported;
    }

    /// <summary>Point a session at its exported <c>.txt</c>.</summary>
    public void SetTranscriptFile(long id, string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET transcript_file = $file WHERE id = $id";
        command.Parameters.AddWithValue("$file", path);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private void WriteSegments(IReadOnlyList<TranscriptSegment> segments, long sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO session_segments (session_id, n, text, t_start_ms, t_end_ms, created_at)
            VALUES ($id, $n, $text, $start, $end, $created)
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        var n = command.Parameters.Add("$n", SqliteType.Integer);
        var text = command.Parameters.Add("$text", SqliteType.Text);
        var start = command.Parameters.Add("$start", SqliteType.Integer);
        var end = command.Parameters.Add("$end", SqliteType.Integer);
        var created = command.Parameters.Add("$created", SqliteType.Text);
        command.Prepare();

        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            n.Value = i + 1;
            text.Value = s.Text;
            start.Value = s.TStartMs;
            end.Value = s.TEndMs;
            created.Value = s.CreatedAt;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Rename edits metadata only — it deliberately does NOT rename the transcript file
    /// (SPEC.md §10, C9), so a link from an older export keeps resolving.
    /// </summary>
    public void Rename(long id, string name)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET session_name = $name WHERE id = $id";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Delete the row and, by cascade, its caption segments. Removing the transcript file is
    /// a separate, confirmed step (SPEC-06) — see <see cref="SessionFiles.DeleteTranscript"/>.
    /// </summary>
    public void Delete(long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public SessionRecord? Fetch(long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>List sessions with an optional name search and sort (backs SPEC-06).</summary>
    public IReadOnlyList<SessionRecord> All(SessionSort sort = SessionSort.CreatedDesc, string? search = null,
                                            string? mode = null)
    {
        using var command = _connection.CreateCommand();
        var query = search?.Trim();
        var filters = new List<string>();
        if (!string.IsNullOrEmpty(query)) filters.Add("session_name LIKE $q");
        if (mode is not null) filters.Add("mode = $mode");
        var where = filters.Count == 0 ? "" : "WHERE " + string.Join(" AND ", filters) + " ";
        command.CommandText = $"SELECT {Columns} FROM sessions {where}ORDER BY {OrderBy(sort)}";
        if (!string.IsNullOrEmpty(query)) command.Parameters.AddWithValue("$q", $"%{query}%");
        if (mode is not null) command.Parameters.AddWithValue("$mode", mode);

        using var reader = command.ExecuteReader();
        var rows = new List<SessionRecord>();
        while (reader.Read()) rows.Add(Read(reader));
        return rows;
    }

    /// <summary>Mark a saved session as an interview and point it at its interview folder.</summary>
    public void SetInterview(long id, string dir)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET mode = $mode, interview_dir = $dir WHERE id = $id";
        command.Parameters.AddWithValue("$mode", SessionRecord.InterviewMode);
        command.Parameters.AddWithValue("$dir", dir);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public int Count() => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM sessions") ?? 0);

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────

    private const string Columns =
        "id, session_name, created_at, ended_at, duration_seconds, transcript_file, mode, interview_dir";

    private static string OrderBy(SessionSort sort) => sort switch
    {
        SessionSort.CreatedDesc => "created_at DESC",
        SessionSort.CreatedAsc => "created_at ASC",
        SessionSort.NameAsc => "session_name ASC",
        SessionSort.NameDesc => "session_name DESC",
        SessionSort.DurationDesc => "duration_seconds DESC",
        SessionSort.DurationAsc => "duration_seconds ASC",
        _ => "created_at DESC",
    };

    private static SessionRecord Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        SessionName = reader.GetString(1),
        CreatedAt = reader.GetString(2),
        EndedAt = reader.IsDBNull(3) ? null : reader.GetString(3),
        DurationSeconds = reader.GetInt32(4),
        TranscriptFile = reader.IsDBNull(5) ? null : reader.GetString(5),
        Mode = reader.GetString(6),
        InterviewDir = reader.IsDBNull(7) ? null : reader.GetString(7),
    };

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private List<string> Strings(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    /// <summary>
    /// Run <paramref name="work"/> atomically. Commands made with <c>CreateCommand</c> inside
    /// it join the connection's open transaction.
    /// </summary>
    internal T InTransaction<T>(Func<T> work)
    {
        using var transaction = _connection.BeginTransaction();
        var result = work();
        transaction.Commit();
        return result;
    }

    public void Dispose() => _connection.Dispose();
}
