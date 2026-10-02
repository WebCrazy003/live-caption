using LocalCaption.Core;
using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;
using LocalCaption.Core.Transcripts;

namespace LocalCaption.Interview;

/// <summary>
/// What the app does to interviews at launch and after crash recovery (SPEC-15 §History,
/// SPEC-16 §5.5) — the port of <c>sweepInterviews</c> and <c>linkRecoveredInterview</c> in
/// macOS <c>AppEnvironment.swift</c>. Neither touches the answer engine, and neither throws:
/// every failure is logged (<see cref="InterviewLog"/>) and the remaining steps still run.
/// </summary>
public static class InterviewRecovery
{
    /// <summary>
    /// On launch: answers left <c>streaming</c> by a quit or crash become
    /// <c>failed("app closed")</c>, and running prep or summary jobs fail, so the UI never shows
    /// a spinner that can't finish; and the outbox's leftover screenshot files (from a turn cut
    /// off before it deleted them) are removed. Call once at startup, before any interview is
    /// shown. Never throws.
    /// </summary>
    /// <param name="store">The database.</param>
    /// <param name="outboxRoot">
    /// The outbox folder — <see cref="AppPaths.Outbox"/> in the app. Its files are deleted; the
    /// folder is kept. Required so a test can never sweep the real one.
    /// </param>
    /// <returns>How many interviews were changed.</returns>
    public static int SweepInterrupted(Store store, string outboxRoot)
    {
        ClearOutbox(outboxRoot);
        var changed = 0;
        foreach (var rec in Read(store.InterviewsInFlight))
        {
            if (!rec.FailInterruptedTurns()) continue;
            if (Try(() => store.SaveInterview(rec), $"sweeping interview {rec.Id}")) changed++;
        }
        return changed;
    }

    /// <summary>
    /// After a leftover journal was recovered into session <paramref name="sessionId"/>: link the
    /// interview recorded alongside that capture (same <c>capture_session_uuid</c>, no session
    /// yet) — set its session and <c>ended_at</c>, mark the session as an interview, and rename
    /// it "&lt;interviewee-company-step-date&gt; (recovered)" when the details name it. Never throws.
    /// </summary>
    /// <param name="store">The database.</param>
    /// <param name="captureId">The recovered journal's session id (<see cref="RecoveredSession.SessionId"/>).</param>
    /// <param name="sessionId">The row the recovery inserted; <c>null</c> does nothing.</param>
    /// <param name="start">When the capture started (names the session).</param>
    /// <returns>Whether an interview was linked.</returns>
    public static bool Link(Store store, Guid captureId, long? sessionId, DateTimeOffset start)
    {
        if (sessionId is not { } id) return false;
        var linked = false;
        // The Mac stores `uuidString` (upper-case); the query matches either case.
        foreach (var rec in Read(() => store.InterviewsByCapture(captureId.ToString("D"))))
        {
            rec.SessionId = id;
            rec.EndedAt ??= TimeFormat.Iso(DateTimeOffset.Now);
            // Each step on its own, as Swift's three `try?`: one failing doesn't skip the others.
            linked |= Try(() => store.SaveInterview(rec), $"linking interview {rec.Id} to session {id}");
            Try(() => store.MarkInterview(id), $"marking session {id} as an interview");
            Try(() =>
            {
                if (rec.SessionName(start) is { } name) store.Rename(id, name + " (recovered)");
            }, $"renaming session {id}");
        }
        return linked;
    }

    /// <summary>Delete every file in <paramref name="root"/> (PNGs and half-written <c>.tmp</c> files); a missing folder is fine.</summary>
    private static void ClearOutbox(string root)
    {
        string[] files;
        try { files = Directory.Exists(root) ? Directory.GetFiles(root) : []; }
        catch (Exception e)
        {
            InterviewLog.Write($"listing the outbox failed: {e.Message}");
            return;
        }
        foreach (var file in files) Try(() => File.Delete(file), $"deleting outbox file {Path.GetFileName(file)}");
    }

    private static bool Try(Action work, string what)
    {
        try
        {
            work();
            return true;
        }
        catch (Exception e)
        {
            InterviewLog.Write($"{what} failed: {e.Message}");
            return false;
        }
    }

    private static IReadOnlyList<InterviewRecord> Read(Func<IReadOnlyList<InterviewRecord>> query)
    {
        try { return query(); }
        catch (Exception e)
        {
            InterviewLog.Write($"reading interviews failed: {e.Message}");
            return [];
        }
    }
}
