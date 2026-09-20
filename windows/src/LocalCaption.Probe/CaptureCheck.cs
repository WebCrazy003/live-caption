using System.Diagnostics;
using LocalCaption.Audio;
using LocalCaption.Core.Audio;
using NAudio.CoreAudioApi;

namespace LocalCaption.Probe;

/// <summary>
/// Runs <see cref="EndpointLoopbackCapture"/> against a real endpoint and checks the one
/// property everything downstream depends on: <b>one second of wall clock produces one
/// second of 16 kHz mono audio</b>.
/// </summary>
/// <remarks>
/// <para>The raw probe answers what the driver does. This answers what B1 does with it —
/// the same question, one layer up, on the same hardware. It is the cheapest possible guard
/// against the §4.3 failure reappearing inside our own code: a resampler fed the wrong rate,
/// a clock correction applied twice, a stall nobody noticed.</para>
/// <para>It also feeds the samples through <see cref="CaptureBuffer"/>, because that is
/// where they go in the real app and a five-second latched overflow is worth exercising
/// against a live stream rather than only against a unit test.</para>
/// </remarks>
internal static class CaptureCheck
{
    public static int Run(MMDevice device, ProbeOptions options)
    {
        // Mode A taps a process, mode B an endpoint. Everything below is identical, which is
        // the point: they meet at IAudioCapture and the checks do not know which they have.
        using IAudioCapture capture = options.Process is { Length: > 0 } process
            ? new ProcessLoopbackCapture(Target(process))
            : new EndpointLoopbackCapture(device.ID, keepalive: !options.NoKeepalive);

        var buffer = new CaptureBuffer();

        long samples = 0;
        CaptureFault? fault = null;
        double? firstSampleAt = null;

        capture.Samples += batch =>
        {
            firstSampleAt ??= MonotonicClock.Now;
            Interlocked.Add(ref samples, batch.Length);
            buffer.Append(batch);
        };
        capture.Fault += f => fault ??= f;

        Console.WriteLine($"capturing {options.Seconds:0.#}s through LocalCaption.Audio…");

        var startedAt = MonotonicClock.Now;
        var run = Stopwatch.StartNew();
        try
        {
            capture.Start();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Start FAILED — {e.GetType().Name}: {e.Message}");
            return 1;
        }

        Console.WriteLine($"  source     {capture.Source}");
        Console.WriteLine($"  format     {capture.Format}");

        // Drain on the same cadence the app will (§6.2's CaptureProcessor poll), so the
        // buffer is exercised rather than merely filled.
        var drained = 0L;
        var dropped = 0;
        var worstGapMs = 0;
        var peak = 0f;
        while (run.Elapsed.TotalSeconds < options.Seconds)
        {
            Thread.Sleep(250);
            var batch = buffer.Drain();
            drained += batch.Samples.Count;
            dropped += batch.DroppedSamples;
            worstGapMs = Math.Max(worstGapMs, batch.CallbackGapMs);
            peak = Math.Max(peak, capture.Level);
            if (!Console.IsOutputRedirected)
                Console.Write($"\r  level      {Bar(capture.Level)} {capture.Level:0.000}   ");
        }

        // Read before Stop tears the stream down — afterwards there is no keepalive left to
        // report on.
        var clockReport = ((LocalCaption.Audio.WasapiCapture)capture).ClockReport;
        var driftMs = ((LocalCaption.Audio.WasapiCapture)capture).DriftMs;
        var stoppedAt = MonotonicClock.Now;
        capture.Stop();
        run.Stop();

        var tail = buffer.Drain();
        drained += tail.Samples.Count;
        dropped += tail.DroppedSamples;

        var seconds = Interlocked.Read(ref samples) / (double)AudioNormalizer.TargetSampleRate;

        // Two different numbers, and conflating them would hide a real fault behind a fake
        // one. Opening the stream costs time before any audio exists; the session's clock
        // starts at the first sample, so drift is measured from there.
        var openMs = ((firstSampleAt ?? stoppedAt) - startedAt) * 1000;
        var capturedSpan = stoppedAt - (firstSampleAt ?? startedAt);

        if (!Console.IsOutputRedirected) Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("── capture layer ───────────────────────────────────────────────────");
        Console.WriteLine($"  samples    {Interlocked.Read(ref samples)} at 16 kHz = {seconds:0.00}s");
        Console.WriteLine($"  open       {openMs,8:0} ms before the first sample");
        Console.WriteLine($"  stream     {capturedSpan:0.00}s from first sample to stop");
        Console.WriteLine($"  drift      {driftMs,+8:0} ms   ← §4.3's acceptance is ±100 ms");
        Console.WriteLine($"  buffer     {drained} samples drained · {dropped} dropped · worst callback gap {worstGapMs} ms");
        // A clock that tracks perfectly while capturing silence is a session that records
        // nothing — exactly the failure §4.7.5's meter exists to make obvious.
        Console.WriteLine($"  peak level {peak:0.000}{(peak <= 0.0001f ? "   ← NOTHING was captured" : "")}");
        Console.WriteLine($"  clock      {clockReport}");
        if (fault is not null) Console.WriteLine($"  fault      {fault.Message} (recoverable: {fault.Recoverable})");

        Console.WriteLine();
        var within = Math.Abs(driftMs) <= 100;
        Console.WriteLine(within
            ? "  PASS — the sample clock tracked the wall clock within §4.3's ±100 ms."
            : $"  FAIL — {driftMs:0} ms of drift. The clock is not tracking; check the keepalive first.");
        Console.WriteLine(dropped == 0
            ? "  PASS — no samples lost to the capture buffer."
            : $"  FAIL — the buffer latched an overflow and lost {dropped} samples.");

        return within && dropped == 0 ? 0 : 1;
    }

    /// <summary>
    /// "self" means this probe, which with --play makes mode A a self-contained test: one
    /// process renders the tone and captures its own tree.
    /// </summary>
    private static string Target(string process) =>
        process.Equals("self", StringComparison.OrdinalIgnoreCase)
            ? System.Diagnostics.Process.GetCurrentProcess().ProcessName
            : process;

    private static string Bar(float level)
    {
        var filled = (int)Math.Round(Math.Clamp(level, 0, 1) * 20);
        return "[" + new string('#', filled) + new string('.', 20 - filled) + "]";
    }
}
