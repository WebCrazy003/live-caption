using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace LocalCaption.Audio;

/// <summary>
/// Watches the render endpoints and says when the capture stream has to be rebuilt.
/// </summary>
/// <remarks>
/// <para><b>SPEC-WINDOWS.md §4.2.</b> On this machine endpoint changes are routine rather
/// than exceptional: plug in the HDMI monitor, pair Bluetooth headphones, or connect a Jump
/// Desktop session and the default render endpoint moves — mid-call (§4.6, §4.7). Mode B
/// follows the default, so every one of those invalidates the stream.</para>
/// <para>Events arrive in bursts — one session connecting produced several — so they are
/// coalesced into a single rebuild rather than restarting the client three times in a
/// second.</para>
/// <para>Mode A (process loopback) is immune to all of this, which is the main reason §4.1
/// made it the default. This watcher still runs in mode A for one case: the endpoint the
/// keepalive is rendering to going away.</para>
/// </remarks>
public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Lock _gate = new();
    private readonly TimeSpan _settle;
    private Timer? _debounce;
    private bool _registered;

    /// <param name="settle">
    /// How long to wait for the burst to finish before rebuilding. Long enough that a
    /// device change producing four notifications causes one restart; short enough that a
    /// live session recovers in well under a second.
    /// </param>
    public DeviceWatcher(TimeSpan? settle = null)
    {
        _settle = settle ?? TimeSpan.FromMilliseconds(400);
        _enumerator.RegisterEndpointNotificationCallback(this);
        _registered = true;
    }

    /// <summary>
    /// Raised once per burst, on a thread-pool thread. The handler must not block: it is
    /// called from a COM notification path.
    /// </summary>
    public event Action<string>? Changed;

    /// <summary>The current default render endpoint, or null if there is none.</summary>
    public string? DefaultRenderId
    {
        get
        {
            try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID; }
            catch (Exception) { return null; }
        }
    }

    private void Schedule(string reason)
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ =>
            {
                try { Changed?.Invoke(reason); } catch (Exception) { /* never throw into COM */ }
            }, null, _settle, Timeout.InfiniteTimeSpan);
        }
    }

    // ── IMMNotificationClient ────────────────────────────────────────────────────────────
    //
    // Only render-side changes matter. Capture endpoints, and anything about the roles we
    // do not follow, would only cause pointless restarts.

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Console) Schedule("the default output device changed");
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
        Schedule($"an output device became {newState}");

    public void OnDeviceAdded(string pwstrDeviceId) { }

    public void OnDeviceRemoved(string deviceId) => Schedule("an output device was removed");

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
        }

        if (_registered)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch (Exception) { }
            _registered = false;
        }

        _enumerator.Dispose();
    }
}
