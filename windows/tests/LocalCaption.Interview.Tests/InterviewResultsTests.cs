using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;
using LocalCaption.Core.Transcripts;

namespace LocalCaption.Interview.Tests;

/// <summary>
/// SPEC-15: ending an interview, the summary on the same thread, history reopen, the launch
/// sweep and crash-recovery linking. Port of <c>InterviewResultsTests.swift</c>.
/// </summary>
public sealed class InterviewResultsTests : InterviewTestBase
{
    private const string SummaryMarkdown =
        "## Overview\nGood.\n## Questions asked\n- Why us\n## Follow-ups\n-\n## Prepare next time\n-\n## Thank-you note\nThanks!";

    /// <summary>Prepare, start recording, ask once — ready for Stop.</summary>
    private async Task<(InterviewController Interview, long RowId)> RunInterview()
    {
        var interview = NewController();
        interview.TranscriptSource = () => ([], "why us", 3000);
        interview.RecordingStarted(Guid.NewGuid(), DateTimeOffset.Now);
        await interview.EnsureThreadAsync();
        Engine.Reply = _ => [new AnswerEvent.Completed("**Q:** Why us?\nBecause.")];
        await interview.AskAsync();
        var row = Store.Insert(new SessionRecord { SessionName = "Interview 1", CreatedAt = TimeFormat.Iso(DateTimeOffset.Now) });
        return (interview, row.Id!.Value);
    }

    [Fact]
    public Task EndLinksTheSessionAndSummarizesOnlyWhenAsked() => OnUi(async () =>
    {
        var (interview, rowId) = await RunInterview();
        Engine.Reply = text => text.StartsWith("INTERVIEW FINISHED", StringComparison.Ordinal)
            ? [new AnswerEvent.Completed(SummaryMarkdown)] : [];
        var sentBefore = Engine.SentTurns.Count;
        var sessionsChanged = 0;
        interview.SessionsChanged += (_, _) => sessionsChanged++;

        await interview.SessionSavedAsync(rowId, "Thanks for joining. Why us?");
        Assert.Equal(sentBefore, Engine.SentTurns.Count);   // ending never summarizes on its own
        Assert.Equal(InterviewStatus.Pending, interview.Record?.Summary.Status);
        Assert.Equal(1, sessionsChanged);

        await interview.GenerateSummaryAsync("Thanks for joining. Why us?");

        var rec = Saved(interview);
        Assert.Equal(SummaryMarkdown, rec.SummaryText);   // the summary is stored in the database
        Assert.Equal("Thanks for joining. Why us?", rec.Transcript);   // and the transcript
        Assert.Equal(rowId, rec.SessionId);
        Assert.NotNull(rec.EndedAt);
        Assert.Equal(InterviewStatus.Done, rec.Summary.Status);
        Assert.NotNull(rec.Summary.CompletedAt);
        Assert.Equal(rec.Id, Store.Interview(rowId)?.Id);

        var summaryTurn = Engine.SentTurns[^1];
        Assert.Equal("thr1", summaryTurn.ThreadId);   // prep, asks and summary share one thread
        Assert.Equal([new CodexRpc.Input.Text(InterviewPrompt.Summary("Thanks for joining. Why us?"))], summaryTurn.Input);
        Assert.Equal("medium", summaryTurn.Effort);   // summary uses prep_reasoning_effort

        Assert.Equal("interview", Store.Fetch(rowId)?.Mode);
        Assert.Equal(SummaryMarkdown, interview.SummaryText);
        Assert.True(interview.IsFinished);
        Assert.False(interview.Summarizing);
    });

    [Fact]
    public Task FollowUpAfterEndGoesToTheSameThread() => OnUi(async () =>
    {
        var (interview, rowId) = await RunInterview();
        await interview.SessionSavedAsync(rowId, "x");
        Engine.Reply = _ => [new AnswerEvent.Completed("Dear Alex, thank you…")];
        await interview.SendFollowUpAsync("Draft a thank-you email");
        Assert.Equal("thr1", Engine.SentTurns[^1].ThreadId);
        Assert.Equal(InterviewTurnKind.Typed, interview.Turns[^1].Kind);
        Assert.Equal("Dear Alex, thank you…", interview.Turns[^1].Answer);
        Assert.True(interview.IsFinished);
    });

    [Fact]
    public Task FailedSummaryCanBeGeneratedLater() => OnUi(async () =>
    {
        var (interview, rowId) = await RunInterview();
        Engine.Reply = _ => [new AnswerEvent.Failed(new EngineError.Network("offline"), "")];
        await interview.SessionSavedAsync(rowId, "x");
        await interview.GenerateSummaryAsync("x");
        Assert.Equal(InterviewStatus.Failed, interview.Record?.Summary.Status);
        Assert.Equal("Network problem: offline", interview.SummaryError);
        Assert.Equal(InterviewStatus.Failed, Saved(interview).Summary.Status);

        Engine.Reply = _ => [new AnswerEvent.Completed(SummaryMarkdown)];
        await interview.GenerateSummaryAsync("x");
        Assert.Equal(InterviewStatus.Done, interview.Record?.Summary.Status);
        Assert.Null(interview.SummaryError);
    });

