using LocalCaption.Core.Audio;
using LocalCaption.Core.Captions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace LocalCaption.Asr;

/// <summary>What a loaded engine ended up using, for the log and the Settings readout.</summary>
/// <param name="Backend">The backend that was <i>asked for</i> after probing.</param>
/// <param name="Library">
/// The native library whisper.cpp <i>actually</i> loaded. Not the same thing: Whisper.net
/// walks its runtime order and falls back to the CPU library without raising anything when
/// a GPU one cannot load — a missing CUDA dependency looks exactly like a slow machine.
/// §5.2 requires the active backend to be visible rather than merely felt, and this is the
/// field that makes it so.
/// </param>
public sealed record EngineInfo(
    string InterimModel, string FinalModel, AsrBackend Backend,
    bool WordTimings, int Threads, string RuntimeInfo, string Library = "unknown")
{
    /// <summary>
    /// True when CUDA was asked for and something else loaded — the §5.8 symptom that would
    /// otherwise present only as captions quietly getting slower.
    /// </summary>
    /// <remarks>
    /// CUDA only. Metal is not a separate runtime library in Whisper.net — the macOS build
    /// reports it in the <c>Cpu</c> slot — so asking the same question there would raise a
    /// false alarm on every Mac development run.
    /// </remarks>
    public bool FellBack =>
        Backend is AsrBackend.Cuda &&
        !Library.Equals(nameof(AsrBackend.Cuda), StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        $"{Backend.ToString().ToLowerInvariant()}→{Library.ToLowerInvariant()} · interim={InterimModel} final={FinalModel} · " +
        $"threads={Threads} · word-timings={(WordTimings ? "dtw" : "none")}" +
        (FellBack ? "  ⚠ fell back to the CPU library" : "");
}

/// <summary>
/// Dual-model whisper.cpp engine: a fast <b>interim</b> model for provisional partials and
/// an accurate <b>final</b> model for committed captions. Both load once and stay resident.
/// Replaces the macOS <c>WhisperEngine.swift</c>.
/// </summary>
/// <remarks>
/// <para><b>The two lanes never share decoder state.</b> <c>whisper_context</c> is not
/// thread-safe, so each lane gets its own factory <i>and</i> its own processor — including
/// when the same model name is chosen for both roles, which is the case that looks safe to
/// share and is not. This is the rule that came out of the macOS <c>e5ec3bd</c> fix
/// (SPEC-WINDOWS.md §5.1), and it is why <see cref="Prepare"/> builds two of everything
/// rather than reusing one factory.</para>
/// <para><b>DTW is a factory-level option, not a per-call one.</b> Word timings come from
/// <see cref="WhisperFactoryOptions.UseDtwTimeStamps"/> plus the model's alignment-head
/// preset, so they must be decided before the weights load. The interim lane needs them —
/// <see cref="RollingCaption"/> merges sliding windows purely by word midpoint — while the
/// final lane does not, and pays nothing for skipping them.</para>
/// </remarks>
public sealed class WhisperEngine : IAsyncDisposable
{
    private const double SampleRate = 16000.0;
    /// <summary>Below this, a window is not worth a decode — it is a click, not a word.</summary>
    private const double MinimumSeconds = 0.2;

    private WhisperFactory? _interimFactory;
    private WhisperFactory? _finalFactory;
    private WhisperProcessor? _interim;
    private WhisperProcessor? _final;

    public string InterimName { get; }
    public string FinalName { get; }
    public AsrBackend RequestedBackend { get; }
    public int Threads { get; }
    public EngineInfo? Info { get; private set; }
    public bool IsLoaded => _interim is not null && _final is not null;

    public WhisperEngine(string interimModel, string finalModel,
                         AsrBackend backend = AsrBackend.Auto, int threads = 0)
    {
        InterimName = interimModel;
        FinalName = finalModel;
        RequestedBackend = backend;
        // 0 means "physical cores" (§9.2). Environment.ProcessorCount counts logical
        // processors, and oversubscribing whisper.cpp with SMT siblings costs throughput.
        Threads = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount / 2);
    }

    /// <summary>
    /// Download whatever is missing, load both models resident, and warm them.
    /// </summary>
    public async Task<EngineInfo> PrepareAsync(string modelsDirectory,
                                               Action<string>? onStatus = null,
                                               ModelDownloader.Progress? onDownload = null,
                                               CancellationToken cancellationToken = default)
    {
        var interimSpec = ModelCatalog.Resolve(InterimName);
        var finalSpec = ModelCatalog.Resolve(FinalName);

        using (var downloader = new ModelDownloader())
        {
            foreach (var spec in Distinct(interimSpec, finalSpec))
            {
                var path = ModelCatalog.PathFor(spec, modelsDirectory);
                if (!File.Exists(path)) onStatus?.Invoke($"Downloading {spec.Name}…");
                await downloader.EnsureAsync(spec, modelsDirectory, onDownload, cancellationToken)
                                .ConfigureAwait(false);
            }
        }

        var backend = BackendProbe.Resolve(RequestedBackend);
        onStatus?.Invoke($"Loading {InterimName} + {FinalName}…");

        // The interim lane needs word timings; the final lane does not and skips the cost.
        _interimFactory = CreateFactory(ModelCatalog.PathFor(interimSpec, modelsDirectory),
                                        backend, interimSpec.Heads, wordTimings: true);
        _finalFactory = CreateFactory(ModelCatalog.PathFor(finalSpec, modelsDirectory),
                                      backend, finalSpec.Heads, wordTimings: false);

        _interim = BuildInterim(_interimFactory);
        _final = BuildFinal(_finalFactory);

        // Loading weights does not warm the first prediction — whisper.cpp still has to
        // compile its GPU kernels. Doing that here rather than on first speech is the
        // difference between a clean first caption and a multi-second stall.
        onStatus?.Invoke("Warming speech models…");
        var silence = new float[(int)(2 * SampleRate)];
        foreach (var processor in new[] { _interim, _final })
        {
            await foreach (var _ in processor.ProcessAsync(silence, cancellationToken).ConfigureAwait(false)) { }
        }

        Info = new EngineInfo(InterimName, FinalName, backend,
                              WordTimings: interimSpec.Heads != WhisperAlignmentHeadsPreset.None,
                              Threads, WhisperFactory.GetRuntimeInfo() ?? "unknown",
                              Library: LoadedLibrary());
        onStatus?.Invoke($"Ready — {Info}");
        return Info;
    }

    /// <summary>
    /// Which native library Whisper.net settled on, read after the first factory is built.
    /// </summary>
    /// <remarks>
    /// Whisper.net resolves this once per process, on first load, by trying each library in
    /// its runtime order and moving on when one will not load. Nothing is thrown and nothing
    /// is logged, so without reading it back a CUDA build that silently ran on the CPU is
    /// indistinguishable from a CUDA build that was simply slow (§5.2).
    /// </remarks>
    private static string LoadedLibrary()
    {
        try { return RuntimeOptions.LoadedLibrary?.ToString() ?? "cpu"; }
        catch (Exception) { return "unknown"; }
    }

    /// <summary>Two specs, or one when both roles chose the same model.</summary>
    private static IEnumerable<ModelSpec> Distinct(ModelSpec a, ModelSpec b) =>
        a.FileName == b.FileName ? [a] : [a, b];

    private WhisperFactory CreateFactory(string modelPath, AsrBackend backend,
                                         WhisperAlignmentHeadsPreset heads, bool wordTimings)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Speech model weights are missing: {modelPath}", modelPath);

        var options = WhisperFactoryOptions.Default;
        options.UseGpu = BackendProbe.UsesGpu(backend);
        options.UseDtwTimeStamps = wordTimings && heads != WhisperAlignmentHeadsPreset.None;
        options.HeadsPreset = options.UseDtwTimeStamps ? heads : WhisperAlignmentHeadsPreset.None;
        return WhisperFactory.FromPath(modelPath, options);
    }

    /// <summary>
    /// Interim decoding: greedy, no context carried between windows, token timestamps on.
    /// Speed is the whole point — an interim result that misses its budget is discarded.
    /// </summary>
    private WhisperProcessor BuildInterim(WhisperFactory factory) => factory.CreateBuilder()
        .WithLanguage("en")
        .WithThreads(Threads)
        .WithTokenTimestamps()
        .WithProbabilities()
        // Each interim window is an independent 6 s tail, so carrying context from the last
        // one would let a hallucination propagate forward across every later window.
        .WithNoContext()
        .WithGreedySamplingStrategy(g => g.WithBestOf(1))
        .WithTemperature(0f)
        .Build();

    /// <summary>
    /// Final decoding: accuracy over latency. This output is what gets written to the
    /// transcript and can never be revised, so it keeps temperature fallback enabled.
    /// </summary>
    private WhisperProcessor BuildFinal(WhisperFactory factory) => factory.CreateBuilder()
        .WithLanguage("en")
        .WithThreads(Threads)
        .WithProbabilities()
        .WithNoContext()
        .Build();

    // ── Decoding ─────────────────────────────────────────────────────────────────────────

    public Task<SpeechOutcome> TranscribeInterimAsync(IReadOnlyList<float> audio, CancellationToken cancellationToken = default) =>
        RunAsync(_interim, audio, wantWords: true, cancellationToken);

    public Task<SpeechOutcome> TranscribeFinalAsync(IReadOnlyList<float> audio, CancellationToken cancellationToken = default) =>
        RunAsync(_final, audio, wantWords: false, cancellationToken);

    private static async Task<SpeechOutcome> RunAsync(WhisperProcessor? processor, IReadOnlyList<float> audio,
                                                      bool wantWords, CancellationToken cancellationToken)
    {
        if (processor is null) return new SpeechOutcome.Failure("Speech model is not loaded");
        if (audio.Count / SampleRate <= MinimumSeconds) return new SpeechOutcome.Empty();

        var samples = audio as float[] ?? [.. audio];
        var kept = new List<string>();
        var words = new List<CaptionWord>();
        var rejected = false;

        try
        {
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                // §5.6: whisper.cpp reports no_speech_prob, but avg_logprob and
                // compression_ratio have to be derived before the gate can run.
                var logprob = SegmentQuality.AverageLogprob(
                    (segment.Tokens ?? []).Select(t => (double)t.ProbabilityLog));
                var compression = SegmentQuality.CompressionRatio(segment.Text ?? "");

                if (Filters.IsLowQuality(logprob, segment.NoSpeechProbability, compression))
                {
                    rejected = true;
                    continue;
                }

                var cleaned = Filters.Clean(segment.Text ?? "");
                if (cleaned.Length == 0) continue;

                kept.Add(cleaned);
                if (wantWords) words.AddRange(WordTimings.FromTokens(segment));
            }
        }
        catch (OperationCanceledException) { return new SpeechOutcome.Cancelled(); }
        catch (Exception e) { return new SpeechOutcome.Failure(e.Message); }

        var text = Filters.Clean(string.Join(" ", kept));
        if (text.Length == 0) return rejected ? new SpeechOutcome.Filtered() : new SpeechOutcome.Empty();
        if (Filters.IsHallucination(text)) return new SpeechOutcome.Filtered();

        // whisper.cpp has no temperature-fallback counter to report, unlike WhisperKit.
        return new SpeechOutcome.Success(text, words, Fallbacks: 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_interim is not null) await _interim.DisposeAsync().ConfigureAwait(false);
        if (_final is not null) await _final.DisposeAsync().ConfigureAwait(false);
        _interimFactory?.Dispose();
        _finalFactory?.Dispose();
        _interim = null;
        _final = null;
        _interimFactory = null;
        _finalFactory = null;
    }
}
