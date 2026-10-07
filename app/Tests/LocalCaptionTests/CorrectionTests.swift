import XCTest
@testable import LocalCaption
import LocalCaptionKit

/// Accent mode's final pass (SPEC-18) against a fake Codex.
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
