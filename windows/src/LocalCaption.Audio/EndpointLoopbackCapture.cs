using NAudio.CoreAudioApi;

namespace LocalCaption.Audio;

/// <summary>
/// Mode B — capture everything rendered to one output endpoint (SPEC-WINDOWS.md §4.1).
/// </summary>
/// <remarks>
/// <para>The simpler, better-understood of the two capture paths, and the fallback when no
/// process is selected or process loopback fails. It is a strict improvement on the macOS
/// capture layer: no permission prompt, no virtual audio cable, no Multi-Output Device and
/// no setup instructions — the whole BlackHole chapter of <c>SPEC.md</c> disappears.</para>
/// <para>Its weakness is routing, and on this machine that is not theoretical: the default
/// endpoint moves when an HDMI monitor is plugged in, headphones pair, or a Jump Desktop
/// session connects mid-call (§4.6, §4.7). That is why mode A is the default and this is
/// the safety net.</para>
/// <para><b>The keepalive is not optional here.</b> B0 measured this endpoint class
/// delivering zero packets in twelve seconds with nothing rendering
/// (<c>windows/BENCH-RESULTS.md</c> §3); with the keepalive it held −18 ms over the same
/// twelve. See <see cref="SilentRenderKeepalive"/> for why that contradicts §4.3's
/// instruction to skip it on virtual devices.</para>
/// </remarks>
public sealed class EndpointLoopbackCapture : WasapiCapture
{
    private readonly string? _deviceId;
    private readonly bool _useKeepalive;

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private SilentRenderKeepalive? _keepalive;
    private DeviceWatcher? _watcher;
    private int _restarting;

    /// <param name="deviceId">
    /// The endpoint to capture, or null to follow the system default — which is what
    /// <c>audio.output_device</c> means when it is null (§9.2, §4.6).
    /// </param>
    /// <param name="keepalive">
    /// Whether to render silence to the endpoint (§4.3 mitigation 1). Always true in the
    /// app; false exists so the wall-clock fallback can be exercised deliberately, because
    /// otherwise the only way to test it is to wait for the keepalive to fail in the field.
    /// </param>
    public EndpointLoopbackCapture(string? deviceId = null, bool keepalive = true)
    {
        _deviceId = deviceId;
        _useKeepalive = keepalive;
    }

    /// <summary>The endpoint currently being captured, for §4.2's change handling.</summary>
    public string? DeviceId => _device?.ID;

    public override string ClockReport =>
        $"{base.ClockReport} · keepalive {(_keepalive?.IsRunning == true ? "on" : _keepalive?.Failure ?? "off")}";

    protected override OpenedStream OpenStream()
    {
        // One watcher for the life of the capture, not one per stream — a restart must not
        // stack up another subscription.
        _watcher ??= StartWatching();
        _enumerator ??= new MMDeviceEnumerator();
        _device = Resolve(_enumerator, _deviceId);

        var mix = _device.AudioClient.MixFormat;

        // Before the capture client, not after: the engine has to be running for loopback to
        // produce anything, and on a non-default endpoint nothing else will start it.
        if (_useKeepalive)
        {
            _keepalive = new SilentRenderKeepalive();
            _keepalive.Start(_device);
        }

        var client = _device.AudioClient;
        client.Initialize(AudioClientShareMode.Shared,
                          AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
                          bufferDuration: 2_000_000,       // 200 ms, in 100 ns units
                          periodicity: 0, mix, Guid.Empty);

        return new OpenedStream(client, mix, _device.FriendlyName);
    }

    protected override void CloseStream()
    {
        _keepalive?.Dispose();
        _keepalive = null;
    }

    protected override void Released()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>
    /// Follow endpoint changes for as long as this capture lives (§4.2).
    /// </summary>
    /// <remarks>
    /// <para>This belongs to the capture rather than to whatever is driving it: "follow the
    /// system default" is a promise <i>this class</i> makes, and a caller that constructs it
    /// should not have to wire up device notifications to make that promise true. Found the
    /// hard way — with the watching one layer up, a capture used anywhere else silently
    /// stayed on the old endpoint.</para>
    /// <para>On this machine the default moves when an HDMI monitor is plugged in, headphones
    /// pair, or a Jump Desktop session connects — mid-call. §4.2 calls that routine here.</para>
    /// </remarks>
    private DeviceWatcher StartWatching()
    {
        var watcher = new DeviceWatcher();
        watcher.Changed += _ =>
        {
            if (!ShouldFollow(watcher)) return;

            // Restart tears down and rebuilds, and the rebuild runs through OpenStream
            // again. Without this guard a burst of notifications could re-enter it.
            if (Interlocked.Exchange(ref _restarting, 1) == 1) return;
            try { Restart(); }
            catch (Exception) { /* the next notification, or the fault path, will retry */ }
            finally { Interlocked.Exchange(ref _restarting, 0); }
        };
        return watcher;
    }

    /// <summary>
    /// Whether this change affects the stream we are on. Notifications arrive for every
    /// endpoint, and rebuilding for an unrelated one would drop audio for nothing.
    /// </summary>
    private bool ShouldFollow(DeviceWatcher watcher) =>
        _deviceId is { Length: > 0 }
            ? DeviceId != _deviceId          // a pinned endpoint we had fallen back from is available again
            : DeviceId != watcher.DefaultRenderId;   // following the default, and it moved

    private static MMDevice Resolve(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try { return enumerator.GetDevice(deviceId); }
            catch (Exception)
            {
                // A remembered endpoint that has been unplugged is not an error worth
                // stopping for — §4.6's picker falls back to "follow default" when its
                // choice is gone, which is also what happens when a Jump Desktop session
                // ends mid-session.
            }
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
    }
}
