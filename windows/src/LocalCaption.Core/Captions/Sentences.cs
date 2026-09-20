using System.Text;

namespace LocalCaption.Core.Captions;

/// <summary>Sentence utilities behind the clipboard's "Copy last N sentences" (SPEC.md §9.4).</summary>
public static class Sentences
{
    /// <summary>
    /// Split on terminal punctuation (<c>.</c>, <c>!</c>, <c>?</c>), keeping the mark. A
    /// trailing fragment without punctuation is still a sentence — the live line usually is.
    /// </summary>
    public static List<string> Split(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            current.Append(ch);
            if (ch is not ('.' or '!' or '?')) continue;
            var trimmed = current.ToString().Trim();
            if (trimmed.Length > 0) result.Add(trimmed);
            current.Clear();
        }
        var tail = current.ToString().Trim();
        if (tail.Length > 0) result.Add(tail);
        return result;
    }

    /// <summary>The last <paramref name="n"/> sentences, joined with spaces.</summary>
    public static string LastN(string text, int n)
    {
        if (n <= 0) return "";
        var all = Split(text);
        return string.Join(" ", all.Skip(Math.Max(0, all.Count - n)));
    }

    /// <summary>
    /// Append a provisional caption to the committed text before selecting. This is what
    /// lets the clipboard expose an utterance at its speech endpoint, without waiting for
    /// the final transcription pass.
    /// </summary>
    public static string LastN(string text, string provisional, int n)
    {
        var committed = text.Trim();
        var temporary = provisional.Trim();
        var combined = committed.Length == 0 ? temporary
                     : temporary.Length == 0 ? committed
                     : committed + " " + temporary;
        return LastN(combined, n);
    }
}
