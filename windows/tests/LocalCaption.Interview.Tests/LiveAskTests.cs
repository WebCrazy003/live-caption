using LocalCaption.Core.Interview;
using Segment = LocalCaption.Core.Interview.AskSelection.Segment;

namespace LocalCaption.Interview.Tests;

/// <summary>
/// SPEC-14: what an Ask sends, the busy policy, typed/regenerate turns, and the screenshot tray
/// (on a fake clipboard — never the user's). Port of <c>LiveAskTests.swift</c>.
/// </summary>
public sealed class LiveAskTests : InterviewTestBase
{
    private (IReadOnlyList<Segment> Segments, string Interim, int AudioMs) _transcript = ([], "", 0);

    private async Task<InterviewController> PreparedInterview()
    {
        var interview = NewController();
        interview.TranscriptSource = () => _transcript;
        await interview.EnsureThreadAsync();
        Assert.Equal(new InterviewThreadState.Open(), interview.ThreadState);
        Engine.Reply = _ => [new AnswerEvent.Delta("**Q:** Why us?\n"), new AnswerEvent.Completed("**Q:** Why us?\nBecause…")];
        return interview;
    }

    // ── Selection → turn ─────────────────────────────────────────────────────────────────

    [Fact]
    public Task AskWithoutAnyStepOpensTheCoachAndAnswers() => OnUi(async () =>
    {
        var interview = NewController();
        interview.TranscriptSource = () => _transcript;
        _transcript = ([], "tell me about yourself", 2000);
        await interview.AskAsync();
        Assert.Single(Engine.Threads);
        Assert.Equal([InterviewPrompt.Ask("tell me about yourself")], Engine.SentTexts);
        Assert.Equal(InterviewTurnStatus.Completed, interview.Turns[^1].Status);
    });

    [Fact]
    public Task NothingNewOpensNoCoach() => OnUi(async () =>
    {
        var interview = NewController();
        interview.TranscriptSource = () => ([], "", 0);
        await interview.AskAsync();
        Assert.Equal("Nothing new since your last ask", interview.Status);
        Assert.Empty(Engine.Threads);
        Assert.Equal(0, Engine.Calls - 3);   // only the setup's refresh (status, models, usage)
    });

    [Fact]
    public Task AskSendsTheInterviewersLatestWordsThenOnlyWhatIsNew() => OnUi(async () =>
    {
        var interview = await PreparedInterview();
        _transcript = ([new Segment("Thanks for joining.", 0, 1500)], "why do you want to work here", 6000);

        await interview.AskAsync();

        Assert.Equal([InterviewPrompt.Ask("Thanks for joining. why do you want to work here")], Engine.SentTexts);
        Assert.Equal("low", Engine.SentTurns[^1].Effort);
        var turn = interview.Turns[^1];
        Assert.Equal(InterviewTurnKind.Ask, turn.Kind);
        Assert.Equal(0, turn.AudioFromMs);
        Assert.Equal(6000, turn.AudioToMs);
        Assert.Equal(InterviewTurnStatus.Completed, turn.Status);
        Assert.NotNull(turn.TtftMs);
        Assert.NotNull(turn.TotalMs);

        // The interim's final lands; nothing else was said → nothing new.
        _transcript = ([new Segment("Thanks for joining.", 0, 1500), new Segment("Why do you want to work here?", 2000, 6800)],
                       "", 7000);
        await interview.AskAsync();
        Assert.Equal("Nothing new since your last ask", interview.Status);
        Assert.Single(Engine.SentTexts);

        _transcript = ([.. _transcript.Segments, new Segment("And when could you start?", 9000, 11000)], "", 12000);
        await interview.AskAsync();
        Assert.Equal(InterviewPrompt.Ask("And when could you start?"), Engine.SentTexts[^1]);
    });

    [Fact]
    public Task LastSentencesMode() => OnUi(async () =>
    {
        Config.SendMode = "last_sentences";
        Config.SendSentences = 1;
        var interview = await PreparedInterview();
        _transcript = ([new Segment("Hi there. Tell me about Kafka.", 0, 3000)], "", 3500);
        await interview.AskAsync();
        Assert.Equal([InterviewPrompt.Ask("Tell me about Kafka.")], Engine.SentTexts);
    });

