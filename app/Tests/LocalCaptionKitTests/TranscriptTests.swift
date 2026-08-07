import XCTest
@testable import LocalCaptionKit

final class TranscriptTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lc-txt-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: dir) }

    private func seg(_ text: String, _ start: Int, _ end: Int) -> TranscriptSegment {
        TranscriptSegment(text: text, tStartMs: start, tEndMs: end, createdAt: "2026-08-07T10:00:00Z")
    }

    private var start: Date { Date(timeIntervalSince1970: 1_754_560_338) } // fixed instant

    func testFileTextHasHeaderAndBody() {
        var t = Transcript()
        t.append(seg("Thanks for joining today.", 3000, 5000))
        t.append(seg("Happy to be here.", 7000, 8000))
        let out = t.fileText(sessionName: "Interview A", start: start,
                             end: start.addingTimeInterval(1904), durationSeconds: 1904,
                             showTimestamps: false)
        XCTAssertTrue(out.contains("Session: Interview A"))
        XCTAssertTrue(out.contains("Duration: 00:31:44"))
        XCTAssertTrue(out.contains("Thanks for joining today.\nHappy to be here."))
        XCTAssertFalse(out.contains("[00:00:03]"))
    }

    func testTimestampsWhenEnabled() {
        var t = Transcript()
        t.append(seg("Hello there.", 3000, 5000))
        let out = t.body(showTimestamps: true)
        XCTAssertEqual(out, "[00:00:03] Hello there.")
    }

    func testSaveWritesTxtAndJsonSidecar() throws {
        var t = Transcript()
        t.append(seg("One.", 1000, 2000))
        let r = try TranscriptWriter.save(transcript: t, folder: dir, sessionName: "S",
                                          start: start, end: start.addingTimeInterval(60),
                                          durationSeconds: 60, showTimestamps: false)
        XCTAssertTrue(FileManager.default.fileExists(atPath: r.txtURL.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: r.jsonURL.path))
        XCTAssertEqual(r.txtURL.pathExtension, "txt")

        // Sidecar round-trips the segments.
        let data = try Data(contentsOf: r.jsonURL)
        let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        XCTAssertEqual(obj?["duration_seconds"] as? Int, 60)
        XCTAssertEqual((obj?["segments"] as? [[String: Any]])?.count, 1)
    }

    func testFilenameCollisionSuffix() throws {
        var t = Transcript(); t.append(seg("x", 0, 1))
        let first = try TranscriptWriter.save(transcript: t, folder: dir, sessionName: "S",
                                              start: start, end: start, durationSeconds: 0, showTimestamps: false)
        let second = try TranscriptWriter.save(transcript: t, folder: dir, sessionName: "S",
                                               start: start, end: start, durationSeconds: 0, showTimestamps: false)
        XCTAssertNotEqual(first.txtURL, second.txtURL)
        XCTAssertTrue(second.txtURL.lastPathComponent.contains(" (2)"))
    }
}
