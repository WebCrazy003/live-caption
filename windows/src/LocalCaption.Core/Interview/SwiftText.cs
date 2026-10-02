using System.Globalization;

namespace LocalCaption.Core.Interview;

/// <summary>
/// The two Swift <c>String</c> semantics the Interview ports depend on, so the shared vectors
/// and ordinary input agree with the macOS build.
/// </summary>
internal static class SwiftText
{
    /// <summary>
    /// <c>split(whereSeparator: \.isWhitespace)</c>: words separated by runs of whitespace
    /// (line breaks included), empty runs omitted.
    /// </summary>
    public static List<string> SplitOnWhitespace(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var space = i == text.Length || char.IsWhiteSpace(text[i]);
            if (!space && start < 0) start = i;
            else if (space && start >= 0)
            {
                words.Add(text[start..i]);
                start = -1;
            }
        }
        return words;
    }

    /// <summary>
    /// <c>trimmingCharacters(in: .whitespaces)</c>: general category Zs plus TAB only.
    /// Unlike <see cref="string.Trim()"/>, line breaks are kept.
    /// </summary>
    public static string TrimWhitespaces(string text)
    {
        static bool IsSpace(char c) => c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start])) start++;
        while (end > start && IsSpace(text[end - 1])) end--;
        return text[start..end];
    }
}
