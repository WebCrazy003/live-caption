import Foundation

/// Decides which transcript text an Ask sends (SPEC-14 §What gets sent). Pure; shared with the
/// Windows build through `testdata/ask/`.
///
/// The live interim line is always included: the final decode lags ~2 s behind speech and the
/// user presses right as the interviewer stops, so waiting for finals would cost the answer.
public enum AskSelection {
    /// A committed final, in session audio time.
    public struct Segment: Equatable, Sendable {
        public let text: String
        public let tStartMs: Int
        public let tEndMs: Int
        public init(text: String, tStartMs: Int, tEndMs: Int) {
            self.text = text; self.tStartMs = tStartMs; self.tEndMs = tEndMs
        }
        public init(_ s: TranscriptSegment) { self.init(text: s.text, tStartMs: s.tStartMs, tEndMs: s.tEndMs) }
    }

    /// Where the previous Ask ended.
    public struct Mark: Equatable, Sendable {
        public let audioMs: Int
        /// The utterance in flight at that press was sent as interim text, so its final — which
        /// straddles `audioMs` — counts as already asked.
        public let interimWasSent: Bool
        public init(audioMs: Int, interimWasSent: Bool) {
            self.audioMs = audioMs; self.interimWasSent = interimWasSent
        }
    }

    public enum Mode: Equatable, Sendable {
        case sinceLastAsk
        case lastSentences(Int)
    }

    public struct Result: Equatable, Sendable {
        /// Empty when there is nothing new to send.
        public let text: String
        public let mark: Mark
        /// Audio span the ask covers, for the interview record.
        public let fromMs: Int
        public let toMs: Int
    }

    public static func select(segments: [Segment], interim: String, mode: Mode, mark: Mark?,
                              pressAudioMs: Int, maxWords: Int) -> Result {
        let provisional = interim.trimmingCharacters(in: .whitespacesAndNewlines)
        let text: String
        switch mode {
        case .sinceLastAsk:
            let fresh = segments.filter { seg in
                guard let mark else { return true }
                if seg.tStartMs >= mark.audioMs { return true }
                let straddles = seg.tEndMs > mark.audioMs
                return straddles && !mark.interimWasSent
            }
            text = join(fresh.map(\.text) + [provisional])
        case .lastSentences(let n):
            text = Sentences.lastN(join(segments.map(\.text)), appending: provisional, n: n)
        }
        return Result(text: lastWords(text, maxWords),
                      mark: Mark(audioMs: pressAudioMs, interimWasSent: !provisional.isEmpty),
                      fromMs: mark?.audioMs ?? 0,
                      toMs: pressAudioMs)
    }

    private static func join(_ parts: [String]) -> String {
        parts.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
            .joined(separator: " ")
    }

    /// Keep the last `n` words — the question is at the end of what was said.
    static func lastWords(_ text: String, _ n: Int) -> String {
        let words = text.split(whereSeparator: \.isWhitespace)
        guard n > 0 else { return "" }
        return words.suffix(n).joined(separator: " ")
    }
}

extension Config.Interview {
    /// Clamp ranges from SPEC-11 §Config.
    public var clampedSendSentences: Int { min(20, max(1, sendSentences)) }
    public var clampedMaxWords: Int { min(2000, max(50, maxWords)) }

    public var askMode: AskSelection.Mode {
        switch sendMode {
        case .sinceLastAsk: return .sinceLastAsk
        case .lastSentences: return .lastSentences(clampedSendSentences)
        }
    }
}
