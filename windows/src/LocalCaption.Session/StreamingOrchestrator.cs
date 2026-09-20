using LocalCaption.Asr;
using LocalCaption.Audio;
using LocalCaption.Core;
using LocalCaption.Core.Audio;
using LocalCaption.Core.Captions;
using LocalCaption.Core.Data;

namespace LocalCaption.Session;

/// <summary>
/// Streaming ASR: capture → VAD endpointing → interim/final decode.
/// </summary>
/// <remarks>
/// <para>Port of the macOS <c>StreamingOrchestrator.swift</c>. Dual-model (§5.1): a fast
/// interim model drives provisional partials aligned by word timestamps, and an accurate
/// final model produces committed captions on endpoint. Finalised segments flow out through
/// <see cref="OnFinal"/> with sample-based, pause-aware timing.</para>
/// <para><b>The capture source is chosen here, not below.</b> §4.1 makes process loopback
/// the default and endpoint loopback the fallback; both arrive as
/// <see cref="IAudioCapture"/> and nothing downstream knows which it got.</para>
/// <para>Nothing in this class blocks: the pipeline owns its own single-threaded
/// <c>SynchronizationContext</c> (§6.2), and the capture layer runs its own thread. The
/// poll loop here only moves samples between them.</para>
/// </remarks>
public sealed class StreamingOrchestrator : IAsyncDisposable
{
    private readonly List<string> _issues = [];

    private WhisperEngine? _engine;
    private IAudioCapture? _capture;
    private CaptureProcessor? _processor;
    private CaptionPipeline? _pipeline;
    private CancellationTokenSource? _loop;
    private Task? _loopTask;
    private Task? _endingTask;
    private int _totalSamples;
    private Guid _activeCapture;
    private int _consecutiveFailures;
    private SpeechSegmenter.Tuning _tuning = new();
    private string? _gpuBanner;

    public string Hypothesis { get; private set; } = "";
    public string Status { get; private set; } = "Preparing…";
    public string Detail { get; private set; } = "";
    public double DownloadFraction { get; private set; }
    public bool IsDownloading { get; private set; }
    public bool ModelReady { get; private set; }
    public string? ErrorText { get; private set; }

    public string InterimName { get; private set; } = "";
    public string FinalName { get; private set; } = "";
    public string ModelLabel => FinalName.Length == 0 ? "" : $"{InterimName} · {FinalName}";

    /// <summary>What the capture layer is tapping, for the session header (§4.7.5).</summary>
    public string SourceName => _capture?.Source ?? "";

    /// <summary>Live peak level, 0–1, for the meter §4.7.5 makes a requirement.</summary>
    public float Level => _capture?.Level ?? 0;

    /// <summary>Elapsed recorded audio. Sample-based, so it freezes while paused.</summary>
    public int RecordedMs => _totalSamples / 16;

    /// <summary>
    /// How far the capture clock has drifted from elapsed time, and what it took to hold it.
    /// </summary>
    /// <remarks>
    /// Worth logging at Stop and worth watching over a long session: §4.3's acceptance is
    /// ±100 ms, and the failure it guards against — a transcript whose timestamps are quietly
    /// wrong from the first pause onward — is invisible without this.
    /// </remarks>
    public string ClockReport => _capture is WasapiCapture wasapi
        ? $"drift {wasapi.DriftMs:+0;-0} ms · {wasapi.ClockReport}"
        : "no capture";

    /// <summary>Raised with the durable-journal acknowledgement still pending — see §6.2.</summary>
    public Func<string, int, int, Task>? OnFinal { get; set; }
    public Action<string>? OnSpeechEnded { get; set; }
    public Action<string>? OnFinalized { get; set; }
    public Action? OnCaptureMustPause { get; set; }
    public Action? OnChanged { get; set; }

    // ── model preparation (once) ─────────────────────────────────────────────────────────

