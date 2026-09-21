using LocalCaption.Asr;
using LocalCaption.Audio;
using LocalCaption.Core;
using LocalCaption.Core.Audio;
using LocalCaption.Core.Captions;
using LocalCaption.Core.Data;

namespace LocalCaption.Session;

/// <summary>
/// How fast the two decode lanes are running, for the status bar.
/// </summary>
/// <param name="InterimMs">Smoothed decode time of the live (interim) model.</param>
/// <param name="FinalMs">Smoothed decode time of the final model.</param>
/// <param name="LagMs">How far the last decoded window trailed live audio.</param>
/// <param name="RealtimeFactor">Seconds of audio the final model clears per second of work.</param>
/// <param name="Decodes">How many decodes have been measured this session.</param>
public sealed record SpeedReading(int InterimMs = 0, int FinalMs = 0, int LagMs = 0,
                                  double RealtimeFactor = 0, int Decodes = 0);

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
    private readonly AutoGain _gain = new();
    private bool _gainOn = true;
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

    /// <summary>
    /// Live peak level, 0–1, for the meter §4.7.5 makes a requirement — of the audio the
    /// speech gate is actually given, so the meter shows what the app hears rather than how
    /// loud the speakers happen to be.
    /// </summary>
    public float Level => _capture is null ? 0 : _gainOn ? Math.Max(_gain.Level, _capture.Level) : _capture.Level;

    /// <summary>How much the quiet-audio gain is adding right now, in dB. 0 when it is idle or off.</summary>
    public double GainDb => _capture is not null && _gainOn ? _gain.GainDb : 0;

    /// <summary>Elapsed recorded audio. Sample-based, so it freezes while paused.</summary>
    public int RecordedMs => _totalSamples / 16;

    /// <summary>
    /// Decode speed, smoothed. Replaced whole on every metric so a reader on another thread
    /// sees one consistent reading rather than a half-updated one.
    /// </summary>
    public SpeedReading Speed { get; private set; } = new();

    /// <summary>What the loaded engine ended up using — backend, library, threads (§5.2).</summary>
    public EngineInfo? EngineInfo => _engine?.Info;

    /// <summary>True while audio is being captured; the engine must not be swapped under it.</summary>
    public bool IsCapturing => _capture is not null;

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

        var engine = new WhisperEngine(InterimName, FinalName, backend, config.Asr.Threads, config.Asr.Vocabulary,
                                       config.Asr.FinalBeamSize);
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

    /// <summary>
    /// Throw the loaded models away and load whatever <paramref name="config"/> now names.
    /// </summary>
    /// <remarks>
    /// Only while capture is stopped — the same rule <see cref="FallBackToCpuAsync"/> lives by.
    /// A pipeline holds the engine for as long as it exists, and pausing is what disposes the
    /// pipeline, so "paused" is the one moment a session can change models and carry on.
    /// </remarks>
    public async Task<bool> ReloadModelAsync(Config config, CancellationToken cancellationToken = default)
    {
        if (_endingTask is { } ending) await ending.ConfigureAwait(false);
        if (_capture is not null) return false;

        if (_engine is { } old) { try { await old.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } }
        _engine = null;
        ModelReady = false;
        GpuLost = false;
        _consecutiveFailures = 0;
        Speed = new SpeedReading();      // the old models' numbers say nothing about the new ones
        Status = "Switching speech models…";
        Changed();

        await PrepareModelAsync(config, cancellationToken).ConfigureAwait(false);
        return ModelReady;
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
        // Greedy on the CPU regardless of the setting: beam search roughly doubles decode
        // time, and a machine that has just lost its GPU has none to spare.
        _engine = new WhisperEngine(interim, final, AsrBackend.Cpu, config.Asr.Threads, config.Asr.Vocabulary);

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
        Speed = new SpeedReading();

        // The gain is deliberately NOT reset between sessions. The volume someone had five
        // minutes ago is the best guess there is at the volume they have now; if it has gone
        // up the gain falls within milliseconds, and if it has not, the first word of the
        // new session is caught instead of being spent re-learning the level.
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
        pipeline.OnMetric = Measure;

        var capture = Create(config);
        _capture = capture;
        // Level first, then everything else. The gate downstream is an absolute loudness, and
        // a loopback capture is as quiet as the speakers are set — see AutoGain for the
        // session that taught this. In place and length-preserving, so the sample clock,
        // which is a count of these samples, cannot tell it happened.
        _gainOn = config.Audio.AutoGain;
        capture.Samples += samples => buffer.Append(_gainOn ? _gain.Process(samples) : samples);
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
    private static IAudioCapture Create(Config config)
    {
        var mode = config.Audio.CaptureMode;

        if (mode.Equals("process", StringComparison.OrdinalIgnoreCase) && config.Audio.TargetProcess is { Length: > 0 } target)
            return new ProcessLoopbackCapture(target);

        // auto: only when it is unambiguous (see MeetingApps.TheOnePlaying). Otherwise the
        // whole device — noisier, never wrong.
        if (mode.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            MeetingApps.TheOnePlaying(AudioSessions.List()) is { } call)
            return new ProcessLoopbackCapture(call.Executable);

        return new EndpointLoopbackCapture(config.Audio.OutputDevice);
    }

    /// <summary>
    /// The models <paramref name="config"/> would load that are not on disk yet.
    /// </summary>
    /// <remarks>After the §5.8 substitution, because that is what would actually be fetched.</remarks>
    public static IReadOnlyList<ModelSpec> MissingModels(Config config)
    {
        var backend = Enum.TryParse<AsrBackend>(config.Asr.Backend, ignoreCase: true, out var parsed) ? parsed : AsrBackend.Auto;
        var onGpu = BackendProbe.UsesGpu(BackendProbe.Resolve(backend));
        var (interim, final, _) = AsrFallback.Choose(onGpu, config.Asr.InterimModel, config.Asr.FinalModel);

        return [.. new[] { interim, final }.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ModelCatalog.Resolve)
            .Where(spec => !File.Exists(ModelCatalog.PathFor(spec, AppPaths.Models)))];
    }

    /// <summary>
    /// Fetch whatever <paramref name="config"/> needs, without touching the loaded engine.
    /// </summary>
    /// <remarks>
    /// A model swap mid-recording pauses capture for as long as the swap takes. Loading is
    /// seconds; a 3 GB download is not. So the download happens first, with the recording
    /// still running on the old models, and the pause only begins once the file is here.
    /// </remarks>
    public async Task<bool> PredownloadAsync(Config config, CancellationToken cancellationToken = default)
    {
        var missing = MissingModels(config);
        if (missing.Count == 0) return true;

        try
        {
            using var downloader = new ModelDownloader();
            foreach (var spec in missing)
                await downloader.EnsureAsync(spec, AppPaths.Models, UpdateDownload, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ErrorText = $"Could not download the model: {e.Message}";
            return false;
        }
        finally
        {
            IsDownloading = false;
            Changed();
        }
    }

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

    /// <summary>
    /// Fold one decode into the speed reading.
    /// </summary>
    /// <remarks>
    /// <para>Only decodes that actually ran the model count. A cancelled one was stopped by
    /// Pause or Stop, and an "empty" one is usually a window too short to decode at all —
    /// either would make a struggling machine look fast.</para>
    /// <para>A timeout <i>does</i> count. It ran for the whole interim budget and was then
    /// thrown away, so its time is a floor on how slow the model really is — and leaving it
    /// out is how this readout once sat on a flattering 408 ms while every live decode was
    /// being abandoned at two seconds.</para>
    /// </remarks>
    private void Measure(CaptionMetric metric)
    {
        if (metric.Outcome is not ("success" or "filtered" or "timeout")) return;

        static int Smooth(int previous, int sample) =>
            previous == 0 ? sample : (int)Math.Round(previous * 0.7 + sample * 0.3);

        var speed = Speed;
        if (metric.IsFinal)
        {
            var audioMs = Math.Max(1, metric.WindowEndMs - metric.WindowStartMs);
            var factor = (double)audioMs / Math.Max(1, metric.DecodeMs);
            speed = speed with
            {
                FinalMs = Smooth(speed.FinalMs, metric.DecodeMs),
                RealtimeFactor = speed.RealtimeFactor == 0 ? factor : speed.RealtimeFactor * 0.7 + factor * 0.3,
            };
        }
        else
        {
            speed = speed with { InterimMs = Smooth(speed.InterimMs, metric.DecodeMs), LagMs = metric.AudioLagMs };
        }

        Speed = speed with { Decodes = speed.Decodes + 1 };
        Changed();
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
