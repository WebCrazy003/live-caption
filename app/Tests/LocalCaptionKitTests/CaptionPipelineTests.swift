import XCTest
@testable import LocalCaptionKit

private actor DecodeGate {
    private var pending: [(SpeechRequest, CheckedContinuation<SpeechOutcome, Never>)] = []
    private var arrivals: [SpeechRequest] = []
    private var observer: CheckedContinuation<SpeechRequest, Never>?
    func decode(_ request: SpeechRequest) async -> SpeechOutcome {
        await withCheckedContinuation { continuation in
            pending.append((request, continuation))
            if let observer { self.observer = nil; observer.resume(returning: request) }
            else { arrivals.append(request) }
        }
    }
    func next() async -> SpeechRequest {
        if !arrivals.isEmpty { return arrivals.removeFirst() }
        return await withCheckedContinuation { observer = $0 }
    }
    func complete(_ result: SpeechOutcome) { pending.removeFirst().1.resume(returning: result) }
    var count: Int { pending.count }
}

private final class TestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Double = 0
    func now() -> Double { lock.lock(); defer { lock.unlock() }; return value }
    func advance(_ seconds: Double) { lock.lock(); value += seconds; lock.unlock() }
}

@MainActor
final class CaptionPipelineTests: XCTestCase {
    private func request(_ session: UUID, _ id: Int, start: Int = 0, samples: Int = 16000,
                         final: Bool = false, now: Double = 0) -> SpeechRequest {
        SpeechRequest(session: session, utterance: id, startSample: start,
                      audio: Array(repeating: 0.1, count: samples), isFinal: final, submittedAt: now)
    }
    private func result(_ text: String) -> SpeechOutcome {
        .success(text: text, words: [CaptionWord(text, start: 0, end: 0.5)], fallbacks: 0)
    }

    func testFiveSecondFinalDoesNotBlockNewSpeechOrClearIt() async {
        let session = UUID(), clock = TestClock(), final = DecodeGate()
        let pipeline = CaptionPipeline(session: session, now: { clock.now() },
            interim: { _ in .success(text: "new speech", words: [CaptionWord("new speech", start: 0, end: 0.5)], fallbacks: 0) },
            final: { await final.decode($0) })
        var committed: [String] = []
        var finalizedPending: [String] = []
        pipeline.onFinal = { text, _, _ in committed.append(text) }
        pipeline.onFinalized = { finalizedPending.append($0) }
        pipeline.submit(request(session, 0, final: true))
        _ = await final.next()
        clock.advance(5)
        let updated = expectation(description: "interim before final finishes")
        pipeline.onHypothesis = { text in if text == "new speech" { updated.fulfill() } }
        pipeline.submit(request(session, 1, start: 16000, now: 5))
        await fulfillment(of: [updated], timeout: 1)
        pipeline.onHypothesis = nil
        XCTAssertTrue(committed.isEmpty)
        await final.complete(result("old final"))
        await pipeline.finish()
        XCTAssertEqual(committed, ["old final"])
        XCTAssertEqual(pipeline.hypothesis, "new speech")
        XCTAssertEqual(finalizedPending, ["new speech"])
        XCTAssertEqual(pipeline.metrics.first(where: \.isFinal)?.decodeMs, 5000)
    }

    func testPendingSnapshotsCoalesceWithoutStarvingInFlightResult() async {
        let session = UUID(), gate = DecodeGate()
        let pipeline = CaptionPipeline(session: session, interim: { await gate.decode($0) }, final: { _ in .empty })
        pipeline.submit(request(session, 0))
        _ = await gate.next()
        pipeline.submit(request(session, 0, samples: 24000))
        pipeline.submit(request(session, 0, samples: 32000))
        XCTAssertEqual(pipeline.pendingInterim?.endSample, 32000)
        await gate.complete(result("first"))
        let next = await gate.next()
        XCTAssertEqual(pipeline.hypothesis, "first")
        XCTAssertEqual(next.endSample, 32000)
        let done = expectation(description: "latest published")
        pipeline.onHypothesis = { text in if text == "newest" { done.fulfill() } }
        await gate.complete(result("newest"))
        await fulfillment(of: [done], timeout: 1)
        await pipeline.finish()
    }

    func testCompletedUtteranceRejectsLateInterimAndWrongGeneration() async {
        let session = UUID(), interim = DecodeGate(), final = DecodeGate()
        let pipeline = CaptionPipeline(session: session, interim: { await interim.decode($0) }, final: { await final.decode($0) })
        pipeline.submit(request(UUID(), 99))
        XCTAssertNil(pipeline.pendingInterim)
        pipeline.submit(request(session, 0))
        _ = await interim.next()
        pipeline.submit(request(session, 0, final: true))
        _ = await final.next()
        let committed = expectation(description: "final retired")
        pipeline.onHypothesis = { _ in committed.fulfill() }
        await final.complete(result("final"))
        await fulfillment(of: [committed], timeout: 1)
        pipeline.onHypothesis = nil
        await interim.complete(result("late provisional"))
        await pipeline.finish()
        XCTAssertEqual(pipeline.hypothesis, "")
    }

