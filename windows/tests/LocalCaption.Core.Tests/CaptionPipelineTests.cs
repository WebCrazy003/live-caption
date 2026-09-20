using LocalCaption.Core.Audio;
using LocalCaption.Core.Captions;

namespace LocalCaption.Core.Tests;

/// <summary>
/// Lets a test hold a decode open and release it on cue, so lane ordering and coalescing can
/// be asserted deterministically rather than raced against a timer. The counterpart of the
/// macOS suite's <c>DecodeGate</c> actor.
/// </summary>
internal sealed class DecodeGate
{
    private readonly Lock _lock = new();
    private readonly Queue<TaskCompletionSource<SpeechOutcome>> _pending = new();
    private readonly Queue<SpeechRequest> _arrivals = new();
    private TaskCompletionSource<SpeechRequest>? _observer;

    public Task<SpeechOutcome> Decode(SpeechRequest request, CancellationToken cancellationToken)
    {
        TaskCompletionSource<SpeechOutcome> completion;
        TaskCompletionSource<SpeechRequest>? waiting = null;

        lock (_lock)
        {
            completion = new TaskCompletionSource<SpeechOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(completion);
            if (_observer is { } observer) { _observer = null; waiting = observer; }
            else _arrivals.Enqueue(request);
        }

        waiting?.TrySetResult(request);
        cancellationToken.Register(() => completion.TrySetResult(new SpeechOutcome.Cancelled()));
        return completion.Task;
    }

    /// <summary>Await the next decode to arrive at the gate.</summary>
    public Task<SpeechRequest> NextAsync()
    {
        lock (_lock)
        {
            if (_arrivals.Count > 0) return Task.FromResult(_arrivals.Dequeue());
            _observer = new TaskCompletionSource<SpeechRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _observer.Task;
        }
    }

    /// <summary>Release the oldest waiting decode with the given outcome.</summary>
    public void Complete(SpeechOutcome outcome)
    {
        TaskCompletionSource<SpeechOutcome> completion;
        lock (_lock) { completion = _pending.Dequeue(); }
        completion.TrySetResult(outcome);
    }

    public int Count { get { lock (_lock) return _pending.Count(c => !c.Task.IsCompleted); } }
}

/// <summary>A clock the test moves by hand, so decode budgets are not wall-clock races.</summary>
internal sealed class TestClock
{
    private readonly Lock _lock = new();
    private double _value;
    public double Now { get { lock (_lock) return _value; } }
    public void Advance(double seconds) { lock (_lock) _value += seconds; }
}