    // ── Busy policy ──────────────────────────────────────────────────────────────────────

    [Fact]
    public Task InterruptPolicyCutsTheOldAnswerAndAnswersTheNewQuestion() => OnUi(async () =>
    {
        var interview = await PreparedInterview();
        Engine.HoldIf = t => t.Contains("first question");
        _transcript = ([], "first question", 1000);
        var first = interview.AskAsync();
        await WaitUntil(() => interview.IsStreaming);
        Assert.True(interview.IsStreaming);

        _transcript = ([], "second question", 2000);
        await interview.AskAsync();
        await first;

        Assert.Equal(1, Engine.Interrupts);
        Assert.Equal([InterviewTurnStatus.Interrupted, InterviewTurnStatus.Completed], interview.Turns.Select(t => t.Status));
        Assert.Equal("Partial", interview.Turns[0].Answer);
        Assert.Equal(InterviewPrompt.Ask("second question"), Engine.SentTexts[^1]);
    });

    [Fact]
    public Task InterruptPolicySendsAnywayWhenTheOldAnswerWontStop() => OnUi(async () =>
    {
        Options = new InterviewControllerOptions { BusyInterruptWait = TimeSpan.FromMilliseconds(100) };
        var interview = await PreparedInterview();
        Engine.HoldIf = t => t.Contains("first question");
        Engine.IgnoreInterrupts = true;
        _transcript = ([], "first question", 1000);
        var first = interview.AskAsync();
        await WaitUntil(() => interview.IsStreaming);

        _transcript = ([], "second question", 2000);
        await interview.AskAsync();   // waits BusyInterruptWait, then sends regardless (as Swift)
        Assert.Equal(1, Engine.Interrupts);
        Assert.Equal(InterviewPrompt.Ask("second question"), Engine.SentTexts[^1]);
        Assert.Equal(InterviewTurnStatus.Completed, interview.Turns[^1].Status);

        Engine.Release();
        await first;
    });

    [Fact]
    public Task QueuePolicyMergesFurtherAsksAndSendsThemAfterwards() => OnUi(async () =>
    {
        Config.BusyPolicy = "queue";
        var interview = await PreparedInterview();
        Engine.HoldIf = t => t.Contains("one") && !t.Contains("two");
        _transcript = ([], "one", 1000);
        var first = interview.AskAsync();
        await WaitUntil(() => interview.IsStreaming);

        _transcript = ([], "two", 2000);
        await interview.AskAsync();
        _transcript = ([], "three", 3000);
        await interview.AskAsync();
        Assert.Equal("Queued — sends when this answer finishes.", interview.Status);
        Assert.Single(Engine.SentTexts);   // nothing more is sent while the first answer streams

        Engine.Release();
        await first;
        Assert.Equal(0, Engine.Interrupts);
        Assert.Equal([InterviewPrompt.Ask("one"), InterviewPrompt.Ask("two three")], Engine.SentTexts);
        Assert.Equal([InterviewTurnStatus.Completed, InterviewTurnStatus.Completed], interview.Turns.Select(t => t.Status));
        var merged = interview.Turns[^1];
        Assert.Equal("two three", merged.Question);
        Assert.Equal((1000, 3000), (merged.AudioFromMs, merged.AudioToMs));
    });

    [Fact]
    public Task QueuePolicyReplacesAQueuedAskWithAnotherKind() => OnUi(async () =>
    {
        Config.BusyPolicy = "queue";
        var interview = await PreparedInterview();
        Engine.HoldIf = t => t.Contains("one");
        _transcript = ([], "one", 1000);
        var first = interview.AskAsync();
        await WaitUntil(() => interview.IsStreaming);

        _transcript = ([], "two", 2000);
        await interview.AskAsync();
        await interview.SendTypedAsync("typed instead");

        Engine.Release();
        await first;
        Assert.Equal([InterviewPrompt.Ask("one"), "typed instead"], Engine.SentTexts);
    });

    // ── Typed / regenerate ───────────────────────────────────────────────────────────────

