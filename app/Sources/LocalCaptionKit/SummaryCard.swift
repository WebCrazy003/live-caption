import Foundation

/// One "Key points" card — the summary of a single ~100-word block (SPEC-10). The view model
/// for the right-hand panel. `parse` is the **strict normalizer** the spec requires: raw small-
/// model output is unreliable (broken bullets, repetition, stray special tokens), so it is never
/// shown as-is — it is parsed into this fixed shape.
public struct SummaryCard: Identifiable, Equatable, Sendable {
    public let id: Int          // sequence index, assigned by the caller (stable for SwiftUI)
    public let main: String     // the one-line headline ask; may be "" only if truly empty
    public let bullets: [String]
    public let want: String     // "" when none

    public init(id: Int, main: String, bullets: [String], want: String) {
        self.id = id
        self.main = main
        self.bullets = bullets
        self.want = want
    }

    /// True when the model produced nothing usable for this block.
    public var isEmpty: Bool { main.isEmpty && bullets.isEmpty && want.isEmpty }

    // MARK: Parsing

    /// Normalize raw model output into a card. Tolerant by design — never throws, never crashes:
    /// - strips Whisper/LLM special tokens (`<|eot_id|>` etc.),
    /// - maps `SUMMARY:`/`MAIN:` → main and `TODO:`/`WANT:` → want (case-insensitive), plus `- ` bullets, flattening nested `- - ` bullets,
    /// - de-duplicates near-identical bullets (the small-model repetition failure mode),
    /// - caps bullets at `maxBullets`,
    /// - falls back to the first free line (or first bullet) when no `MAIN:` was emitted.
    /// Ask-phrases that mark a direct request to the listener. Used to gate the ToDo: a small
    /// model hallucinates tasks out of plain statements, so a ToDo is only trusted when the
    /// *source* transcript actually contains one of these signals (or a question mark).
    private static let requestSignals = [
        "can you", "could you", "would you", "will you", "can we", "could we",
        "please", "send me", "send us", "let me know", "let us know",
        "i need you", "we need you", "do you mind", "would you mind",
        "make sure", "get back to me", "email me", "call me", "you need to",
    ]

    /// True when `text` reads like a direct request to the listener (has an ask-phrase or "?").
    public static func looksLikeRequest(_ text: String) -> Bool {
        if text.contains("?") { return true }
        let lower = text.lowercased()
        return requestSignals.contains { lower.contains($0) }
    }

    /// Parse raw model output into a card. When `requestContext` is supplied (the source
    /// transcript block), the ToDo is dropped unless that text actually asks something of the
    /// listener — a deterministic guard against the small model inventing tasks from statements.
    public static func parse(_ raw: String, id: Int, maxBullets: Int = 4,
                             requestContext: String? = nil) -> SummaryCard {
        let cleaned = raw.replacingOccurrences(
            of: "<\\|[^|]*\\|>", with: "", options: .regularExpression)

        var main = "", want = "", firstFreeLine = ""
        var bullets: [String] = []

        for rawLine in cleaned.split(separator: "\n", omittingEmptySubsequences: false) {
            let line = String(rawLine).trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty else { continue }

            let isBulletLine = line.first == "-" || line.first == "•" || line.first == "*"
            // Flatten any run of leading bullet markers ("- - x" -> "x").
            let content = String(line.drop(while: { "-•*".contains($0) || $0 == " " }))
                .trimmingCharacters(in: .whitespaces)
            guard !content.isEmpty else { continue }
            let upper = content.uppercased()

            if upper.hasPrefix("SUMMARY:") {
                if main.isEmpty { main = tail(content, after: "SUMMARY:") }
            } else if upper.hasPrefix("MAIN:") {
                if main.isEmpty { main = tail(content, after: "MAIN:") }
            } else if upper.hasPrefix("TODO:") {
                if want.isEmpty { want = tail(content, after: "TODO:") }
            } else if upper.hasPrefix("WANT:") {
                if want.isEmpty { want = tail(content, after: "WANT:") }
            } else if isBulletLine {
                bullets.append(content)
            } else if firstFreeLine.isEmpty {
                firstFreeLine = content
            }
        }

        // De-dupe bullets on an alphabetic key, then cap.
        var seen = Set<String>()
        var uniqueBullets: [String] = []
        for b in bullets {
            let key = b.lowercased().filter { $0.isLetter || $0 == " " }
            if !key.isEmpty, !seen.contains(key) {
                seen.insert(key)
                uniqueBullets.append(b)
            }
        }
        if maxBullets >= 0 { uniqueBullets = Array(uniqueBullets.prefix(maxBullets)) }

        // WANT: drop a placeholder "-"; strip a leading dash the model sometimes adds.
        var wantClean = String(want.drop(while: { "-•* ".contains($0) })).trimmingCharacters(in: .whitespaces)
        if wantClean == "-" { wantClean = "" }
        // Gate: trust a ToDo only when the source block truly asks the listener something.
        if let ctx = requestContext, !wantClean.isEmpty, !looksLikeRequest(ctx) {
            wantClean = ""
        }

        // MAIN fallback so a card without an explicit MAIN still shows something.
        if main.isEmpty {
            if !firstFreeLine.isEmpty { main = firstFreeLine }
            else if !uniqueBullets.isEmpty { main = uniqueBullets.removeFirst() }
        }

        return SummaryCard(id: id, main: main, bullets: uniqueBullets, want: wantClean)
    }

    private static func tail(_ s: String, after prefix: String) -> String {
        String(s.dropFirst(prefix.count)).trimmingCharacters(in: .whitespaces)
    }
}