    /// <summary>Download what is missing, load both models, warm them. Idempotent.</summary>
    public async Task PrepareModelAsync(Config config, CancellationToken cancellationToken = default)
    {
        if (ModelReady) return;

        ErrorText = null;
        InterimName = config.Asr.InterimModel;
        FinalName = config.Asr.FinalModel;

        var backend = Enum.TryParse<AsrBackend>(config.Asr.Backend, ignoreCase: true, out var parsed)
            ? parsed
            : AsrBackend.Auto;

        // §5.8: probe at every model load, not once at install — Eco mode and the MUX switch
        // change the answer between runs. If the GPU is not there, a turbo-sized choice is
        // not "slower", it is unusable, so the models are downgraded and the banner says so.
        var onGpu = BackendProbe.UsesGpu(BackendProbe.Resolve(backend));
        var (interim, final, banner) = AsrFallback.Choose(onGpu, InterimName, FinalName);
        InterimName = interim;
        FinalName = final;
        _gpuBanner = banner;

        var engine = new WhisperEngine(InterimName, FinalName, backend, config.Asr.Threads);
        _engine = engine;

        try
        {
            var info = await engine.PrepareAsync(AppPaths.Models,
                onStatus: status => { Status = status; Changed(); },
                onDownload: (model, fraction) => UpdateDownload(model, fraction),
                cancellationToken).ConfigureAwait(false);

            IsDownloading = false;
            DownloadFraction = 1;
            ModelReady = true;
            Status = $"Ready — {info}";

            // §5.2: a silent fall back to the CPU must be visible rather than merely felt.
            //
            // Two ways to end up there, and the quieter one is the common one on this
            // machine: asking for `auto` when no CUDA runtime is present resolves to the CPU
            // without anything having "failed", so FellBack is false and nothing is said. The
            // user then waits 17 seconds a window for large-v3-turbo and has no idea why.
            ErrorText = info.FellBack
                ? "The GPU backend could not be loaded, so speech recognition is running on the CPU. " +
                  "Captions will lag badly — see Settings ▸ Speech recognition."
                : _gpuBanner;
        }
        catch (Exception e)
        {
            IsDownloading = false;
            ModelReady = false;
            Status = "Failed";
            ErrorText = e.Message;
        }

        Changed();
    }

    /// <summary>The GPU vanished mid-session and the engine has not yet been rebuilt (§5.8).</summary>
    public bool GpuLost { get; private set; }

    /// <summary>
    /// Rebuild the engine on the CPU after the GPU has gone, so a paused session can resume.
    /// </summary>
    /// <remarks>
    /// §5.8's last line is the whole point: never lose the session over a GPU state change.
    /// The transcript so far is already journalled; this just gets the next word decoded.
    /// </remarks>
    public async Task FallBackToCpuAsync(Config config, CancellationToken cancellationToken = default)
    {
        if (!GpuLost) return;

        var (interim, final) = AsrFallback.CpuModels;
        Status = $"Switching to {interim} + {final} on the CPU…";
        Changed();

        if (_engine is { } old) { try { await old.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } }

        InterimName = interim;
        FinalName = final;
        ModelReady = false;
        _engine = new WhisperEngine(interim, final, AsrBackend.Cpu, config.Asr.Threads);

        try
        {
            var info = await _engine.PrepareAsync(AppPaths.Models, cancellationToken: cancellationToken)
                                    .ConfigureAwait(false);
            ModelReady = true;
            GpuLost = false;
            _consecutiveFailures = 0;
            Status = $"Ready — {info}";
            ErrorText = AsrFallback.Choose(false, interim, final).Banner;
        }
        catch (Exception e)
        {
            ModelReady = false;
            ErrorText = $"Could not fall back to the CPU: {e.Message}";
        }

        Changed();
    }

    public void ApplyTuning(Config config)
    {
        // Sensitivity 0–3 maps onto the RMS gate; the table is the macOS one verbatim.
        float[] thresholds = [0.030f, 0.020f, 0.015f, 0.008f];
        var index = Math.Clamp(config.Audio.VadSensitivity, 0, 3);
        _tuning = new SpeechSegmenter.Tuning(config.Asr.EndpointSilenceMs, config.Asr.InterimIntervalMs,
                                             config.Asr.MaxUtteranceS, thresholds[index]);
    }

    // ── capture lifetime ─────────────────────────────────────────────────────────────────