    [Fact]
    public Task TypedAndRegenerateGoToTheSameThread() => OnUi(async () =>
    {
        var interview = await PreparedInterview();
        _transcript = ([], "why us", 1000);
        await interview.AskAsync();
        await interview.RegenerateAsync();
        await interview.SendTypedAsync("  make it about Swift  ");

        Assert.Equal(["thr1"], Engine.SentTurns.Select(s => s.ThreadId).Distinct());
        Assert.Equal([InterviewTurnKind.Ask, InterviewTurnKind.Regenerate, InterviewTurnKind.Typed],
                     interview.Turns.Select(t => t.Kind));
        Assert.Equal([InterviewPrompt.Regenerate, "make it about Swift"], Engine.SentTexts.TakeLast(2));
        Assert.Equal("Another answer: why us", interview.Turns[1].Question);
        Assert.Equal(3, Saved(interview).Turns.Count);
    });

    [Fact]
    public Task AnswerTextIsSavedWhenTheTurnEndsNotPerDelta() => OnUi(async () =>
    {
        var interview = await PreparedInterview();
        Engine.HoldIf = _ => true;
        var turn = interview.SendTypedAsync("hold on");
        await WaitUntil(() => interview.Turns.Count > 0 && interview.Turns[^1].Answer == "Partial");
        Assert.Equal("Partial", interview.Turns[^1].Answer);
        var stored = Saved(interview).Turns[^1];
        Assert.Equal(InterviewTurnStatus.Streaming, stored.Status);   // the turn is saved when it starts…
        Assert.Equal("", stored.Answer);                              // …but streamed text stays in memory

        Engine.Release("Full answer");
        await turn;
        Assert.Equal("Full answer", Saved(interview).Turns[^1].Answer);
    });

    [Fact]
    public Task AnAskThatCannotOpenTheCoachBeeps() => OnUi(async () =>
    {
        Engine.FailStart = new EngineError.SignedOut();
        var interview = NewController();
        interview.TranscriptSource = () => ([], "hello?", 1000);
        await interview.AskAsync();
        Assert.Equal(1, Beeps);
        Assert.Equal(new EngineError.SignedOut().Message, interview.Status);
        Assert.Empty(Engine.SentTurns);
    });

    [Fact]
    public Task SlowAnswersSayStillThinking() => OnUi(async () =>
    {
        var interview = await PreparedInterview();
        var seen = new List<string?>();
        interview.Changed += (_, _) => seen.Add(interview.Status);
        Engine.Reply = _ => [new AnswerEvent.Slow(), new AnswerEvent.Completed("ok")];
        await interview.SendTypedAsync("hard one");
        Assert.Contains("Still thinking…", seen);
        Assert.Null(interview.Status);   // cleared when the turn ends
    });

    // ── Screenshot tray ──────────────────────────────────────────────────────────────────

    [Fact]
    public Task ScreenshotHotkeyAddsTheSelectedAreaToThePrompt() => OnUi(async () =>
    {
        var interview = await PreparedInterview();   // works with clipboard auto-add off
        byte[]? next = Png(1);
        Screen.Next = () => next;

        await interview.TakeScreenshotAsync();
        Assert.Single(interview.PendingImages);
        Assert.Equal("Screenshot added — 1 in this prompt", interview.Status);
        Assert.False(interview.Capturing);

        next = null;                                  // Esc: nothing added, no message
        interview.ClearPending();
        await interview.TakeScreenshotAsync();
        Assert.Empty(interview.PendingImages);

        next = Png(2);
        for (var i = 0; i < InterviewController.MaxPendingImages; i++) await interview.TakeScreenshotAsync();
        var calls = Screen.Calls;
        await interview.TakeScreenshotAsync();
        Assert.Equal(calls, Screen.Calls);            // a full prompt doesn't open the selector
        Assert.Equal(InterviewController.MaxPendingImages, interview.PendingImages.Count);
        Assert.Equal("The prompt already has 10 screenshots.", interview.Status);

        interview.ClearPending();
        await interview.TakeScreenshotAsync();
        _transcript = ([], "what does this chart show", 4000);
        await interview.AskAsync();
        var sent = Engine.SentTurns[^1];
        Assert.Equal(2, sent.Input.Count);            // text + the screenshot
        Assert.Empty(interview.PendingImages);
    });

