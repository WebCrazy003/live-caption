using System.Diagnostics;
using System.IO;
using System.Windows;

namespace LocalCaption.App.Interview.Sessions;

/// <summary>Explorer and Recycle Bin operations on a session's transcript export.</summary>
internal static class SessionShell
{
    /// <summary>
    /// Select the file in File Explorer — or, when it has gone, open its folder, as the main
    /// window's sidebar does. Problems are reported in a message box over <paramref name="owner"/>.
    /// </summary>
    public static void ShowInFolder(Window? owner, string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
            {
                using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
                return;
            }

            var folder = Path.GetDirectoryName(path);
            if (folder is { Length: > 0 } && Directory.Exists(folder))
            {
                using var _ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                return;
            }

            Report(owner, $"That folder no longer exists.\n\n{path}");
        }
        catch (Exception e)
        {
            Report(owner, $"Windows could not open that location.\n\n{path}\n\n{e.Message}");
        }
    }

    /// <summary>
    /// Send a transcript and its <c>.json</c> sidecar to the Recycle Bin (never a hard delete,
    /// like the sidebar). False when the <c>.txt</c> could not be moved — open in an editor, or
    /// the user cancelled Windows' error dialog — so the caller keeps the session row.
    /// </summary>
    public static bool RecycleTranscript(string txtPath)
    {
        foreach (var path in new[] { txtPath, Path.ChangeExtension(txtPath, ".json") })
        {
            if (!File.Exists(path)) continue;
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception)
            {
                // A sidecar that will not go is not worth keeping the row for; the transcript is.
                if (path == txtPath) return false;
            }
        }
        return true;
    }

    public static void Report(Window? owner, string message)
    {
        if (owner is { IsLoaded: true })
            MessageBox.Show(owner, message, "Local Caption", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(message, "Local Caption", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
