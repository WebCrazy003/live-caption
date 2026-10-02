using System.Globalization;
using System.Text;

namespace LocalCaption.Core.Interview;

/// <summary>
/// Folder names for library documents and skills (specs/SPEC-13, shared rules) — the port of
/// macOS's <c>LibrarySlug</c>. Pinned by <c>testdata/library/slug.json</c>.
/// </summary>
/// <remarks>
/// <para>macOS folds with <c>String.folding([.diacriticInsensitive, .caseInsensitive,
/// .widthInsensitive])</c>. Probing every assigned code point up to U+2FFFF (2026-10-02)
/// showed that this is <b>not</b> Unicode compatibility folding (<c>FormKD</c>):
/// superscripts, ordinals, fractions, circled and math letters, <c>ĳ</c>/<c>ǆ</c> digraphs
/// keep their own identity and so become separators. What it is, to the letter of the
/// output here, is:</para>
/// <list type="number">
/// <item>width folding — the Halfwidth and Fullwidth Forms block (U+FF00–U+FFEF) to its
/// compatibility equivalent (<c>ＣＶ</c> → <c>CV</c>);</item>
/// <item>full case folding — which, unlike <see cref="string.ToLowerInvariant"/>, expands
/// <c>ß ẞ ſ ẛ ŉ ẚ</c> and the Latin ligatures <c>ﬀ–ﬆ</c> (<see cref="CaseFoldExpansions"/>,
/// the only 13 code points where the two disagree on the slug);</item>
/// <item>diacritic folding — canonical decomposition (<c>FormD</c>) with every
/// grapheme-extending mark dropped (<c>Mn</c>, <c>Me</c> and <c>Other_Grapheme_Extend</c>,
/// so ZWNJ and skin tones vanish while ZWJ separates), except eight precomposed vowel signs
/// Foundation keeps whole.</item>
/// </list>
/// <para>Checked against Foundation per code point (each alone and between two letters) and
/// on 30 000 random mixed-script strings, with no difference.</para>
/// </remarks>
public static class LibrarySlug
{
    /// <summary>
    /// Lowercase ASCII <c>[a-z0-9-]</c>, runs of anything else collapsed to one dash, no
    /// leading or trailing dash; <c>"item"</c> when nothing is left. A taken slug gets
    /// <c>-2</c>, <c>-3</c>… (the first one free).
    /// </summary>
    public static string Make(string title, IReadOnlySet<string>? taken = null)
    {
        var output = new StringBuilder();
        var dash = false;
        foreach (var ch in Fold(title))
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                output.Append(ch);
                dash = false;
            }
            else if (!dash && output.Length > 0)
            {
                output.Append('-');
                dash = true;
            }
        }
        while (output.Length > 0 && output[^1] == '-') output.Length--;
        var slug = output.Length == 0 ? "item" : output.ToString();

        if (taken is null || !taken.Contains(slug)) return slug;
        var n = 2;
        while (taken.Contains($"{slug}-{n}")) n++;
        return $"{slug}-{n}";
    }

    /// <summary>
    /// Code points whose full case folding (Unicode <c>CaseFolding.txt</c>, status F/S)
    /// changes the slug, mapped to their folded form. Everything else folds correctly through
    /// <see cref="string.ToLowerInvariant"/>.
    /// </summary>
    internal static readonly IReadOnlyDictionary<int, string> CaseFoldExpansions = new Dictionary<int, string>
    {
        [0x00DF] = "ss",            // ß
        [0x0149] = "ʼn",       // ŉ
        [0x017F] = "s",             // ſ
        [0x1E9A] = "aʾ",       // ẚ
        [0x1E9B] = "ṡ",        // ẛ → ṡ
        [0x1E9E] = "ss",            // ẞ
        [0xFB00] = "ff",
        [0xFB01] = "fi",
        [0xFB02] = "fl",
        [0xFB03] = "ffi",
        [0xFB04] = "ffl",
        [0xFB05] = "st",
        [0xFB06] = "st",
    };

    /// <summary>
    /// What diacritic folding removes besides <c>Mn</c>/<c>Me</c>: the rest of Unicode's
    /// <c>Grapheme_Extend</c> — the <c>Other_Grapheme_Extend</c> code points (length marks,
    /// ZWNJ, emoji skin-tone modifiers, tag characters). ZWJ is not among them.
    /// </summary>
    private static bool IsOtherGraphemeExtend(int v) => v is
        0x09BE or 0x09D7 or 0x0B3E or 0x0B57 or 0x0BBE or 0x0BD7 or 0x0CC2 or 0x0CD5 or 0x0CD6 or
        0x0D3E or 0x0D57 or 0x0DCF or 0x0DDF or 0x1B35 or 0x200C or 0x302E or 0x302F or
        0x1133E or 0x11357 or 0x114B0 or 0x114BD or 0x115AF or 0x1171E or 0x11930 or 0x1D165 or
        (>= 0x1D16E and <= 0x1D172) or (>= 0x1F3FB and <= 0x1F3FF) or (>= 0xE0020 and <= 0xE007F);

    /// <summary>
    /// Precomposed vowel signs Foundation leaves whole although <c>FormD</c> splits them into
    /// marks that would all be dropped; on macOS they separate words, so they do here.
    /// </summary>
    private static bool IsKeptWhole(int v) => v is
        0x0CC0 or 0x0CC7 or 0x0CC8 or 0x0CCA or 0x0CCB or 0x1B3B or 0x1B3D or 0x1B43;

    /// <summary>
    /// Foundation's diacritic + case + width folding, as far as a slug can see it: the result
    /// has the same <c>[a-z0-9]</c> characters and the same separator runs, not necessarily
    /// the same other characters.
    /// </summary>
    internal static string Fold(string title)
    {
        // EnumerateRunes replaces a lone surrogate with U+FFFD, so Normalize cannot throw.
        var mapped = new StringBuilder(title.Length);
        foreach (var rune in title.EnumerateRunes())
        {
            if (rune.Value is >= 0xFF00 and <= 0xFFEF)
                mapped.Append(rune.ToString().Normalize(NormalizationForm.FormKD));
            else if (CaseFoldExpansions.TryGetValue(rune.Value, out var folded))
                mapped.Append(folded);
            else if (IsKeptWhole(rune.Value))
                mapped.Append(' ');
            else
                mapped.Append(rune.ToString());
        }

        var decomposed = mapped.ToString().Normalize(NormalizationForm.FormD);
        var output = new StringBuilder(decomposed.Length);
        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) continue;
            if (IsOtherGraphemeExtend(rune.Value)) continue;
            output.Append(rune.ToString());
        }
        return output.ToString().ToLowerInvariant();
    }
}