    [Fact]
    public Task CopiedScreenshotsPileUpAndTheNextAskSendsThemAll() => OnUi(async () =>
    {
        Config.IncludeClipboardImages = true;
        var interview = await PreparedInterview();
        Clipboard.CopyImages(Png(1));                 // already there when watching starts
        interview.PollClipboard();
        Assert.Empty(interview.PendingImages);        // an image already on the clipboard is ignored

        Clipboard.CopyImages(Png(2));
        interview.PollClipboard();
        interview.PollClipboard();                    // no change → nothing new
        Clipboard.CopyImages(Png(3));
        interview.PollClipboard();
        Assert.Equal(2, interview.PendingImages.Count);
        Assert.Equal(2, Clipboard.Reads);
        interview.RemovePending(interview.PendingImages[0].Id);
        Clipboard.CopyImages(Png(4));
        interview.PollClipboard();
        Assert.Equal(2, interview.PendingImages.Count);
        Assert.Equal("Screenshot added — 2 in this prompt", interview.Status);

        _transcript = ([], "can you walk me through this", 4000);
        await interview.AskAsync();

        var sent = Engine.SentTurns[^1];
        Assert.Equal(3, sent.Input.Count);            // text + both screenshots
        Assert.Equal(new CodexRpc.Input.Text(InterviewPrompt.Ask("can you walk me through this", imageCount: 2)), sent.Input[0]);
        Assert.Empty(interview.PendingImages);        // the tray empties on send
        var turn = interview.Turns[^1];
        Assert.Equal(["1-1.png", "1-2.png"], turn.Images);
        var id = interview.Record!.Id;
        Assert.Equal(Png(4), Store.InterviewImage(id, "1-2.png"));   // stored in the database
        Assert.Equal(Png(3), interview.Image("1-1.png"));
        Assert.Equal(0, Clipboard.ImageCount);        // cleared from the clipboard once accepted
        Assert.Equal(1, Clipboard.Clears);
        Assert.Equal([true, true], sent.ImageFilesExisted);   // the outbox files existed while Codex read them…
        Assert.All(sent.Input.OfType<CodexRpc.Input.LocalImage>(), i =>
        {
            Assert.StartsWith(Outbox, i.Path);
            Assert.False(File.Exists(i.Path));        // …and are removed after the turn
        });

        Clipboard.CopyText();                         // the clear moved the sequence on: no stale re-read
        interview.PollClipboard();
        Assert.Empty(interview.PendingImages);
    });

    [Fact]
    public Task ScreenshotOnlyAskAndTypedSendTakeTheTray() => OnUi(async () =>
    {
        Config.IncludeClipboardImages = true;
        var interview = await PreparedInterview();
        interview.PollClipboard();
        Clipboard.CopyImages(Png(1));
        interview.PollClipboard();
        _transcript = ([], "", 0);
        await interview.AskAsync();
        Assert.Equal(InterviewPrompt.Ask("", imageCount: 1), Engine.SentTexts[^1]);   // image-only ask
        Assert.Equal("(screenshot)", interview.Turns[^1].Question);

        Clipboard.CopyImages(Png(2));
        interview.PollClipboard();
        await interview.SendTypedAsync("solve it in Swift");
        Assert.Equal("solve it in Swift\n(1 screenshot(s) attached.)", Engine.SentTexts[^1]);
        Assert.Equal(2, Engine.SentTurns[^1].Input.Count);

        Clipboard.CopyImages(Png(3));
        interview.PollClipboard();
        await interview.SendTypedAsync("   ");
        Assert.Equal("(see the attached screenshot(s))\n(1 screenshot(s) attached.)", Engine.SentTexts[^1]);
        Assert.Equal("(screenshot)", interview.Turns[^1].Question);

        var count = Engine.SentTurns.Count;
        await interview.SendTypedAsync("  ");          // nothing typed and an empty tray: nothing sent
        Assert.Equal(count, Engine.SentTurns.Count);
    });

