import XCTest
@testable import LocalCaptionKit

final class JournalTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lc-journal-\(UUID().uuidString)", isDirectory: true)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: dir) }

    private func seg(_ text: String, _ end: Int) -> TranscriptSegment {
        TranscriptSegment(text: text, tStartMs: end - 1000, tEndMs: end, createdAt: "2026-08-07T10:00:00Z")
    }

    func testAppendThenRecover() throws {
        let id = UUID()
        let j = try Journal(sessionId: id, directory: dir)
        try j.append(seg("first", 2000))
        try j.append(seg("second", 4000))

        // Simulate a crash: journal file still present, not deleted.
        let pending = Journal.pending(in: dir)
        XCTAssertEqual(pending.count, 1)
        XCTAssertEqual(pending.first?.sessionId, id)
        XCTAssertEqual(pending.first?.segments.map(\.text), ["first", "second"])
    }

    func testCleanStopDeletesJournal() throws {
        let j = try Journal(sessionId: UUID(), directory: dir)
        try j.append(seg("x", 1000))
        j.deleteFile()
        XCTAssertEqual(Journal.pending(in: dir).count, 0)
    }

    func testRecoverSkipsMalformedLines() throws {
        let id = UUID()
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let url = dir.appendingPathComponent("\(id.uuidString).jsonl")
        let good = try JSONEncoder().encode(seg("ok", 1000))
        var blob = good; blob.append(0x0A)
        blob.append(contentsOf: Array("{ not json\n".utf8))
        try blob.write(to: url)

        let pending = Journal.pending(in: dir)
        XCTAssertEqual(pending.first?.segments.map(\.text), ["ok"])
    }
}
