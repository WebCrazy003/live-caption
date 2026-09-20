using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LocalCaption.Audio;

/// <summary>
/// Renders zero-valued frames to an endpoint so its audio engine keeps running, and
/// loopback keeps producing buffers.
/// </summary>
/// <remarks>
/// <para><b>SPEC-WINDOWS.md §4.3 mitigation 1 — and B0 promoted it from a convenience to
/// the load-bearing one</b> (<c>windows/BENCH-RESULTS.md</c> §3).</para>
/// <para>Measured on the G15: the Jump Desktop Virtual Speaker with nothing rendering
/// delivered <b>zero packets in twelve seconds</b>. With this running and nothing else
/// changed, the same endpoint held <b>−18 ms over twelve seconds</b>. The same endpoint
/// also sat through eight seconds of digital silence <i>inside</i> an active render stream
/// without dropping a single packet — so what keeps loopback alive is a stream existing,
/// not the samples being non-zero.</para>
/// <para><b>This contradicts §4.3's instruction to skip the keepalive on remote-desktop
/// virtual devices.</b> The spec's reasoning was that pushing silence into the Jump Desktop
/// speaker streams it over the network for no benefit; the measurement says the benefit is
/// the entire sample clock. It is also not gateable as written — that endpoint reports its
/// form factor as <c>Speakers</c>, not <c>RemoteNetworkDevice</c>. So this runs on every
/// endpoint, and the cost is a stream of zeros that any transport worth using will
/// compress to nothing.</para>
/// </remarks>
public sealed class SilentRenderKeepalive : IDisposable
{
    private WasapiOut? _output;

    public bool IsRunning => _output is not null;

    /// <summary>Why it is not running, when it is not. Null while healthy.</summary>
    public string? Failure { get; private set; }

    /// <summary>
    /// Start rendering silence. Never throws: the §4.3 device-position padding and the
    /// wall-clock rule in <see cref="Core.Audio.SampleClock"/> are what make a failure here
    /// survivable rather than fatal, and a capture that runs without a keepalive is still
    /// better than one that refuses to start.
    /// </summary>
    public bool Start(MMDevice device)
    {
        Stop();
        try
        {
            var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: 200);
            output.Init(new SilenceProvider(output.OutputWaveFormat));
            output.Play();
            _output = output;
            Failure = null;
            return true;
        }
        catch (Exception e)
        {
            Failure = e.Message;
            return false;
        }
    }

    public void Stop()
    {
        var output = _output;
        _output = null;
        if (output is null) return;
        try { output.Stop(); } catch (Exception) { }
        try { output.Dispose(); } catch (Exception) { }
    }

    public void Dispose() => Stop();
}
