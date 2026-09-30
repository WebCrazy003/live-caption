using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalCaption.Core.Captions;

/// <summary>Text cleanup and hallucination suppression for ASR output.</summary>
public static partial class Filters
{
    [GeneratedRegex(@"<\|[^|]*\|>")] private static partial Regex SpecialTokens();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    /// <summary>
    /// Strip Whisper special/timestamp tokens (<c>&lt;|…|&gt;</c>) and <c>[BLANK_AUDIO]</c>,
    /// then collapse whitespace. Runs on every decode before anything else sees it.
    /// </summary>
    public static string Clean(string s)
    {
        var t = SpecialTokens().Replace(s, "");
        t = t.Replace("[BLANK_AUDIO]", "");
        t = Whitespace().Replace(t, " ");
        return t.Trim();
    }

    private static readonly HashSet<string> Blocklist = new(StringComparer.Ordinal)
    {
        "you", "thank you", "thanks", "thanks for watching", "bye", "bye bye",
        "so", "the", "uh", "um", "you know", "thank you so much", "okay", "mm", "hmm", "yeah",
    };

    /// <summary>
    /// Filter Whisper's silence hallucinations ("you you you", "Thank you.", …). Matching is
    /// on a stem that is lowercased, space-trimmed, then stripped of leading/trailing
    /// <c>.!?,-</c>, so "Thank you." and "thank you" are the same stem.
    /// </summary>
    public static bool IsHallucination(string text)
    {
        // Swift trims CharacterSet.whitespaces here — spaces and tabs, NOT newlines.
        var stem = text.ToLowerInvariant().Trim(' ', '\t').Trim('.', '!', '?', ',', '-');
        if (stem.Length == 0) return true;
        if (Blocklist.Contains(stem)) return true;

        var words = stem.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 3)
        {
            var unique = new HashSet<string>(words, StringComparer.Ordinal).Count;
            if (unique == 1) return true;                             // "you you you"
            if ((double)unique / words.Length < 0.34) return true;    // heavy repetition
        }
        return false;
    }

    /// <summary>
    /// Metadata junk/hallucination gate (SPEC.md §8.3). On Windows two of the three inputs
    /// are derived rather than reported — see SPEC-WINDOWS.md §5.6 — but the thresholds are
    /// identical to macOS and must not drift.
    /// </summary>
    public static bool IsLowQuality(double avgLogprob, double noSpeechProb, double compressionRatio)
    {
        if (noSpeechProb > 0.6) return true;        // silence mistaken for speech
        if (avgLogprob < -1.0) return true;         // low-confidence garble
        if (compressionRatio > 2.4) return true;    // repetition loop
        return false;
    }

    // ── English-only gate ───────────────────────────────────────────────────────────────
    // The two word lists are part of the cross-platform contract: keep them identical to
    // Filters.swift. testdata/filters/non-english.json is the regression test.

    /// <summary>
    /// English words that settle the question on sight. Words that are also everyday words
    /// in the languages below ("a", "no", "me", "in", "so", "was", "die") are left out on
    /// purpose, so a foreign sentence cannot be kept by one of them.
    /// </summary>
    private static readonly HashSet<string> EnglishMarkers = Words("""
        the and that this these those it its it's you your you're of to for with have had are were
        been being be but not can can't just like if about would could should which their there they
        them then than from when where what who why how my we our she his him i'm i've i'll i'd don't
        doesn't didn't isn't that's there's what's let's we're they're yeah yes okay ok right know
        think want need get got going go see say said make time one two three because really very
        some any out up by at is does did well now here more much many other into over only even back
        good new first way work thing things something people actually maybe sort mean sure thanks
        thank please hello hi hey sorry great yep nope cool nice still too after before again each
        every both few most such same own through between against during without within while
        where's who's he's she's wasn't weren't won't wouldn't couldn't shouldn't haven't hasn't
        aren't you've you'll you'd we've we'll they've they'll look take give use find tell ask try
        call keep let put seem feel leave show
        café résumé cliché naïve fiancé fiancée façade touché entrée déjà
        """);

    /// <summary>
    /// Function words of the languages the final model drifts into — Spanish, Portuguese,
    /// French, German, Italian. Anything that is also an English word or a common
    /// lower-case abbreviation ("per", "la", "es", "un", "si") is left out.
    /// </summary>
    private static readonly HashSet<string> ForeignWords = Words("""
        que el los las una uno del por pero muy está están eso esto este esta como pues bueno sí
        también porque cuando donde tiene tengo puede vamos nada todo todos ahora aquí gracias hola
        usted ustedes nosotros ellos ella ser va ir qué
        não você isso então tá bom vai com uma ele ela eu meu minha muito obrigado obrigada mas são
        tem foi para pra dos das mais aí já só sem de da te sua seu
        les des du une et est je ils elle nous vous ça cette c'est j'ai qui dans sur pas avec bien
        très sont suis alors donc oui merci quoi où ici aussi être avoir fait peut faut vais leur
        ces moi toi lui
        der das und ich nicht ist ein eine auch für zu wir aber habe haben sind oder wenn noch schon
        mit von dem ja nein danke bitte mir mich dich auf aus bei nach über wie nur mal sehr kann
        können muss wird werden dass diese dieser
        che di della dei sono questo questa non più anche ci gli hai siamo cosa così quando perché
        allora ecco grazie prego ciao tutti tutto molto bene fatto essere
        """);

    private static HashSet<string> Words(string list) =>
        new(list.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    /// <summary>Lu, Ll, Lt and Lo only, so both platforms agree on what a letter is.</summary>
    private static bool IsLetter(Rune r) => Rune.GetUnicodeCategory(r) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
        UnicodeCategory.TitlecaseLetter or UnicodeCategory.OtherLetter;

    /// <summary>Basic Latin, Latin-1, Latin Extended-A/B and Latin Extended Additional, plus µ.</summary>
    private static bool IsLatin(int v) =>
        v is (>= 0x41 and <= 0x5A) or (>= 0x61 and <= 0x7A) or 0xAA or 0xB5 or 0xBA
            or (>= 0xC0 and <= 0xD6) or (>= 0xD8 and <= 0xF6) or (>= 0xF8 and <= 0x24F)
            or (>= 0x1E00 and <= 0x1EFF);

    /// <summary>
    /// True when a caption is not English and must not be shown (SPEC.md §8.3).
    /// <para>
    /// Decoding is forced to English, but a multilingual final model still answers foreign
    /// speech, and sometimes plain noise, in another language. Two checks, both biased
    /// towards keeping text. <b>Script:</b> two or more letters outside the Latin alphabet,
    /// or a caption whose only letter is one; a lone "π" inside an English sentence passes.
    /// <b>Latin-script languages:</b> any <see cref="EnglishMarkers"/> word keeps the
    /// caption; otherwise it is dropped when it has an accented word or inverted
    /// punctuation, two <see cref="ForeignWords"/>, or nothing but <see cref="ForeignWords"/>.
    /// </para>
    /// <para>
    /// All-caps tokens are acronyms ("MIT", "DAS"), never evidence. This is word lists, not
    /// language identification: a foreign phrase with no function words and no accents
    /// ("Maus…") passes, and so does one inside an otherwise English caption.
    /// </para>
    /// </summary>
    public static bool IsNonEnglish(string text)
    {
        // Normalize throws on unpaired surrogates; such text is judged as it stands.
        try { text = text.Normalize(NormalizationForm.FormC); } catch (ArgumentException) { }
        text = text.Replace('\u2019', '\'');

        int latin = 0, other = 0;
        foreach (var r in text.EnumerateRunes())
        {
            if (!IsLetter(r)) continue;
            if (IsLatin(r.Value)) latin++; else other++;
        }
        if (other >= 2 || (other == 1 && latin == 0)) return true;

        var tokens = new List<string>();
        var current = new StringBuilder();
        void Flush()
        {
            var token = current.ToString().Trim('\'');
            if (token.Length > 0) tokens.Add(token);
            current.Clear();
        }
        foreach (var r in text.EnumerateRunes())
        {
            if (IsLetter(r) || r.Value == '\'') current.Append(r.ToString()); else Flush();
        }
        Flush();
        if (tokens.Count == 0) return false;

        var foreign = 0;
        var accented = text.Contains('¿') || text.Contains('¡');
        foreach (var token in tokens)
        {
            if (token.Length >= 2 && token.All(c => c is >= 'A' and <= 'Z')) continue;
            var lower = token.ToLowerInvariant();
            if (token == "I" || EnglishMarkers.Contains(lower)) return false;
            var hasAccent = token.Any(c => c > 0x7F);
            if (hasAccent) accented = true;
            if (hasAccent || ForeignWords.Contains(lower)) foreign++;
        }
        return accented || foreign >= 2 || foreign == tokens.Count;
    }

    public static int WordCount(string s) => s.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

    public static int SentenceCount(string s) => s.Count(c => c is '.' or '!' or '?');
}
