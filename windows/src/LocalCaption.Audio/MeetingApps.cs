namespace LocalCaption.Audio;

/// <summary>What sort of program an audio source is, for the picker and for <c>auto</c>.</summary>
public enum SourceKind
{
    Other,
    Browser,
    VirtualMachine,
    Meeting,
}

/// <summary>
/// Recognises the programs a call is likely to be coming out of.
/// </summary>
/// <remarks>
/// <para>Three kinds matter. Meeting apps are the obvious one. Browsers are the common one —
/// Google Meet has no executable of its own, it is a tab. And virtual machines are the
/// easily forgotten one: a call taken inside a VM, so that a shared screen never shows this
/// window, reaches the host as the VM program's audio. All three capture the same way; the
/// difference is only in knowing that <c>vmware-vmx</c> is where the interviewer's voice is.</para>
/// <para>Names are compared without <c>.exe</c> and without case. The list is deliberately
/// short and boring: it is used to make a guess, and a guess should only be made from names
/// that cannot mean anything else.</para>
/// </remarks>
public static class MeetingApps
{
    private static readonly HashSet<string> Meetings = new(StringComparer.OrdinalIgnoreCase)
    {
        "ms-teams", "Teams", "msteams", "Zoom", "CptHost", "Webex", "CiscoCollabHost", "atmgr",
        "Slack", "Discord", "Skype", "lync", "GoToMeeting", "g2mcomm", "BlueJeans", "RingCentral",
    };

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "msedgewebview2",
    };

    private static readonly HashSet<string> VirtualMachines = new(StringComparer.OrdinalIgnoreCase)
    {
        "vmware-vmx", "vmware", "vmplayer", "VirtualBoxVM", "VirtualBox", "vmconnect", "vmwp",
        "mstsc", "msrdc", "qemu-system-x86_64", "prl_vm_app", "JumpDesktop", "AnyDesk", "TeamViewer",
    };

    public static SourceKind Classify(string executable)
    {
        var name = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable[..^4] : executable;
        if (Meetings.Contains(name)) return SourceKind.Meeting;
        if (VirtualMachines.Contains(name)) return SourceKind.VirtualMachine;
        if (Browsers.Contains(name)) return SourceKind.Browser;
        return SourceKind.Other;
    }

    /// <summary>
    /// The one program that is plainly the call, or null when that is not plain.
    /// </summary>
    /// <remarks>
    /// <para>Recording forty minutes of the wrong program's silence is the worst thing this
    /// app can do (§4.7.5), so this only answers when there is no judgement involved:
    /// exactly one recognised program is making sound right now. Two — a meeting and a
    /// browser both playing — or none, and the caller falls back to the whole output device,
    /// which can never be the wrong source, only a noisier one.</para>
    /// </remarks>
    public static AudioSource? TheOnePlaying(IEnumerable<AudioSource> sources)
    {
        var playing = sources.Where(s => s.Active && Classify(s.Executable) != SourceKind.Other).ToList();
        return playing.Count == 1 ? playing[0] : null;
    }
}
