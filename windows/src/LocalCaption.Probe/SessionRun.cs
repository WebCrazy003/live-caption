using LocalCaption.Core;
using LocalCaption.Core.Data;
using LocalCaption.Session;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LocalCaption.Probe;

/// <summary>
/// Drives a whole session end to end — capture, decode, journal, transcript, database row —
/// against a real endpoint, with real speech played into it.
/// </summary>
/// <remarks>
/// <para>The B3 smoke check. Everything below the UI runs here, so it answers the only
/// question that matters at this stage: <b>does pressing Start and then Stop leave a
/// transcript on disk with words in it?</b></para>
/// <para>It writes to a throwaway folder and database, never the real ones, and forces
/// <c>tiny.en</c> for both lanes because the CUDA path is still blocked (see
/// <c>BENCH-RESULTS.md</c> §1) and <c>large-v3-turbo</c> on this CPU needs seventeen seconds
/// per window.</para>
/// </remarks>
internal static class SessionRun
{
    public static async Task<int> RunAsync(MMDevice device, ProbeOptions options)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"lc-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);

        var config = new Config();
        config.Audio.CaptureMode = "endpoint";
        config.Audio.OutputDevice = device.ID;
        // Default to the fast pair; --models lets the shipping pair be exercised.
        var models = (options.Models ?? "tiny.en,tiny.en")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        config.Asr.InterimModel = models[0];
        config.Asr.FinalModel = models.Length > 1 ? models[1] : models[0];
        // §8: a session that cannot be written keeps its journal, so the next launch can still
        // recover it. A full disk must not turn into a lost interview.
        config.General.TranscriptFolder = options.FailSave
            ? Path.Combine("Z:\\", "no-such-volume", Guid.NewGuid().ToString("N"))
            : scratch;
        config.Caption.ShowTimestamps = true;

        using var store = new Store(Path.Combine(scratch, "sessions.db"));
        using var env = new AppEnvironment(config, store);
        await using var controller = new SessionController(env);

        controller.Changed += () =>
        {
            if (!Console.IsOutputRedirected)
                Console.Write($"\r  {controller.Phase,-10} {controller.Elapsed}  " +
                              $"{Trim(controller.Orchestrator.Hypothesis)}".PadRight(78));
        };

        Console.WriteLine($"session: transcripts → {scratch}");
        Console.WriteLine($"  models     {config.Asr.InterimModel} + {config.Asr.FinalModel}");

        await controller.PrepareAsync();
        Console.WriteLine($"  prepare    {controller.Phase} · {controller.Orchestrator.Status}");
        if (controller.Phase != SessionPhase.Ready)
        {
            Console.Error.WriteLine($"  FAILED — {controller.Orchestrator.ErrorText}");
            return 1;
        }

        using var speech = StartSpeech(device, options);
        if (speech is null) Console.WriteLine("  speech     FAILED to start — the transcript will be empty");

        await controller.StartAsync();
        if (controller.Phase != SessionPhase.Recording)
        {
            Console.Error.WriteLine($"  FAILED to start — {controller.SaveError ?? controller.Orchestrator.ErrorText}");
            return 1;
        }

        Console.WriteLine($"  source     {controller.Orchestrator.SourceName}");
        await Task.Delay(TimeSpan.FromSeconds(options.Seconds));

        var clock = controller.Orchestrator.ClockReport;
        await controller.StopAsync();
        speech?.Dispose();

        if (!Console.IsOutputRedirected) Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("── session ─────────────────────────────────────────────────────────");
        Console.WriteLine($"  phase      {controller.Phase}");
        Console.WriteLine($"  elapsed    {controller.Elapsed}");
        Console.WriteLine($"  clock      {clock}");
        Console.WriteLine($"  saved to   {controller.SavedTranscriptPath ?? "(nothing)"}");
        if (controller.SaveError is { } error) Console.WriteLine($"  error      {error}");
        if (controller.Orchestrator.ErrorText is { } issue) Console.WriteLine($"  issue      {issue}");
        Console.WriteLine($"  db rows    {store.Count()}");
        Console.WriteLine($"  journals   {Journal.Pending().Count} left over (0 is a clean stop)");

