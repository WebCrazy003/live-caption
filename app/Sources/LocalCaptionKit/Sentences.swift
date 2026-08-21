import Foundation

/// Sentence utilities for the clipboard "Copy last N sentences" feature (SPEC.md §9.4).
public enum Sentences {
    /// Split text into sentences on terminal punctuation (`.`, `!`, `?`), keeping the mark.
    public static func split(_ text: String) -> [String] {
        var out: [String] = []
        var cur = ""
        for ch in text {
            cur.append(ch)
            if ch == "." || ch == "!" || ch == "?" {
                let trimmed = cur.trimmingCharacters(in: .whitespacesAndNewlines)
                if !trimmed.isEmpty { out.append(trimmed) }
                cur = ""
            }
        }
        let tail = cur.trimmingCharacters(in: .whitespacesAndNewlines)
        if !tail.isEmpty { out.append(tail) }   // trailing fragment without punctuation
        return out
    }

    /// The last `n` sentences of `text`, joined with spaces.
    public static func lastN(_ text: String, n: Int) -> String {
        guard n > 0 else { return "" }
        return split(text).suffix(n).joined(separator: " ")
    }

    /// Append a provisional caption to committed text before selecting the last `n`
    /// sentences. This lets clipboard callers expose an utterance at its endpoint without
    /// waiting for the final transcription pass.
    public static func lastN(_ text: String, appending provisional: String, n: Int) -> String {
        let committed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let temporary = provisional.trimmingCharacters(in: .whitespacesAndNewlines)
        let combined: String
        if committed.isEmpty { combined = temporary }
        else if temporary.isEmpty { combined = committed }
        else { combined = committed + " " + temporary }
        return lastN(combined, n: n)
    }
}
