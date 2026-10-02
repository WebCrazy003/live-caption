using LocalCaption.Audio;
using LocalCaption.Core.Captions;
using LocalCaption.Core.Data;
using LocalCaption.Core.Transcripts;

namespace LocalCaption.Session;

/// <summary>
/// Where a session is in its life: <c>PREPARING → READY → RECORDING ⇄ PAUSED → SAVING →
/// SAVED</c>, with <c>FAILED</c> and the transitional <c>PAUSING</c> ("Finishing speech…").
/// </summary>
public enum SessionPhase
{
    Preparing, Ready, Recording, Pausing, Paused, Saving, Saved, Failed,
}

/// <summary>
/// Owns the session state machine, the in-memory transcript, the crash-recovery journal, the
/// elapsed clock and the save-on-stop step. Drives <see cref="StreamingOrchestrator"/>.
/// </summary>
/// <remarks>
/// <para>Port of the macOS <c>SessionController.swift</c> (SPEC-WINDOWS.md §8). The live AI
/// summary is deliberately absent — §1.3 puts it out of scope for Windows v1.</para>
/// <para><b>Every behaviour §8 calls "hard-won" is here on purpose</b>, because each one came
/// from a bug in the shipped app rather than from a design:</para>
/// <list type="bullet">
///   <item><description>Pause finalises the in-flight utterance and freezes the
///   sample-based clock, so <c>duration_seconds</c> excludes paused time.</description></item>
///   <item><description>Capture-fell-behind and transcription backlog both trigger an
///   automatic pause that drains retained audio rather than dropping it.</description></item>
///   <item><description>A failed journal write never silently succeeds: the segment is kept
///   in memory and the user is told to Stop to save.</description></item>
///   <item><description>Start is refused while an unsaved session exists, and a failed
///   capture start deletes its own empty journal so a retry is clean.</description></item>
/// </list>
/// </remarks>
public sealed class SessionController : IAsyncDisposable
{
    private readonly AppEnvironment _env;
    private readonly List<string> _paragraphs = [];

    private Transcript _transcript = new();
    private JournalWriter? _journal;
    private CancellationTokenSource? _clock;
    private bool _transitioning;
    private bool _capturePauseRequested;
    private bool _modelsStale;
    private Guid _sessionId = Guid.NewGuid();
    private DateTimeOffset _startedAt = DateTimeOffset.Now;
    private IDisposable? _awake;

    public SessionController(AppEnvironment env)
    {
        _env = env;
        Orchestrator = new StreamingOrchestrator();
        Orchestrator.OnFinal = IngestFinalAsync;
        Orchestrator.OnChanged = () => Changed?.Invoke();
        Orchestrator.OnSpeechEnded = interim => CopyLastN(interim);
        Orchestrator.OnFinalized = pending => CopyLastN(pending);
        Orchestrator.OnCaptureMustPause = () =>
        {
            _capturePauseRequested = true;
            if (!_transitioning) FinishTransition();
        };
    }

    public StreamingOrchestrator Orchestrator { get; }

    public SessionPhase Phase { get; private set; } = SessionPhase.Preparing;
    public string SessionName { get; private set; } = "";
    public IReadOnlyList<string> Paragraphs => _paragraphs;
    public string Current { get; private set; } = "";
    public string Elapsed { get; private set; } = "00:00:00";
    public string? SavedTranscriptPath { get; private set; }
    public string? SaveError { get; private set; }

    /// <summary>Raised whenever anything a view would render has changed.</summary>
    public event Action? Changed;

    /// <summary>Set when a caption has been copied, for the brief UI confirmation.</summary>
    public event Action? Copied;

    /// <summary>
    /// Raised once per turn, when the speaker has been quiet for <c>send.turn_gap_ms</c> —
    /// with the turn's text. What "send automatically" listens to.
    /// </summary>
    public event Action<string>? TurnCompleted;

    public string DisplayName => SessionName.Length == 0 ? "New Session" : SessionName;

    /// <summary>True once any final has been committed — gates "Copy last N".</summary>
    public bool HasTranscript => !_transcript.IsEmpty;

