import Foundation
import OSLog
import LocalCaptionKit

/// Accent mode's live correction (SPEC-18 §Live correction): one Codex thread per session.
/// Finals go in as they are captioned; whenever lines are waiting and no turn is in flight, all
/// of them go to Codex as one turn. Each line comes back accepted or rejected, in order.
@MainActor
final class LiveCorrector {
    struct Options: Equatable {
        var model: String
        var effort: String
        var vocabulary: String
        var twoSources: Bool
        /// A line waits this long for its secondary text before going without it.
        var secondaryWait: TimeInterval = 2
        /// Fresh thread every N turns, seeded with the last corrected lines (bounds context growth).
        var resetEvery = 40
    }

    /// (segment start ms, accepted text or nil for "keep the raw caption").
    var onResult: ((Int, String?) -> Void)?
    /// Correction stopped for the rest of the session (signed out, usage limit…).
    var onStopped: ((String) -> Void)?

    private struct Pending {
        let line: CorrectionPrompt.Line
        let startMs: Int
        let addedAt: Date
    }

    private let engine: AnswerEngine
    private let options: Options
    private let instructions: String
    private let log = Logger(subsystem: "com.livecaption.app", category: "correction")
    private var queue: [Pending] = []
    private var corrected: [(number: Int, text: String)] = []
    private var threadId: String?
    private var turns = 0
    private var inFlight = false
    /// The batch with Codex now, and its generation: a reply for an abandoned batch is ignored.
    private var inFlightBatch: [Pending] = []
    private var generation = 0
    private var stopped = false
    private var tick: Task<Void, Never>?

    init(engine: AnswerEngine, options: Options) {
        self.engine = engine
        self.options = options
        instructions = CorrectionPrompt.instructions(twoSources: options.twoSources, vocabulary: options.vocabulary)
    }

    var isIdle: Bool { queue.isEmpty && !inFlight }

    func add(number: Int, startMs: Int, primary: String) {
        guard !stopped else { onResult?(startMs, nil); return }
        queue.append(Pending(line: .init(number: number, primary: primary, secondary: nil), startMs: startMs, addedAt: Date()))
        pump()
        startTicking()
    }

    func setSecondary(startMs: Int, text: String) {
        guard let i = queue.firstIndex(where: { $0.startMs == startMs }) else { return }
        let p = queue[i]
        queue[i] = Pending(line: .init(number: p.line.number, primary: p.line.primary, secondary: text),
                           startMs: p.startMs, addedAt: p.addedAt)
        pump()
    }

    /// Wait for queued lines to be corrected (Pause/Stop), up to `timeout`; whatever is left is
    /// settled raw.
    func drain(timeout: TimeInterval) async {
        let deadline = Date().addingTimeInterval(timeout)
        while !isIdle, Date() < deadline {
            pump(force: true)
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        if inFlight {                       // abandon the turn still with Codex
            generation += 1
            inFlight = false
            settleRaw(inFlightBatch); inFlightBatch = []
        }
        settleRaw(queue); queue.removeAll()
    }

    func finish() async {
        tick?.cancel(); tick = nil
        stopped = true
        if let threadId { await engine.archiveThread(id: threadId) }
        threadId = nil
    }

    // MARK: turns

    private func eligible(_ p: Pending, force: Bool) -> Bool {
        force || !options.twoSources || p.line.secondary != nil
            || Date().timeIntervalSince(p.addedAt) >= options.secondaryWait
    }

    /// Send the waiting prefix of the queue, if nothing is in flight.
    private func pump(force: Bool = false) {
        guard !inFlight, !stopped else { return }
        let batch = Array(queue.prefix { eligible($0, force: force) })
        guard !batch.isEmpty else { return }
        queue.removeFirst(batch.count)
        inFlight = true
        inFlightBatch = batch
        generation += 1
        let gen = generation
        Task { await self.run(batch, generation: gen) }
    }

    private func startTicking() {
        guard tick == nil else { return }
        tick = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 250_000_000)
                guard let self, !self.stopped else { return }
                self.pump()
            }
        }
    }

    private func run(_ batch: [Pending], generation gen: Int) async {
        defer {
            if gen == generation { inFlight = false; inFlightBatch = [] }
            pump()
        }
        do {
            var context: [(number: Int, text: String)] = []
            if threadId == nil || (turns > 0 && turns % options.resetEvery == 0) {
                if let old = threadId { await engine.archiveThread(id: old) }
                threadId = try await engine.startThread(ThreadConfig(model: options.model, baseInstructions: instructions))
                context = Array(corrected.suffix(15))
            }
            guard let threadId else { return }
            let text = CorrectionPrompt.turn(batch.map(\.line), context: context)
            let reply = try await Self.complete(engine.send(threadId: threadId, input: [.text(text)],
                                                            effort: options.effort, model: nil))
            turns += 1
            guard gen == generation else { return }     // drained while Codex answered
            let got = CorrectionPrompt.parse(reply)
            var rejected = 0
            for p in batch {
                var accepted: String?
                if let line = got[p.line.number],
                   case .accept(let t) = CorrectionPrompt.check(line, line: p.line, vocabulary: options.vocabulary) {
                    accepted = t
                    corrected.append((p.line.number, t))
                } else { rejected += 1 }
                onResult?(p.startMs, accepted)
            }
            if rejected > 0 { log.info("correction kept \(rejected) of \(batch.count) raw") }
        } catch let error as EngineError {
            guard gen == generation else { return }
            settleRaw(batch)
            switch error {
            case .notInstalled, .tooOld, .signedOut, .usageLimit:
                stopped = true
                tick?.cancel(); tick = nil
                settleRaw(queue); queue.removeAll()
                onStopped?(error.localizedDescription)
            default:
                log.error("correction turn failed: \(error.localizedDescription, privacy: .public)")
                threadId = nil         // start clean on the next turn
            }
        } catch {
            guard gen == generation else { return }
            settleRaw(batch)
            threadId = nil
        }
    }

    private func settleRaw(_ items: [Pending]) {
        for p in items { onResult?(p.startMs, nil) }
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
            let reply = try await LiveCorrector.complete(engine.send(threadId: thread, input: [.text(text)],
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
}