    [Fact]
    public Task AnInterruptedSummaryFails() => OnUi(async () =>
    {
        var (interview, rowId) = await RunInterview();
        await interview.SessionSavedAsync(rowId, "x");
        Engine.Reply = _ => [new AnswerEvent.Delta("## Over"), new AnswerEvent.Interrupted("## Over")];
        await interview.GenerateSummaryAsync("x");
        Assert.Equal("The summary was interrupted.", interview.SummaryError);
        Assert.Equal(InterviewStatus.Failed, Saved(interview).Summary.Status);
    });

    [Fact]
    public Task ReopeningFromHistoryResumesTheThreadBeforeSummarizing() => OnUi(async () =>
    {
        var (live, rowId) = await RunInterview();
        await live.SessionSavedAsync(rowId, "x");
        var stored = Saved(live);

        var reopened = NewController(stored);
        Assert.Equal(new InterviewThreadState.Open(), reopened.ThreadState);
        Assert.Single(reopened.Record!.Turns);
        Assert.True(reopened.IsFinished);

        Engine.Reply = _ => [new AnswerEvent.Completed(SummaryMarkdown)];
        await reopened.GenerateSummaryAsync("x");
        Assert.Equal(["thr1"], Engine.Resumed);   // a reopened thread is resumed (lockdown re-applied) first
        Assert.Equal(InterviewStatus.Done, reopened.Record?.Summary.Status);

        // And the next reopen shows the saved summary without asking Codex.
        var again = NewController(Saved(reopened));
        Assert.Equal(SummaryMarkdown, again.SummaryText);
    });

    [Fact]
    public Task LaunchSweepFailsCutOffTurns() => OnUi(async () =>
    {
        var interview = NewController();
        await interview.EnsureThreadAsync();
        Engine.HoldIf = _ => true;
        var turn = interview.SendTypedAsync("left hanging");
        await WaitUntil(() => interview.IsStreaming);
        Assert.Equal(InterviewTurnStatus.Streaming, Saved(interview).Turns[^1].Status);

        Assert.Equal(1, InterviewRecovery.SweepInterrupted(Store, Outbox));   // as on the next launch
        var rec = Saved(interview);
        Assert.Equal(InterviewTurnStatus.Failed, rec.Turns[^1].Status);
        Assert.Equal("app closed", rec.Turns[^1].Error);
        Assert.Equal(0, InterviewRecovery.SweepInterrupted(Store, Outbox));   // nothing left to sweep
        Engine.Release();
        await turn;
    });

    [Fact]
    public void LaunchSweepFailsARunningSummary()
    {
        var rec = new InterviewRecord("Interview", TimeFormat.Iso(DateTimeOffset.Now), "gpt-6-luna", "low", new InterviewSetup());
        rec.Summary.Status = InterviewStatus.Running;
        Store.SaveInterview(rec);
        Assert.Equal(1, InterviewRecovery.SweepInterrupted(Store, Outbox));
        Assert.Equal(InterviewStatus.Failed, Store.Interview(rec.Id)?.Summary.Status);
    }

    [Fact]
    public Task RecoveredCaptureIsLinkedToItsInterview() => OnUi(async () =>
    {
        var interview = NewController();
        interview.Draft = interview.Draft with { Candidate = "victor", Company = "Peloton", Step = 2 };
        var capture = Guid.NewGuid();
        var start = DateTimeOffset.Now;
        interview.RecordingStarted(capture, start);
        await interview.EnsureThreadAsync();
        var row = Store.Insert(new SessionRecord { SessionName = "Recovered", CreatedAt = TimeFormat.Iso(start) });

        Assert.True(InterviewRecovery.Link(Store, capture, row.Id, start));

        var rec = Saved(interview);
        Assert.Equal(row.Id, rec.SessionId);
        Assert.NotNull(rec.EndedAt);
        var session = Store.Fetch(row.Id!.Value);
        Assert.Equal("interview", session?.Mode);
        Assert.Equal($"victor-Peloton-2-{TimeFormat.Day(start)} (recovered)", session?.SessionName);

        // Linked once: a second recovery of the same capture leaves it alone.
        var other = Store.Insert(new SessionRecord { SessionName = "Again", CreatedAt = TimeFormat.Iso(start) });
        Assert.False(InterviewRecovery.Link(Store, capture, other.Id, start));
        Assert.False(InterviewRecovery.Link(Store, Guid.NewGuid(), null, start));
    });

