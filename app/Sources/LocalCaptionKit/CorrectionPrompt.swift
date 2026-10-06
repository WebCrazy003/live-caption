import Foundation

/// Accent mode's Codex correction (SPEC-18 §Live correction, §Final pass): the instructions,
/// the numbered lines a turn sends, and the checks a reply line must pass before it replaces a
/// caption. Pure — the app's `LiveCorrector` and `FinalPass` own the Codex threads.
///
/// The rules are the ones measured in the spike (`spike/rtx-asr-spike/fix_codex.py`).
public enum CorrectionPrompt {
    /// One line to correct: its number in the session (1-based), the primary model's text and,
    /// when there is one, the secondary model's.
    public struct Line: Equatable, Sendable {
        public let number: Int
        public let primary: String
        public let secondary: String?
        public init(number: Int, primary: String, secondary: String?) {
            self.number = number; self.primary = primary; self.secondary = secondary
        }
    }

    public static func instructions(twoSources: Bool, vocabulary: String) -> String {
        let sources = twoSources
            ? "You get two transcripts of the same audio from different recognisers: P and W. They make different mistakes."
            : "You get one transcript from a speech recogniser (P)."
        var text = """
        You correct machine captions of a casual conversation, often accented English (for example \
        Nigerian English, sometimes Pidgin), several people, sometimes a noisy room.
        Each numbered line is one stretch of speech. \(sources)
        Write the words most likely actually spoken, using the conversation so far and the vocabulary.
        - Fix misheard words (e.g. a product name heard as an ordinary word). Keep the speaker's own \
        grammar and Pidgin; do not polish or summarise.
        - Do not add anything neither transcript supports. Mark a word you cannot work out as [?].
        - A line that is not English becomes [non-English]; pure noise becomes an empty line.
        Do not use any tools. Reply with exactly one line per input line, `<number><TAB><text>`, nothing else.
        """
        let words = vocabularyList(vocabulary)
        if !words.isEmpty {
            text += "\nVocabulary (names and terms likely to come up): " + words.joined(separator: ", ")
        }
        return text
    }

    /// The text of one turn. `context` (already-corrected lines) seeds a fresh thread.
    public static func turn(_ lines: [Line], context: [(number: Int, text: String)] = []) -> String {
        var parts: [String] = []
        if !context.isEmpty {
            parts.append("Already corrected (context only, do not repeat):\n"
                         + context.map { "\($0.number)\t\($0.text)" }.joined(separator: "\n"))
        }
        parts.append("New lines to correct:\n" + lines.map { line in
            var s = "\(line.number)\tP: \(oneLine(line.primary))"
            if let w = line.secondary { s += "\n\(line.number)\tW: \(oneLine(w))" }
            return s
        }.joined(separator: "\n"))
        return parts.joined(separator: "\n\n")
    }

    /// `<number>\t<text>` lines of a reply, keyed by number. Other lines are ignored.
    public static func parse(_ reply: String) -> [Int: String] {
        var out: [Int: String] = [:]
        for raw in reply.split(separator: "\n", omittingEmptySubsequences: true) {
            let parts = raw.split(separator: "\t", maxSplits: 1, omittingEmptySubsequences: false)
            guard parts.count == 2, let n = Int(parts[0].trimmingCharacters(in: .whitespaces)) else { continue }
            out[n] = parts[1].trimmingCharacters(in: .whitespaces)
        }
        return out
    }

    public enum Verdict: Equatable, Sendable {
        case accept(String)
        /// Keep the raw caption. `reason` is logged.
        case reject(reason: String)
    }

    /// Whether a reply line may replace its caption (SPEC-18 B18): it must be non-empty, and at
    /// least half of its words must occur in one of the sources or the vocabulary, so Codex can
    /// fix words but not invent content. `[?]` and `[non-English]` count as supported.
    public static func check(_ corrected: String, line: Line, vocabulary: String) -> Verdict {
        let words = tokens(corrected)
        guard !words.isEmpty else { return .reject(reason: "empty") }
        var known = Set(tokens(line.primary))
        if let w = line.secondary { known.formUnion(tokens(w)) }
        for term in vocabularyList(vocabulary) { known.formUnion(tokens(term)) }
        let supported = words.filter { $0 == "?" || $0 == "nonenglish" || known.contains($0) }.count
        return supported * 2 >= words.count ? .accept(corrected) : .reject(reason: "unsupported words")
    }

    /// Lower-cased words with punctuation stripped; `[non-English]` → `nonenglish`, `[?]` → `?`.
    static func tokens(_ text: String) -> [String] {
        text.replacingOccurrences(of: "[non-English]", with: " nonenglish ")
            .replacingOccurrences(of: "[?]", with: " ? ")
            .lowercased()
            .components(separatedBy: CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "'?")).inverted)
            .map { $0.trimmingCharacters(in: CharacterSet(charactersIn: "'")) }
            .filter { !$0.isEmpty }
    }

    static func vocabularyList(_ vocabulary: String) -> [String] {
        vocabulary.split(whereSeparator: { $0 == "," || $0 == "\n" })
            .map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    private static func oneLine(_ s: String) -> String {
        s.replacingOccurrences(of: "\n", with: " ").replacingOccurrences(of: "\t", with: " ")
    }
}
