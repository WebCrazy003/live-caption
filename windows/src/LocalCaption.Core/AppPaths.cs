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
/// ├── localcaption.db   sqlite sessions, captions and interviews
/// └── interview\        Interview Assist library, outbox, Codex home (specs/SPEC-11)
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

    // Interview Assist (specs/SPEC-11 §On-disk layout), as on macOS. Kept out of the
    // transcript folder: it holds the CV. Nothing here is created by Bootstrap.

    /// <summary><c>interview\</c></summary>
    public static string Interview => Path.Combine(Root, "interview");

    /// <summary><c>interview\library\</c> — imported documents and skills.</summary>
    public static string Library => Path.Combine(Interview, "library");

    /// <summary><c>interview\library\index.json</c> (<c>InterviewLibraryIndex</c>).</summary>
    public static string LibraryIndex => Path.Combine(Library, "index.json");

    public static string Skills => Path.Combine(Library, "skills");
    public static string Documents => Path.Combine(Library, "documents");

    /// <summary>Short-lived PNGs handed to Codex as <c>localImage</c> input; deleted when the turn ends.</summary>
    public static string Outbox => Path.Combine(Interview, "outbox");

    /// <summary>Codex's working directory. Must stay empty (specs/SPEC-12 §Lockdown).</summary>
    public static string Workspace => Path.Combine(Interview, "workspace");

    /// <summary>
    /// Dedicated <c>CODEX_HOME</c>, so the user's own Codex config never loads
    /// (specs/SPEC-12 §Lockdown).
    /// </summary>
    public static string CodexHome => Path.Combine(Interview, "codex-home");

    /// <summary>Codex's stderr (specs/SPEC-12).</summary>
    public static string CodexLog => Path.Combine(Interview, "codex.log");

    /// <summary>Create the directory tree on first launch. Idempotent.</summary>
    public static string Bootstrap()
    {
        foreach (var dir in new[] { Root, Transcripts, Journal, Models })
            Directory.CreateDirectory(dir);
        return Root;
    }
}