        if (options.FailSave)
        {
            // The inverse test: everything above should have gone wrong, and gone wrong
            // safely.
            var failedLoudly = controller.Phase == SessionPhase.Failed && controller.SaveError is not null;

            // On disk, not "offered for recovery": the controller still holds the journal
            // open, because the session is not finished — the user can fix the problem and
            // press Stop again. Journal.Pending deliberately skips files another handle
            // holds, so asking it here would report a healthy session as a lost one.
            var journalKept = Directory.Exists(AppPaths.Journal) &&
                              Directory.GetFiles(AppPaths.Journal, "*.jsonl").Length > 0;

            Console.WriteLine();
            Console.WriteLine(failedLoudly
                ? "  PASS — the save failed and said so, rather than reporting success."
                : $"  FAIL — phase {controller.Phase}, error {controller.SaveError ?? "(none)"}.");
            Console.WriteLine(journalKept
                ? "  PASS — the journal is still on disk, so the session can still be saved."
                : "  FAIL — the journal was deleted; the session is gone.");

            // And once this process lets go of it, it is recoverable like any other.
            await controller.DisposeAsync();
            var recoverable = Journal.Pending().Count > 0;
            Console.WriteLine(recoverable
                ? "  PASS — and it is offered for recovery once the session releases it."
                : "  FAIL — it never became recoverable.");

            // Leave nothing behind for the next run to trip over.
            foreach (var pending in Journal.Pending()) Journal.Remove(pending.Path);
            return failedLoudly && journalKept && recoverable ? 0 : 1;
        }

        var ok = controller.Phase == SessionPhase.Saved && controller.SavedTranscriptPath is not null;
        if (ok)
        {
            Console.WriteLine();
            Console.WriteLine("── transcript ──────────────────────────────────────────────────────");
            foreach (var line in File.ReadAllLines(controller.SavedTranscriptPath!)) Console.WriteLine($"  {line}");
        }

        Console.WriteLine();
        Console.WriteLine(ok
            ? "  PASS — the session saved a transcript and cleaned up its journal."
            : "  FAIL — see the phase and error above.");
        Console.WriteLine(controller.HasTranscript
            ? "  PASS — speech was recognised and committed."
            : "  FAIL — no final segment was ever committed.");

        return ok && controller.HasTranscript ? 0 : 1;
    }

    /// <summary>Play the sample into the endpoint so the loopback capture has something to hear.</summary>
    private static IDisposable? StartSpeech(MMDevice device, ProbeOptions options)
    {
        if (options.Wav is not { Length: > 0 } path || !File.Exists(path)) return null;

        try
        {
            var samples = ReadMono16k(path);
            var format = WaveFormat.CreateIeeeFloatWaveFormat(device.AudioClient.MixFormat.SampleRate,
                                                              device.AudioClient.MixFormat.Channels);
            var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: 100);
            output.Init(new SpeechPlayer(format, samples));
            output.Play();
            return output;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  speech     {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>16-bit mono PCM only — this reads one known fixture, not arbitrary WAVs.</summary>
    private static float[] ReadMono16k(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var data = FindDataChunk(bytes);
        var samples = new float[data.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(data.Slice(i * 2, 2)) / 32768f;
        return samples;
    }

    private static ReadOnlySpan<byte> FindDataChunk(byte[] bytes)
    {
        var offset = 12;                                  // past "RIFF" + size + "WAVE"
        while (offset + 8 < bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BitConverter.ToInt32(bytes, offset + 4);
            if (id == "data") return bytes.AsSpan(offset + 8, Math.Min(size, bytes.Length - offset - 8));
            offset += 8 + size + (size % 2);
        }
        return ReadOnlySpan<byte>.Empty;
    }

    private static string Trim(string text) => text.Length <= 48 ? text : "…" + text[^47..];
}
