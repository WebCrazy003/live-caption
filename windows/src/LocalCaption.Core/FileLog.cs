using System.Diagnostics;

namespace LocalCaption.Core;

/// <summary>
/// Sends <see cref="Trace"/> output — Codex, hotkeys, screenshot, clipboard, speech fallback — to
/// <c>logs\app.log</c> under the app folder, so faults on a PC without a debugger can be read
/// (specs/SPEC-17 §2). The previous run's log is kept as <c>app.log.1</c>.
/// </summary>
public static class FileLog
{
    public static string Folder => Path.Combine(AppPaths.Root, "logs");
    public static string FilePath => Path.Combine(Folder, "app.log");

    /// <summary>Start logging; never throws — a read-only folder just means no file log.</summary>
    public static void Start(string? folder = null)
    {
        try
        {
            folder ??= Folder;
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "app.log");
            if (File.Exists(path)) File.Move(path, path + ".1", overwrite: true);
            var listener = new TextWriterTraceListener(
                new StreamWriter(path, append: false, Data.Files.Utf8NoBom) { AutoFlush = true }, "file")
            {
                TraceOutputOptions = TraceOptions.DateTime,
            };
            Trace.Listeners.Add(listener);
            Trace.AutoFlush = true;
            Trace.WriteLine($"{DateTimeOffset.Now:O} LocalCaption {AppInfo.Version()} started");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
