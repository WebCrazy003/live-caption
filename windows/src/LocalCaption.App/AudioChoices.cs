using LocalCaption.Audio;
using LocalCaption.Core.Data;
using NAudio.CoreAudioApi;

namespace LocalCaption.App;

/// <summary>
/// One thing the app could listen to: a capture mode, and the application or device within it.
/// </summary>
/// <param name="Label">What the picker shows.</param>
/// <param name="Mode"><c>process</c> or <c>endpoint</c> — <c>audio.capture_mode</c>.</param>
/// <param name="Value">An executable name, a device ID, or null for the system default.</param>
public sealed record SourceChoice(string Label, string Mode, string? Value);

/// <summary>
/// The lists behind the two source pickers — the full one in Settings and the one-line one
/// in the toolbar — so they cannot drift apart in what they offer or how they name it.
/// </summary>
public static class AudioChoices
{
    /// <summary>Active output devices, as (name, device ID).</summary>
    public static IEnumerable<(string Label, string Id)> Endpoints()
    {
        MMDeviceCollection devices;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var device in devices)
        {
            string label, id;
            try { label = device.FriendlyName; id = device.ID; }
            catch (Exception) { continue; }
            yield return (label, id);
        }
    }

    /// <summary>
    /// Every source in one flat list: the system default, then applications, then devices.
    /// </summary>
    /// <remarks>
    /// Applications come first because §4.1 makes process capture the recommended mode, and
    /// the ones making sound right now lead — mid-call, the app you want is the noisy one. A
    /// configured application that is not currently running is still listed, so opening the
    /// picker never silently changes what is selected.
    /// </remarks>
    public static List<SourceChoice> All(Config config)
    {
        var choices = new List<SourceChoice>
        {
            new("Everything this PC plays", "endpoint", null),
            new("Auto — the call, when only one is playing", "auto", null),
        };

        var target = config.Audio.TargetProcess;
        var targetListed = false;
        foreach (var session in AudioSessions.List())
        {
            if (choices.Any(c => c.Mode == "process" &&
                                 string.Equals(c.Value, session.Executable, StringComparison.OrdinalIgnoreCase))) continue;
            // Said in the user's terms: a call in a VM reaches the host as the VM's audio, and
            // "vmware-vmx" is not a name anyone would recognise as "my interview".
            var kind = MeetingApps.Classify(session.Executable) switch
            {
                SourceKind.Meeting => "Meeting",
                SourceKind.Browser => "Browser",
                SourceKind.VirtualMachine => "Virtual machine",
                _ => "App",
            };
            choices.Add(new SourceChoice(
                session.Active ? $"{kind} · {session.Name}  ● playing" : $"{kind} · {session.Name}",
                "process", session.Executable));
            targetListed |= string.Equals(session.Executable, target, StringComparison.OrdinalIgnoreCase);
        }

        if (target is { Length: > 0 } && !targetListed)
            choices.Add(new SourceChoice($"App · {target} (not running)", "process", target));

        foreach (var (label, id) in Endpoints())
            choices.Add(new SourceChoice($"Device · {label}", "endpoint", id));

        return choices;
    }

    /// <summary>Which entry of <paramref name="choices"/> the config currently means.</summary>
    public static SourceChoice Current(IReadOnlyList<SourceChoice> choices, Config config)
    {
        if (config.Audio.CaptureMode.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return choices.First(c => c.Mode == "auto");

        var process = config.Audio.CaptureMode.Equals("process", StringComparison.OrdinalIgnoreCase) &&
                      config.Audio.TargetProcess is { Length: > 0 };

        var match = process
            ? choices.FirstOrDefault(c => c.Mode == "process" &&
                  string.Equals(c.Value, config.Audio.TargetProcess, StringComparison.OrdinalIgnoreCase))
            : choices.FirstOrDefault(c => c.Mode == "endpoint" && c.Value == config.Audio.OutputDevice);

        return match ?? choices[0];
    }
}
