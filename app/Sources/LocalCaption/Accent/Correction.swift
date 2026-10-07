import Foundation
import LocalCaptionKit

/// Accent mode's final pass (SPEC-18 §Final pass): after Stop, the whole session from both raw
/// sources, in blocks, with the final model. Returns each segment's final text by start time.
enum FinalPass {
    /// Blocks of this much audio per turn; each turn sees the previous block's corrected tail.
    static let blockMs = 10 * 60 * 1000

    static func run(segments: [TranscriptSegment], engine: AnswerEngine, model: String, effort: String,
                    vocabulary: String) async throws -> [Int: String] {
        let twoSources = segments.contains { $0.altText != nil }
        let instructions = CorrectionPrompt.instructions(twoSources: twoSources, vocabulary: vocabulary)
        let thread = try await engine.startThread(ThreadConfig(model: model, baseInstructions: instructions))
        defer { Task { await engine.archiveThread(id: thread) } }

        let lines = segments.enumerated().map { i, s in
            (CorrectionPrompt.Line(number: i + 1, primary: s.text, secondary: s.altText), s.tStartMs)
        }
        var out: [Int: String] = [:]
        var previous: [(number: Int, text: String)] = []
        var start = 0
        while start < lines.count {
            let blockEnd = lines[start].1 + blockMs
            var end = start
            while end < lines.count, lines[end].1 < blockEnd { end += 1 }
            let block = Array(lines[start..<end])
            let text = CorrectionPrompt.turn(block.map(\.0), context: previous)
            let reply = try await complete(engine.send(threadId: thread, input: [.text(text)],
                                                                     effort: effort, model: nil))
            let got = CorrectionPrompt.parse(reply)
            previous = []
            for (line, startMs) in block {
                guard let candidate = got[line.number],
                      case .accept(let t) = CorrectionPrompt.check(candidate, line: line, vocabulary: vocabulary)
                else { continue }
                out[startMs] = t
                previous.append((line.number, t))
            }
            previous = Array(previous.suffix(15))
            start = end
        }
        return out
    }

    /// The final text of one turn.
    static func complete(_ stream: AsyncThrowingStream<AnswerEvent, Error>) async throws -> String {
        var partial = ""
        for try await event in stream {
            switch event {
            case .delta(let d): partial += d
            case .completed(let text): return text
            case .interrupted: throw EngineError.other("interrupted")
            case .failed(let error, _): throw error
            case .started, .thinking, .slow: break
            }
        }
        if partial.isEmpty { throw EngineError.other("no reply") }
        return partial
    }
}
