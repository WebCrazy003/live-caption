using System.Runtime.InteropServices;

namespace LocalCaption.Audio;

/// <summary>
/// Keeps the machine awake for the length of a recording.
/// </summary>
/// <remarks>
/// <para><b>SPEC-WINDOWS.md §4.7.6.</b> The user is not sitting at the G15 — they are
/// watching it over Jump Desktop — so nothing they do counts as activity. "A machine that
/// sleeps at minute 30 of a 90-minute interview is the worst failure mode this app has."</para>
/// <para><c>ES_DISPLAY_REQUIRED</c> is deliberately <b>not</b> set: the screen may sleep
/// harmlessly, and on a laptop keeping the panel lit for ninety minutes is a cost with no
/// benefit.</para>
/// <para>The flags apply to the calling thread, so both calls have to happen on the same
/// one. <see cref="Acquire"/> returns a handle that releases on dispose from wherever it is
/// disposed — which is why the work is marshalled onto a thread this class owns.</para>
/// </remarks>
public static class SleepPrevention
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        Continuous = 0x80000000,
        AwayModeRequired = 0x00000040,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    /// <summary>
    /// Ask Windows to stay awake until the returned handle is disposed. Never throws —
    /// failing to prevent sleep must not prevent recording.
    /// </summary>
    public static IDisposable Acquire() => new Handle();

    private sealed class Handle : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _release = new(false);

        public Handle()
        {
            // One dedicated thread holds the request for its whole lifetime, because the
            // state is per-thread and a thread-pool thread would take it away with it.
            _thread = new Thread(Hold)
            {
                IsBackground = true,
                Name = "LocalCaption sleep prevention",
            };
            _thread.Start();
        }

        private void Hold()
        {
            try
            {
                SetThreadExecutionState(ExecutionState.Continuous |
                                        ExecutionState.SystemRequired |
                                        ExecutionState.AwayModeRequired);
                _release.Wait();
            }
            catch (Exception) { }
            finally
            {
                try { SetThreadExecutionState(ExecutionState.Continuous); } catch (Exception) { }
            }
        }

        public void Dispose()
        {
            _release.Set();
            try { _thread.Join(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            _release.Dispose();
        }
    }
}
