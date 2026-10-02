using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;
using Microsoft.Data.Sqlite;

namespace LocalCaption.Core.Tests;

/// <summary>
/// The interview tables (owner, 2026-10-02: all interview session data in a local database) —
/// the port of the macOS <c>InterviewStoreTests</c>, without the legacy-folder import, which
/// Windows does not have (specs/SPEC-16 §3).
/// </summary>
public sealed class InterviewStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lc-istore-{Guid.NewGuid():N}");
    private readonly Store _store;

    public InterviewStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new Store(Path.Combine(_dir, "db.sqlite"));
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Json(InterviewRecord? r) => InterviewRecordTests.Json(r);

    private static InterviewRecord Sample(string? id = null) => new(
        "Senior iOS", "2026-10-02T09:00:00Z", "gpt-6-luna", "low",
        new InterviewSetup
        {
            SkillIds = ["s1"], DocumentIds = ["cv1"], JdTextInline = "JD text", Instructions = "Be brief.",
            AnswerLength = "short", CvTitle = "Jane CV", Candidate = "Jane", Company = "Acme", Step = 2,
        },
        id: id)
    {
        CvText = "Jane Doe\nSwift.",
        ThreadId = "thr1",
        CaptureSessionUuid = "cap-1",
        Turns =
        [
            new() { N = 1, Kind = InterviewTurnKind.Skill, Question = "/discovery-cv", Answer = "Profile…",
                    Status = InterviewTurnStatus.Completed, AskedAt = "t1" },
            new() { N = 2, Kind = InterviewTurnKind.Ask, Question = "why us", AudioFromMs = 0, AudioToMs = 5000,
                    Images = ["2-1.png", "2-2.png"], Answer = "Because…", Status = InterviewTurnStatus.Completed,
                    AskedAt = "t2", TtftMs = 1300, TotalMs = 3600 },
        ],
    };

    [Fact]
    public void RoundTripKeepsEverything()
    {
        var r = Sample();
        _store.SaveInterview(r);
        Assert.Equal(Json(r), Json(_store.Interview(r.Id)));

        r.Turns[1].Answer = "Edited";
        r.Turns.Add(new() { N = 3, Kind = InterviewTurnKind.Typed, Question = "shorter",
                            Status = InterviewTurnStatus.Streaming, AskedAt = "t3" });
        r.SummaryText = "## Overview\nGood.";
        r.Summary = new InterviewSummary { Status = InterviewStatus.Done, CompletedAt = "t4" };
        r.Transcript = "Thanks for joining. Why us?";
        r.EndedAt = "t5";
        _store.SaveInterview(r);
        Assert.Equal(Json(r), Json(_store.Interview(r.Id)));   // an update replaces the turns and keeps the rest
        Assert.Equal([r.Id], _store.AllInterviews().Select(i => i.Id));
    }

    [Fact]
    public void ScreenshotsAreStoredAsBytes()
    {
        var r = Sample();
        _store.SaveInterview(r);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        _store.AddInterviewImage(r.Id, Store.ImageName(2, 1), 2, png);
        Assert.Equal(png, _store.InterviewImage(r.Id, "2-1.png"));
        Assert.Null(_store.InterviewImage(r.Id, "missing.png"));
        _store.SaveInterview(r);   // saving the record again leaves screenshots alone
        Assert.Equal(1, _store.InterviewImageCount(r.Id));
    }

    [Fact]
    public void DeleteRemovesTurnsAndScreenshots()
    {
        var r = Sample();
        _store.SaveInterview(r);
        _store.AddInterviewImage(r.Id, "2-1.png", 2, [1]);
        _store.DeleteInterview(r.Id);
        Assert.Null(_store.Interview(r.Id));
        Assert.Equal(0, _store.InterviewImageCount(r.Id));
    }

    [Fact]
    public void SessionLinkAndMode()
    {
        var session = _store.Insert(new SessionRecord { SessionName = "Interview 1", CreatedAt = "2026-10-02T09:00:00Z" });
        var r = Sample();
        r.SessionId = session.Id;
        _store.SaveInterview(r);
        _store.MarkInterview(session.Id!.Value);
        Assert.Equal(r.Id, _store.Interview(session.Id.Value)?.Id);
        Assert.Equal("interview", _store.Fetch(session.Id.Value)?.Mode);
        _store.Delete(session.Id.Value);
        Assert.Null(_store.Interview(r.Id)?.SessionId);   // deleting the session keeps the interview, unlinked
    }

    // ── Windows-side extras: the mapping rules the Mac build relies on ─────────────────────

    [Fact]
    public void RowMappingMatchesTheMac()
    {
        var r = Sample();
        r.Setup.Company = "";
        r.Prep = new InterviewPrep { Status = InterviewStatus.Done, Briefing = "Legacy briefing" };
        _store.SaveInterview(r);

        using (var db = new SqliteConnection($"Data Source={Path.Combine(_dir, "db.sqlite")}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT skill_ids, cv_document_id, company, legacy_briefing, summary_status, answer_length
                FROM interviews WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", r.Id);
            using var row = command.ExecuteReader();
            Assert.True(row.Read());
            Assert.Equal("""["s1"]""", row.GetString(0));
            Assert.Equal("cv1", row.GetString(1));
            Assert.True(row.IsDBNull(2));                    // empty company → NULL
            Assert.Equal("Legacy briefing", row.GetString(3));
            Assert.Equal("pending", row.GetString(4));
            Assert.Equal("short", row.GetString(5));

            // A newer build's values read back forgivingly.
            using var update = db.CreateCommand();
            update.CommandText = """
                UPDATE interview_turns SET kind = 'dictated', status = 'paused', images = 'not json' WHERE n = 2;
                UPDATE interviews SET summary_status = 'queued' WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", r.Id);
            update.ExecuteNonQuery();
        }

        var back = _store.Interview(r.Id)!;
        Assert.Equal("", back.Setup.Company);
        Assert.Equal(InterviewStatus.Done, back.Prep.Status);       // legacy_briefing ↔ prep
        Assert.Equal("Legacy briefing", back.Prep.Briefing);
        Assert.Equal(InterviewStatus.Pending, back.Summary.Status);  // unknown summary status → pending
        Assert.Equal(InterviewTurnKind.Typed, back.Turns[1].Kind);     // unknown kind → typed
        Assert.Equal(InterviewTurnStatus.Failed, back.Turns[1].Status); // unknown status → failed
        Assert.Empty(back.Turns[1].Images);
    }

    [Fact]
    public void NewestFirstAndNewestPerSession()
    {
        var session = _store.Insert(new SessionRecord { SessionName = "S", CreatedAt = "2026-10-02T09:00:00Z" });
        var older = Sample();
        older.CreatedAt = "2026-10-01T09:00:00Z";
        older.SessionId = session.Id;
        var newer = Sample();
        newer.SessionId = session.Id;
        _store.SaveInterview(older);
        _store.SaveInterview(newer);
        Assert.Equal([newer.Id, older.Id], _store.AllInterviews().Select(i => i.Id));
        Assert.Equal(newer.Id, _store.Interview(session.Id!.Value)?.Id);
        Assert.Null(_store.Interview(session.Id.Value + 1));
    }

    [Fact]
    public void SaveIsOneTransaction()
    {
        var r = Sample();
        _store.SaveInterview(r);
        // A duplicate turn number violates the primary key half way through the turns.
        var broken = Sample(r.Id);
        broken.Name = "Changed";
        broken.Turns.Add(new() { N = 1, Kind = InterviewTurnKind.Ask, Question = "dup", AskedAt = "t" });
        Assert.Throws<SqliteException>(() => _store.SaveInterview(broken));
        Assert.Equal(Json(r), Json(_store.Interview(r.Id)));   // nothing of the failed save remains
    }
    [Fact]
    public void LatestInterviewIsTheNewest()
    {
        Assert.Null(_store.LatestInterview());
        var older = Sample();
        older.CreatedAt = "2026-10-01T09:00:00Z";
        var newer = Sample();
        var sameTimeLater = Sample();   // same created_at as `newer`, saved after it: rowid breaks the tie
        _store.SaveInterview(newer);
        _store.SaveInterview(older);
        _store.SaveInterview(sameTimeLater);
        Assert.Equal(_store.AllInterviews()[0].Id, _store.LatestInterview()?.Id);
        Assert.Equal(sameTimeLater.Id, _store.LatestInterview()?.Id);
        Assert.Equal(Json(sameTimeLater), Json(_store.LatestInterview()));   // turns included
    }

    [Fact]
    public void InterviewsByCaptureMatchAnyCaseAndOnlyUnlinked()
    {
        var capture = Guid.NewGuid();
        var mac = Sample();
        mac.CaptureSessionUuid = capture.ToString("D").ToUpperInvariant();   // as the Mac writes it
        var linked = Sample();
        linked.CaptureSessionUuid = mac.CaptureSessionUuid;
        linked.SessionId = _store.Insert(new SessionRecord { SessionName = "S", CreatedAt = "2026-10-02T09:00:00Z" }).Id;
        var other = Sample();
        other.CaptureSessionUuid = Guid.NewGuid().ToString("D");
        _store.SaveInterview(mac);
        _store.SaveInterview(linked);
        _store.SaveInterview(other);

        Assert.Equal([mac.Id], _store.InterviewsByCapture(capture.ToString("D")).Select(i => i.Id));   // lower-case query
        Assert.Equal([mac.Id], _store.InterviewsByCapture(capture.ToString("D").ToUpperInvariant()).Select(i => i.Id));
        Assert.Empty(_store.InterviewsByCapture(Guid.NewGuid().ToString("D")));
        Assert.Equal(Json(mac), Json(_store.InterviewsByCapture(capture.ToString("D"))[0]));
    }

    [Fact]
    public void InterviewsInFlightAreTheOnesWithAStreamingTurnOrARunningSummary()
    {
        var done = Sample();
        var streaming = Sample();
        streaming.Turns[^1].Status = InterviewTurnStatus.Streaming;
        var summarizing = Sample();
        summarizing.Summary.Status = InterviewStatus.Running;
        var failedSummary = Sample();
        failedSummary.Summary.Status = InterviewStatus.Failed;
        foreach (var r in new[] { done, streaming, summarizing, failedSummary }) _store.SaveInterview(r);

        Assert.Equal(new[] { streaming.Id, summarizing.Id }.Order(), _store.InterviewsInFlight().Select(i => i.Id).Order());
    }
}