/// <summary>
/// Port of the macOS <c>CaptionPipelineTests</c>. These assert the ordering guarantees in
/// SPEC-WINDOWS.md §6.2: only pending interim snapshots coalesce, finals never reorder and
/// never drop, and a final is durable before it is published.
/// </summary>
public class CaptionPipelineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static SpeechRequest Request(Guid session, int id, int start = 0, int samples = 16000,
                                         bool final = false, double now = 0) =>
        new(session, id, start, new float[samples], final, now);

    private static SpeechOutcome Result(string text) =>
        new SpeechOutcome.Success(text, [new CaptionWord(text, 0, 0.5)], 0);

    /// <summary>A signal a test can await without pinning itself to a real duration.</summary>
    private sealed class Signal
    {
        private readonly TaskCompletionSource _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Fire() => _source.TrySetResult();
        public async Task WaitAsync(string what)
        {
            var completed = await Task.WhenAny(_source.Task, Task.Delay(Timeout)).ConfigureAwait(false);
            Assert.True(completed == _source.Task, $"timed out waiting for: {what}");
        }
    }

    [Fact]
    public async Task SlowFinalDoesNotBlockNewSpeechOrClearIt()
    {
        var session = Guid.NewGuid();
        var clock = new TestClock();
        var finalGate = new DecodeGate();

        await using var pipeline = new CaptionPipeline(session,
            interim: (_, _) => Task.FromResult<SpeechOutcome>(
                new SpeechOutcome.Success("new speech", [new CaptionWord("new speech", 0, 0.5)], 0)),
            final: finalGate.Decode,
            now: () => clock.Now);

        var committed = new List<string>();
        var finalizedPending = new List<string>();
        pipeline.OnFinal = (text, _, _) => { committed.Add(text); return Task.CompletedTask; };
        pipeline.OnFinalized = text => finalizedPending.Add(text);

        await pipeline.SubmitAsync(Request(session, 0, final: true));
        await finalGate.NextAsync();
        clock.Advance(5);

        // While that final is still decoding, fresh speech must still reach the screen.
        var updated = new Signal();
        await pipeline.InvokeAsync(() => pipeline.OnHypothesis = t => { if (t == "new speech") updated.Fire(); });
        await pipeline.SubmitAsync(Request(session, 1, start: 16000, now: 5));
        await updated.WaitAsync("an interim to publish while the final is in flight");
        await pipeline.InvokeAsync(() => pipeline.OnHypothesis = null);

        Assert.Empty(committed);
        finalGate.Complete(Result("old final"));
        await pipeline.FinishAsync();

        Assert.Equal(["old final"], committed);
        Assert.Equal("new speech", pipeline.Hypothesis);
        Assert.Equal(["new speech"], finalizedPending);
        Assert.Equal(5000, pipeline.Metrics.First(m => m.IsFinal).DecodeMs);
    }

    [Fact]
    public async Task PendingSnapshotsCoalesceWithoutStarvingInFlightResult()
    {
        var session = Guid.NewGuid();
        var gate = new DecodeGate();

        await using var pipeline = new CaptionPipeline(session,
            interim: gate.Decode,
            final: (_, _) => Task.FromResult<SpeechOutcome>(new SpeechOutcome.Empty()));

        await pipeline.SubmitAsync(Request(session, 0));
        await gate.NextAsync();

        // Two more snapshots arrive while the first decodes. They collapse into the widest
        // one rather than queueing up — but must not cancel the result already in flight.
        await pipeline.SubmitAsync(Request(session, 0, samples: 24000));
        await pipeline.SubmitAsync(Request(session, 0, samples: 32000));
        Assert.Equal(32000, (await pipeline.InvokeAsync(() => pipeline.PendingInterim))!.EndSample);

        gate.Complete(Result("first"));
        var next = await gate.NextAsync();
        Assert.Equal("first", pipeline.Hypothesis);
        Assert.Equal(32000, next.EndSample);

        var done = new Signal();
        await pipeline.InvokeAsync(() => pipeline.OnHypothesis = t => { if (t == "newest") done.Fire(); });
        gate.Complete(Result("newest"));
        await done.WaitAsync("the coalesced snapshot to publish");
        await pipeline.FinishAsync();
    }

    [Fact]
    public async Task CompletedUtteranceRejectsLateInterimAndWrongGeneration()
    {
        var session = Guid.NewGuid();
        var interimGate = new DecodeGate();
        var finalGate = new DecodeGate();

        await using var pipeline = new CaptionPipeline(session,
            interim: interimGate.Decode, final: finalGate.Decode);

        // A request carrying another session's id is a wrong-generation result.
        await pipeline.SubmitAsync(Request(Guid.NewGuid(), 99));
        Assert.Null(await pipeline.InvokeAsync(() => pipeline.PendingInterim));

        await pipeline.SubmitAsync(Request(session, 0));
        await interimGate.NextAsync();
        await pipeline.SubmitAsync(Request(session, 0, final: true));
        await finalGate.NextAsync();

        var retired = new Signal();
        await pipeline.InvokeAsync(() => pipeline.OnHypothesis = _ => retired.Fire());
        finalGate.Complete(Result("final"));
        await retired.WaitAsync("the final to retire its utterance");
        await pipeline.InvokeAsync(() => pipeline.OnHypothesis = null);

        // The interim now lands after its utterance was committed: it must not resurrect it.
        interimGate.Complete(Result("late provisional"));
        await pipeline.FinishAsync();
        Assert.Equal("", pipeline.Hypothesis);
    }

    [Fact]
    public async Task FinalQueuePreservesOrderAndSignalsOverloadOnce()
    {
        var session = Guid.NewGuid();
        var gate = new DecodeGate();

        await using var pipeline = new CaptionPipeline(session,
            interim: (_, _) => Task.FromResult<SpeechOutcome>(new SpeechOutcome.Empty()),
            final: gate.Decode,
            backlogSeconds: 2, backlogCount: 16);

        var overloads = 0;
        var finals = new List<int>();
        pipeline.OnOverload = () => overloads++;
        pipeline.OnFinal = (_, start, _) => { finals.Add(start); return Task.CompletedTask; };

        for (var id = 0; id < 3; id++)
            await pipeline.SubmitAsync(Request(session, id, start: id * 16000, final: true));

        // Three seconds of backlog crosses the two-second threshold — once, not per request.
        Assert.Equal(1, overloads);

        for (var id = 0; id < 3; id++)
        {
            var next = await gate.NextAsync();
            Assert.Equal(id, next.Utterance);
            gate.Complete(Result($"final {id}"));
        }

        await pipeline.FinishAsync();
        Assert.Equal([0, 1000, 2000], finals);
        Assert.Empty(pipeline.FinalQueue);
    }

    [Fact]
    public async Task EmptyInterimPreservesTextAndFailedFinalReportsGap()
    {
        var session = Guid.NewGuid();
        var gate = new DecodeGate();

        await using var pipeline = new CaptionPipeline(session,
            interim: gate.Decode,
            final: (_, _) => Task.FromResult<SpeechOutcome>(new SpeechOutcome.Failure("test")));

        await pipeline.SubmitAsync(Request(session, 0));
        await gate.NextAsync();

        var visible = new Signal();
        await pipeline.InvokeAsync(() => pipeline.OnHypothesis = _ => visible.Fire());
        gate.Complete(Result("provisional"));
        await visible.WaitAsync("the first interim to become visible");
        await pipeline.InvokeAsync(() => pipeline.OnHypothesis = null);

        // A filtered interim must leave the words already on screen alone.
        await pipeline.SubmitAsync(Request(session, 0, samples: 24000));
        await gate.NextAsync();
        var filtered = new Signal();
        await pipeline.InvokeAsync(() =>
            pipeline.OnMetric = m => { if (m.Outcome == "filtered") filtered.Fire(); });
        gate.Complete(new SpeechOutcome.Filtered());
        await filtered.WaitAsync("the filtered interim to complete");
        Assert.Equal("provisional", pipeline.Hypothesis);

        var saved = false;
        var issues = new List<string>();
        await pipeline.InvokeAsync(() =>
        {
            pipeline.OnFinal = (_, _, _) => { saved = true; return Task.CompletedTask; };
            pipeline.OnIssue = issues.Add;
        });

        await pipeline.SubmitAsync(Request(session, 0, samples: 24000, final: true));
        await pipeline.FinishAsync();

        Assert.False(saved);
        Assert.Contains(issues, i => i.Contains("missing"));
        Assert.Equal("", pipeline.Hypothesis);
    }

    [Fact]
    public async Task SlowInterimDoesNotOverlapReplacementDecode()
    {
        var session = Guid.NewGuid();
        var gate = new DecodeGate();
        var warned = new Signal();

        await using var pipeline = new CaptionPipeline(session,
            interim: gate.Decode,
            final: (_, _) => Task.FromResult<SpeechOutcome>(new SpeechOutcome.Empty()),
            interimBudget: 0.02);

        await pipeline.InvokeAsync(() =>
            pipeline.OnCatchingUp = behind => { if (behind) warned.Fire(); });

        await pipeline.SubmitAsync(Request(session, 0));
        await gate.NextAsync();
        await pipeline.SubmitAsync(Request(session, 0, samples: 24000));
        await warned.WaitAsync("the cooperative deadline to fire");

        Assert.Equal(1, gate.Count);   // cancellation must not start a second call on one model

        await pipeline.InvokeAsync(() => pipeline.OnCatchingUp = null);
        gate.Complete(Result("expired"));
        await gate.NextAsync();
        Assert.Equal("", pipeline.Hypothesis);   // an expired result is never displayed

        gate.Complete(Result("fresh"));
        await pipeline.FinishAsync();
        Assert.Contains(pipeline.Metrics, m => m.Outcome == "timeout");
    }

    [Fact]
    public async Task FinishAwaitsDurableFinalAcknowledgment()
    {
        var session = Guid.NewGuid();
        var journalGate = new DecodeGate();
        var placeholder = Request(Guid.NewGuid(), 0);

        await using var pipeline = new CaptionPipeline(session,
            interim: (_, _) => Task.FromResult<SpeechOutcome>(new SpeechOutcome.Empty()),
            final: (_, _) => Task.FromResult<SpeechOutcome>(
                new SpeechOutcome.Success("saved", [], 0)));

        pipeline.OnFinal = async (_, _, _) =>
            await journalGate.Decode(placeholder, CancellationToken.None).ConfigureAwait(true);

        await pipeline.SubmitAsync(Request(session, 0, final: true));
        await journalGate.NextAsync();

        // Finish must not return while a segment is still being made durable.
        var stop = pipeline.FinishAsync();
        var raced = await Task.WhenAny(stop, Task.Delay(150));
        Assert.True(raced != stop, "Finish returned before the journal acknowledged the segment");

        journalGate.Complete(new SpeechOutcome.Empty());
        await stop;
    }

    [Fact]
    public async Task ResultsTooFarBehindLiveAudioAreNotDisplayed()
    {
        var session = Guid.NewGuid();
        var gate = new DecodeGate();

        await using var pipeline = new CaptionPipeline(session,
            interim: gate.Decode,
            final: (_, _) => Task.FromResult<SpeechOutcome>(new SpeechOutcome.Empty()));

        await pipeline.SubmitAsync(Request(session, 0));
        await gate.NextAsync();
        await pipeline.AdvanceAudioAsync(5 * 16000);

        var decoded = new Signal();
        await pipeline.InvokeAsync(() => pipeline.OnMetric = _ => decoded.Fire());
        gate.Complete(Result("old speech"));
        await decoded.WaitAsync("the stale result to complete");

        // Four seconds behind live audio: decoded and measured, but never shown.
        Assert.Equal("", pipeline.Hypothesis);
        Assert.Equal(4000, pipeline.Metrics[^1].AudioLagMs);
        await pipeline.FinishAsync();
    }
}