    [Fact]
    public void ARecordWrittenByTheMacLinksWhateverTheCase()
    {
        var capture = Guid.NewGuid();
        var rec = new InterviewRecord("Interview", TimeFormat.Iso(DateTimeOffset.Now), "gpt-6-luna", "low", new InterviewSetup())
        {
            CaptureSessionUuid = capture.ToString("D").ToUpperInvariant(),
        };
        Store.SaveInterview(rec);
        var row = Store.Insert(new SessionRecord { SessionName = "Recovered", CreatedAt = TimeFormat.Iso(DateTimeOffset.Now) });
        Assert.True(InterviewRecovery.Link(Store, capture, row.Id, DateTimeOffset.Now));
        Assert.Equal("Recovered", Store.Fetch(row.Id!.Value)?.SessionName);   // no details → name kept
    }

    // ── Sessions window → Open in interview panel (owner, 2026-10-02) ────────────────────

    /// <summary>
    /// The controller half of Swift's <c>testOpenInPanelShowsTheSavedInterviewAndContinuesItsThread</c>:
    /// <c>Load</c> shows the saved interview as right after End interview, and the next turn
    /// resumes its thread first.
    /// </summary>
    [Fact]
    public Task LoadShowsTheSavedInterviewAndContinuesItsThread() => OnUi(async () =>
    {
        var (finished, rowId) = await RunInterview();
        await finished.SessionSavedAsync(rowId, "Thanks for joining. Why us?");

        var panel = NewController();
        panel.ShowingPreparation = true;
        panel.Load(Store.Interview(rowId)!);
        Assert.Equal(finished.Record!.Id, panel.Record?.Id);
        Assert.True(panel.IsFinished);
        Assert.False(panel.ShowingPreparation);
        Assert.Equal(new InterviewThreadState.Open(), panel.ThreadState);
        Assert.False(panel.HasUnstartedPreparation);

        Engine.Reply = _ => [new AnswerEvent.Completed("Sure.")];
        await panel.SendFollowUpAsync("Draft a thank-you email");
        Assert.Equal([finished.Record.ThreadId!], Engine.Resumed);   // the saved thread is resumed first
        Assert.Equal(InterviewTurnStatus.Completed, panel.Turns[^1].Status);
        Assert.Single(Engine.Threads);   // no new thread
    });

    [Fact]
    public Task InterviewDetailsAreStoredAndNameTheSession() => OnUi(async () =>
    {
        var interview = NewController();
        interview.Draft = interview.Draft with { Candidate = " victor ", Company = "Capital on Tap", Step = 3 };
        await interview.EnsureThreadAsync();
        var day = TimeFormat.Day(DateTimeOffset.Now);
        Assert.Equal($"victor-Capital on Tap-3-{day}", interview.SessionName(DateTimeOffset.Now));
        var rec = Saved(interview);
        Assert.Equal("victor", rec.Setup.Candidate);
        Assert.Equal("Capital on Tap", rec.Setup.Company);
        Assert.Equal(3, rec.Setup.Step);

        interview.Draft = interview.Draft with { Step = 4 };   // corrected in the form after the record exists
        interview.DetailsChanged();
        Assert.Equal(4, Saved(interview).Setup.Step);
    });

    // ── End interview waits for a streaming answer ───────────────────────────────────────

    [Fact]
    public Task EndLetsAStreamingAnswerFinish() => OnUi(async () =>
    {
        var (interview, rowId) = await RunInterview();
        Engine.HoldIf = _ => true;
        var turn = interview.SendTypedAsync("one more");
        await WaitUntil(() => interview.IsStreaming);

        var end = interview.SessionSavedAsync(rowId, "x");
        Assert.False(end.IsCompleted);   // waiting for the answer…
        Engine.Release("Finished.");
        await end;
        await turn;
        Assert.Equal(0, Engine.Interrupts);
        Assert.Equal(InterviewTurnStatus.Completed, interview.Turns[^1].Status);
    });

    [Fact]
    public Task EndInterruptsAnAnswerThatTakesTooLongAndDropsTheQueue() => OnUi(async () =>
    {
        Options = new InterviewControllerOptions
        {
            EndAnswerWait = TimeSpan.FromMilliseconds(150), EndInterruptGrace = TimeSpan.FromMilliseconds(150),
        };
        Config.BusyPolicy = "queue";
        var (interview, rowId) = await RunInterview();
        Engine.HoldIf = t => t.Contains("one more");
        var turn = interview.SendTypedAsync("one more");
        await WaitUntil(() => interview.IsStreaming);
        await interview.SendTypedAsync("queued");

        await interview.SessionSavedAsync(rowId, "x");
        Assert.Equal(1, Engine.Interrupts);
        Assert.False(interview.IsStreaming);
        await turn;
        Assert.Equal(InterviewTurnStatus.Interrupted, interview.Turns[^1].Status);
        Assert.DoesNotContain("queued", Engine.SentTexts);   // the queued request was dropped
    });
}
