using System.Diagnostics;
using System.Threading.Channels;

namespace LocalCaption.Interview;

/// <summary>
/// Diagnostics from the Codex engine — the counterpart of the Mac's
/// <c>Logger(subsystem: "com.livecaption.app", category: "codex")</c>. Lines go to
/// <see cref="Trace"/> and to <see cref="Message"/>, which the app can route to its own log.
/// </summary>
/// <remarks>
/// The engine logs from its reader task and while holding its lock, so <see cref="Message"/> is
/// never raised there: lines are queued and handed to subscribers on one background task, in
/// order. A subscriber that blocks (a UI <c>Dispatcher.Invoke</c>) only delays later lines, and
/// one that throws is skipped — neither can stall or kill the engine.
/// </remarks>
public static class InterviewLog
{
    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private static int _pumpStarted;

    /// <summary>Raised for every line, in order, on a background thread (never the writer's).</summary>
    public static event Action<string>? Message;

    internal static void Write(string line)
    {
        Trace.WriteLine("[codex] " + line);
        if (Message is null) return;
        Queue.Writer.TryWrite(line);
        if (Interlocked.Exchange(ref _pumpStarted, 1) == 0) _ = Task.Run(PumpAsync);
    }

    private static async Task PumpAsync()
    {
        await foreach (var line in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (Message is not { } handlers) continue;
            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<string>)handler)(line);
                }
                catch (Exception e)
                {
                    Trace.WriteLine($"[codex] a log subscriber failed: {e.Message}");
                }
            }
        }
    }
}
