namespace LocalCaption.Audio;

/// <summary>Why a capture stopped or degraded, and whether it can be recovered.</summary>
/// <param name="Message">Shown to the user, so it says what happened, not which HRESULT.</param>
/// <param name="Recoverable">
/// True when restarting the client against the current device is worth trying — a device
/// change or an invalidated endpoint (§4.2). False means the session must pause and the user
/// has to choose something: the target process exited, or the endpoint is gone entirely.
/// </param>
public sealed record CaptureFault(string Message, bool Recoverable, Exception? Cause = null);

/// <summary>
/// One source of 16 kHz mono audio, whatever it is underneath.
/// </summary>
/// <remarks>
/// <para>Two implementations, per SPEC-WINDOWS.md §4.1:
/// <see cref="ProcessLoopbackCapture"/> (mode A, the v1 default — taps one app's render
/// stream, immune to endpoint routing) and <see cref="EndpointLoopbackCapture"/> (mode B,
/// the fallback — taps everything going to one output device).</para>
/// <para>The contract is deliberately the same shape as the macOS capture layer's: a stream
/// of float32 16 kHz mono buffers, so §6's ported logic needs no changes and the
/// <see cref="Core.Audio.CaptureBuffer"/> on the other end does not know which mode
/// produced them.</para>
/// </remarks>
public interface IAudioCapture : IDisposable
{
    /// <summary>What is being captured, for the session header and the log (§4.7.5).</summary>
    string Source { get; }

    /// <summary>The negotiated format and what it is being converted to.</summary>
    string Format { get; }

    /// <summary>
    /// Peak level of the most recent audio, 0–1, for the Active Session meter.
    /// </summary>
    /// <remarks>
    /// §4.7.5 promotes this from polish to a requirement: the worst outcome of the whole
    /// remote setup is recording forty minutes of silence because the wrong process or
    /// endpoint was captured, and a meter makes that impossible to miss in seconds.
    /// </remarks>
    float Level { get; }

    /// <summary>16 kHz mono float samples, including the §4.3 clock corrections.</summary>
    event Action<float[]>? Samples;

    /// <summary>Raised on the capture thread; never throws into it.</summary>
    event Action<CaptureFault>? Fault;

    void Start();
    void Stop();
}
