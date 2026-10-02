namespace LocalCaption.Core;

/// <summary>
/// Resolves and bootstraps the app's on-disk locations.
/// </summary>
/// <remarks>
/// <para>Windows layout (SPEC-WINDOWS.md §9), the counterpart of macOS's
/// <c>~/Library/Application Support/LocalCaption</c>:</para>
/// <code>
/// %LOCALAPPDATA%\LocalCaption\
/// ├── transcripts\      final .txt (+ .json sidecar)
/// ├── journal\          crash-recovery .jsonl
/// ├── models\           whisper.cpp GGUF weights
/// ├── config.json       versioned app config
/// └── localcaption.db   sqlite session metadata
/// </code>
/// <para><see cref="Environment.SpecialFolder.LocalApplicationData"/> is
/// <c>%LOCALAPPDATA%</c> on Windows. It resolves elsewhere on macOS, which is harmless:
/// this type exists so the Core test suite can run on the Mac (stage A1), and every test
/// passes an explicit directory rather than relying on these.</para>
/// </remarks>
public static class AppPaths
{
    /// <summary>
    /// <c>%LOCALAPPDATA%\LocalCaption</c>, unless <c>LOCALCAPTION_HOME</c> names somewhere else.
    /// </summary>
    /// <remarks>
    /// The override is for trying a build without putting real transcripts, settings and a
    /// multi-gigabyte model folder in its hands — a scratch profile, or a portable copy on a
    /// stick. Unset, which is always the case for an installed app, nothing changes.
    /// </remarks>
    public static string Root { get; } =
        Environment.GetEnvironmentVariable("LOCALCAPTION_HOME") is { Length: > 0 } home
            ? Path.GetFullPath(home)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalCaption");

    public static string Transcripts => Path.Combine(Root, "transcripts");
    public static string Journal => Path.Combine(Root, "journal");
    public static string Models => Path.Combine(Root, "models");
    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string DatabaseFile => Path.Combine(Root, "localcaption.db");

    /// <summary>Create the directory tree on first launch. Idempotent.</summary>
    public static string Bootstrap()
    {
        foreach (var dir in new[] { Root, Transcripts, Journal, Models })
            Directory.CreateDirectory(dir);
        return Root;
    }
}
