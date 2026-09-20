using LocalCaption.Core.Audio;
using LocalCaption.Core.Transcripts;

namespace LocalCaption.Core.Captions;

/// <summary>One decode's timing and outcome. Drives the metrics log and the overload UI.</summary>
public readonly record struct CaptionMetric(
    Guid Session, int Utterance, int WindowStartMs, int WindowEndMs, bool IsFinal,
    int QueueMs, int DecodeMs, int AudioLagMs,
    int FinalQueueDepth, int FinalBacklogMs, string Outcome, int Fallbacks);

/// <summary>
/// Two independent serial lanes over the speech models. Only <b>pending</b> interim
/// snapshots coalesce; final requests never reorder and never drop.
/// </summary>
/// <remarks>
/// <para>All state lives on one <see cref="SynchronizationContext"/> — a
/// <see cref="SerialDispatcher"/> by default, the WPF dispatcher in the app
/// (SPEC-WINDOWS.md §6.2). Nothing here takes a lock: serialisation comes from the context,
/// and every <c>await</c> resumes back on it, which is what lets an interim result publish
/// while a slow final decode is still running.</para>
/// <para>The ordering guarantees are the contract. A final that arrives late must not erase
/// newer speech, and a stale interim must not resurrect an utterance that has already been
/// committed.</para>
/// </remarks>
public sealed class CaptionPipeline : IAsyncDisposable
{
    /// <summary>Runs one decode. Cancellation is cooperative — see the interim watchdog.</summary>
    public delegate Task<SpeechOutcome> Decode(SpeechRequest request, CancellationToken cancellationToken);

    private const int SamplesPerMs = 16;
    private const int SampleRate = 16000;
    private const int MetricsCap = 256;

    // ── Callbacks. All are raised on the pipeline's context. ─────────────────────────────

    public Action<string>? OnHypothesis { get; set; }
    /// <summary>Awaited before the final is published: durability first (§6.2).</summary>
    public Func<string, int, int, Task>? OnFinal { get; set; }
    public Action<string>? OnSpeechEnded { get; set; }
    public Action<string>? OnFinalized { get; set; }
    public Action<string>? OnIssue { get; set; }
    public Action? OnOverload { get; set; }
    public Action<bool>? OnCatchingUp { get; set; }
    public Action<CaptionMetric>? OnMetric { get; set; }

    // ── Observable state ─────────────────────────────────────────────────────────────────

    public Guid Session { get; }
    public string Hypothesis { get; private set; } = "";
    public SpeechRequest? PendingInterim { get; private set; }
    public IReadOnlyList<SpeechRequest> FinalQueue => _finalQueue;
    public IReadOnlyList<CaptionMetric> Metrics => _metrics;

    private readonly List<SpeechRequest> _finalQueue = [];
    private readonly List<CaptionMetric> _metrics = [];
    private readonly Dictionary<int, RollingCaption> _captions = [];

    private readonly SerialDispatcher? _ownedDispatcher;
    private readonly SynchronizationContext _context;
    private readonly Decode _interimDecode;
    private readonly Decode _finalDecode;
    private readonly Func<double> _now;
    private readonly Func<double, CancellationToken, Task> _delay;
    private readonly double _interimBudget;
    private readonly double _backlogSeconds;
    private readonly int _backlogCount;

    private Task? _interimTask;
    private Task? _finalTask;
    private CancellationTokenSource? _activeInterim;
    private int _completedThrough = -1;
    private int _lastFinalEnqueued = -1;
    private int _latestSample;
    private int _queuedFinalSamples;
    private bool _overloadSignaled;
    private bool _closing;

    public CaptionPipeline(Guid session, Decode interim, Decode final,
                           double interimBudget = 2, double backlogSeconds = 40, int backlogCount = 16,
                           Func<double>? now = null,
                           Func<double, CancellationToken, Task>? delay = null,
                           SynchronizationContext? context = null)
    {
        Session = session;
        _interimDecode = interim;
        _finalDecode = final;
        _interimBudget = interimBudget;
        _backlogSeconds = backlogSeconds;
        _backlogCount = backlogCount;
        _now = now ?? (() => MonotonicClock.Now);
        _delay = delay ?? (async (seconds, token) =>
            await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(true));

        if (context is null)
        {
            _ownedDispatcher = new SerialDispatcher();
            _context = _ownedDispatcher;
        }
        else
        {
            _context = context;
        }
    }

