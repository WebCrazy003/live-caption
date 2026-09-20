using System.Diagnostics;
using System.Globalization;
using LocalCaption.Audio;
using LocalCaption.Probe;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// LocalCaption.Probe — SPEC-WINDOWS.md §4.7.3 and §4.3 (stage B0).
//
// Not shipped. The companion to LocalCaption.Bench: the bench answers what the GPU can do,
// this answers what the audio stack will hand us. Two questions, both cheap now and
// expensive in B1:
//
//   W9   Does WASAPI loopback work on the Jump Desktop Virtual Speaker? It is a software
//        driver, not hardware, and the owner takes interviews through it. Initialize, the
//        reported mix format, packets during silence, u64DevicePosition monotonicity.
//
//   §4.3 The silence gap — the one real trap in the audio layer. Loopback stops delivering
//        packets while nothing is playing on many drivers, and because the whole app derives
//        its clock from sample count, every timestamp after the first pause is then wrong.
//        This measures the gap directly and reports the padding correction it implies.
//
// Run it against each endpoint, once with audio playing and once with the machine silent,
// and paste the output into windows/BENCH-RESULTS.md.

var options = ProbeOptions.Parse(args);
if (options is null) return 0;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("LocalCaption.Probe measures WASAPI. It only runs on Windows.");
    return 2;
}

using var enumerator = new MMDeviceEnumerator();
var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
var defaultId = TryDefaultRenderId(enumerator);

if (options.SetDefault is { Length: > 0 } wanted)
{
    var target = Select(endpoints, wanted, defaultId);
    if (target is null) { Console.Error.WriteLine($"no endpoint matches '{wanted}'"); return 1; }
    var moved = DefaultEndpoint.Set(target.ID);
    Console.WriteLine(moved
        ? $"default is now {target.FriendlyName}"
        : $"could not set the default to {target.FriendlyName}");
    return moved ? 0 : 1;
}

if (options.Switch)
{
    // §4.2, which this machine exercises every time a monitor or a remote session arrives.
    return SwitchCheck.Run(options);
}

if (options.Recover)
{
    // §9.4: journals that outlived their session. This is the other half of the kill test —
    // pull the power, then prove the captions are still there.
    var scratch = Path.Combine(Path.GetTempPath(), $"lc-recover-{Guid.NewGuid():N}");
    Directory.CreateDirectory(scratch);
    var recoveryConfig = new LocalCaption.Core.Data.Config();
    recoveryConfig.General.TranscriptFolder = scratch;
    recoveryConfig.Caption.ShowTimestamps = true;
    using var store = new LocalCaption.Core.Data.Store(Path.Combine(scratch, "sessions.db"));
    using var recovery = new LocalCaption.Session.AppEnvironment(recoveryConfig, store);

    Console.WriteLine($"leftover journals: {recovery.PendingRecoveries.Count}");
    foreach (var pending in recovery.PendingRecoveries.ToList())
    {
        var when = pending.StartedAt is { } at ? LocalCaption.Core.Transcripts.TimeFormat.Human(at) : "unknown time";
        Console.WriteLine($"  {when} · {pending.Segments.Count} segment(s) · {Path.GetFileName(pending.Path)}");
        Console.WriteLine(recovery.Recover(pending) ? "    recovered" : "    FAILED — journal kept");
    }

    foreach (var file in Directory.GetFiles(scratch, "*.txt"))
    {
        Console.WriteLine();
        foreach (var line in File.ReadAllLines(file)) Console.WriteLine($"  {line}");
    }
    return 0;
}

if (options.ListSources)
{
    // §4.5's picker, as data: the processes that currently hold a render session.
    var sources = AudioSessions.List();
    Console.WriteLine($"processes with an audio session ({sources.Count}):");
    foreach (var source in sources)
        Console.WriteLine($"  {(source.Active ? "▶" : " ")} {source.Executable,-24} {source.Name}");
    return sources.Count == 0 ? 1 : 0;
}

if (options.List || endpoints.Count == 0)
{
    Console.WriteLine($"render endpoints ({endpoints.Count} active):");
    for (var i = 0; i < endpoints.Count; i++) Console.WriteLine("  " + Describe(i, endpoints[i], defaultId));
    return endpoints.Count == 0 ? 1 : 0;
}

var device = Select(endpoints, options.Device, defaultId);
if (device is null)
{
    Console.Error.WriteLine($"no active render endpoint matches '{options.Device}'. Try --list.");
    return 1;
}

