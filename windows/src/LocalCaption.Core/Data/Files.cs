using System.Text;

namespace LocalCaption.Core.Data;

/// <summary>
/// File-writing conventions shared by the transcript, sidecar, journal and config.
/// </summary>
/// <remarks>
/// SPEC-WINDOWS.md §9.1: transcripts must be <b>UTF-8 without a BOM</b> with <c>\n</c> line
/// endings — not <c>\r\n</c> — so the Windows output stays byte-identical to the macOS
/// output and §17 can verify parity with a plain diff. .NET's default
/// <see cref="Encoding.UTF8"/> emits a BOM, which would silently break that on the very
/// first byte, so nothing here may use it.
/// </remarks>
public static class Files
{
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Write text atomically: temp file, then rename over the destination. A crash or power
    /// loss leaves either the old file or the new one, never a truncated one.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, contents, Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Normalise to <c>\n</c> endings regardless of what produced the string.</summary>
    public static string Lf(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
}
