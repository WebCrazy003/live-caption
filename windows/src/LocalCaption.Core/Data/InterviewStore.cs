using System.Text.Encodings.Web;
using System.Text.Json;
using LocalCaption.Core.Interview;
using LocalCaption.Core.Transcripts;
using Microsoft.Data.Sqlite;

namespace LocalCaption.Core.Data;

/// <summary>
/// The interview tables (owner, 2026-10-02: "a local DB storing all interview session data")
/// — the port of macOS's <c>InterviewStore.swift</c>. One row per interview, one per turn,
/// one per screenshot (PNG bytes), in the same <c>localcaption.db</c> as the sessions list.
/// </summary>
/// <remarks>
/// <para>The tables come from migrations <c>v3_interview_store</c> and
/// <c>v4_segments_and_details</c> in <c>Store.cs</c>; the mapping here is the Mac's, column for
/// column, so either build reads what the other wrote (specs/SPEC-16 §3): JSON-array text for
/// <c>skill_ids</c> and <c>images</c>, the first document id as <c>cv_document_id</c>, the
/// legacy prep briefing as <c>legacy_briefing</c>, and an empty company as NULL.</para>
/// <para>Reading is forgiving where a newer build could have written something this one does
/// not know: an unknown turn kind reads as <c>typed</c>, an unknown turn status as
/// <c>failed</c>, an unknown summary status as <c>pending</c>.</para>
/// </remarks>
public sealed partial class Store
{
    // ── Interviews ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Insert or update an interview and replace its turns, in one transaction. Screenshots
    /// are written separately (<see cref="AddInterviewImage"/>) and are not touched here.
    /// </summary>
    public void SaveInterview(InterviewRecord r) => InTransaction(() =>
    {
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO interviews (id, session_id, name, created_at, started_at, ended_at,
                    capture_session_uuid, engine, model, reasoning_effort, thread_id, cv_document_id,
                    cv_title, cv_text, jd_text, instructions, answer_length, skill_ids, legacy_briefing,
                    summary_status, summary_text, summary_completed_at, transcript,
                    candidate_name, company, interview_step)
                VALUES ($id, $session, $name, $created, $started, $ended,
                    $capture, $engine, $model, $effort, $thread, $cvDocument,
                    $cvTitle, $cvText, $jd, $instructions, $length, $skills, $briefing,
                    $summaryStatus, $summaryText, $summaryCompleted, $transcript,
                    $candidate, $company, $step)
                ON CONFLICT(id) DO UPDATE SET
                    session_id = excluded.session_id, name = excluded.name, started_at = excluded.started_at,
                    ended_at = excluded.ended_at, capture_session_uuid = excluded.capture_session_uuid,
                    engine = excluded.engine, model = excluded.model, reasoning_effort = excluded.reasoning_effort,
                    thread_id = excluded.thread_id, cv_document_id = excluded.cv_document_id,
                    cv_title = excluded.cv_title, cv_text = excluded.cv_text, jd_text = excluded.jd_text,
                    instructions = excluded.instructions, answer_length = excluded.answer_length,
                    skill_ids = excluded.skill_ids, legacy_briefing = excluded.legacy_briefing,
                    summary_status = excluded.summary_status, summary_text = excluded.summary_text,
                    summary_completed_at = excluded.summary_completed_at, transcript = excluded.transcript,
                    candidate_name = excluded.candidate_name, company = excluded.company,
                    interview_step = excluded.interview_step
                """;
            var p = command.Parameters;
            p.AddWithValue("$id", r.Id);
            p.AddWithValue("$session", Db(r.SessionId));
            p.AddWithValue("$name", r.Name);
            p.AddWithValue("$created", r.CreatedAt);
            p.AddWithValue("$started", Db(r.StartedAt));
            p.AddWithValue("$ended", Db(r.EndedAt));
            p.AddWithValue("$capture", Db(r.CaptureSessionUuid));
            p.AddWithValue("$engine", r.Engine);
            p.AddWithValue("$model", r.Model);
            p.AddWithValue("$effort", r.ReasoningEffort);
            p.AddWithValue("$thread", Db(r.ThreadId));
            p.AddWithValue("$cvDocument", Db(r.Setup.DocumentIds.FirstOrDefault()));
            p.AddWithValue("$cvTitle", Db(r.Setup.CvTitle));
            p.AddWithValue("$cvText", Db(r.CvText));
            p.AddWithValue("$jd", Db(r.Setup.JdTextInline));
            p.AddWithValue("$instructions", r.Setup.Instructions);
            p.AddWithValue("$length", r.Setup.AnswerLength);
            p.AddWithValue("$skills", JsonList(r.Setup.SkillIds));
            p.AddWithValue("$briefing", Db(r.Prep.Briefing.Length == 0 ? null : r.Prep.Briefing));
            p.AddWithValue("$summaryStatus", InterviewRecord.Raw(r.Summary.Status));
            p.AddWithValue("$summaryText", Db(r.SummaryText));
            p.AddWithValue("$summaryCompleted", Db(r.Summary.CompletedAt));
            p.AddWithValue("$transcript", Db(r.Transcript));
            p.AddWithValue("$candidate", Db(r.Setup.Candidate));
            p.AddWithValue("$company", Db(r.Setup.Company.Length == 0 ? null : r.Setup.Company));
            p.AddWithValue("$step", Db(r.Setup.Step));
            command.ExecuteNonQuery();
        }

        using (var delete = _connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM interview_turns WHERE interview_id = $id";
            delete.Parameters.AddWithValue("$id", r.Id);
            delete.ExecuteNonQuery();
        }

        using var insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO interview_turns (interview_id, n, kind, question, answer, status, error,
                audio_from_ms, audio_to_ms, images, asked_at, ttft_ms, total_ms)
            VALUES ($id, $n, $kind, $question, $answer, $status, $error,
                $from, $to, $images, $asked, $ttft, $total)
            """;
        foreach (var t in r.Turns)
        {
            insert.Parameters.Clear();
            var p = insert.Parameters;
            p.AddWithValue("$id", r.Id);
            p.AddWithValue("$n", t.N);
            p.AddWithValue("$kind", InterviewRecord.Raw(t.Kind));
            p.AddWithValue("$question", t.Question);
            p.AddWithValue("$answer", t.Answer);
            p.AddWithValue("$status", InterviewRecord.Raw(t.Status));
            p.AddWithValue("$error", Db(t.Error));
            p.AddWithValue("$from", Db(t.AudioFromMs));
            p.AddWithValue("$to", Db(t.AudioToMs));
            p.AddWithValue("$images", JsonList(t.Images));
            p.AddWithValue("$asked", t.AskedAt);
            p.AddWithValue("$ttft", Db(t.TtftMs));
            p.AddWithValue("$total", Db(t.TotalMs));
            insert.ExecuteNonQuery();
        }
    });

