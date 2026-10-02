using LocalCaption.Core.Interview;

namespace LocalCaption.Interview.Tests;

/// <summary>
/// The controller's threading rule (the Swift class is <c>@MainActor</c>): one owner thread with
/// a <see cref="SynchronizationContext"/>; engine events arriving on other threads are applied on
/// it; calls from anywhere else are refused. Plus SPEC-16 §4.1: Caption only mode never touches
/// the engine.
/// </summary>
public sealed class InterviewThreadingTests : InterviewTestBase
{
    [Fact]
    public async Task ConstructingWithoutASynchronizationContextIsRefused()
    {
        var error = await Task.Run(() =>
            Record.Exception(() => new InterviewController(Store, Library, Codex, () => Config)));
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public async Task CallsFromAnotherThreadAreRefused()
    {
        InterviewController? interview = null;
        await Ui.Run(() =>
        {
            interview = NewController();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => interview!.AskAsync()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => interview!.EnsureThreadAsync()));
        Assert.Throws<InvalidOperationException>(() => interview!.PollClipboard());
        Assert.Throws<InvalidOperationException>(() => interview!.ShowingPreparation = false);
        Assert.Throws<InvalidOperationException>(() => interview!.Draft = new InterviewDraft());
        Assert.Equal(0, Engine.Calls);
    }

    /// <summary>
    /// A hotkey calls the async methods fire-and-forget: a wrong-thread call must throw at the
    /// call, not inside a returned task nobody observes.
    /// </summary>
    [Fact]
    public async Task AsyncMethodsCalledFromAnotherThreadThrowAtTheCall()
    {
        InterviewController? interview = null;
        await Ui.Run(() =>
        {
            interview = NewController();
            return Task.CompletedTask;
        });
        var i = interview!;
        var calls = new Func<Task>[]
        {
            i.AskAsync, i.EnsureThreadAsync, () => i.SendTypedAsync("x"), i.RegenerateAsync,
            () => i.RunAsync(InterviewStep.DiscoveryCv), () => i.StartPreparationAsync(), i.DiscardUnstartedAsync,
            () => i.SessionSavedAsync(null, "x"), () => i.GenerateSummaryAsync("x"), i.StopStreamingAsync,
            i.TakeScreenshotAsync, () => i.SendFollowUpAsync("x"),
        };
        foreach (var call in calls)
        {
            Task? returned = null;
            Assert.Throws<InvalidOperationException>(() => { returned = call(); });   // thrown synchronously…
            Assert.Null(returned);                                               // …no task was handed back
        }
        Assert.Equal(0, Engine.Calls);
    }

    [Fact]
    public Task EngineEventsFromOtherThreadsAreAppliedOnTheOwnerThread() => OnUi(async () =>
    {
        Engine.EventsFromThreadPool = true;
        Engine.Reply = _ =>
        [
            new AnswerEvent.Thinking(), new AnswerEvent.Delta("One "), new AnswerEvent.Delta("two "),
            new AnswerEvent.Slow(), new AnswerEvent.Completed("One two three"),
        ];
        var interview = NewController();
        var threads = new HashSet<int>();
        var changes = 0;
        interview.Changed += (_, _) =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            changes++;
        };

        await interview.SendTypedAsync("hello");
        Assert.Equal(Ui.ThreadId, Environment.CurrentManagedThreadId);   // the await resumed on the owner
        Engine.Reply = _ => [new AnswerEvent.Completed("Summary")];
        await interview.SessionSavedAsync(null, "x");
        await interview.GenerateSummaryAsync("x");

        Assert.Equal([Ui.ThreadId], threads);
        Assert.True(changes > 5);
        Assert.Equal("One two three", interview.Turns[^1].Answer);
        Assert.Equal("Summary", interview.SummaryText);
    });

    [Fact]
    public Task ConcurrentAsksOnTheOwnerThreadShareOneThread() => OnUi(async () =>
    {
        Engine.EventsFromThreadPool = true;
        Config.BusyPolicy = "queue";
        var interview = NewController();
        var text = "first";
        interview.TranscriptSource = () => ([], text, 1000);
        var a = interview.AskAsync();
        text = "second";
        var b = interview.AskAsync();   // started while the first is still opening the thread
        await Task.WhenAll(a, b);
        Assert.Single(Engine.Threads);
        Assert.Equal(2, interview.Turns.Count);
    });

    [Fact]
    public Task CaptionOnlyModeNeverTouchesTheEngine() => Ui.Run(() =>
    {
        // No Codex refresh here: the app never starts Codex in Caption only mode.
        Config.Mode = "caption";
        Config.IncludeClipboardImages = true;
        var interview = NewController();
        interview.TranscriptSource = () => ([], "hello", 1000);
        interview.PollClipboard();
        Clipboard.CopyImages(Png(1));
        interview.PollClipboard();
        interview.Draft = interview.Draft with { Candidate = "victor" };
        interview.DetailsChanged();
        interview.ShowingPreparation = false;
        _ = interview.PreparationBlocker;
        _ = interview.MissingSkills;
        _ = interview.SessionName(DateTimeOffset.Now);
        interview.ClearPending();
        interview.ResetForNewInterview();
        Assert.Null(interview.Record);
        Assert.Equal(0, Engine.Calls);
        Assert.Equal(0, Clipboard.Reads);   // nor the clipboard (SPEC-16 §11.2)
        Assert.Empty(interview.PendingImages);
        return Task.CompletedTask;
    });
}
