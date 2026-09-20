using System.Diagnostics;
using System.Globalization;
using LocalCaption.Asr;
using LocalCaption.Bench;
using LocalCaption.Core.Audio;

// LocalCaption.Bench — SPEC-WINDOWS.md §5.4 (stage B0).
//
// Not shipped. This exists to answer three questions BEFORE app code depends on the answers:
//   1. Is large-v3-turbo fast enough to be the default final model?
//   2. Is turbo-only streaming viable — one model for both lanes? If a 6 s window decodes in
//      well under 500 ms, the dual-model architecture collapses to one resident model and
//      takes the interim/final split and the two-lane isolation rule with it.
//   3. What does GPU contention cost? The lanes decode concurrently on one GPU while a
//      meeting app may also be using it, so isolated numbers flatter the design.
//
// The macOS architecture was not guessed — spike/RESULTS.md measured it and killed the
// original CPU plan outright. This is the same step on the ASUS. Output belongs in
// windows/BENCH-RESULTS.md.

var options = BenchOptions.Parse(args);
if (options is null) return 0;

// Whisper.net 1.9.1 cannot load on macOS arm64: Whisper.net.Runtime depends on
// Whisper.net.Runtime.Metal, both ship a ggml library, and both run ggml's static
// initialiser — the second trips GGML_ASSERT(prev != ggml_uncaught_exception) and abort()s
// the process from inside dlopen. That is a native abort, not an exception, so it cannot be
// caught; the only useful thing to do is refuse early and say why. Windows is unaffected —
// it is the platform this benchmark exists to measure (§5.4, stage B0).
if (OperatingSystem.IsMacOS() && !args.Contains("--force"))
{
    Console.Error.WriteLine("""
        LocalCaption.Bench cannot run on macOS.

          Whisper.net 1.9.1 loads two ggml libraries here (Whisper.net.Runtime depends on
          Whisper.net.Runtime.Metal) and the second one abort()s in ggml's static
          initialiser. It is an upstream packaging bug, not a fault in this code, and it
          does not affect Windows.

          This tool is stage B0 (SPEC-WINDOWS.md §5.4): run it on the G15, where the
          numbers it produces are the ones that decide the shipping model defaults.

          Pass --force to attempt it anyway (expect an abort).
        """);
    return 2;
}

var audio = options.WavPath is { } wav
    ? WavReader.ReadMono16k(wav)
    : WavReader.SyntheticSpeech(options.SyntheticSeconds);

var source = options.WavPath is { } path ? Path.GetFileName(path) : $"synthetic {options.SyntheticSeconds:0.#}s";
var duration = audio.Length / 16000.0;

Console.WriteLine($"LocalCaption.Bench — {BackendProbe.Describe(BackendProbe.Resolve(AsrBackend.Auto))}");
Console.WriteLine($"audio: {source} · {duration:0.00}s · {audio.Length} samples @ 16 kHz");
Console.WriteLine($"models: {options.ModelsDirectory}");
if (options.WavPath is null)
    Console.WriteLine("note: synthetic audio — timings are valid, the transcript text is not.");
Console.WriteLine();

var results = new List<Row>();

foreach (var backend in options.Backends)
{
    foreach (var modelName in options.Models)
    {
        var spec = ModelCatalog.Resolve(modelName);
        var row = await MeasureAsync(spec, backend, audio, options);
        results.Add(row);
        Console.WriteLine(row);
    }
}

// Question 3: the lanes contending for one device. Isolated numbers are optimistic, and the
// interim budget in §5.4 is explicitly "measured under concurrent final decoding".
if (options.Contention && options.Models.Count >= 2)
{
    Console.WriteLine();
    Console.WriteLine("── under contention (both lanes decoding concurrently) ─────────────");
    foreach (var backend in options.Backends)
        await MeasureContentionAsync(options.Models[0], options.Models[^1], backend, audio, options);
}

Console.WriteLine();
Console.WriteLine(Verdict(results, duration));
return 0;