    /// <summary>Run <paramref name="action"/> on the pipeline's context and await it.</summary>
    public Task InvokeAsync(Action action)
    {
        if (_ownedDispatcher is { } dispatcher) return dispatcher.InvokeAsync(action);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(_ =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception e) { completion.TrySetException(e); }
        }, null);
        return completion.Task;
    }

    public async Task<T> InvokeAsync<T>(Func<T> func)
    {
        T result = default!;
        await InvokeAsync(() => { result = func(); }).ConfigureAwait(false);
        return result;
    }

    // ── Submission ───────────────────────────────────────────────────────────────────────

    /// <summary>Hand a decode request to the right lane. Safe to call from any thread.</summary>
    public void Submit(SpeechRequest request) => _context.Post(_ => SubmitCore(request), null);

    /// <summary>As <see cref="Submit"/>, but awaits the request being queued.</summary>
    public Task SubmitAsync(SpeechRequest request) => InvokeAsync(() => SubmitCore(request));

    /// <summary>Tell the pipeline how far live audio has reached, for the staleness gate.</summary>
    public void AdvanceAudio(int sample) => _context.Post(
        _ => _latestSample = Math.Max(_latestSample, sample), null);

    public Task AdvanceAudioAsync(int sample) => InvokeAsync(
        () => _latestSample = Math.Max(_latestSample, sample));

    private void SubmitCore(SpeechRequest request)
    {
        // A request from a previous session is a wrong-generation result: drop it silently.
        if (request.Session != Session || _closing) return;
        _latestSample = Math.Max(_latestSample, request.EndSample);

        if (request.IsFinal)
        {
            if (request.Utterance <= _lastFinalEnqueued) return;
            _lastFinalEnqueued = request.Utterance;

            OnSpeechEnded?.Invoke(TextThrough(request.Utterance));
            _finalQueue.Add(request);
            _queuedFinalSamples += request.Audio.Count;

            if (!_overloadSignaled &&
                ((double)_queuedFinalSamples / SampleRate >= _backlogSeconds || _finalQueue.Count >= _backlogCount))
            {
                _overloadSignaled = true;
                OnIssue?.Invoke("Transcription is falling behind. Pausing capture to finish the recorded speech.");
                OnOverload?.Invoke();
            }
            StartFinalWorker();
        }
        else
        {
            if (request.Utterance <= _completedThrough) return;
            // A newly arrived snapshot never invalidates an in-flight result that is ahead
            // of it; only a strictly wider window replaces what is pending.
            if (PendingInterim is null || request.EndSample > PendingInterim.EndSample)
                PendingInterim = request;
            StartInterimWorker();
        }
    }

    // ── Interim lane ─────────────────────────────────────────────────────────────────────

    private void StartInterimWorker()
    {
        if (_interimTask is not null) return;
        _interimTask = RunInterimLaneAsync();
    }

    private async Task RunInterimLaneAsync()
    {
        while (PendingInterim is { } request && !_closing)
        {
            PendingInterim = null;
            if (request.Utterance <= _completedThrough) continue;

            var started = _now();
            using var inference = new CancellationTokenSource();
            _activeInterim = inference;

            // Cooperative deadline. Cancelling the in-flight decode rather than starting a
            // replacement alongside it is deliberate: two concurrent calls on one model is
            // what this lane exists to prevent.
            using var watchdogStop = new CancellationTokenSource();
            var watchdog = WatchdogAsync(inference, watchdogStop.Token);

            SpeechOutcome outcome;
            try
            {
                outcome = await _interimDecode(request, inference.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                outcome = new SpeechOutcome.Cancelled();
            }
            catch (Exception e)
            {
                outcome = new SpeechOutcome.Failure(e.Message);
            }

            await watchdogStop.CancelAsync().ConfigureAwait(true);
            _activeInterim = null;

            var expired = _now() - started >= _interimBudget;
            if (expired) outcome = new SpeechOutcome.TimedOut();
            Record(request, started, outcome);

            if (!_closing && request.Utterance > _completedThrough
                && _latestSample - request.EndSample <= 3 * SampleRate
                && outcome is SpeechOutcome.Success success)
            {
                // Results more than 3 s behind live audio are not worth showing — by the
                // time they land the speaker has moved on.
                if (!_captions.TryGetValue(request.Utterance, out var caption))
                    caption = new RollingCaption();

                if (caption.Update(success.Words, request.StartSample, request.EndSample))
                {
                    _captions[request.Utterance] = caption;
                    Publish();
                    OnCatchingUp?.Invoke(false);
                }
                else if (success.Words.Count == 0)
                {
                    OnIssue?.Invoke("The live model did not provide word timings. Waiting for final transcription.");
                }
            }
            else if (outcome is SpeechOutcome.Failure)
            {
                OnCatchingUp?.Invoke(true);
            }
        }
        _interimTask = null;
    }

    private async Task WatchdogAsync(CancellationTokenSource inference, CancellationToken stop)
    {
        try { await _delay(_interimBudget, stop).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        if (stop.IsCancellationRequested) return;

        try { await inference.CancelAsync().ConfigureAwait(true); }
        catch (ObjectDisposedException) { return; }
        OnCatchingUp?.Invoke(true);
    }

    // ── Final lane ───────────────────────────────────────────────────────────────────────

    private void StartFinalWorker()
    {
        if (_finalTask is not null) return;
        _finalTask = RunFinalLaneAsync();
    }

    private async Task RunFinalLaneAsync()
    {
        while (_finalQueue.Count > 0)
        {
            var request = _finalQueue[0];
            _finalQueue.RemoveAt(0);

            var started = _now();
            SpeechOutcome outcome;
            try
            {
                outcome = await _finalDecode(request, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception e)
            {
                outcome = new SpeechOutcome.Failure(e.Message);
            }
            Record(request, started, outcome);

            switch (outcome)
            {
                case SpeechOutcome.Success success:
                    try
                    {
                        if (OnFinal is { } onFinal)
                            await onFinal(success.Text, request.StartSample / SamplesPerMs,
                                          request.EndSample / SamplesPerMs).ConfigureAwait(true);
                    }
                    catch (Exception e)
                    {
                        OnIssue?.Invoke($"Transcript could not be journaled: {e.Message}");
                    }
                    break;

                case SpeechOutcome.Empty or SpeechOutcome.Filtered:
                    if (!string.IsNullOrEmpty(_captions.GetValueOrDefault(request.Utterance)?.Text))
                    {
                        OnIssue?.Invoke($"Final transcription rejected speech at " +
                                        $"{TimeFormat.Stamp(request.StartSample / SamplesPerMs)}. " +
                                        $"Temporary words were not saved.");
                    }
                    break;

                default:
                    OnIssue?.Invoke($"Final transcription failed at " +
                                    $"{TimeFormat.Stamp(request.StartSample / SamplesPerMs)}. " +
                                    $"This interval is missing from the saved transcript.");
                    break;
            }

            _completedThrough = request.Utterance;
            _captions.Remove(request.Utterance);
            _queuedFinalSamples -= request.Audio.Count;
            Publish();
            // The clipboard refreshes only after the matching temporary text is retired, so
            // a late final can neither duplicate it nor erase newer speech.
            OnFinalized?.Invoke(Hypothesis);
        }
        _finalTask = null;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drain both lanes and settle. The caller stops and drains capture first. Finals
    /// include their persistence acknowledgement, so when this returns every segment that
    /// will be saved is durable — no next session or model call may begin before then.
    /// </summary>
    public Task FinishAsync() => InvokeAsync(async () =>
    {
        _closing = true;
        PendingInterim = null;
        if (_activeInterim is { } active)
        {
            try { await active.CancelAsync().ConfigureAwait(true); } catch (ObjectDisposedException) { }
        }

        if (_interimTask is { } interim) await interim.ConfigureAwait(true);
        if (_finalTask is { } final) await final.ConfigureAwait(true);
        OnCatchingUp?.Invoke(false);
    });

    private Task InvokeAsync(Func<Task> func)
    {
        if (_ownedDispatcher is { } dispatcher) return dispatcher.InvokeAsync(func);

        var completion = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(_ =>
        {
            try { completion.TrySetResult(func()); }
            catch (Exception e) { completion.TrySetException(e); }
        }, null);
        return Unwrap(completion.Task);

        static async Task Unwrap(Task<Task> outer) => await (await outer.ConfigureAwait(false)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await FinishAsync().ConfigureAwait(false);
        _ownedDispatcher?.Dispose();
    }

    // ── Publishing and metrics ───────────────────────────────────────────────────────────

    private string TextThrough(int utterance = int.MaxValue) => string.Join(" ", _captions.Keys
        .Where(k => k <= utterance).OrderBy(k => k)
        .Select(k => _captions[k].Text)
        .Where(t => !string.IsNullOrEmpty(t)));

    private void Publish()
    {
        Hypothesis = TextThrough();
        OnHypothesis?.Invoke(Hypothesis);
    }

    private void Record(SpeechRequest request, double started, SpeechOutcome outcome)
    {
        var fallbacks = outcome is SpeechOutcome.Success success ? success.Fallbacks : 0;
        var metric = new CaptionMetric(
            Session: request.Session,
            Utterance: request.Utterance,
            WindowStartMs: request.StartSample / SamplesPerMs,
            WindowEndMs: request.EndSample / SamplesPerMs,
            IsFinal: request.IsFinal,
            QueueMs: (int)(Math.Max(0, started - request.SubmittedAt) * 1000),
            DecodeMs: (int)(Math.Max(0, _now() - started) * 1000),
            AudioLagMs: Math.Max(0, _latestSample - request.EndSample) / SamplesPerMs,
            FinalQueueDepth: _finalQueue.Count + (_finalTask is null ? 0 : 1),
            FinalBacklogMs: _queuedFinalSamples / SamplesPerMs,
            Outcome: outcome.Label,
            Fallbacks: fallbacks);

        _metrics.Add(metric);
        if (_metrics.Count > MetricsCap) _metrics.RemoveRange(0, _metrics.Count - MetricsCap);
        OnMetric?.Invoke(metric);
    }
}
