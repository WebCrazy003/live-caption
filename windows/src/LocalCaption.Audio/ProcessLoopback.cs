using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LocalCaption.Audio;

/// <summary>
/// The COM interop for process loopback (SPEC-WINDOWS.md §4.5), which NAudio does not wrap.
/// </summary>
/// <remarks>
/// <para>Activating a process-loopback client is unlike opening an endpoint in three ways,
/// each of which §4.5 warns "will otherwise cost a day":</para>
/// <list type="bullet">
///   <item><description>Activation is <b>asynchronous through a completion handler</b> —
///   there is no <c>GetDefaultAudioEndpoint</c> equivalent — so it is wrapped in a
///   <see cref="TaskCompletionSource{TResult}"/> here.</description></item>
///   <item><description>There is <b>no mix format to query</b>. The format is specified in
///   <c>Initialize</c> rather than negotiated.</description></item>
///   <item><description>The activation parameters travel as a <b>VT_BLOB PROPVARIANT</b>,
///   which has to be laid out by hand.</description></item>
/// </list>
/// <para>Requires Windows build 20348 or newer. The target here is 26200 (§0.5).</para>
/// </remarks>
internal static class ProcessLoopback
{
    /// <summary>The pseudo-device that process loopback activates against.</summary>
    private const string VirtualAudioDevice = "VAD\\Process_Loopback";

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    /// <summary>PROPVARIANT's <c>VT_BLOB</c>.</summary>
    private const ushort VtBlob = 65;

    private const int ActivationTypeProcessLoopback = 1;

    /// <summary>
    /// Include the target's whole process tree.
    /// </summary>
    /// <remarks>
    /// §4.5: this matters for browsers. Google Meet's audio is rendered by a Chrome
    /// <i>audio-service child process</i>, not the PID the user picked, so targeting the
    /// main browser process and excluding the tree captures silence.
    /// </remarks>
    private const int IncludeTargetProcessTree = 0;

    /// <summary>
    /// Activate an <c>IAudioClient</c> that taps one process tree's render stream.
    /// </summary>
    /// <remarks>
    /// Runs the activation on a thread-pool thread, which is MTA — the completion handler
    /// is invoked on an MTA thread and calling in from an STA UI thread is asking for a
    /// deadlock.
    /// </remarks>
    public static AudioClient Activate(int processId, TimeSpan timeout)
    {
        var task = Task.Run(() => ActivateCore(processId));
        if (!task.Wait(timeout))
            throw new TimeoutException($"Process loopback activation for PID {processId} did not complete.");
        return task.Result;
    }

    private static AudioClient ActivateCore(int processId)
    {
        var activation = new ActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = IncludeTargetProcessTree,
        };

        var blob = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        var variant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobPropVariant>());

        try
        {
            Marshal.StructureToPtr(activation, blob, fDeleteOld: false);
            Marshal.StructureToPtr(new BlobPropVariant
            {
                Type = VtBlob,
                Size = (uint)Marshal.SizeOf<ActivationParams>(),
                Data = blob,
            }, variant, fDeleteOld: false);

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualAudioDevice, IID_IAudioClient, variant, handler, out _);

            // The handler fires on an MTA thread once the audio service has answered.
            if (!handler.Completed.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The audio service did not answer the activation request.");

            handler.Completed.Task.Result.GetActivateResult(out var result, out var activated);
            Marshal.ThrowExceptionForHR(result);

            return new AudioClient((NAudio.CoreAudioApi.Interfaces.IAudioClient)activated);
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(blob);
        }
    }

    /// <summary>
    /// The format process loopback is asked for, since none can be queried.
    /// </summary>
    /// <remarks>
    /// §4.5 specifies 48 kHz / 2 ch / float32 and normalisation as in §4.4, rather than
    /// asking for 16 kHz mono directly: it is the format the engine is certain to produce,
    /// and <see cref="AudioNormalizer"/> has to handle the endpoint case anyway.
    /// </remarks>
    public static WaveFormat CaptureFormat => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    // ── interop ──────────────────────────────────────────────────────────────────────────

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult([MarshalAs(UnmanagedType.Error)] out int activateResult,
                               [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComVisible(true)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly TaskCompletionSource<IActivateAudioInterfaceAsyncOperation> Completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) =>
            Completed.TrySetResult(operation);
    }

    /// <summary>AUDIOCLIENT_ACTIVATION_PARAMS with its one-member union flattened.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>
    /// A PROPVARIANT holding a BLOB, laid out for x64.
    /// </summary>
    /// <remarks>
    /// The union begins at offset 8, and BLOB's pointer is 8-aligned — hence the explicit
    /// padding. Getting this wrong hands the audio service a garbage length and the
    /// activation fails with an HRESULT that says nothing about why.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct BlobPropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint Size;
        public uint Padding;
        public IntPtr Data;
    }
}