async Task<Row> MeasureAsync(ModelSpec spec, AsrBackend backend, float[] samples, BenchOptions opts)
{
    var loadWatch = Stopwatch.StartNew();
    await using var engine = new WhisperEngine(spec.Name, spec.Name, backend, opts.Threads);
    try
    {
        await engine.PrepareAsync(opts.ModelsDirectory, onStatus: opts.Verbose ? Console.Error.WriteLine : null,
                                  onDownload: opts.Verbose ? Report : null);
    }
    catch (Exception e)
    {
        return Row.Failed(backend, spec.Name, e.Message);
    }
    loadWatch.Stop();

    // The interim lane is the one with a budget, and the one that needs word timings.
    var timings = new List<double>();
    var text = "";
    var words = 0;
    for (var i = 0; i < opts.Runs; i++)
    {
        var watch = Stopwatch.StartNew();
        var outcome = await engine.TranscribeInterimAsync(samples);
        watch.Stop();
        timings.Add(watch.Elapsed.TotalMilliseconds);
        if (outcome is SpeechOutcome.Success success)
        {
            text = success.Text;
            words = success.Words.Count;
        }
        else if (i == 0)
        {
            text = $"({outcome.Label})";
        }
    }

    return new Row(backend, spec.Name, loadWatch.Elapsed.TotalMilliseconds,
                   Percentile(timings, 50), Percentile(timings, 90),
                   samples.Length / 16000.0, words, text);
}

async Task MeasureContentionAsync(string interimName, string finalName, AsrBackend backend,
                                  float[] samples, BenchOptions opts)
{
    await using var engine = new WhisperEngine(interimName, finalName, backend, opts.Threads);
    try { await engine.PrepareAsync(opts.ModelsDirectory); }
    catch (Exception e)
    {
        Console.WriteLine($"  {backend,-8} {interimName} + {finalName}: FAILED — {e.Message}");
        return;
    }

    var interimTimings = new List<double>();
    var finalTimings = new List<double>();

    for (var i = 0; i < opts.Runs; i++)
    {
        var finalWatch = Stopwatch.StartNew();
        var finalTask = Task.Run(async () =>
        {
            await engine.TranscribeFinalAsync(samples);
            finalWatch.Stop();
        });

        // Give the final lane a head start so the interim really does land mid-decode.
        await Task.Delay(20);
        var interimWatch = Stopwatch.StartNew();
        await engine.TranscribeInterimAsync(samples);
        interimWatch.Stop();
        interimTimings.Add(interimWatch.Elapsed.TotalMilliseconds);

        await finalTask;
        finalTimings.Add(finalWatch.Elapsed.TotalMilliseconds);
    }

    Console.WriteLine($"  {backend,-8} interim {interimName,-16} p50 {Percentile(interimTimings, 50),7:0} ms  " +
                      $"p90 {Percentile(interimTimings, 90),7:0} ms   ← budget < 500 ms p90");
    Console.WriteLine($"  {backend,-8} final   {finalName,-16} p50 {Percentile(finalTimings, 50),7:0} ms  " +
                      $"p90 {Percentile(finalTimings, 90),7:0} ms   ← budget < ~2000 ms p90");
}

void Report(string model, double fraction)
{
    if (fraction >= 1) Console.Error.WriteLine($"  {model}: downloaded");
}

static double Percentile(List<double> values, int percentile)
{
    if (values.Count == 0) return 0;
    var sorted = values.OrderBy(v => v).ToList();
    var rank = (percentile / 100.0) * (sorted.Count - 1);
    var low = (int)Math.Floor(rank);
    var high = (int)Math.Ceiling(rank);
    return low == high ? sorted[low] : sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
}

