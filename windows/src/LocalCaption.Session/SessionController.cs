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
        if (_transitioning || HasUnsavedSession || !Orchestrator.ModelReady) return;
        if (Phase is not (SessionPhase.Ready or SessionPhase.Saved or SessionPhase.Failed)) return;

        _transitioning = true;
        try
        {
            _sessionId = Guid.NewGuid();
            _startedAt = DateTimeOffset.Now;
            Orchestrator.ApplyTuning(_env.Config);

            SessionName = _env.Config.General.SessionNamePrefix + TimeFormat.FileStamp(_startedAt);
            _transcript = new Transcript();
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

    private string CommittedText => string.Join(" ", _transcript.Segments.Select(s => s.Text));

    // ── clipboard (write-only — §7.4, §12.1) ─────────────────────────────────────────────

    /// <summary>Copy the last N completed sentences, N from Settings.</summary>
    public void CopyLastN() => CopyLastN("");

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
        _awake?.Dispose();
        _awake = null;
        await Orchestrator.DisposeAsync().ConfigureAwait(false);
        _journal?.Dispose();
        _journal = null;
    }
}