Console.WriteLine($"LocalCaption.Probe — {Environment.OSVersion.VersionString}");
Console.WriteLine($"endpoint: {device.FriendlyName}");
Console.WriteLine($"  id         {device.ID}");
Console.WriteLine($"  default    {(device.ID == defaultId ? "yes — mode B would pick this one" : "no")}");
Console.WriteLine($"  form       {FormFactor(device)}");

WaveFormat mix;
try
{
    mix = device.AudioClient.MixFormat;
}
catch (Exception e)
{
    Console.Error.WriteLine($"  FAILED to read the mix format: {e.Message}");
    return 1;
}

// §4.4: 48 kHz / 2 ch / 32-bit float is the common case, but 44.1 kHz and 6 channels are
// both realistic here — one HDMI monitor or a Realtek surround driver is enough. Whatever
// it reports is what the downmix and the resampler have to accept.
var carried = mix is NAudio.Wave.WaveFormatExtensible extensible ? Unwrap(extensible) : mix.Encoding.ToString();
Console.WriteLine($"  mix format {mix.SampleRate} Hz · {mix.Channels} ch · {mix.BitsPerSample}-bit {carried}");
Console.WriteLine($"  needs      {(mix.Channels > 1 ? "downmix + " : "")}resample {mix.SampleRate} to 16000 " +
                  (mix.SampleRate % 16000 == 0
                      ? "(integer ratio)"
                      : "(non-integer ratio — WdlResamplingSampleProvider, never decimation)"));
Console.WriteLine();

var keepalive = options.Keepalive ? StartRender(device, mix, silent: true, options) : null;
if (options.Keepalive)
{
    Console.WriteLine(keepalive is not null
        ? "keepalive: rendering silence to this endpoint (§4.3 mitigation 1)"
        : "keepalive: FAILED to start — measuring without it");
}

var playback = options.Play ? StartRender(device, mix, silent: false, options) : null;
if (options.Play)
{
    var gap = options.GapSeconds > 0
        ? $", silent from {(options.Seconds - options.GapSeconds) / 2:0.#}s to {(options.Seconds + options.GapSeconds) / 2:0.#}s"
        : "";
    Console.WriteLine(playback is not null
        ? $"playing: 440 Hz tone to this endpoint{gap}"
        : "playing: FAILED to start — the capture below is a silent run");
}

if (options.Acceptance)
{
    return await AcceptanceCheck.RunAsync(device, options);
}

if (options.Session)
{
    // B3 end to end: everything below the UI, against a real endpoint.
    return await SessionRun.RunAsync(device, options);
}

if (options.Capture)
{
    // Same endpoint, same conditions, but through LocalCaption.Audio rather than the raw
    // client: format normalisation, the §4.3 sample clock and the keepalive all in play.
    // What the raw probe proves about the driver, this proves about our own layer — and
    // with --play --gap it is §4.3's acceptance test, end to end.
    var exit = CaptureCheck.Run(device, options);
    playback?.Dispose();
    keepalive?.Dispose();
    return exit;
}

var result = Capture(device, mix, options);
playback?.Dispose();
keepalive?.Dispose();

if (result is null) return 1;
Console.WriteLine();
Console.WriteLine(result.Report(mix, options));
return 0;

// ── helpers ──────────────────────────────────────────────────────────────────────────────

static string Describe(int index, MMDevice device, string? defaultId)
{
    string format;
    try
    {
        var f = device.AudioClient.MixFormat;
        format = $"{f.SampleRate} Hz · {f.Channels} ch · {f.BitsPerSample}-bit {f.Encoding}";
    }
    catch (Exception e) { format = $"mix format unavailable — {e.GetType().Name}"; }

    var mark = device.ID == defaultId ? "  ← default" : "";
    return $"[{index}] {device.FriendlyName,-46} {format}{mark}";
}

/// <summary>The subtype behind a WAVEFORMATEXTENSIBLE, which is what §4.4 has to accept.</summary>
static string Unwrap(NAudio.Wave.WaveFormatExtensible extensible)
{
    try { return $"{extensible.ToStandardWaveFormat().Encoding} (extensible)"; }
    catch (InvalidOperationException) { return "Extensible — unrecognised subtype"; }
}

static string? TryDefaultRenderId(MMDeviceEnumerator enumerator)
{
    try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID; }
    catch (Exception) { return null; }
}