    public InterviewRecord? Interview(string id) =>
        Interviews("SELECT * FROM interviews WHERE id = $key", id).FirstOrDefault();

    /// <summary>The interview linked to a saved session (the newest, should there be several).</summary>
    public InterviewRecord? Interview(long sessionId) =>
        Interviews("SELECT * FROM interviews WHERE session_id = $key ORDER BY created_at DESC LIMIT 1", sessionId)
            .FirstOrDefault();

    /// <summary>
    /// Every interview linked to <paramref name="sessionId"/>, newest first — what "Delete …
    /// Interview Data" must remove (<see cref="Interview(long)"/> returns only the newest).
    /// </summary>
    public IReadOnlyList<InterviewRecord> Interviews(long sessionId) =>
        Interviews("SELECT * FROM interviews WHERE session_id = $key ORDER BY created_at DESC, rowid DESC", sessionId);

    /// <summary>
    /// The columns a sessions list needs from every linked interview — no turns, no CV text —
    /// newest first, so the first listing per session is the one <see cref="Interview(long)"/>
    /// would return. One query, where <see cref="AllInterviews"/> reads every turn of every interview.
    /// </summary>
    public IReadOnlyList<InterviewListing> InterviewListings()
    {
        var listings = new List<InterviewListing>();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, candidate_name, company, interview_step, name, cv_title, jd_text
            FROM interviews WHERE session_id IS NOT NULL
            ORDER BY created_at DESC, rowid DESC
            """;
        using var row = command.ExecuteReader();
        while (row.Read())
            listings.Add(new InterviewListing(
                SessionId: row.GetInt64(0),
                Candidate: row.IsDBNull(1) ? null : row.GetString(1),
                Company: row.IsDBNull(2) ? "" : row.GetString(2),
                Step: row.IsDBNull(3) ? null : row.GetInt32(3),
                Name: row.GetString(4),
                CvTitle: row.IsDBNull(5) ? null : row.GetString(5),
                JdText: row.IsDBNull(6) ? null : row.GetString(6)));
        return listings;
    }

    /// <summary>Every interview, newest first.</summary>
    public IReadOnlyList<InterviewRecord> AllInterviews() =>
        Interviews("SELECT * FROM interviews ORDER BY created_at DESC, rowid DESC", null);

    /// <summary>The newest interview (the first of <see cref="AllInterviews"/>), or null when there is none.</summary>
    public InterviewRecord? LatestInterview() =>
        Interviews("SELECT * FROM interviews ORDER BY created_at DESC, rowid DESC LIMIT 1", null).FirstOrDefault();

    /// <summary>
    /// The interviews recorded alongside capture <paramref name="captureSessionUuid"/> that no
    /// saved session claims yet (<c>session_id IS NULL</c>), newest first. The uuid matches
    /// whatever its case: the Mac writes <c>uuidString</c> upper-case.
    /// </summary>
    public IReadOnlyList<InterviewRecord> InterviewsByCapture(string captureSessionUuid) =>
        Interviews("""
            SELECT * FROM interviews
            WHERE capture_session_uuid = $key COLLATE NOCASE AND session_id IS NULL
            ORDER BY created_at DESC, rowid DESC
            """, captureSessionUuid);

    /// <summary>
    /// The interviews a quit or crash may have cut off: a turn still <c>streaming</c>, or a
    /// summary still <c>running</c> — what the launch sweep fails. (A running prep is not
    /// stored: its status is not a column.)
    /// </summary>
    public IReadOnlyList<InterviewRecord> InterviewsInFlight() =>
        Interviews($"""
            SELECT * FROM interviews
            WHERE summary_status = '{InterviewRecord.Raw(InterviewStatus.Running)}'
               OR id IN (SELECT interview_id FROM interview_turns
                         WHERE status = '{InterviewRecord.Raw(InterviewTurnStatus.Streaming)}')
            ORDER BY created_at DESC, rowid DESC
            """, null);

    /// <summary>Deletes the interview and, by cascade, its turns and its screenshots.</summary>
    public void DeleteInterview(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM interviews WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Mark a saved session as an interview (the link itself is <c>interviews.session_id</c>).</summary>
    public void MarkInterview(long sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET mode = $mode WHERE id = $id";
        command.Parameters.AddWithValue("$mode", SessionRecord.InterviewMode);
        command.Parameters.AddWithValue("$id", sessionId);
        command.ExecuteNonQuery();
    }

    // ── Screenshots ──────────────────────────────────────────────────────────────────────

    /// <summary>Store (or replace) one screenshot's PNG bytes under its name.</summary>
    public void AddInterviewImage(string interviewId, string name, int? turn, byte[] png)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO interview_images (interview_id, name, turn_n, png, created_at)
            VALUES ($id, $name, $turn, $png, $created)
            """;
        command.Parameters.AddWithValue("$id", interviewId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$turn", Db(turn));
        command.Parameters.Add("$png", SqliteType.Blob).Value = png;
        command.Parameters.AddWithValue("$created", TimeFormat.Iso(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    public byte[]? InterviewImage(string interviewId, string name)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT png FROM interview_images WHERE interview_id = $id AND name = $name";
        command.Parameters.AddWithValue("$id", interviewId);
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() as byte[];
    }

    public int InterviewImageCount(string interviewId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM interview_images WHERE interview_id = $id";
        command.Parameters.AddWithValue("$id", interviewId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary><c>&lt;turn&gt;-&lt;index&gt;.png</c> — the name a screenshot has in <c>interview_images</c>.</summary>
    public static string ImageName(int turn, int index) => $"{turn}-{index}.png";

    // ── Mapping ──────────────────────────────────────────────────────────────────────────

    /// <summary>Compact, with non-ASCII left as UTF-8. (Swift also escapes <c>/</c>; ids and image names have none.)</summary>
    private static readonly JsonSerializerOptions ListOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string JsonList(List<string> list) => JsonSerializer.Serialize(list, ListOptions);

    /// <summary>A JSON array of strings, or empty for anything else — a null element included (as on macOS).</summary>
    private static List<string> ParseList(string? text)
    {
        if (text is null) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<string?>>(text);
            return list is null || list.Contains(null) ? [] : [.. list.OfType<string>()];
        }
        catch (JsonException) { return []; }
    }

    private static object Db(object? value) => value ?? DBNull.Value;

    private List<InterviewRecord> Interviews(string sql, object? key)
    {
        var records = new List<InterviewRecord>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = sql;
            if (key is not null) command.Parameters.AddWithValue("$key", key);
            using var reader = command.ExecuteReader();
            while (reader.Read()) records.Add(ReadInterview(reader));
        }
        foreach (var r in records) r.Turns = ReadTurns(r.Id);
        return records;
    }

    private static InterviewRecord ReadInterview(SqliteDataReader row)
    {
        var cvDocument = Text(row, "cv_document_id");
        var briefing = Text(row, "legacy_briefing");
        return new InterviewRecord(
            name: row.GetString(row.GetOrdinal("name")),
            createdAt: row.GetString(row.GetOrdinal("created_at")),
            model: row.GetString(row.GetOrdinal("model")),
            reasoningEffort: row.GetString(row.GetOrdinal("reasoning_effort")),
            setup: new InterviewSetup
            {
                SkillIds = ParseList(Text(row, "skill_ids")),
                DocumentIds = cvDocument is null ? [] : [cvDocument],
                JdTextInline = Text(row, "jd_text"),
                Instructions = row.GetString(row.GetOrdinal("instructions")),
                AnswerLength = row.GetString(row.GetOrdinal("answer_length")),
                CvTitle = Text(row, "cv_title"),
                Candidate = Text(row, "candidate_name"),
                Company = Text(row, "company") ?? "",
                Step = Int(row, "interview_step"),
            },
            id: row.GetString(row.GetOrdinal("id")),
            engine: row.GetString(row.GetOrdinal("engine")))
        {
            SessionId = Long(row, "session_id"),
            StartedAt = Text(row, "started_at"),
            EndedAt = Text(row, "ended_at"),
            CaptureSessionUuid = Text(row, "capture_session_uuid"),
            ThreadId = Text(row, "thread_id"),
            CvText = Text(row, "cv_text"),
            SummaryText = Text(row, "summary_text"),
            Transcript = Text(row, "transcript"),
            Prep = briefing is null ? new InterviewPrep()
                                    : new InterviewPrep { Status = InterviewStatus.Done, Briefing = briefing },
            Summary = new InterviewSummary
            {
                Status = InterviewRecord.Parse<InterviewStatus>(Text(row, "summary_status")) ?? InterviewStatus.Pending,
                CompletedAt = Text(row, "summary_completed_at"),
            },
        };
    }

    private List<InterviewTurn> ReadTurns(string interviewId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM interview_turns WHERE interview_id = $id ORDER BY n";
        command.Parameters.AddWithValue("$id", interviewId);
        using var t = command.ExecuteReader();
        var turns = new List<InterviewTurn>();
        while (t.Read())
            turns.Add(new InterviewTurn
            {
                N = t.GetInt32(t.GetOrdinal("n")),
                Kind = InterviewRecord.Parse<InterviewTurnKind>(Text(t, "kind")) ?? InterviewTurnKind.Typed,
                Question = t.GetString(t.GetOrdinal("question")),
                AudioFromMs = Int(t, "audio_from_ms"),
                AudioToMs = Int(t, "audio_to_ms"),
                Images = ParseList(Text(t, "images")),
                Answer = t.GetString(t.GetOrdinal("answer")),
                Status = InterviewRecord.Parse<InterviewTurnStatus>(Text(t, "status")) ?? InterviewTurnStatus.Failed,
                Error = Text(t, "error"),
                AskedAt = t.GetString(t.GetOrdinal("asked_at")),
                TtftMs = Int(t, "ttft_ms"),
                TotalMs = Int(t, "total_ms"),
            });
        return turns;
    }

    private static string? Text(SqliteDataReader row, string column)
    {
        var i = row.GetOrdinal(column);
        return row.IsDBNull(i) ? null : row.GetString(i);
    }

    private static long? Long(SqliteDataReader row, string column)
    {
        var i = row.GetOrdinal(column);
        return row.IsDBNull(i) ? null : row.GetInt64(i);
    }

    private static int? Int(SqliteDataReader row, string column)
    {
        var i = row.GetOrdinal(column);
        return row.IsDBNull(i) ? null : row.GetInt32(i);
    }
}

/// <summary>
/// One interview as a sessions list shows and searches it (<see cref="Store.InterviewListings"/>):
/// the linked session, "interviewee · company · step N", and the text the search looks in.
/// </summary>
/// <param name="SessionId">The saved session it is linked to.</param>
/// <param name="Candidate">The interviewee, if entered.</param>
/// <param name="Company">The company; empty when none (stored as NULL).</param>
/// <param name="Step">Which round, if entered.</param>
/// <param name="Name">The interview's name (the JD's first line, else "Interview").</param>
/// <param name="CvTitle">The CV's title as snapshotted.</param>
/// <param name="JdText">The pasted job description.</param>
public sealed record InterviewListing(long SessionId, string? Candidate, string Company, int? Step,
                                      string Name, string? CvTitle, string? JdText);