    public async Task StartCaptureAsync(Config config)
    {
        if (_endingTask is { } ending) await ending.ConfigureAwait(false);
        _totalSamples = 0;
        await BeginCaptureAsync(config).ConfigureAwait(false);
    }

    /// <summary>Resume after a pause — the sample clock carries on where it stopped.</summary>
    public async Task ResumeCaptureAsync(Config config)
    {
        if (_endingTask is { } ending) await ending.ConfigureAwait(false);
        await BeginCaptureAsync(config).ConfigureAwait(false);
    }

    public Task PauseAndFinalizeAsync() => EndCaptureAndFinalizeAsync();

    public Task StopAndFinalizeAsync() => EndCaptureAndFinalizeAsync();

    private async Task BeginCaptureAsync(Config config)
    {
        if (_capture is not null || _engine is null)
            throw new InvalidOperationException("Capture is already active, or the speech model is not ready.");

        ErrorText = null;
        Detail = "";
        Hypothesis = "";

        var session = Guid.NewGuid();
        _activeCapture = session;

        var buffer = new CaptureBuffer();
        var segmenter = new SpeechSegmenter(session, _tuning, _totalSamples);
        var processor = new CaptureProcessor(buffer, segmenter);
        _processor = processor;

        var engine = _engine;
        var pipeline = new CaptionPipeline(session,
            interim: async (request, token) => Watch(await engine.TranscribeInterimAsync(request.Audio, token)),
            final: async (request, token) => Watch(await engine.TranscribeFinalAsync(request.Audio, token)));
        _pipeline = pipeline;

        pipeline.OnHypothesis = text => { Hypothesis = text; Changed(); };
        pipeline.OnFinal = async (text, start, end) =>
        {
            if (OnFinal is { } handler) await handler(text, start, end).ConfigureAwait(false);
        };
        pipeline.OnSpeechEnded = text => OnSpeechEnded?.Invoke(text);
        pipeline.OnFinalized = text => OnFinalized?.Invoke(text);
        pipeline.OnIssue = issue => { ErrorText = issue; _issues.Add(issue); Changed(); };
        pipeline.OnOverload = () => OnCaptureMustPause?.Invoke();
        pipeline.OnCatchingUp = behind =>
        {
            Detail = behind ? "Live captions are catching up…" : "";
            Changed();
        };

        var capture = Create(config);
        _capture = capture;
        capture.Samples += samples => buffer.Append(samples);
        capture.Fault += fault =>
        {
            if (_activeCapture != session) return;
            ErrorText = fault.Message;
            // §4.2: a recoverable fault rebuilds the stream and keeps the session; anything
            // else pauses and asks the user, rather than recording silence for an hour.
            if (fault.Recoverable && capture is WasapiCapture wasapi) wasapi.Restart();
            else OnCaptureMustPause?.Invoke();
            Changed();
        };

        try
        {
            capture.Start();
        }
        catch (Exception e)
        {
            _activeCapture = Guid.Empty;
            capture.Dispose();
            _capture = null;
            _processor = null;
            await pipeline.DisposeAsync().ConfigureAwait(false);
            _pipeline = null;
            ErrorText = e.Message;
            throw;
        }

        Status = $"Recording — {capture.Source}";
        _loop = new CancellationTokenSource();
        _loopTask = PollAsync(processor, _loop.Token);
        Changed();
    }

    /// <summary>
    /// The capture source §4.1 asks for: process loopback by default, endpoint loopback as
    /// the fallback and whenever no process has been chosen.
    /// </summary>
    private static IAudioCapture Create(Config config) =>
        config.Audio.CaptureMode.Equals("process", StringComparison.OrdinalIgnoreCase) &&
        config.Audio.TargetProcess is { Length: > 0 } target
            ? new ProcessLoopbackCapture(target)
            : new EndpointLoopbackCapture(config.Audio.OutputDevice);

