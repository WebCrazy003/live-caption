using System.Diagnostics;
using LocalCaption.Audio;
using LocalCaption.Core.Audio;
using NAudio.CoreAudioApi;

namespace LocalCaption.Probe;

/// <summary>
/// §4.2's acceptance test: move the default playback device out from under a live capture
/// and check the session survives it.
/// </summary>
/// <remarks>
/// <para>The capture follows the system default, so this is the mode-B failure the spec says
/// is routine on this machine — a monitor, a headset or a Jump Desktop session arriving
/// mid-call. What has to hold afterwards: audio keeps flowing, the source name changes to
/// the new endpoint, and the sample clock still tracks the wall clock, because the seconds
/// spent switching are still part of the interview.</para>
/// <para>The previous default is restored whatever happens.</para>
/// </remarks>
internal static class SwitchCheck
{
    public static int Run(ProbeOptions options)
    {
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        if (endpoints.Count < 2)
        {
            Console.Error.WriteLine("Two active render endpoints are needed to switch between. Found " +
                                    $"{endpoints.Count}.");
            return 1;
        }

        string original;
        try { original = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID; }
        catch (Exception e)
        {
            Console.Error.WriteLine($"No default render endpoint to start from — {e.Message}");
            return 1;
        }

        var other = endpoints.FirstOrDefault(d => d.ID != original) ?? endpoints[0];

        Console.WriteLine("── §4.2 device change ──────────────────────────────────────────────");
        Console.WriteLine($"  from       {endpoints.First(d => d.ID == original).FriendlyName}");
        Console.WriteLine($"  to         {other.FriendlyName}");
        Console.WriteLine($"  capture    follows the system default");
        Console.WriteLine();

        // deviceId null == follow the system default, which is what §4.6's picker means by
        // its own default setting.
        using var capture = new EndpointLoopbackCapture(deviceId: null);
        var buffer = new CaptureBuffer();
        long samples = 0;
        var faults = new List<CaptureFault>();

        capture.Samples += batch =>
        {
            Interlocked.Add(ref samples, batch.Length);
            buffer.Append(batch);
        };
        capture.Fault += faults.Add;

        capture.Start();
        var before = capture.Source;
        Console.WriteLine($"  recording  {before}");

        var half = Math.Max(2, options.Seconds / 2);
        Thread.Sleep(TimeSpan.FromSeconds(half));

        var atSwitch = Interlocked.Read(ref samples);
        var watch = Stopwatch.StartNew();

        using (DefaultEndpoint.Swap(other.ID, restoreTo: original))
        {
            Console.WriteLine($"  switched   default is now {other.FriendlyName}");
            Thread.Sleep(TimeSpan.FromSeconds(half));
            watch.Stop();
        }

        var after = capture.Source;
        var drift = capture.DriftMs;
        var clock = capture.ClockReport;
        var afterSwitch = Interlocked.Read(ref samples) - atSwitch;
        capture.Stop();

        var expected = watch.Elapsed.TotalSeconds * AudioNormalizer.TargetSampleRate;

        Console.WriteLine();
        Console.WriteLine($"  source now {after}");
        Console.WriteLine($"  audio after the switch  {afterSwitch} samples " +
                          $"({afterSwitch / (double)AudioNormalizer.TargetSampleRate:0.00}s of {watch.Elapsed.TotalSeconds:0.00}s)");
        Console.WriteLine($"  drift      {drift,+8:0} ms   ← §4.3's acceptance is ±100 ms");
        Console.WriteLine($"  clock      {clock}");
        foreach (var fault in faults)
            Console.WriteLine($"  fault      {fault.Message} (recoverable: {fault.Recoverable})");

        Console.WriteLine();
        var keptRecording = afterSwitch > expected * 0.5;
        var followed = after != before;
        var onTime = Math.Abs(drift) <= 100;

        Console.WriteLine(keptRecording
            ? "  PASS — audio kept arriving across the device change."
            : "  FAIL — the stream stopped when the default moved.");
        Console.WriteLine(followed
            ? $"  PASS — capture followed the default to {after}."
            : "  FAIL — capture stayed on the old endpoint.");
        Console.WriteLine(onTime
            ? "  PASS — the sample clock still tracks the wall clock."
            : $"  FAIL — {drift:0} ms of drift; the switchover time was lost.");

        return keptRecording && followed && onTime ? 0 : 1;
    }
}
