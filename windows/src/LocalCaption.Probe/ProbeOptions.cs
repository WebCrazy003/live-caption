using System.Globalization;

namespace LocalCaption.Probe;

/// <summary>Command line for the B0 loopback probe.</summary>
public sealed record ProbeOptions(string? Device, double Seconds, bool List, bool Keepalive,
                                  bool Play, double GapSeconds, bool Capture, bool NoKeepalive,
                                  string? Process, bool ListSources, bool Session, string? Wav, bool Recover, bool Switch, bool FailSave, bool Acceptance, string? Models, string? SetDefault)
{
    public static ProbeOptions? Parse(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
                LocalCaption.Probe — WASAPI loopback reconnaissance (§4.7.3, §4.3).

                  --list              List active render endpoints and exit.
                  --list-sources      List processes holding an audio session (§4.5) and exit.
                  --session           Run a whole session end to end (B3): capture, decode,
                                      journal, transcript, database row. Throwaway folder.
                  --wav <path>        With --session: 16 kHz mono speech to play into the
                                      endpoint, so there is something to transcribe.
                  --recover           List leftover journals and save them as transcripts
                                      (§9.4). Run it after killing a session mid-recording.
                  --switch-device     §4.2: move the default playback device out from under a
                                      live capture and check it recovers. Puts it back after.
                  --set-default <n>   Make endpoint n the default and exit. Test plumbing for
                                      §17.18; remember to put it back.
                  --fail-save         With --session: point the transcript folder somewhere
                                      unwritable, to check §8 keeps the journal for recovery.
                  --acceptance        The §17 criteria that can run unattended: the 20 s
                                      silence gap, and a silent soak producing no captions.
                  --models <a,b>      With --session: interim,final. Default tiny.en,tiny.en.
                  --device <n|text>   Endpoint index or name fragment. Default: the default endpoint.
                  --seconds <n>       Capture duration. Default 20.
                  --play              Render a 440 Hz tone to the endpoint while capturing.
                  --gap <n>           Silence the tone for n seconds in the middle (§4.3's
                                      acceptance shape: audio, then nothing, then audio).
                  --keepalive         Also render silence to the endpoint (§4.3 mitigation 1).
                  --capture           Exercise LocalCaption.Audio end to end instead of the
                                      raw WASAPI layer: normalisation, sample clock, keepalive.
                  --no-keepalive      With --capture: run without §4.3 mitigation 1, to prove the
                                      wall-clock fallback holds the clock on its own.
                  --process <name>     With --capture: mode A instead of mode B — tap that
                                      executable's render stream. "self" taps this probe,
                                      which with --play is a self-contained mode A test.

                The three runs worth doing per endpoint:
                  (bare)                 does it deliver anything when the machine is quiet?
                  --play                 does the endpoint work at all?
                  --play --gap 8         does the clock survive a pause? ← the real question

                Writes nothing. Capture the output into windows/BENCH-RESULTS.md.
                """);
            return null;
        }

        string? Value(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        return new ProbeOptions(
            Device: Value("--device"),
            Seconds: double.TryParse(Value("--seconds"), CultureInfo.InvariantCulture, out var s) ? s : 20,
            List: args.Contains("--list"),
            Keepalive: args.Contains("--keepalive"),
            Capture: args.Contains("--capture"),
            NoKeepalive: args.Contains("--no-keepalive"),
            Process: Value("--process"),
            ListSources: args.Contains("--list-sources"),
            Session: args.Contains("--session"),
            Recover: args.Contains("--recover"),
            Switch: args.Contains("--switch-device"),
            FailSave: args.Contains("--fail-save"),
            Acceptance: args.Contains("--acceptance"),
            Models: Value("--models"),
            SetDefault: Value("--set-default"),
            Wav: Value("--wav"),
            Play: args.Contains("--play") || args.Contains("--gap"),
            GapSeconds: double.TryParse(Value("--gap"), CultureInfo.InvariantCulture, out var g) ? g : 0);
    }
}