    func testFinalQueuePreservesOrderAndSignalsOverloadOnce() async {
        let session = UUID(), gate = DecodeGate()
        let pipeline = CaptionPipeline(session: session, backlogSeconds: 2, backlogCount: 16,
            interim: { _ in .empty }, final: { await gate.decode($0) })
        var overloads = 0, finals: [Int] = []
        pipeline.onOverload = { overloads += 1 }
        pipeline.onFinal = { _, start, _ in finals.append(start) }
        for id in 0..<3 { pipeline.submit(request(session, id, start: id * 16000, final: true)) }
        XCTAssertEqual(overloads, 1)
        for id in 0..<3 {
            let next = await gate.next()
            XCTAssertEqual(next.utterance, id)
            await gate.complete(result("final \(id)"))
        }
        await pipeline.finish()
        XCTAssertEqual(finals, [0, 1000, 2000])
        XCTAssertTrue(pipeline.finalQueue.isEmpty)
    }

    func testEmptyInterimPreservesTextAndFailedFinalReportsGap() async {
        let session = UUID(), gate = DecodeGate()
        let pipeline = CaptionPipeline(session: session, interim: { await gate.decode($0) }, final: { _ in .failure("test") })
        pipeline.submit(request(session, 0))
        _ = await gate.next()
        let visible = expectation(description: "visible")
        pipeline.onHypothesis = { _ in visible.fulfill() }
        await gate.complete(result("provisional"))
        await fulfillment(of: [visible], timeout: 1)
        pipeline.onHypothesis = nil
        pipeline.submit(request(session, 0, samples: 24000))
        _ = await gate.next()
        let filtered = expectation(description: "filtered interim completed")
        pipeline.onMetric = { metric in if metric.outcome == "filtered" { filtered.fulfill() } }
        await gate.complete(.filtered)
        await fulfillment(of: [filtered], timeout: 1)
        XCTAssertEqual(pipeline.hypothesis, "provisional")
        var saved = false, issues: [String] = []
        pipeline.onFinal = { _, _, _ in saved = true }
        pipeline.onIssue = { issues.append($0) }
        pipeline.submit(request(session, 0, samples: 24000, final: true))
        await pipeline.finish()
        XCTAssertFalse(saved)
        XCTAssertTrue(issues.contains { $0.contains("missing") })
        XCTAssertEqual(pipeline.hypothesis, "")
    }

    func testSlowInterimDoesNotOverlapReplacementDecode() async {
        let session = UUID(), gate = DecodeGate()
        let pipeline = CaptionPipeline(session: session, interimBudget: 0.02,
            interim: { await gate.decode($0) }, final: { _ in .empty })
        let warned = expectation(description: "cooperative deadline")
        pipeline.onCatchingUp = { behind in if behind { warned.fulfill() } }
        pipeline.submit(request(session, 0))
        _ = await gate.next()
        pipeline.submit(request(session, 0, samples: 24000))
        await fulfillment(of: [warned], timeout: 1)
        let active = await gate.count
        XCTAssertEqual(active, 1, "Cancellation must not start a second call on the same model")
        pipeline.onCatchingUp = nil
        await gate.complete(result("expired"))
        _ = await gate.next()
        XCTAssertEqual(pipeline.hypothesis, "")
        await gate.complete(result("fresh"))
        await pipeline.finish()
        XCTAssertTrue(pipeline.metrics.contains { $0.outcome == "timeout" })
    }

    func testFinishAwaitsDurableFinalAcknowledgment() async {
        let session = UUID(), journalGate = DecodeGate()
        let pipeline = CaptionPipeline(session: session, interim: { _ in .empty },
            final: { _ in .success(text: "saved", words: [], fallbacks: 0) })
        pipeline.onFinal = { _, _, _ in _ = await journalGate.decode(self.request(session, 0)) }
        pipeline.submit(request(session, 0, final: true))
        _ = await journalGate.next()
        var finished = false
        let stop = Task { await pipeline.finish(); finished = true }
        await Task.yield()
        XCTAssertFalse(finished)
        await journalGate.complete(.empty)
        await stop.value
        XCTAssertTrue(finished)
    }

    func testResultsTooFarBehindLiveAudioAreNotDisplayed() async {
        let session = UUID(), gate = DecodeGate()
        let pipeline = CaptionPipeline(session: session, interim: { await gate.decode($0) }, final: { _ in .empty })
        pipeline.submit(request(session, 0))
        _ = await gate.next()
        pipeline.advanceAudio(to: 5 * 16000)
        let decoded = expectation(description: "stale result completed")
        pipeline.onMetric = { _ in decoded.fulfill() }
        await gate.complete(result("old speech"))
        await fulfillment(of: [decoded], timeout: 1)
        XCTAssertEqual(pipeline.hypothesis, "")
        XCTAssertEqual(pipeline.metrics.last?.audioLagMs, 4000)
        await pipeline.finish()
    }
}