    private async Task PollAsync(CaptureProcessor processor, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Receive(await processor.PollAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Watch decode outcomes for the GPU disappearing underneath us (§5.8).
    /// </summary>
    /// <remarks>
    /// Eco mode, a MUX flip or a driver reset take the device away mid-decode, and what
    /// reaches here is a run of failures. §5.8 is explicit that this must be treated exactly
    /// like a capture failure — surface it, auto-pause, drain the retained audio, let the user
    /// resume — rather than growing a second recovery mechanism. So it ends in the same
    /// <see cref="OnCaptureMustPause"/> the capture layer uses.
    /// </remarks>
    private SpeechOutcome Watch(SpeechOutcome outcome)
    {
        if (outcome is not SpeechOutcome.Failure)
        {
            _consecutiveFailures = 0;
            return outcome;
        }

        if (++_consecutiveFailures < AsrFallback.FailuresBeforeGpuPresumedLost) return outcome;
        if (GpuLost) return outcome;                      // already handled; do not pause twice

        GpuLost = _engine?.Info?.Backend is AsrBackend.Cuda or AsrBackend.Metal;
        ErrorText = GpuLost
            ? "The GPU stopped responding — Eco mode or a driver reset can do this. Recording is " +
              "paused; resume to continue on the CPU."
            : "Speech recognition is failing repeatedly. Recording is paused.";

        Changed();

        // Off the pipeline's own context. This runs inside a decode callback, and the pause
        // it triggers ends in CaptionPipeline.FinishAsync — which posts back to that same
        // single-threaded context. The capture-fault path raises this from the capture
        // thread and never had the problem; this one would be asking the pipeline to wind
        // itself up from inside itself.
        var pause = OnCaptureMustPause;
        if (pause is not null) _ = Task.Run(pause);

        return outcome;
    }

    private void Receive(CaptureProcessor.Output output)
    {
        _totalSamples = output.TotalSamples;
        _pipeline?.AdvanceAudio(_totalSamples);

        foreach (var request in output.Requests) _pipeline?.Submit(request);

        if (output.Batch.DroppedSamples > 0)
        {
            // Latched overflow: the buffer stopped accepting rather than silently dropping a
            // growing backlog, so the loss is exact and worth reporting in milliseconds.
            ErrorText = $"Audio capture fell behind; {output.Batch.DroppedSamples / 16} ms could not be " +
                        "captured. Pausing to finish the retained audio.";
            OnCaptureMustPause?.Invoke();
            Changed();
        }
    }

    private async Task EndCaptureAndFinalizeAsync()
    {
        if (_endingTask is { } inFlight)
        {
            await inFlight.ConfigureAwait(false);
            return;
        }

        var task = EndCoreAsync();
        _endingTask = task;
        try { await task.ConfigureAwait(false); }
        finally { _endingTask = null; }
    }

    private async Task EndCoreAsync()
    {
        // Stop delivery first, then drain every retained sample before closing the lanes.
        // Each resume builds a fresh buffer and a fresh utterance generation.
        _activeCapture = Guid.Empty;

        _capture?.Stop();
        _capture?.Dispose();
        _capture = null;

        if (_loop is { } loop)
        {
            await loop.CancelAsync().ConfigureAwait(false);
            if (_loopTask is { } task) { try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            loop.Dispose();
            _loop = null;
            _loopTask = null;
        }

        if (_processor is { } processor)
            Receive(await processor.PollAsync(finish: true).ConfigureAwait(false));

        if (_pipeline is { } pipeline)
        {
            await pipeline.FinishAsync().ConfigureAwait(false);
            await pipeline.DisposeAsync().ConfigureAwait(false);
        }

        _processor = null;
        _pipeline = null;
        Hypothesis = "";
        Detail = "";
        Changed();
    }

    private void UpdateDownload(string model, double fraction)
    {
        IsDownloading = true;
        DownloadFraction = fraction;
        Status = $"Downloading {model}… {(int)(fraction * 100)}%";
        Changed();
    }

    private void Changed() => OnChanged?.Invoke();

    public async ValueTask DisposeAsync()
    {
        await EndCaptureAndFinalizeAsync().ConfigureAwait(false);
        if (_engine is { } engine) await engine.DisposeAsync().ConfigureAwait(false);
        _engine = null;
    }
}