    [Fact]
    public Task TrayIsOffWhenTheSettingIsOffAndCapped() => OnUi(async () =>
    {
        var interview = await PreparedInterview();
        interview.PollClipboard();
        Clipboard.CopyImages(Png(1));
        interview.PollClipboard();
        Assert.Empty(interview.PendingImages);        // setting off → nothing captured…
        Assert.Equal(0, Clipboard.Reads);             // …and the clipboard isn't even read

        Config.IncludeClipboardImages = true;
        for (var i = 0; i < InterviewController.MaxPendingImages + 2; i++)
        {
            Clipboard.CopyImages(Png(10 + i));
            interview.PollClipboard();
        }
        Assert.Equal(InterviewController.MaxPendingImages, interview.PendingImages.Count);
        Assert.Equal("The prompt already has 10 screenshots.", interview.Status);
    });

    [Fact]
    public Task ClipboardIsLeftAloneIfTheUserCopiedSomethingNew() => OnUi(async () =>
    {
        Config.IncludeClipboardImages = true;
        var interview = await PreparedInterview();
        interview.PollClipboard();
        Clipboard.CopyImages(Png(1));
        interview.PollClipboard();
        Clipboard.CopyText();                         // copied later, before the send
        _transcript = ([], "what is this", 1000);
        await interview.AskAsync();
        Assert.Equal(2, Engine.SentTurns[^1].Input.Count);
        Assert.Equal(0, Clipboard.Clears);
    });

    [Fact]
    public Task ClipboardIsKeptWhenClearAfterSendIsOff() => OnUi(async () =>
    {
        Config.IncludeClipboardImages = true;
        Config.ClearClipboardImagesAfterSend = false;
        var interview = await PreparedInterview();
        interview.PollClipboard();
        Clipboard.CopyImages(Png(1));
        interview.PollClipboard();
        _transcript = ([], "what is this", 1000);
        await interview.AskAsync();
        Assert.Equal(0, Clipboard.Clears);
        Assert.Equal(1, Clipboard.ImageCount);
    });

    [Fact]
    public Task TheClipboardIsClearedOnlyOnceCodexAcceptsTheTurn() => OnUi(async () =>
    {
        Config.IncludeClipboardImages = true;
        var interview = await PreparedInterview();
        interview.PollClipboard();
        Clipboard.CopyImages(Png(1));
        interview.PollClipboard();
        Engine.OmitStarted = true;                   // refused before Codex accepted it
        Engine.Reply = _ => [new AnswerEvent.Failed(new EngineError.Busy(), "")];
        _transcript = ([], "what is this", 1000);
        await interview.AskAsync();
        Assert.Equal(0, Clipboard.Clears);
        Assert.Equal(InterviewTurnStatus.Failed, interview.Turns[^1].Status);
        Assert.Equal("Still answering the previous question.", interview.Turns[^1].Error);

        Engine.OmitStarted = false;
        Engine.Reply = _ => [new AnswerEvent.Completed("ok")];
        Clipboard.CopyImages(Png(2));
        interview.PollClipboard();
        _transcript = ([], "and this", 2000);
        await interview.AskAsync();
        Assert.Equal(1, Clipboard.Clears);
    });

    [Fact]
    public Task AModelWithoutImageInputGetsTheTextOnly() => OnUi(async () =>
    {
        Engine.ModelList = [new CodexRpc.Model("gpt-6-luna", "GPT-6-Luna", "", false, "medium", ["low"], false)];
        await Codex.RefreshAsync();
        var interview = await PreparedInterview();
        Screen.Next = () => Png(1);
        await interview.TakeScreenshotAsync();
        var seen = new List<string?>();
        interview.Changed += (_, _) => seen.Add(interview.Status);
        _transcript = ([], "what is this", 1000);
        await interview.AskAsync();
        Assert.Contains("This model can't read images — sending the text only.", seen);
        Assert.Single(Engine.SentTurns[^1].Input);
        Assert.Equal(InterviewPrompt.Ask("what is this"), Engine.SentTexts[^1]);
        Assert.Empty(interview.Turns[^1].Images);
    });
}
