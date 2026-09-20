using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace LocalCaption.Audio;

/// <summary>One application that currently has an audio session, for the source picker.</summary>
/// <param name="ProcessId">Live PID. Not persisted — §4.5 stores the executable name.</param>
/// <param name="Executable">What <c>audio.target_process</c> records.</param>
/// <param name="Name">What the picker shows.</param>
/// <param name="Active">True when it is making sound right now, not merely able to.</param>
public sealed record AudioSource(int ProcessId, string Executable, string Name, bool Active);

/// <summary>
/// Lists the applications worth offering as a capture target (SPEC-WINDOWS.md §4.5).
/// </summary>
/// <remarks>
/// <para>Settings → Audio gains a source picker built from this: the processes that
/// currently have a render session, with friendly names.</para>
/// <para>Sessions are enumerated per endpoint and one process can hold several — a browser
/// with three noisy tabs — so they are collapsed to one entry per process, and a process
/// counts as active if any of its sessions is.</para>
/// </remarks>
public static class AudioSessions
{
    /// <summary>Applications with a render session, most likely target first.</summary>
    public static IReadOnlyList<AudioSource> List()
    {
        var found = new Dictionary<int, AudioSource>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                CollectFrom(device, found);
            }
        }
        catch (Exception)
        {
            // An unreadable session list is a picker with fewer entries, not a failure —
            // the user can still capture the endpoint (mode B).
        }

        return [.. found.Values.OrderByDescending(s => s.Active).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>PIDs that currently hold a render session.</summary>
    public static HashSet<int> ProcessIds() => [.. List().Select(s => s.ProcessId)];

    private static void CollectFrom(MMDevice device, Dictionary<int, AudioSource> found)
    {
        try
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                var pid = (int)session.GetProcessID;

                // PID 0 is the system session — not something anyone means to caption.
                if (pid <= 0) continue;

                var active = session.State == AudioSessionState.AudioSessionStateActive;
                if (found.TryGetValue(pid, out var existing))
                {
                    if (active && !existing.Active) found[pid] = existing with { Active = true };
                    continue;
                }

                if (Describe(pid) is { } source) found[pid] = source with { Active = active };
            }
        }
        catch (Exception)
        {
            // One endpoint's sessions being unavailable should not lose the others'.
        }
    }

    private static AudioSource? Describe(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var name = string.IsNullOrWhiteSpace(process.MainWindowTitle)
                ? process.ProcessName
                : process.MainWindowTitle;
            return new AudioSource(pid, process.ProcessName, name, Active: false);
        }
        catch (Exception)
        {
            // The process exited between enumerating and describing it.
            return null;
        }
    }
}
