using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;
using LocalCaption.Core.Transcripts;

namespace LocalCaption.Interview.Tests;

/// <summary>
/// Windows-only robustness of the controller: platform services that throw (the clipboard is
/// shared with every other process on Windows), switching interviews while work is in flight,
/// and the launch sweep's housekeeping.
/// </summary>
public sealed class InterviewRobustnessTests : InterviewTestBase
{
    private async Task<InterviewController> ReadyForImages()
    {
        Config.IncludeClipboardImages = true;
        var interview = NewController();
        await interview.EnsureThreadAsync();
        interview.PollClipboard();   // watching starts
        return interview;
    }

    // ── A throwing clipboard (H1, M2, L8) ────────────────────────────────────────────────

    [Fact]
    public Task AClipboardClearThatThrowsDoesNotLeaveTheTurnStreaming() => OnUi(async () =>
    {
        var interview = await ReadyForImages();
        Clipboard.CopyImages(Png(1));
        interview.PollClipboard();
        Clipboard.FailClears = 1;   // another process holds the clipboard when Codex accepts the turn
        await interview.SendTypedAsync("what is this");

        Assert.False(interview.IsStreaming);
        Assert.False(interview.IsBusy);
        Assert.Equal(InterviewTurnStatus.Completed, interview.Turns[^1].Status);
        Assert.Equal(InterviewTurnStatus.Completed, Saved(interview).Turns[^1].Status);   // the end of the turn was saved
        Assert.Equal(0, Clipboard.Clears);
        Assert.Equal(1, Clipboard.ImageCount);   // left as it was

        await interview.SendTypedAsync("and now");   // the next turn is not blocked
        Assert.Equal(InterviewTurnStatus.Completed, interview.Turns[^1].Status);
    });

    [Fact]
    public Task MergedAsksRunEveryClipboardCallbackEvenWhenOneThrows() => OnUi(async () =>
    {
        Config.BusyPolicy = InterviewConfig.BusyPolicyQueue;
        var interview = await ReadyForImages();
        var text = "one";
        interview.TranscriptSource = () => ([], text, 1000);
        Engine.HoldIf = t => t.Contains("one");
        var first = interview.AskAsync();
        await WaitUntil(() => interview.IsStreaming);

        Clipboard.CopyImages(Png(1));
        interview.PollClipboard();
        text = "two";
        await interview.AskAsync();   // queued, with a clipboard callback
        Clipboard.CopyImages(Png(2));
        interview.PollClipboard();
        text = "three";
        await interview.AskAsync();   // merged: a second callback
        Clipboard.FailClears = 1;     // the first callback throws…

        Engine.Release();
        await first;
        Assert.Equal(1, Clipboard.Clears);   // …and the second still ran
        Assert.False(interview.IsStreaming);
        Assert.Equal(2, Engine.SentTurns[^1].Input.Count - 1);   // both screenshots went with the merged ask
    });

    [Fact]
    public Task AClipboardReadThatThrowsIsRetriedOnTheNextPoll() => OnUi(async () =>
    {
        var interview = await ReadyForImages();
        Clipboard.CopyImages(Png(1));
        Clipboard.FailReads = 1;
        interview.PollClipboard();   // doesn't throw
        Assert.Empty(interview.PendingImages);

        interview.PollClipboard();   // the same change, read again
        Assert.Single(interview.PendingImages);
        Assert.Equal(Png(1), interview.PendingImages[0].Png);
    });

    [Fact]
    public Task AClipboardThatNeverReadsIsGivenUpAfterAFewPolls() => OnUi(async () =>
    {
        var interview = await ReadyForImages();
        Clipboard.CopyImages(Png(1));
        Clipboard.FailReads = InterviewController.MaxClipboardReadAttempts;
        for (var i = 0; i < InterviewController.MaxClipboardReadAttempts; i++) interview.PollClipboard();
        var reads = Clipboard.Reads;
        interview.PollClipboard();
        Assert.Equal(reads, Clipboard.Reads);   // that change is skipped now
        Assert.Empty(interview.PendingImages);

        Clipboard.CopyImages(Png(2));           // a new copy is read as usual
        interview.PollClipboard();
        Assert.Single(interview.PendingImages);
    });

    [Fact]
    public Task AFullTrayNeverDecodesTheClipboard() => OnUi(async () =>
    {
        var interview = await ReadyForImages();
        for (var i = 0; i < InterviewController.MaxPendingImages; i++)
        {
            Clipboard.CopyImages(Png(i));
            interview.PollClipboard();
        }
        Assert.Equal(InterviewController.MaxPendingImages, interview.PendingImages.Count);
        var reads = Clipboard.Reads;

        Clipboard.CopyImages(Png(99));
        interview.PollClipboard();
        Assert.Equal(reads, Clipboard.Reads);
        Assert.Equal("The prompt already has 10 screenshots.", interview.Status);

        interview.ClearPending();
        Clipboard.CopyText();                   // text while full said nothing; after, nothing to add
        interview.PollClipboard();
        Assert.Empty(interview.PendingImages);
    });

