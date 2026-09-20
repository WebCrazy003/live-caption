using System.Diagnostics;
using LocalCaption.Audio;
using LocalCaption.Core.Audio;
using LocalCaption.Core.Data;
using LocalCaption.Session;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LocalCaption.Probe;

/// <summary>
/// The §17 acceptance criteria that can be checked without a person in the room.
/// </summary>
/// <remarks>
/// <para>Two of them, and they are the two with teeth:</para>
/// <list type="number">
///   <item><description><b>§17.3</b> — a 20-second silence gap mid-recording leaves every
///   later timestamp accurate to ±100 ms. This is §4.3's trap, at the exact duration the
///   spec names.</description></item>
///   <item><description><b>§17.6</b> — a five-minute silent soak produces no captions.
///   Whisper hallucinates on silence; <c>Filters.IsHallucination</c> exists to stop it, and
///   until now nothing had pointed a real silent capture at it.</description></item>
/// </list>
/// <para>The rest of §17 is either already covered (config repair, by the shared vectors),
/// needs the GPU path (interim budgets, the dGPU toggle), or needs a human and a cable.</para>
/// </remarks>
internal static class AcceptanceCheck
{
    public static async Task<int> RunAsync(MMDevice device, ProbeOptions options)
    {
        Console.WriteLine("── §17 acceptance ──────────────────────────────────────────────────");
        Console.WriteLine();

        var gapOk = CheckSilenceGap(device);
        Console.WriteLine();
        var silenceOk = await CheckSilentSoakAsync(device, options);

        Console.WriteLine();
        Console.WriteLine(gapOk && silenceOk
            ? "  Both automatable §17 criteria pass."
            : "  One or more criteria failed — see above.");
        return gapOk && silenceOk ? 0 : 1;
    }

    /// <summary>§17.3 — 20 seconds of silence must not cost the clock anything.</summary>
    private static bool CheckSilenceGap(MMDevice device)
    {
        const double seconds = 44;
        const double gap = 20;

        Console.WriteLine($"§17.3  a {gap:0}s silence gap mid-recording, timestamps within ±100 ms");

        using var capture = new EndpointLoopbackCapture(device.ID);
        long samples = 0;
        capture.Samples += batch => Interlocked.Add(ref samples, batch.Length);

        var format = WaveFormat.CreateIeeeFloatWaveFormat(device.AudioClient.MixFormat.SampleRate,
                                                          device.AudioClient.MixFormat.Channels);
        WasapiOut? tone = null;
        try
        {
            tone = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: 100);
            tone.Init(new ToneWithGap(format, seconds, gap));
            tone.Play();
        }
        catch (Exception e)
        {
            Console.WriteLine($"  could not play the test signal — {e.Message}");
        }

        capture.Start();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));

        var drift = capture.DriftMs;
        var report = capture.ClockReport;
        capture.Stop();
        tone?.Dispose();

        var audio = Interlocked.Read(ref samples) / (double)AudioNormalizer.TargetSampleRate;
        Console.WriteLine($"  captured   {audio:0.00}s over {seconds:0}s");
        Console.WriteLine($"  drift      {drift,+8:0} ms");
        Console.WriteLine($"  clock      {report}");

        var ok = Math.Abs(drift) <= 100;
        Console.WriteLine(ok
            ? "  PASS — the clock held across the gap."
            : $"  FAIL — {drift:0} ms. Every timestamp after the gap is wrong by that much.");
        return ok;
    }

    /// <summary>§17.6 — silence must produce no captions at all.</summary>
    private static async Task<bool> CheckSilentSoakAsync(MMDevice device, ProbeOptions options)
    {
        var seconds = options.Seconds > 0 ? options.Seconds : 300;
        Console.WriteLine($"§17.6  a {seconds / 60:0.#}-minute silent soak produces no captions");

        var scratch = Path.Combine(Path.GetTempPath(), $"lc-silent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);

        var config = new Config();
        config.Audio.CaptureMode = "endpoint";
        config.Audio.OutputDevice = device.ID;
        config.Asr.InterimModel = "tiny.en";
        config.Asr.FinalModel = "tiny.en";
        config.General.TranscriptFolder = scratch;

        using var store = new Store(Path.Combine(scratch, "sessions.db"));
        using var env = new AppEnvironment(config, store);
        await using var controller = new SessionController(env);

        await controller.PrepareAsync();
        if (controller.Phase != SessionPhase.Ready)
        {
            Console.WriteLine($"  FAIL — models not ready: {controller.Orchestrator.ErrorText}");
            return false;
        }

        await controller.StartAsync();
        if (controller.Phase != SessionPhase.Recording)
        {
            Console.WriteLine($"  FAIL — could not start: {controller.SaveError ?? controller.Orchestrator.ErrorText}");
            return false;
        }

        // Nothing plays. The keepalive keeps packets flowing, so the pipeline sees a steady
        // stream of digital silence — which is exactly what Whisper invents words from.
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < seconds) await Task.Delay(1000);

        await controller.StopAsync();

        var captions = controller.Paragraphs.Count + (controller.Current.Length > 0 ? 1 : 0);
        var text = string.Join(" ", controller.Paragraphs.Append(controller.Current)).Trim();

        Console.WriteLine($"  elapsed    {controller.Elapsed}");
        Console.WriteLine($"  captions   {captions}");
        if (text.Length > 0) Console.WriteLine($"  text       {(text.Length > 120 ? text[..120] + "…" : text)}");

        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }

        var ok = !controller.HasTranscript;
        Console.WriteLine(ok
            ? "  PASS — silence produced nothing, as it must."
            : "  FAIL — Whisper hallucinated over silence and it reached the transcript.");
        return ok;
    }
}