static MMDevice? Select(List<MMDevice> endpoints, string? wanted, string? defaultId)
{
    if (string.IsNullOrWhiteSpace(wanted))
        return endpoints.FirstOrDefault(d => d.ID == defaultId) ?? endpoints.FirstOrDefault();
    if (int.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        return index >= 0 && index < endpoints.Count ? endpoints[index] : null;
    return endpoints.FirstOrDefault(d => d.FriendlyName.Contains(wanted, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The endpoint's form factor, which §4.3 uses to decide whether the silent-render keepalive
/// is appropriate: pushing keepalive silence into a remote-desktop virtual speaker streams it
/// over the network for no benefit, so that endpoint must rely on padding alone.
/// </summary>
static string FormFactor(MMDevice device)
{
    try
    {
        // PKEY_AudioEndpoint_FormFactor — {1da5d803-d492-4edd-8c23-e0c0ffee7f0e}, pid 0.
        var key = new PropertyKey
        {
            formatId = new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"),
            propertyId = 0,
        };
        var factor = Convert.ToInt32(device.Properties[key].Value, CultureInfo.InvariantCulture);
        string[] names =
        [
            "RemoteNetworkDevice", "Speakers", "LineLevel", "Headphones", "Microphone", "Headset",
            "Handset", "UnknownDigitalPassthrough", "SPDIF", "DigitalAudioDisplayDevice",
            "UnknownFormFactor",
        ];
        return factor >= 0 && factor < names.Length ? $"{names[factor]} ({factor})" : $"code {factor}";
    }
    catch (Exception e) { return $"unreadable ({e.GetType().Name})"; }
}

/// <summary>
/// Open a second client on the endpoint in <i>render</i> mode, either writing zeros or
/// playing the test tone.
/// </summary>
/// <remarks>
/// Silent is §4.3 mitigation 1 — zero-valued frames keep the audio engine pumping so loopback
/// keeps producing buffers. Best-effort on purpose: mitigation 2 (device-position padding) is
/// meant to be the correctness guarantee, and this only has to make the common case pleasant.
/// Audible is the test signal, which exists so that "no packets" can be told apart from
/// "nothing was playing".
/// </remarks>
static IDisposable? StartRender(MMDevice device, WaveFormat mix, bool silent, ProbeOptions options)
{
    try
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
        var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: 100);
        output.Init(silent
            ? new SilenceProvider(format)
            : new ToneWithGap(format, options.Seconds, options.GapSeconds));
        output.Play();
        return output;
    }
    catch (Exception) { return null; }
}

static CaptureResult? Capture(MMDevice device, WaveFormat mix, ProbeOptions options)
{
    var client = device.AudioClient;
    try
    {
        // Shared mode + LOOPBACK, polled. §4.1 requires the shipping capture thread to be
        // event-driven, but events on a loopback stream are themselves raised by the render
        // engine — the very thing that stops during silence — so a poll loop is the honest
        // instrument for measuring whether packets arrive at all.
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback,
                          bufferDuration: 2_000_000, periodicity: 0, mix, Guid.Empty);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Initialize FAILED — {e.GetType().Name}: {e.Message}");
        Console.Error.WriteLine("W9 for this endpoint: loopback is unusable here. Mode A (process loopback) is the mitigation.");
        return null;
    }

    var capture = client.AudioCaptureClient;
    var result = new CaptureResult();
    var bytesPerFrame = mix.Channels * (mix.BitsPerSample / 8);

    Console.WriteLine($"capturing {options.Seconds:0.#}s from this endpoint…");
    client.Start();
    var run = Stopwatch.StartNew();
    var sinceLastPacket = Stopwatch.StartNew();

    try
    {
        while (run.Elapsed.TotalSeconds < options.Seconds)
        {
            if (capture.GetNextPacketSize() == 0)
            {
                Thread.Sleep(5);
                continue;
            }

            while (capture.GetNextPacketSize() != 0)
            {
                var buffer = capture.GetBuffer(out var frames, out var flags, out var devicePosition, out _);
                result.Observe(frames, flags, devicePosition, sinceLastPacket.Elapsed.TotalMilliseconds,
                               buffer, bytesPerFrame, mix);
                capture.ReleaseBuffer(frames);
                sinceLastPacket.Restart();
            }
        }
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"capture stopped after {run.Elapsed.TotalSeconds:0.0}s — {e.GetType().Name}: {e.Message}");
        result.Error = e.Message;
    }
    finally
    {
        try { client.Stop(); } catch (Exception) { }
    }

    // Silence that runs to the end of the capture is still silence — and a run with no
    // packets at all is one long gap, not zero gaps.
    result.Finish(sinceLastPacket.Elapsed.TotalMilliseconds);
    result.WallClockSeconds = run.Elapsed.TotalSeconds;
    result.Played = options.Play;
    return result;
}
