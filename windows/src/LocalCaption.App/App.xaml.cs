using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using LocalCaption.Core.Transcripts;
using LocalCaption.Session;
using Velopack;

namespace LocalCaption.App;

/// <summary>
/// Startup: build the environment, offer to recover anything the last run left behind, then
/// show the window.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Machine-wide, per-user: two copies must not record at once.
    /// </summary>
    /// <remarks>
    /// Not in the spec, added after watching it happen. Two instances capture the same audio
    /// into two journals and two database rows, and before <c>Journal.Pending</c> learned to
    /// skip live journals the second one offered to "recover" the first one's session — which
    /// would have deleted a recording in progress. The failure is silent and the fix is a
    /// mutex, so the mutex wins.
    /// </remarks>
    private const string InstanceMutexName = @"Local\LocalCaption.SingleInstance";

    private Mutex? _instance;
    private AppEnvironment? _env;

    protected override void OnStartup(StartupEventArgs e)
    {
        // First, before any window or any file is touched. The installer re-runs this
        // executable with its own arguments to perform install, update and uninstall steps,
        // and this is what services them — anything done before it would run during those
        // invocations too (§11).
        VelopackApp.Build().Run();

        _instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var isOnly);
        if (!isOnly)
        {
            // Bring the running copy forward rather than explaining the refusal — the user
            // asked for Local Caption and there is one, it is just behind something.
            FocusExistingWindow();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _env = new AppEnvironment();

        if (_env.ConfigWasRepaired)
            MessageBox.Show("The settings file could not be read, so it has been reset. " +
                            "The previous one was kept alongside it.",
                            "Local Caption", MessageBoxButton.OK, MessageBoxImage.Information);

        OfferRecovery(_env);

        MainWindow = new MainWindow(_env);
        MainWindow.Show();
    }

    /// <summary>
    /// §9.4: journals that outlived their session. Offer them before a new session starts,
    /// because starting one is exactly what would bury them.
    /// </summary>
    private static void OfferRecovery(AppEnvironment env)
    {
        if (env.PendingRecoveries.Count == 0) return;

        var summary = new StringBuilder();
        summary.AppendLine("These sessions did not stop cleanly — the app quit or crashed while recording.");
        summary.AppendLine("Their captions were written to disk as they happened and can still be saved.");
        summary.AppendLine();

        foreach (var pending in env.PendingRecoveries)
        {
            var when = pending.StartedAt is { } at
                ? TimeFormat.Human(at.ToLocalTime())
                : "unknown time";
            summary.AppendLine($"  · {when} — {pending.Segments.Count} caption(s)");
        }

        summary.AppendLine();
        summary.AppendLine("Save them as transcripts?");

        var answer = MessageBox.Show(summary.ToString(), "Recover unsaved sessions",
                                     MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;   // leave them; the offer repeats next launch

        var failed = 0;
        foreach (var pending in env.PendingRecoveries.ToList())
            if (!env.Recover(pending))
                failed++;

        if (failed > 0)
            MessageBox.Show($"{failed} session(s) could not be written and have been kept for another try.",
                            "Local Caption", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Raise whichever window the first instance already has open.</summary>
    private static void FocusExistingWindow()
    {
        try
        {
            var mine = Environment.ProcessId;
            foreach (var other in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
            {
                using (other)
                {
                    if (other.Id == mine || other.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindow(other.MainWindowHandle, RestoreIfMinimised);
                    SetForegroundWindow(other.MainWindowHandle);
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Failing to raise the other window is not worth blocking on; this instance is
            // exiting either way.
        }
    }

    private const int RestoreIfMinimised = 9;      // SW_RESTORE

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    protected override void OnExit(ExitEventArgs e)
    {
        _env?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
