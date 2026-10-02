using System.Globalization;
using System.Text;

namespace LocalCaption.Core.Interview;

/// <summary>The <c>name</c> / <c>description</c> of a skill's front matter, each null when absent.</summary>
public sealed record SkillFrontMatter(string? Name = null, string? Description = null);

/// <summary>
/// Reading a skill folder (specs/SPEC-13, shared rules) — the port of macOS's
/// <c>SkillFile</c>. Pinned by <c>testdata/library/skill-files.json</c>.
/// </summary>
public static class SkillFile
{
    public const string MainName = "SKILL.md";

    /// <summary>Per-skill size the Prepare panel warns above (specs/SPEC-13).</summary>
    public const int MaxChars = 60_000;

    /// <summary>
    /// Parse an optional leading <c>---</c> YAML block for <c>name</c> / <c>description</c>
    /// (single-line values, optional quotes). Anything else in the block is ignored.
    /// </summary>
    /// <remarks>
    /// Lines split on <c>\n</c> after CRLF is folded, and are trimmed of spaces and tabs only
    /// (Swift's <c>.whitespaces</c>: <c>Zs</c> + U+0009) — not of a lone CR or a line
    /// separator, which <see cref="string.Trim()"/> would also remove.
    /// </remarks>
    public static SkillFrontMatter FrontMatter(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (SwiftText.TrimWhitespaces(lines[0]) != "---") return new SkillFrontMatter();

        string? name = null, description = null;
        foreach (var line in lines.Skip(1))
        {
            var l = SwiftText.TrimWhitespaces(line);
            if (l == "---") break;
            var colon = l.IndexOf(':');
            if (colon < 0) continue;
            var key = SwiftText.TrimWhitespaces(l[..colon]).ToLowerInvariant();
            var value = SwiftText.TrimWhitespaces(l[(colon + 1)..]);
            if (value.Length >= 2 && value[0] is '"' or '\'' && value[0] == value[^1])
                value = value[1..^1];
            if (key == "name") name = value;
            if (key == "description") description = value;
        }
        return new SkillFrontMatter(name, description);
    }

    /// <summary>
    /// Split a skill folder's relative paths (<c>/</c>-separated) into the reference files
    /// that get inlined — <c>.md</c> and <c>.txt</c>, excluding <c>SKILL.md</c>, sorted by
    /// path — and the ones that are ignored. Anything under a component starting with a dot
    /// is skipped altogether.
    /// </summary>
    public static (IReadOnlyList<string> Included, IReadOnlyList<string> Ignored) Partition(
        IEnumerable<string> relativePaths)
    {
        var included = new List<string>();
        var ignored = new List<string>();
        foreach (var p in relativePaths.OrderBy(p => p, SwiftStringOrder.Instance))
        {
            if (p == MainName) continue;
            if (p.Split('/').Any(c => c.StartsWith('.'))) continue;
            var ext = PathExtension(p).ToLowerInvariant();
            (ext is "md" or "txt" ? included : ignored).Add(p);
        }
        return (included, ignored);
    }

    /// <summary><c>NSString.pathExtension</c>: after the last dot of the last component.</summary>
    private static string PathExtension(string path)
    {
        var last = path.TrimEnd('/');
        last = last[(last.LastIndexOf('/') + 1)..];
        var dot = last.LastIndexOf('.');
        return dot <= 0 ? "" : last[(dot + 1)..];
    }

    /// <summary>
    /// Swift's <c>String</c> ordering: Unicode scalar values of the NFC form — not UTF-16
    /// code units, which put an emoji (a surrogate pair) before <c>ｚ</c> (U+FF5A).
    /// </summary>
    private sealed class SwiftStringOrder : IComparer<string>
    {
        public static readonly SwiftStringOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Nfc(x ?? "").EnumerateRunes();
            var b = Nfc(y ?? "").EnumerateRunes();
            while (true)
            {
                var hasA = a.MoveNext();
                var hasB = b.MoveNext();
                if (!hasA || !hasB) return hasA.CompareTo(hasB);
                var c = a.Current.Value.CompareTo(b.Current.Value);
                if (c != 0) return c;
            }
        }

        private static string Nfc(string s)
        {
            try { return s.IsNormalized(NormalizationForm.FormC) ? s : s.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { return s; }   // a lone surrogate (NTFS allows them): compare as is
        }
    }
}