    /// <summary>
    /// A session exists that has not reached disk. Start is refused while this is true, so a
    /// second recording cannot orphan the first one's journal.
    /// </summary>
    public bool HasUnsavedSession => _journal is not null || (!_transcript.IsEmpty && Phase != SessionPhase.Saved);

    // ── lifecycle ────────────────────────────────────────────────────────────────────────

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        Phase = SessionPhase.Preparing;
        Notify();
        await Orchestrator.PrepareModelAsync(_env.Config, cancellationToken).ConfigureAwait(false);
        Phase = Orchestrator.ModelReady ? SessionPhase.Ready : SessionPhase.Failed;
        Notify();
    }

    public async Task StartAsync()
    {
        if (_transitioning || HasUnsavedSession) return;
        if (!Orchestrator.ModelReady && !_modelsStale) return;
        if (Phase is not (SessionPhase.Ready or SessionPhase.Saved or SessionPhase.Failed)) return;

        _transitioning = true;
        try
        {
            if (!await EnsureModelsCurrentAsync().ConfigureAwait(false)) return;

            _sessionId = Guid.NewGuid();
            _startedAt = DateTimeOffset.Now;
            Orchestrator.ApplyTuning(_env.Config);

            SessionName = _env.Config.General.SessionNamePrefix + TimeFormat.FileStamp(_startedAt);
            _transcript = new Transcript();
            _lastTurnAnnouncedEndMs = -1;
            _paragraphs.Clear();
            Current = "";
            SavedTranscriptPath = null;
            SaveError = null;

            try
            {
                _journal = new JournalWriter(_sessionId);
            }
            catch (Exception e)
            {
                SaveError = $"Could not create the recovery journal: {e.Message}";
                Phase = SessionPhase.Failed;
                return;
            }

            try
            {
                await Orchestrator.StartCaptureAsync(_env.Config).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // No processing loop started, so this journal will never hold anything.
                // Removing it here is what lets the user fix the source and retry cleanly
                // rather than being told an unsaved session is in the way (§8).
                _journal?.DeleteFile();
                _journal?.Dispose();
                _journal = null;
                Phase = SessionPhase.Failed;
                return;
            }

            // §4.7.6: the user is watching over Jump Desktop and touching nothing, so nothing
            // they do counts as activity. A machine that sleeps at minute 30 of a 90-minute
            // interview is the worst failure this app has.
            _awake = SleepPrevention.Acquire();

            Phase = SessionPhase.Recording;
            StartClock();
        }
        finally
        {
            FinishTransition();
        }
    }

    public async Task PauseAsync()
    {
        if (_transitioning || Phase != SessionPhase.Recording) return;

        _transitioning = true;
        try
        {
            Phase = SessionPhase.Pausing;      // "Finishing speech…"
            Notify();
            StopClock();
            await Orchestrator.PauseAndFinalizeAsync().ConfigureAwait(false);
            Elapsed = TimeFormat.Clock(Orchestrator.RecordedMs / 1000);
            Phase = SessionPhase.Paused;
        }
        finally
        {
            FinishTransition();
        }
    }

    public async Task ResumeAsync()
    {
        if (_transitioning) return;
        if (Phase != SessionPhase.Paused && !(Phase == SessionPhase.Failed && HasUnsavedSession)) return;

        _transitioning = true;
        try
        {
            // §5.8: if the GPU went away, the engine is rebuilt on the CPU before the stream
            // restarts. The session and its journal survive the switch — losing a transcript
            // to a power-profile change is the worst failure this app can have.
            if (Orchestrator.GpuLost)
            {
                Phase = SessionPhase.Preparing;
                Notify();
                await Orchestrator.FallBackToCpuAsync(_env.Config).ConfigureAwait(false);
                if (!Orchestrator.ModelReady)
                {
                    Phase = SessionPhase.Failed;
                    return;
                }
            }

            if (!await EnsureModelsCurrentAsync().ConfigureAwait(false)) return;
            Orchestrator.ApplyTuning(_env.Config);

            try
            {
                await Orchestrator.ResumeCaptureAsync(_env.Config).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Phase = SessionPhase.Failed;
                return;
            }

            Phase = SessionPhase.Recording;
            StartClock();
        }
        finally
        {
            FinishTransition();
        }
    }

    public async Task StopAsync()
    {
        if (_transitioning) return;
        if (Phase is not (SessionPhase.Recording or SessionPhase.Paused) &&
            !(Phase == SessionPhase.Failed && HasUnsavedSession)) return;

        _transitioning = true;
        try
        {
            Phase = SessionPhase.Saving;
            Notify();
            StopClock();
            await Orchestrator.StopAndFinalizeAsync().ConfigureAwait(false);

            _awake?.Dispose();
            _awake = null;

            Phase = Save() ? SessionPhase.Saved : SessionPhase.Failed;
        }
        finally
        {
            FinishTransition();
        }
    }

    // ── live reconfiguration (the quick toolbar) ─────────────────────────────────────────

    /// <summary>
    /// Apply a settings change made from the toolbar without ending the session.
    /// </summary>
    /// <param name="reloadModels">The change names different models or a different backend.</param>
    /// <param name="interrupt">
    /// Whether a recording may be paused and resumed to apply it now. False leaves a
    /// recording alone and applies the change at its next pause or the next Start.
    /// </param>
    /// <remarks>
    /// <para>Everything the capture layer and the engine read from config is read when a
    /// stream <i>begins</i> — so applying a change mid-recording means ending the stream and
    /// beginning another, which is exactly what pause and resume already do, in-flight
    /// utterance finalised and journal intact. This adds no second mechanism; it presses the
    /// two buttons in order.</para>
    /// <para>A model swap costs a few seconds in which nothing is captured. That is the
    /// user's trade to make, and they made it by picking the model.</para>
    /// </remarks>
    public async Task ReconfigureAsync(bool reloadModels, bool interrupt = true)
    {
        if (reloadModels)
        {
            // Fetch first, while the recording carries on with the models it has. Only a file
            // that is already here is worth pausing for.
            if (!await Orchestrator.PredownloadAsync(_env.Config).ConfigureAwait(false)) return;
            _modelsStale = true;
        }

        // Mid-transition there is nothing safe to interrupt. The stale flag is honoured by
        // the next Start or Resume, so the choice is not lost — only deferred.
        if (_transitioning) return;

        if (Phase == SessionPhase.Recording)
        {
            if (!interrupt) return;      // deferred to the next pause, by the same flag

            await PauseAsync().ConfigureAwait(false);
            if (Phase == SessionPhase.Paused) await ResumeAsync().ConfigureAwait(false);
            return;
        }

        if (!reloadModels || Phase is not (SessionPhase.Ready or SessionPhase.Saved or
                                           SessionPhase.Failed or SessionPhase.Paused)) return;

        _transitioning = true;
        var before = Phase;
        try
        {
            if (!await EnsureModelsCurrentAsync().ConfigureAwait(false)) return;

            // Back to where it was. Saved must stay Saved — HasUnsavedSession reads the phase,
            // and a saved transcript relabelled Ready would block the next Start.
            Phase = before == SessionPhase.Failed && !HasUnsavedSession ? SessionPhase.Ready : before;
        }
        finally
        {
            FinishTransition();
        }
    }

    /// <summary>Reload the engine if the toolbar changed it. False means it could not load.</summary>
    private async Task<bool> EnsureModelsCurrentAsync()
    {
        if (!_modelsStale && Orchestrator.ModelReady) return true;

        var before = Phase;
        Phase = SessionPhase.Preparing;
        Notify();

        _modelsStale = false;
        var loaded = await Orchestrator.ReloadModelAsync(_env.Config).ConfigureAwait(false);
        Phase = loaded ? before : SessionPhase.Failed;
        return loaded;
    }

    /// <summary>
    /// Leave the transition, then honour a pause the capture layer asked for while we were
    /// busy — an overload during Stop must not restart the machine.
    /// </summary>
    private void FinishTransition()
    {
        _transitioning = false;
        Notify();

        if (!_capturePauseRequested) return;
        _capturePauseRequested = false;
        if (Phase == SessionPhase.Recording) _ = PauseAsync();
    }

    // ── transcript ───────────────────────────────────────────────────────────────────────

    private async Task IngestFinalAsync(string text, int startMs, int endMs)
    {
        var segment = new TranscriptSegment
        {
            Text = text,
            TStartMs = startMs,
            TEndMs = endMs,
            CreatedAt = TimeFormat.Iso(DateTimeOffset.Now),
        };

        try
        {
            if (_journal is not { } journal) throw new InvalidOperationException("no journal");
            await journal.AppendAsync(segment).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Keep the segment in memory for Stop, and say so. Never let a failed disk write
            // look durable — the caller publishes finals on this acknowledgement (§6.2).
            SaveError = $"Recovery journal write failed: {e.Message}. Stop to save the transcript.";
        }

        _transcript.Append(segment);
        AddToParagraphs(text);
        WatchForTurnEnd();
        Notify();
    }

    /// <summary>Group finals into paragraphs; break at ~4 sentences or ~100 words.</summary>
    private void AddToParagraphs(string text)
    {
        Current = Current.Length == 0 ? text : Current + " " + text;
        if (Filters.SentenceCount(Current) < 4 && Filters.WordCount(Current) < 100) return;
        _paragraphs.Add(Current);
        Current = "";
    }

    // Bookmarks are segments so that they are journalled and saved like everything else, but
    // they are the user's marks, not the interviewer's words — nothing copied or sent has them.
    private string CommittedText => string.Join(" ", _transcript.Segments.Where(s => !Turns.IsBookmark(s)).Select(s => s.Text));

    /// <summary>The last unbroken stretch of speech — "what they just asked". See <see cref="Turns"/>.</summary>
    public string LastTurn() => Turns.LastText(_transcript.Segments, _env.Config.Send.TurnGapMs,
                                               Orchestrator.Hypothesis, Orchestrator.RecordedMs);

    /// <summary>Copy the last turn. False when there is nothing to copy yet.</summary>
    public bool CopyLastQuestion()
    {
        var text = LastTurn();
        if (text.Length == 0) return false;
        if (_env.Clipboard?.Invoke(text) == true) Copied?.Invoke();
        return true;
    }

    /// <summary>
    /// Mark this moment in the transcript, to find again afterwards.
    /// </summary>
    /// <remarks>
    /// Goes through the same journal-then-memory path as a caption, so a bookmark survives a
    /// crash exactly as well as the words around it, and lands in the saved file in order.
    /// </remarks>
    public async Task<bool> BookmarkAsync()
    {
        if (Phase is not (SessionPhase.Recording or SessionPhase.Paused)) return false;

        var now = Orchestrator.RecordedMs;
        var segment = new TranscriptSegment
        {
            Text = $"{Turns.BookmarkLead} bookmark {TimeFormat.Clock(now / 1000)}]",
            TStartMs = now,
            TEndMs = now,
            CreatedAt = TimeFormat.Iso(DateTimeOffset.Now),
        };

        try
        {
            if (_journal is { } journal) await journal.AppendAsync(segment).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            SaveError = $"Recovery journal write failed: {e.Message}. Stop to save the transcript.";
        }

        _transcript.Append(segment);

        // On a line of its own: a mark buried mid-paragraph is a mark nobody finds.
        if (Current.Length > 0) _paragraphs.Add(Current);
        _paragraphs.Add(segment.Text);
        Current = "";
        Notify();
        return true;
    }

    // ── turn completion ──────────────────────────────────────────────────────────────────

    private CancellationTokenSource? _turnTimer;
    private int _lastTurnAnnouncedEndMs = -1;

    /// <summary>
    /// Restart the quiet-timer. Called whenever speech is heard; when it finally runs out,
    /// the turn is over.
    /// </summary>
    private void WatchForTurnEnd()
    {
        _turnTimer?.Cancel();
        _turnTimer?.Dispose();
        var timer = new CancellationTokenSource();
        _turnTimer = timer;

        // A final arrives the endpoint silence (~600 ms) plus a decode after the words stop,
        // so that much of the gap has already passed by the time this starts counting.
        var wait = Math.Max(400, _env.Config.Send.TurnGapMs - _env.Config.Asr.EndpointSilenceMs);
        _ = Task.Delay(wait, timer.Token).ContinueWith(_ =>
        {
            if (Orchestrator.Hypothesis.Length > 0) { WatchForTurnEnd(); return; }     // still talking

            var turn = Turns.Last(_transcript.Segments, _env.Config.Send.TurnGapMs);
            if (turn.Count == 0 || turn[^1].TEndMs == _lastTurnAnnouncedEndMs) return;

            _lastTurnAnnouncedEndMs = turn[^1].TEndMs;
            TurnCompleted?.Invoke(string.Join(" ", turn.Select(s => s.Text.Trim())));
        }, timer.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    // ── clipboard (write-only — §7.4, §12.1) ─────────────────────────────────────────────

    /// <summary>Copy the last N completed sentences, N from Settings.</summary>
    public void CopyLastN() => CopyLastN("");

    /// <summary>Copy every committed caption so far — the transcript as it stands.</summary>
    public void CopyAll()
    {
        var paragraphs = Current.Length == 0 ? _paragraphs : [.. _paragraphs, Current];
        var text = string.Join(Environment.NewLine + Environment.NewLine, paragraphs);
        if (text.Length == 0) return;
        if (_env.Clipboard?.Invoke(text) == true) Copied?.Invoke();
    }

    /// <summary>
    /// At an endpoint, copy the latest interim rather than waiting for the final model; the
    /// final refreshes it once the matching provisional is retired.
    /// </summary>
    private void CopyLastN(string interim)
    {
        if (!_env.Config.Clipboard.AutoUpdate && interim.Length > 0) return;

        var text = Sentences.LastN(CommittedText, interim, _env.Config.Clipboard.RecentSentences);
        if (text.Length == 0) return;
        if (_env.Clipboard?.Invoke(text) == true) Copied?.Invoke();
    }

    // ── save ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Write <c>.txt</c> + <c>.json</c> and the database row, then delete the journal.
    /// </summary>
    /// <remarks>
    /// Returns false on failure <b>and keeps the journal</b>, so a session that could not be
    /// written is still recoverable on the next launch rather than lost to a full disk.
    /// </remarks>
    private bool Save()
    {
        if (Current.Length > 0)
        {
            _paragraphs.Add(Current);
            Current = "";
        }

        var ended = DateTimeOffset.Now;
        var duration = Orchestrator.RecordedMs / 1000;

        try
        {
            var result = TranscriptWriter.Save(_transcript, _env.Config.General.TranscriptFolder,
                                               SessionName, _startedAt, ended, duration,
                                               _env.Config.Caption.ShowTimestamps);

            try
            {
                _env.Store.Insert(new SessionRecord
                {
                    SessionName = SessionName,
                    CreatedAt = TimeFormat.Iso(_startedAt),
                    EndedAt = TimeFormat.Iso(ended),
                    DurationSeconds = duration,
                    TranscriptFile = result.TxtPath,
                });
            }
            catch (Exception)
            {
                // The transcript is on disk, which is what matters. A missing index row is a
                // session that will not appear in the list, not a lost interview.
            }

            _journal?.DeleteFile();
            _journal?.Dispose();
            _journal = null;

            SavedTranscriptPath = result.TxtPath;
            SaveError = null;
            return true;
        }
        catch (Exception e)
        {
            SaveError = $"Could not save the transcript: {e.Message}";
            return false;      // keep the journal — the session stays recoverable
        }
    }

    // ── clock (sample-based, frozen while paused) ────────────────────────────────────────

    private void StartClock()
    {
        StopClock();
        var clock = new CancellationTokenSource();
        _clock = clock;
        _ = TickAsync(clock.Token);
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Elapsed = TimeFormat.Clock(Orchestrator.RecordedMs / 1000);
                Notify();
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void StopClock()
    {
        _clock?.Cancel();
        _clock?.Dispose();
        _clock = null;
        Elapsed = TimeFormat.Clock(Orchestrator.RecordedMs / 1000);
    }

    private void Notify() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        StopClock();
        _turnTimer?.Cancel();
        _awake?.Dispose();
        _awake = null;
        await Orchestrator.DisposeAsync().ConfigureAwait(false);
        _journal?.Dispose();
        _journal = null;
    }
}