static string Verdict(List<Row> rows, double audioSeconds)
{
    var lines = new List<string> { "── verdict ────────────────────────────────────────────────────────" };
    var usable = rows.Where(r => r.Error is null).ToList();
    if (usable.Count == 0) return string.Join('\n', lines.Append("  no model completed — see the errors above"));

    foreach (var row in usable)
    {
        var verdict = row.P90 < 500 ? "interim-capable (< 500 ms p90)"
                    : row.P90 < 2000 ? "final-only (< 2 s p90)"
                    : "too slow for either lane";
        lines.Add($"  {row.Backend,-6} {row.Model,-16} {verdict}");
    }

    var turbo = usable.FirstOrDefault(r => r.Model == "large-v3-turbo");
    if (turbo is not null)
    {
        lines.Add("");
        lines.Add(turbo.P90 < 500
            ? "  Q2: turbo-only streaming looks VIABLE — one resident model may replace the\n" +
              "      dual-model split. Confirm under contention before deleting that design."
            : "  Q2: turbo-only streaming is NOT viable at this latency — keep the hybrid.");
    }
    lines.Add("");
    lines.Add($"  RTF is decode_ms / {audioSeconds * 1000:0} ms of audio. Lower is faster.");
    return string.Join('\n', lines);
}

internal sealed record Row(AsrBackend Backend, string Model, double LoadMs, double P50, double P90,
                           double AudioSeconds, int Words, string Text, string? Error = null)
{
    public static Row Failed(AsrBackend backend, string model, string error) =>
        new(backend, model, 0, 0, 0, 0, 0, "", error);

    public override string ToString()
    {
        if (Error is not null) return $"{Backend.ToString().ToLowerInvariant(),-10} {Model,-18} FAILED — {Error}";
        var rtf = AudioSeconds > 0 ? P50 / 1000 / AudioSeconds : 0;
        var preview = Text.Length > 44 ? Text[..44] + "…" : Text;
        return string.Format(CultureInfo.InvariantCulture,
            "{0,-10} {1,-18} {2,7:0}  {3,6:0} / {4,6:0}  {5,5:0.00}  {6,3}w  {7}",
            Backend.ToString().ToLowerInvariant(), Model, LoadMs, P50, P90, rtf, Words, preview);
    }
}

internal sealed record BenchOptions(
    IReadOnlyList<string> Models, IReadOnlyList<AsrBackend> Backends, string ModelsDirectory,
    string? WavPath, double SyntheticSeconds, int Runs, int Threads, bool Contention, bool Verbose)
{
    public static BenchOptions? Parse(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
                LocalCaption.Bench — measure decode latency across backends and models (§5.4).

                  --wav <path>        16 kHz mono WAV. Omitted: synthetic audio (timings only).
                  --seconds <n>       Synthetic audio length. Default 6 — one interim window.
                  --models <a,b,...>  Default: tiny.en,small.en,large-v3-turbo
                  --backends <a,b>    auto | cuda | cpu. Default: auto
                  --models-dir <p>    Where weights live. Default: %LOCALAPPDATA%/LocalCaption/models
                  --runs <n>          Decodes per cell. Default 5.
                  --threads <n>       0 = physical cores. Default 0.
                  --no-contention     Skip the concurrent-lane measurement.
                  --verbose           Show load and download progress.

                Writes nothing. Capture the output into windows/BENCH-RESULTS.md.
                """);
            return null;
        }

        string? Value(string name, string? fallback = null)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        var models = (Value("--models") ?? "tiny.en,small.en,large-v3-turbo")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var backends = (Value("--backends") ?? "auto")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(b => Enum.TryParse<AsrBackend>(b, ignoreCase: true, out var parsed) ? parsed : AsrBackend.Auto)
            .Distinct().ToList();

        var modelsDir = Value("--models-dir") ?? LocalCaption.Core.AppPaths.Models;

        return new BenchOptions(
            models, backends, modelsDir,
            WavPath: Value("--wav"),
            SyntheticSeconds: double.TryParse(Value("--seconds"), CultureInfo.InvariantCulture, out var s) ? s : 6,
            Runs: int.TryParse(Value("--runs"), out var r) ? r : 5,
            Threads: int.TryParse(Value("--threads"), out var t) ? t : 0,
            Contention: !args.Contains("--no-contention"),
            Verbose: args.Contains("--verbose"));
    }
}