    [Fact]
    public Task ImagesCopiedInCaptionOnlyModeAreNotPickedUpLater() => OnUi(async () =>
    {
        var interview = await ReadyForImages();
        Config.Mode = InterviewConfig.CaptionMode;
        Clipboard.CopyImages(Png(1));           // copied while captioning
        interview.PollClipboard();
        Config.Mode = InterviewConfig.InterviewMode;
        interview.PollClipboard();              // watching starts again: what is there now is left alone
        Assert.Empty(interview.PendingImages);
        Assert.Equal(0, Clipboard.Reads);

        Clipboard.CopyImages(Png(2));
        interview.PollClipboard();
        Assert.Single(interview.PendingImages);
    });

    [Fact]
    public Task AScreenshotSelectorThatFaultsIsACancel() => OnUi(async () =>
    {
        var interview = NewController();
        Screen.Failure = new InvalidOperationException("no display");
        await interview.TakeScreenshotAsync();
        Assert.Empty(interview.PendingImages);
        Assert.False(interview.Capturing);

        Screen.Failure = null;
        Screen.Next = () => Png(1);
        await interview.TakeScreenshotAsync();
        Assert.Single(interview.PendingImages);
    });

    // ── The draft (M5) ───────────────────────────────────────────────────────────────────

    [Fact]
    public Task ReplacingTheDraftRaisesChanged() => OnUi(() =>
    {
        var interview = NewController();
        var changes = 0;
        interview.Changed += (_, _) => changes++;
        interview.Draft = interview.Draft with { Company = "Peloton" };
        Assert.Equal(1, changes);
        Assert.Equal("Peloton", interview.Draft.Company);
        interview.Draft = interview.Draft with { Company = "Peloton" };   // equal value: nothing changed
        Assert.Equal(1, changes);
        return Task.CompletedTask;
    });

    // ── Switching interviews while work is in flight (M4, L11) ───────────────────────────

    private InterviewRecord SavedInterview(string thread)
    {
        var rec = new InterviewRecord("Saved", TimeFormat.Iso(DateTimeOffset.Now), "gpt-6-luna", "low", new InterviewSetup())
        {
            ThreadId = thread, EndedAt = TimeFormat.Iso(DateTimeOffset.Now),
        };
        Store.SaveInterview(rec);
        return Store.Interview(rec.Id)!;
    }

    [Fact]
    public Task AThreadThatOpensAfterLoadIsArchivedNotWrittenIntoTheLoadedInterview() => OnUi(async () =>
    {
        var interview = NewController();
        Engine.StartGate = new TaskCompletionSource();
        var opening = interview.EnsureThreadAsync();
        Assert.Equal(new InterviewThreadState.Opening(), interview.ThreadState);
        Assert.False(interview.IsBusy);   // as Swift: opening alone isn't busy
        var orphan = interview.Record!.Id;

        var saved = SavedInterview("saved-thread");
        interview.Load(saved);
        Engine.StartGate.SetResult();

        Assert.Null(await opening);
        Assert.Equal("saved-thread", interview.Record?.ThreadId);   // the loaded interview keeps its thread
        Assert.Equal("saved-thread", Store.Interview(saved.Id)?.ThreadId);
        Assert.Equal(new InterviewThreadState.Open(), interview.ThreadState);
        Assert.Equal(["thr1"], Engine.Archived);                   // the orphan is archived
        Assert.Null(Store.Interview(orphan)?.ThreadId);
    });

    [Fact]
    public Task AnOpenStartedBeforeResetIsNotJoinedAfterIt() => OnUi(async () =>
    {
        var interview = NewController();
        Engine.StartGate = new TaskCompletionSource();
        var before = interview.EnsureThreadAsync();
        interview.ResetForNewInterview();
        var after = interview.EnsureThreadAsync();   // a new interview opens its own thread
        Engine.StartGate.SetResult();

        Assert.Null(await before);
        Assert.Equal("thr2", await after);
        Assert.Equal("thr2", interview.Record?.ThreadId);
        Assert.Equal(["thr1"], Engine.Archived);
    });

