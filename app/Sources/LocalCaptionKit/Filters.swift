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

    public static func wordCount(_ s: String) -> Int {
        s.split(whereSeparator: { $0 == " " || $0 == "\n" }).count
    }

    public static func sentenceCount(_ s: String) -> Int {
        s.reduce(0) { $1 == "." || $1 == "!" || $1 == "?" ? $0 + 1 : $0 }
    }
}
