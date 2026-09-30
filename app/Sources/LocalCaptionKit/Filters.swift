import Foundation

/// Text-level cleanup + hallucination suppression for ASR output.
///
/// This is the pragmatic, text-only filter proven in the `minimal/` app and the Python
/// spike. SPEC-03's metadata-based gates (`no_speech_prob` / `avg_logprob` /
/// `compression_ratio`) are a Phase-4 upgrade layered on top of these.
public enum Filters {
    /// Strip Whisper special/timestamp tokens (`<|...|>`) and `[BLANK_AUDIO]`; collapse whitespace.
    public static func clean(_ s: String) -> String {
        var t = s.replacingOccurrences(of: "<\\|[^|]*\\|>", with: "", options: .regularExpression)
        t = t.replacingOccurrences(of: "[BLANK_AUDIO]", with: "")
        t = t.replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
        return t.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Filter Whisper's silence hallucinations ("you you you", "Thank you.", etc.).
    public static func isHallucination(_ text: String) -> Bool {
        let stem = text.lowercased().trimmingCharacters(in: .whitespaces)
            .trimmingCharacters(in: CharacterSet(charactersIn: ".!?,-"))
        if stem.isEmpty { return true }
        let block: Set<String> = [
            "you", "thank you", "thanks", "thanks for watching", "bye", "bye bye",
            "so", "the", "uh", "um", "you know", "thank you so much", "okay", "mm", "hmm", "yeah",
        ]
        if block.contains(stem) { return true }
        let words = stem.split(separator: " ").map(String.init)
        if words.count >= 3 {
            let unique = Set(words)
            if unique.count == 1 { return true }                                 // "you you you"
            if Double(unique.count) / Double(words.count) < 0.34 { return true }  // heavy repetition
        }
        return false
    }

    /// Metadata-based junk/hallucination gate (SPEC.md §8.3). Reject a decoded segment when
    /// Whisper's own confidence signals say it's noise or a repetition loop:
    /// - `noSpeechProb > 0.6`  (silence mistaken for speech)
    /// - `avgLogprob < -1.0`   (low-confidence garble)
    /// - `compressionRatio > 2.4` (repetition, e.g. "come come come…")
    public static func isLowQuality(avgLogprob: Double, noSpeechProb: Double, compressionRatio: Double) -> Bool {
        if noSpeechProb > 0.6 { return true }
        if avgLogprob < -1.0 { return true }
        if compressionRatio > 2.4 { return true }
        return false
    }

    // MARK: English-only gate

    /// English words that settle the question on sight. Words that are also everyday
    /// words in the languages below ("a", "no", "me", "in", "so", "was", "die") are left
    /// out on purpose, so a foreign sentence cannot be kept by one of them.
    static let englishMarkers = words("""
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
        """)

    /// Function words of the languages the final model drifts into — Spanish, Portuguese,
    /// French, German, Italian. Anything that is also an English word or a common
    /// lower-case abbreviation ("per", "la", "es", "un", "si") is left out.
    static let foreignWords = words("""
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
        """)

    private static func words(_ list: String) -> Set<String> {
        Set(list.split(whereSeparator: { $0 == " " || $0 == "\n" }).map(String.init))
    }

    /// Lu, Ll, Lt and Lo only, so both platforms agree on what a letter is.
    private static func isLetter(_ s: Unicode.Scalar) -> Bool {
        switch s.properties.generalCategory {
        case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .otherLetter: return true
        default: return false
        }
    }

    /// Basic Latin, Latin-1, Latin Extended-A/B and Latin Extended Additional, plus µ.
    private static func isLatin(_ v: UInt32) -> Bool {
        (0x41...0x5A).contains(v) || (0x61...0x7A).contains(v) || v == 0xAA || v == 0xB5 || v == 0xBA
            || (0xC0...0xD6).contains(v) || (0xD8...0xF6).contains(v) || (0xF8...0x24F).contains(v)
            || (0x1E00...0x1EFF).contains(v)
    }

    /// True when a caption is not English and must not be shown (SPEC.md §8.3).
    ///
    /// Decoding is forced to English, but a multilingual final model still answers
    /// foreign speech, and sometimes plain noise, in another language. Two checks, both
    /// biased towards keeping text:
    /// 1. **Script.** Two or more letters outside the Latin alphabet, or a caption whose
    ///    only letter is one. A lone "π" inside an English sentence passes.
    /// 2. **Latin-script languages.** Any `englishMarkers` word keeps the caption.
    ///    Otherwise it is dropped when it has an accented word or inverted punctuation,
    ///    two `foreignWords`, or nothing but `foreignWords`.
    ///
    /// All-caps tokens are acronyms ("MIT", "DAS"), never evidence. This is word lists,
    /// not language identification: a foreign phrase with no function words and no
    /// accents ("Maus…") passes, and so does one inside an otherwise English caption.
    public static func isNonEnglish(_ text: String) -> Bool {
        let text = text.precomposedStringWithCanonicalMapping.replacingOccurrences(of: "\u{2019}", with: "'")

        var latin = 0, other = 0
        for s in text.unicodeScalars where isLetter(s) {
            if isLatin(s.value) { latin += 1 } else { other += 1 }
        }
        if other >= 2 || (other == 1 && latin == 0) { return true }

        var tokens: [String] = []
        var current = String.UnicodeScalarView()
        func flush() {
            let token = String(current).trimmingCharacters(in: CharacterSet(charactersIn: "'"))
            if !token.isEmpty { tokens.append(token) }
            current = String.UnicodeScalarView()
        }
        for s in text.unicodeScalars {
            if isLetter(s) || s == "'" { current.append(s) } else { flush() }
        }
        flush()
        if tokens.isEmpty { return false }

        var foreign = 0
        var accented = text.contains("¿") || text.contains("¡")
        for token in tokens {
            let scalars = token.unicodeScalars
            if scalars.count >= 2, scalars.allSatisfy({ (0x41...0x5A).contains($0.value) }) { continue }
            let lower = token.lowercased()
            if token == "I" || englishMarkers.contains(lower) { return false }
            let hasAccent = scalars.contains { $0.value > 0x7F }
            if hasAccent { accented = true }
            if hasAccent || foreignWords.contains(lower) { foreign += 1 }
        }
        return accented || foreign >= 2 || foreign == tokens.count
    }

    public static func wordCount(_ s: String) -> Int {
        s.split(whereSeparator: { $0 == " " || $0 == "\n" }).count
    }

    public static func sentenceCount(_ s: String) -> Int {
        s.reduce(0) { $1 == "." || $1 == "!" || $1 == "?" ? $0 + 1 : $0 }
    }
}
