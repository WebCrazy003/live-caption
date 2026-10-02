using System.Globalization;
using System.Text;

namespace LocalCaption.Interview;

/// <summary>
/// One block of the small Markdown subset the coach writes (macOS <c>MarkdownText.swift</c>):
/// headings, bullets, numbered items, paragraphs and fenced code. Records, so a view can
/// compare the blocks of two renders and rebuild only what changed while an answer streams.
/// </summary>
public abstract record MarkdownBlock
{
    private MarkdownBlock() { }

    /// <summary><c># Title</c> … <c>###### Title</c>; <paramref name="Level"/> is the number of <c>#</c>.</summary>
    public sealed record Heading(string Text, int Level) : MarkdownBlock;

    /// <summary><c>- item</c>, <c>* item</c> or <c>• item</c>; <paramref name="Indent"/> is leading spaces ÷ 2.</summary>
    public sealed record Bullet(string Text, int Indent) : MarkdownBlock;

    /// <summary><c>1. item</c> or <c>1) item</c>; <paramref name="Number"/> is the digits as written.</summary>
    public sealed record Numbered(string Text, string Number) : MarkdownBlock;

    /// <summary>Consecutive plain lines, joined with a space.</summary>
    public sealed record Paragraph(string Text) : MarkdownBlock;

    /// <summary>A <c>```</c> fence's lines, verbatim (an unclosed fence runs to the end).</summary>
    public sealed record Code(string Text) : MarkdownBlock;
}

/// <summary>Inline styling of a <see cref="MarkdownRun"/>; flags combine (<c>***x***</c> is bold and italic).</summary>
[Flags]
public enum MarkdownStyle
{
    /// <summary>Plain text.</summary>
    None = 0,
    /// <summary><c>**x**</c> or <c>__x__</c>.</summary>
    Bold = 1,
    /// <summary><c>*x*</c> or <c>_x_</c>.</summary>
    Italic = 2,
    /// <summary><c>`x`</c> — never combined with the others' parsing (its content is literal).</summary>
    Code = 4,
    /// <summary><c>~~x~~</c>.</summary>
    Strike = 8,
    /// <summary><c>[x](url)</c>; the run's <see cref="MarkdownRun.Url"/> is set.</summary>
    Link = 16,
}

/// <summary>A stretch of inline text with one style.</summary>
/// <param name="Text">The text as shown (markers removed, escapes and entities resolved).</param>
/// <param name="Style">Its styling.</param>
/// <param name="Url">The link target when <see cref="MarkdownStyle.Link"/> is set.</param>
public readonly record struct MarkdownRun(string Text, MarkdownStyle Style, string? Url = null);

/// <summary>
/// The Markdown parser behind the Answers panel and the summary — pure code, no UI framework,
/// so it is tested on the Mac.
/// </summary>
/// <remarks>
/// <para><see cref="Parse"/> is a line-by-line port of <c>MarkdownText.blocks</c> in
/// <c>app/Sources/LocalCaption/UI/MarkdownText.swift</c>, including its quirks (indent counts
/// spaces only; a heading needs a space after the hashes; a fence line's info string is
/// ignored; an unclosed fence still renders as code, which is what a streaming answer shows
/// mid-fence).</para>
/// <para><see cref="Inline"/> stands in for Foundation's
/// <c>AttributedString(markdown:, .inlineOnlyPreservingWhitespace)</c>: bold, italic,
/// bold-italic, code spans, strikethrough, links, backslash escapes and the common entities.
/// It is a simplified delimiter matcher, not full CommonMark; an unmatched marker stays as
/// literal text (so a half-streamed <c>**bold</c> shows its asterisks until the closer
/// arrives, as on the Mac).</para>
/// <para>Cost is linear in the text for ordinary answers, so re-parsing a whole answer on
/// every coalesced update (≤ 20 a second) is cheap; views diff the blocks to keep layout work
/// to the block that changed.</para>
/// </remarks>
public static class MarkdownBlocks
{
    // ── Blocks ───────────────────────────────────────────────────────────────────────────

    /// <summary>Split <paramref name="markdown"/> into blocks (Swift <c>MarkdownText.blocks</c>).</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        var output = new List<MarkdownBlock>();
        var paragraph = new List<string>();
        List<string>? code = null;

        void Flush()
        {
            if (paragraph.Count == 0) return;
            output.Add(new MarkdownBlock.Paragraph(string.Join(" ", paragraph)));
            paragraph.Clear();
        }

        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (TrimWhitespaces(raw).StartsWith("```", StringComparison.Ordinal))
            {
                if (code is not null)
                {
                    output.Add(new MarkdownBlock.Code(string.Join("\n", code)));
                    code = null;
                }
                else
                {
                    Flush();
                    code = [];
                }
                continue;
            }
            if (code is not null)
            {
                code.Add(raw);
                continue;
            }

