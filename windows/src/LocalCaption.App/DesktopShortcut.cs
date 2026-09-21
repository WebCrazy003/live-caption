using System.IO;
using System.Runtime.InteropServices;

namespace LocalCaption.App;

/// <summary>
/// Puts a "Local Caption" shortcut on the desktop, once.
/// </summary>
/// <remarks>
/// <para>The installer already makes one. This is for the copies that were never installed —
/// a published folder run in place, the portable zip — and for the day someone deletes the
/// icon and wants it back without reinstalling.</para>
/// <para>Written through the shell's own <c>WScript.Shell</c> automation object rather than
/// a hand-rolled <c>IShellLink</c> interop: a dozen lines instead of a hundred, present on
/// every Windows since 98, and it costs nothing until the button is pressed.</para>
/// </remarks>
public static class DesktopShortcut
{
    private const string FileName = "Local Caption.lnk";

    public static string Location =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), FileName);

    public static bool Exists => File.Exists(Location);

    /// <summary>
    /// Create the shortcut if there is none. Returns null on success or when one was already
    /// there, and the reason otherwise.
    /// </summary>
    public static string? EnsureExists()
    {
        if (Exists) return null;

        // Never the .dll and never "dotnet": the path of the process that is actually running
        // is the one thing guaranteed to start this build again.
        var target = Environment.ProcessPath;
        if (string.IsNullOrEmpty(target) || !File.Exists(target)) return "The app could not find its own executable.";

        object? shell = null;
        object? link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return "Windows Script Host is disabled on this PC.";

            shell = Activator.CreateInstance(type);
            dynamic wsh = shell!;
            link = wsh.CreateShortcut(Location);
            dynamic shortcut = link!;
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = Path.GetDirectoryName(target);
            shortcut.IconLocation = target + ",0";
            shortcut.Description = "Live captions for whatever this PC is playing";
            shortcut.Save();
            return Exists ? null : "Windows did not create the shortcut.";
        }
        catch (Exception e)
        {
            return e.Message;
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
