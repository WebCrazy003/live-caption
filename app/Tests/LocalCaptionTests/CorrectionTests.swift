import XCTest
@testable import LocalCaption
import LocalCaptionKit

/// Accent mode's live corrector and final pass (SPEC-18) against a fake Codex.
@MainActor
final class CorrectionTests: XCTestCase {
    typealias Fake = InterviewFlowTests.FakeEngine

    /// Echo every `N\tP: text` line back as `N\t<fix(text)>`.
    private func echo(_ fix: @escaping (String) -> String = { $0 }) -> (String) -> [AnswerEvent] {
        { text in
            let lines = text.split(separator: "\n").compactMap { line -> String? in
                let parts = line.split(separator: "\t", maxSplits: 1)
                guard parts.count == 2, parts[1].hasPrefix("P: ") else { return nil }
                return "\(parts[0])\t\(fix(String(parts[1].dropFirst(3))))"
            }
            return [.completed(lines.joined(separator: "\n"))]
        }
    }

    private func options(twoSources: Bool = false, wait: TimeInterval = 2) -> LiveCorrector.Options {
        var o = LiveCorrector.Options(model: "gpt-6-luna", effort: "low", vocabulary: "Paystack, Claude",
                                      twoSources: twoSources)
        o.secondaryWait = wait
        return o
    }

    private func waitUntil(_ cond: @escaping () -> Bool, timeout: TimeInterval = 3) async {
        let end = Date().addingTimeInterval(timeout)
        while !cond(), Date() < end { try? await Task.sleep(nanoseconds: 20_000_000) }
    }

    func testCorrectsInOrderAndRejectsInvention() async {
        let fake = Fake()
        fake.reply = echo { $0 == "pay stack now" ? "Paystack now" : "We should ship payments on Friday" }
        let c = LiveCorrector(engine: fake, options: options())
        var results: [(Int, String?)] = []
        c.onResult = { results.append(($0, $1)) }
        c.add(number: 1, startMs: 0, primary: "pay stack now")
        await waitUntil { results.count == 1 }
        c.add(number: 2, startMs: 1000, primary: "the cloud thing")
        await waitUntil { results.count == 2 }
        XCTAssertEqual(results.map(\.0), [0, 1000])
        XCTAssertEqual(results[0].1, "Paystack now")
        XCTAssertNil(results[1].1, "a line with words neither source has is kept raw")
        XCTAssertEqual(fake.threads.count, 1)
        XCTAssertTrue(fake.threads[0].baseInstructions.contains("Paystack, Claude"))
        XCTAssertEqual(fake.sent.map(\.effort), ["low", "low"])
        await c.finish()
        XCTAssertEqual(fake.archived, ["thr1"])
    }

    func testWaitsForSecondaryThenSendsBoth() async {
        let fake = Fake()
        fake.reply = echo()
        let c = LiveCorrector(engine: fake, options: options(twoSources: true, wait: 5))
        var results: [Int] = []
        c.onResult = { s, _ in results.append(s) }
        c.add(number: 1, startMs: 0, primary: "I don't use C cloud")
        try? await Task.sleep(nanoseconds: 300_000_000)
        XCTAssertTrue(fake.sent.isEmpty, "waits for the secondary text")
        c.setSecondary(startMs: 0, text: "I don't use Cowork")
        await waitUntil { results.count == 1 }
        guard case .text(let t)? = fake.sent.first?.input.first else { return XCTFail("nothing sent") }
        XCTAssertTrue(t.contains("1\tW: I don't use Cowork"))
    }

    func testSecondaryWaitIsBounded() async {
        let fake = Fake()
        fake.reply = echo()
        let c = LiveCorrector(engine: fake, options: options(twoSources: true, wait: 0.3))
        var results: [Int] = []
        c.onResult = { s, _ in results.append(s) }
        c.add(number: 1, startMs: 0, primary: "hello there")
        await waitUntil { results.count == 1 }
        XCTAssertEqual(results, [0])
        await c.finish()
    }

    func testDrainAbandonsTurnStillWithCodexAndIgnoresItsLateReply() async {
        let fake = Fake()
        fake.holdIf = { _ in true }
        let c = LiveCorrector(engine: fake, options: options())
        var results: [(Int, String?)] = []
        c.onResult = { results.append(($0, $1)) }
        c.add(number: 1, startMs: 0, primary: "pay stack")
        await waitUntil { fake.sent.count == 1 }
        c.add(number: 2, startMs: 1000, primary: "next")
        await c.drain(timeout: 0.3)
        XCTAssertEqual(results.map(\.0), [0, 1000])
        XCTAssertTrue(results.allSatisfy { $0.1 == nil }, "settled raw")
        fake.release(with: "1\tPaystack")
        try? await Task.sleep(nanoseconds: 200_000_000)
        XCTAssertEqual(results.count, 2, "the late reply is ignored")
    }

    func testSignedOutStopsCorrectionAndSettlesRaw() async {
        let fake = Fake()
        fake.failStart = .signedOut
        let c = LiveCorrector(engine: fake, options: options())
        var results: [(Int, String?)] = []
        var stopped: String?
        c.onResult = { results.append(($0, $1)) }
        c.onStopped = { stopped = $0 }
        c.add(number: 1, startMs: 0, primary: "one")
        await waitUntil { stopped != nil }
        XCTAssertNotNil(stopped)
        c.add(number: 2, startMs: 1000, primary: "two")
        XCTAssertEqual(results.map(\.0), [0, 1000])
        XCTAssertTrue(results.allSatisfy { $0.1 == nil })
    }

    func testFinalPassBlocksAndKeys() async throws {
        let fake = Fake()
        fake.reply = echo { $0.replacingOccurrences(of: "pay stack", with: "Paystack") }
        let minute = 60_000
        let segments = (0..<12).map { i in
            TranscriptSegment(text: "line \(i) pay stack", tStartMs: i * 2 * minute, tEndMs: i * 2 * minute + 1000,
                              createdAt: "", altText: "line \(i) Paystack")
        }
        let out = try await FinalPass.run(segments: segments, engine: fake, model: "gpt-6.1-sol", effort: "high",
                                          vocabulary: "Paystack")
        XCTAssertEqual(fake.sent.count, 3, "24 minutes in 10-minute blocks")
        XCTAssertEqual(fake.sent.map(\.effort), ["high", "high", "high"])
        XCTAssertEqual(fake.threads.map(\.model), ["gpt-6.1-sol"])
        XCTAssertEqual(out.count, 12)
        XCTAssertEqual(out[2 * minute], "line 1 Paystack")
        guard case .text(let second)? = fake.sent[1].input.first else { return XCTFail() }
        XCTAssertTrue(second.contains("Already corrected"), "later blocks carry the previous block's tail")
    }
}