    [Fact]
    public Task ATurnStreamingAcrossResetFinishesOnItsOwnRecord() => OnUi(async () =>
    {
        Config.BusyPolicy = InterviewConfig.BusyPolicyQueue;
        var interview = NewController();
        Engine.HoldIf = t => t.Contains("hold");
        var turn = interview.SendTypedAsync("hold on");
        await WaitUntil(() => interview.IsStreaming);
        var first = interview.Record!;
        await interview.SendTypedAsync("queued for the first interview");

        interview.ResetForNewInterview();
        Assert.False(interview.IsStreaming);
        Assert.False(interview.IsBusy);
        await interview.SendTypedAsync("a new interview");   // not queued behind the old answer
        var second = interview.Record!;
        Assert.NotSame(first, second);

        Engine.Release("Finished.");
        await turn;
        var stored = Store.Interview(first.Id)!;
        Assert.Equal(InterviewTurnStatus.Completed, stored.Turns[^1].Status);   // not left 'streaming'
        Assert.Equal("Finished.", stored.Turns[^1].Answer);
        Assert.Single(stored.Turns);
        Assert.DoesNotContain("queued for the first interview", Engine.SentTexts);   // the old queue was dropped
        Assert.Same(second, interview.Record);
        Assert.Single(Store.Interview(second.Id)!.Turns);                    // the new one is untouched
        Assert.Null(interview.Status);
    });

    [Fact]
    public Task ATurnStreamingAcrossLoadDoesNotSaveTheLoadedInterview() => OnUi(async () =>
    {
        var interview = NewController();
        Engine.HoldIf = _ => true;
        var turn = interview.SendTypedAsync("hold on");
        await WaitUntil(() => interview.IsStreaming);
        var first = interview.Record!.Id;

        var saved = SavedInterview("saved-thread");
        interview.Load(saved);
        Engine.Release("Finished.");
        await turn;

        Assert.Empty(Store.Interview(saved.Id)!.Turns);
        Assert.Empty(interview.Turns);
        Assert.Equal(InterviewTurnStatus.Completed, Store.Interview(first)!.Turns[^1].Status);
    });

    [Fact]
    public Task ATurnStreamingWhenItsInterviewIsDiscardedDoesNotBringItBack() => OnUi(async () =>
    {
        var interview = NewController();
        Engine.HoldIf = _ => true;
        var turn = interview.SendTypedAsync("hold on");
        await WaitUntil(() => interview.IsStreaming);
        var id = interview.Record!.Id;

        await interview.DiscardUnstartedAsync();
        Assert.Null(interview.Record);
        Engine.Release("Finished.");
        await turn;
        Assert.Null(Store.Interview(id));
    });

    // ── CV text (E2) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public Task CvTextIsReadFromTheLibraryOnce() => OnUi(() =>
    {
        var cv = ImportCv();
        var rec = new InterviewRecord("Interview", TimeFormat.Iso(DateTimeOffset.Now), "gpt-6-luna", "low",
                                      new InterviewSetup { DocumentIds = [cv] });   // no snapshot
        var interview = NewController(rec);
        Assert.Equal("Jane Doe\nSwift, 8 years.", interview.CvText);
        foreach (var file in Directory.GetFiles(Path.Combine(Tmp, "library"), "text.txt", SearchOption.AllDirectories))
            File.Delete(file);
        Assert.Equal("Jane Doe\nSwift, 8 years.", interview.CvText);   // cached, not re-read

        interview.ResetForNewInterview();
        Assert.Equal("", interview.CvText);
        return Task.CompletedTask;
    });

    // ── The launch sweep (E4) ────────────────────────────────────────────────────────────

    [Fact]
    public void TheLaunchSweepEmptiesTheOutbox()
    {
        Directory.CreateDirectory(Outbox);
        File.WriteAllBytes(Path.Combine(Outbox, "A.png"), Png(1));
        File.WriteAllBytes(Path.Combine(Outbox, "B.png.tmp"), Png(2));
        Assert.Equal(0, InterviewRecovery.SweepInterrupted(Store, Outbox));
        Assert.True(Directory.Exists(Outbox));
        Assert.Empty(Directory.GetFiles(Outbox));
        InterviewRecovery.SweepInterrupted(Store, Path.Combine(Tmp, "missing"));   // no folder: fine
    }

    [Fact]
    public void TheLaunchSweepOnlyRewritesInterviewsThatWereCutOff()
    {
        var done = new InterviewRecord("Done", "2026-10-01T09:00:00Z", "gpt-6-luna", "low", new InterviewSetup())
        {
            Turns = [new() { N = 1, Question = "q", Answer = "a", Status = InterviewTurnStatus.Completed, AskedAt = "t" }],
        };
        var cut = new InterviewRecord("Cut", "2026-10-02T09:00:00Z", "gpt-6-luna", "low", new InterviewSetup())
        {
            Turns = [new() { N = 1, Question = "q", Status = InterviewTurnStatus.Streaming, AskedAt = "t" }],
        };
        Store.SaveInterview(done);
        Store.SaveInterview(cut);
        Assert.Equal(1, InterviewRecovery.SweepInterrupted(Store, Outbox));
        Assert.Equal(InterviewTurnStatus.Failed, Store.Interview(cut.Id)!.Turns[0].Status);
        Assert.Equal(InterviewTurnStatus.Completed, Store.Interview(done.Id)!.Turns[0].Status);
    }
}