            var line = TrimWhitespaces(raw);
            var indent = LeadingSpaces(raw) / 2;
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (HeadingLevel(line) is { } level)
            {
                Flush();
                output.Add(new MarkdownBlock.Heading(TrimWhitespaces(line[level..]), level));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)
                     || line.StartsWith("• ", StringComparison.Ordinal))
            {
                Flush();
                output.Add(new MarkdownBlock.Bullet(line[2..], indent));
            }
            else if (NumberedPrefix(line) is { } dot)
            {
                Flush();
                output.Add(new MarkdownBlock.Numbered(line[(dot + 2)..], line[..dot]));
            }
            else
            {
                paragraph.Add(line);
            }
        }
        if (code is not null) output.Add(new MarkdownBlock.Code(string.Join("\n", code)));
        Flush();
        return output;
    }

    /// <summary>The heading level when the line is <c>#…# text</c> (a space right after the hashes), else null.</summary>
    private static int? HeadingLevel(string line)
    {
        if (!line.StartsWith('#')) return null;
        var i = 0;
        while (i < line.Length && line[i] == '#') i++;
        return i < line.Length && line[i] == ' ' ? i : null;
    }

    /// <summary>The index of the <c>.</c> / <c>)</c> in <c>12. text</c>, else null.</summary>
    private static int? NumberedPrefix(string line)
    {
        var dot = line.IndexOfAny(['.', ')']);
        if (dot <= 0) return null;
        for (var i = 0; i < dot; i++)
            if (!char.IsNumber(line[i])) return null;
        return dot + 1 < line.Length && line[dot + 1] == ' ' ? dot : null;
    }

    private static int LeadingSpaces(string raw)
    {
        var n = 0;
        while (n < raw.Length && raw[n] == ' ') n++;
        return n;
    }

    /// <summary>Swift <c>trimmingCharacters(in: .whitespaces)</c>: space separators and TAB, never line breaks.</summary>
    private static string TrimWhitespaces(string text)
    {
        static bool IsSpace(char c) => c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start])) start++;
        while (end > start && IsSpace(text[end - 1])) end--;
        return start == 0 && end == text.Length ? text : text[start..end];
    }

    // ── Inline ───────────────────────────────────────────────────────────────────────────

    /// <summary>The inline runs of one block's text, adjacent runs of equal style merged.</summary>
    public static IReadOnlyList<MarkdownRun> Inline(string text)
    {
        var runs = new List<MarkdownRun>();
        ParseInline(text, 0, text.Length, MarkdownStyle.None, null, runs);
        return Merge(runs);
    }

    /// <summary>The text with every inline marker removed — what a screen reader or a plain copy should get.</summary>
    public static string PlainText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var run in Inline(text)) sb.Append(run.Text);
        return sb.ToString();
    }

    private static void ParseInline(string s, int start, int end, MarkdownStyle style, string? url, List<MarkdownRun> runs)
    {
        var literal = new StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            runs.Add(new MarkdownRun(literal.ToString(), style, url));
            literal.Clear();
        }

        var i = start;
        while (i < end)
        {
            var c = s[i];

            // \* — a backslash before ASCII punctuation makes it literal.
            if (c == '\\' && i + 1 < end && IsAsciiPunctuation(s[i + 1]))
            {
                literal.Append(s[i + 1]);
                i += 2;
                continue;
            }

            // `code` — content literal; unmatched backticks are literal.
            if (c == '`')
            {
                var ticks = RunLength(s, i, end, '`');
                var close = FindBacktickClose(s, i + ticks, end, ticks);
                if (close >= 0)
                {
                    FlushLiteral();
                    var content = s[(i + ticks)..close].Replace('\n', ' ');
                    if (content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.Trim(' ').Length > 0)
                        content = content[1..^1];
                    runs.Add(new MarkdownRun(content, style | MarkdownStyle.Code, url));
                    i = close + ticks;
                    continue;
                }
                literal.Append('`', ticks);
                i += ticks;
                continue;
            }

            // *em*, **strong**, ***both***, and the _ forms.
            if (c is '*' or '_')
            {
                var length = RunLength(s, i, end, c);
                if (length <= 3 && CanOpen(s, i, length, start, end, c)
                    && FindEmphasisClose(s, i + length, end, c, length, start) is { } close)
                {
                    FlushLiteral();
                    var add = length switch
                    {
                        1 => MarkdownStyle.Italic,
                        2 => MarkdownStyle.Bold,
                        _ => MarkdownStyle.Bold | MarkdownStyle.Italic,
                    };
                    ParseInline(s, i + length, close, style | add, url, runs);
                    i = close + length;
                    continue;
                }
                literal.Append(c, length);
                i += length;
                continue;
            }

            // ~~strike~~
            if (c == '~' && i + 1 < end && s[i + 1] == '~')
            {
                var close = i + 2 < end ? s.IndexOf("~~", i + 2, end - (i + 2), StringComparison.Ordinal) : -1;
                if (close > i + 2 && !char.IsWhiteSpace(s[i + 2]) && !char.IsWhiteSpace(s[close - 1]))
                {
                    FlushLiteral();
                    ParseInline(s, i + 2, close, style | MarkdownStyle.Strike, url, runs);
                    i = close + 2;
                    continue;
                }
                literal.Append("~~");
                i += 2;
                continue;
            }

            // [text](url)
            if (c == '[' && (style & MarkdownStyle.Link) == 0 && FindLink(s, i, end) is { } link)
            {
                FlushLiteral();
                ParseInline(s, i + 1, link.TextEnd, style | MarkdownStyle.Link, link.Url, runs);
                i = link.End;
                continue;
            }

            // &amp; and friends.
            if (c == '&' && Entity(s, i, end) is { } entity)
            {
                literal.Append(entity.Text);
                i += entity.Length;
                continue;
            }

            literal.Append(c);
            i++;
        }
        FlushLiteral();
    }

    private static int RunLength(string s, int i, int end, char c)
    {
        var n = 0;
        while (i + n < end && s[i + n] == c) n++;
        return n;
    }

    private static int FindBacktickClose(string s, int from, int end, int ticks)
    {
        var i = from;
        while (i < end)
        {
            if (s[i] != '`')
            {
                i++;
                continue;
            }
            var n = RunLength(s, i, end, '`');
            if (n == ticks) return i;
            i += n;
        }
        return -1;
    }

    /// <summary>An opener must not be followed by whitespace; an <c>_</c> opener must not sit inside a word.</summary>
    private static bool CanOpen(string s, int i, int length, int start, int end, char c)
    {
        var after = i + length;
        if (after >= end || char.IsWhiteSpace(s[after])) return false;
        if (c == '_' && i > start && char.IsLetterOrDigit(s[i - 1])) return false;
        return true;
    }

    /// <summary>
    /// The start of the closing delimiter for an opener of <paramref name="length"/>, or null.
    /// A closer is a run of the same character not preceded by whitespace (for <c>_</c>, not
    /// followed by a letter or digit). An opener of 1 or 2 matches a run of exactly its length
    /// or of 3+ (taking that run's last characters, so <c>**a *b***</c> nests); 3 needs 3+.
    /// Code spans and escapes are skipped while looking.
    /// </summary>
    private static int? FindEmphasisClose(string s, int from, int end, char c, int length, int start)
    {
        var i = from;
        while (i < end)
        {
            var ch = s[i];
            if (ch == '\\' && i + 1 < end)
            {
                i += 2;
                continue;
            }
            if (ch == '`')
            {
                var ticks = RunLength(s, i, end, '`');
                var close = FindBacktickClose(s, i + ticks, end, ticks);
                i = close >= 0 ? close + ticks : i + ticks;
                continue;
            }
            if (ch != c)
            {
                i++;
                continue;
            }
            var run = RunLength(s, i, end, c);
            var precededBySpace = i == start || char.IsWhiteSpace(s[i - 1]);
            var followedByWord = c == '_' && i + run < end && char.IsLetterOrDigit(s[i + run]);
            var fits = length == 3 ? run >= 3 : run == length || run >= 3;
            if (!precededBySpace && !followedByWord && fits && i > from) return i + run - length;
            i += run;
        }
        return null;
    }

    private readonly record struct LinkMatch(int TextEnd, string Url, int End);

    /// <summary><c>[text](url)</c> starting at <paramref name="i"/>: brackets nest, the URL may hold balanced parentheses.</summary>
    private static LinkMatch? FindLink(string s, int i, int end)
    {
        var depth = 0;
        var j = i;
        for (; j < end; j++)
        {
            if (s[j] == '\\') { j++; continue; }
            if (s[j] == '[') depth++;
            else if (s[j] == ']' && --depth == 0) break;
        }
        if (j >= end || j + 1 >= end || s[j + 1] != '(') return null;
        var textEnd = j;
        var parens = 0;
        var k = j + 2;
        for (; k < end; k++)
        {
            if (s[k] == '(') parens++;
            else if (s[k] == ')')
            {
                if (parens == 0) break;
                parens--;
            }
            else if (s[k] == ' ' || s[k] == '\n') return null;
        }
        if (k >= end) return null;
        var target = s[(j + 2)..k];
        if (target.Length >= 2 && target[0] == '<' && target[^1] == '>') target = target[1..^1];
        return new LinkMatch(textEnd, target, k + 1);
    }

    private static readonly Dictionary<string, string> Entities = new(StringComparer.Ordinal)
    {
        ["&amp;"] = "&", ["&lt;"] = "<", ["&gt;"] = ">", ["&quot;"] = "\"", ["&apos;"] = "'",
        ["&#39;"] = "'", ["&nbsp;"] = " ",
    };

    private static (string Text, int Length)? Entity(string s, int i, int end)
    {
        var semi = s.IndexOf(';', i, Math.Min(end - i, 8));
        if (semi < 0) return null;
        return Entities.TryGetValue(s[i..(semi + 1)], out var text) ? (text, semi + 1 - i) : null;
    }

    private static bool IsAsciiPunctuation(char c) =>
        c < 128 && (char.IsPunctuation(c) || char.IsSymbol(c));

    private static List<MarkdownRun> Merge(List<MarkdownRun> runs)
    {
        var merged = new List<MarkdownRun>(runs.Count);
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].Style == run.Style && merged[^1].Url == run.Url)
                merged[^1] = merged[^1] with { Text = merged[^1].Text + run.Text };
            else
                merged.Add(run);
        }
        return merged;
    }
}
