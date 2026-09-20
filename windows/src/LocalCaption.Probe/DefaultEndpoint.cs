using System.Runtime.InteropServices;

namespace LocalCaption.Probe;

/// <summary>
/// Changes the default playback device, so §4.2's recovery can be tested without a human
/// plugging a cable in.
/// </summary>
/// <remarks>
/// <para><b>Test harness only — never shipped.</b> §4.2 calls endpoint changes routine on
/// this machine and says they "must be tested as such": the default moves when an HDMI
/// monitor arrives, headphones pair, or a Jump Desktop session connects mid-call. Waiting for
/// one of those by hand is not a test anyone runs twice.</para>
/// <para><c>IPolicyConfig</c> is undocumented but has been stable since Vista and is what
/// every "set default audio device" utility uses. Only <c>SetDefaultEndpoint</c> is needed;
/// the earlier vtable slots are declared purely so the one that matters lands in the right
/// place.</para>
/// <para>Whatever this changes, it changes back — see <see cref="Swap"/>.</para>
/// </remarks>
internal static class DefaultEndpoint
{
    /// <summary>Make <paramref name="deviceId"/> the default for every role.</summary>
    public static bool Set(string deviceId)
    {
        try
        {
            var config = (IPolicyConfig)new PolicyConfigClient();
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, role));
            Marshal.ReleaseComObject(config);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Switch the default to <paramref name="deviceId"/> and hand back something that puts
    /// it back — including if the run throws or is cancelled part way.
    /// </summary>
    public static IDisposable Swap(string deviceId, string restoreTo) =>
        new Restorer(Set(deviceId) ? restoreTo : null);

    private sealed class Restorer(string? restoreTo) : IDisposable
    {
        public void Dispose()
        {
            if (restoreTo is { Length: > 0 }) Set(restoreTo);
        }
    }

    private enum Role
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient;

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // Ten slots stand between IUnknown and the one method this needs. Their signatures
        // are irrelevant; their count is not.
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();

        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);

        [PreserveSig] int SetEndpointVisibility();
    }
}
