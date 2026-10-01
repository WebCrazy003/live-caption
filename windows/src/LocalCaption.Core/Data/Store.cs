using Microsoft.Data.Sqlite;

namespace LocalCaption.Core.Data;

/// <summary>A row in the <c>sessions</c> table (SPEC.md §12.3).</summary>
/// <remarks>
/// Metadata only. Transcript <i>text</i> never lives in SQLite — it is written to a
/// <c>.txt</c> file, and this row merely points at it.
/// </remarks>
public sealed record SessionRecord
{
    public long? Id { get; set; }
    public string SessionName { get; set; } = "";
    /// <summary>ISO-8601 UTC string.</summary>
    public string CreatedAt { get; set; } = "";
    public string? EndedAt { get; set; }
    public int DurationSeconds { get; set; }
    public string? TranscriptFile { get; set; }
    /// <summary><c>caption</c> | <c>interview</c> (specs/SPEC-11 §SQLite).</summary>
    public string Mode { get; set; } = "caption";
    public string? InterviewDir { get; set; }
}

/// <summary>Sort options for the session list (SPEC.md §10 / SPEC-06).</summary>
public enum SessionSort
{
    CreatedDesc, CreatedAsc,
    NameAsc, NameDesc,
    DurationDesc, DurationAsc,
}

/// <summary>
/// SQLite session-metadata store (SPEC.md §12.3, SPEC-WINDOWS.md §9.3). WAL mode, with the
/// DDL kept identical to the macOS <c>Store.swift</c> so a database file is readable by
/// either build.
/// </summary>
/// <remarks>
/// macOS uses GRDB's <c>DatabaseMigrator</c>, which keeps its own bookkeeping table. §9.3
/// specifies <c>PRAGMA user_version</c> here instead — the same versioning idea without the
/// dependency. Both converge on the same schema; <c>user_version</c> is the authority for
/// which migrations have run on Windows.
/// </remarks>
public sealed class Store : IDisposable
{
    private const int SchemaVersion = 2;

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

        Execute("PRAGMA journal_mode = WAL");
        Migrate();
    }

    private void Migrate()
    {
        var current = Convert.ToInt32(Scalar("PRAGMA user_version") ?? 0);
        if (current >= SchemaVersion) return;

        if (current < 1)
        {
            // Identical to GRDB's `autoIncrementedPrimaryKey` + column set on macOS.
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
        }

        if (current < 2)
        {
            // macOS migration `v2_interview` (specs/SPEC-11 §SQLite): same columns, same default.
            Execute("ALTER TABLE sessions ADD COLUMN mode TEXT NOT NULL DEFAULT 'caption'");
            Execute("ALTER TABLE sessions ADD COLUMN interview_dir TEXT");
        }

        Execute($"PRAGMA user_version = {SchemaVersion}");
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
    /// Delete the row only. Removing the transcript file is a separate, confirmed step
    /// (SPEC-06) — see <see cref="SessionFiles.DeleteTranscript"/>.
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
        command.CommandText = "UPDATE sessions SET mode = 'interview', interview_dir = $dir WHERE id = $id";
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

    public void Dispose() => _connection.Dispose();
}
