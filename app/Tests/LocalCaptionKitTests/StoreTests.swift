import XCTest
@testable import LocalCaptionKit

final class StoreTests: XCTestCase {
    private var dbURL: URL!

    override func setUpWithError() throws {
        dbURL = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lc-store-\(UUID().uuidString).db")
    }

    override func tearDownWithError() throws {
        for suffix in ["", "-wal", "-shm"] {
            try? FileManager.default.removeItem(at: URL(fileURLWithPath: dbURL.path + suffix))
        }
    }

    private func makeRecord(_ name: String, created: String, duration: Int = 0) -> SessionRecord {
        SessionRecord(sessionName: name, createdAt: created, endedAt: created,
                      durationSeconds: duration, transcriptFile: "\(name).txt")
    }

    func testInsertAssignsIdAndCounts() throws {
        let store = try Store(url: dbURL)
        XCTAssertEqual(try store.count(), 0)
        let inserted = try store.insert(makeRecord("A", created: "2026-08-07T10:00:00Z"))
        XCTAssertNotNil(inserted.id)
        XCTAssertEqual(try store.count(), 1)
    }

    func testListSortedByCreatedDesc() throws {
        let store = try Store(url: dbURL)
        _ = try store.insert(makeRecord("Old", created: "2026-08-01T09:00:00Z"))
        _ = try store.insert(makeRecord("New", created: "2026-08-07T09:00:00Z"))
        let all = try store.all(sort: .createdDesc)
        XCTAssertEqual(all.map(\.sessionName), ["New", "Old"])
        let asc = try store.all(sort: .createdAsc)
        XCTAssertEqual(asc.map(\.sessionName), ["Old", "New"])
    }

    func testSortByDurationAndName() throws {
        let store = try Store(url: dbURL)
        _ = try store.insert(makeRecord("Bravo", created: "2026-08-01T09:00:00Z", duration: 30))
        _ = try store.insert(makeRecord("Alpha", created: "2026-08-02T09:00:00Z", duration: 120))
        XCTAssertEqual(try store.all(sort: .durationDesc).first?.sessionName, "Alpha")
        XCTAssertEqual(try store.all(sort: .nameAsc).map(\.sessionName), ["Alpha", "Bravo"])
    }

    func testSearchByName() throws {
        let store = try Store(url: dbURL)
        _ = try store.insert(makeRecord("Interview Rajat", created: "2026-08-07T09:00:00Z"))
        _ = try store.insert(makeRecord("Standup", created: "2026-08-07T10:00:00Z"))
        XCTAssertEqual(try store.all(search: "inter").map(\.sessionName), ["Interview Rajat"])
        XCTAssertEqual(try store.all(search: "  ").count, 2, "blank search returns all")
    }

    func testRenameEditsMetadataOnly() throws {
        let store = try Store(url: dbURL)
        let rec = try store.insert(makeRecord("Before", created: "2026-08-07T09:00:00Z"))
        try store.rename(id: rec.id!, to: "After")
        let fetched = try store.fetch(id: rec.id!)
        XCTAssertEqual(fetched?.sessionName, "After")
        XCTAssertEqual(fetched?.transcriptFile, "Before.txt", "rename must not touch the file field")
    }

    func testDelete() throws {
        let store = try Store(url: dbURL)
        let rec = try store.insert(makeRecord("Doomed", created: "2026-08-07T09:00:00Z"))
        try store.delete(id: rec.id!)
        XCTAssertEqual(try store.count(), 0)
        XCTAssertNil(try store.fetch(id: rec.id!))
    }

    func testPersistsAcrossReopen() throws {
        do {
            let store = try Store(url: dbURL)
            _ = try store.insert(makeRecord("Persisted", created: "2026-08-07T09:00:00Z"))
        }
        // Reopen the same file — migrations must be idempotent and data intact.
        let reopened = try Store(url: dbURL)
        XCTAssertEqual(try reopened.count(), 1)
        XCTAssertEqual(try reopened.all().first?.sessionName, "Persisted")
    }

    // MARK: Caption segments (owner, 2026-10-02: everything in the database)

    private let segs = [
        TranscriptSegment(text: "Thanks for joining.", tStartMs: 0, tEndMs: 1200, createdAt: "2026-10-02T09:00:01Z"),
        TranscriptSegment(text: "Tell me about yourself.", tStartMs: 1500, tEndMs: 3000, createdAt: "2026-10-02T09:00:03Z"),
    ]

    private func texts(_ list: [TranscriptSegment]) -> [String] { list.map(\.text) }

    func testSegmentsAreSavedWithTheSessionAndDeletedWithIt() throws {
        let store = try Store(url: dbURL)
        let rec = try store.insert(makeRecord("A", created: "2026-10-02T09:00:00Z"), segments: segs)
        let id = try XCTUnwrap(rec.id)
        let back = try store.segments(sessionId: id)
        XCTAssertEqual(texts(back), texts(segs))
        XCTAssertEqual(back.map(\.tStartMs), [0, 1500])
        XCTAssertEqual(back.map(\.tEndMs), [1200, 3000])
        XCTAssertEqual(back.map(\.createdAt), segs.map(\.createdAt))

        try store.delete(id: id)
        XCTAssertEqual(try store.segmentCount(sessionId: id), 0, "segments go with their session")
    }

    func testOldTranscriptFilesAreImportedOnce() throws {
        let dir = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("lc-import-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: dir) }
        let start = Date(timeIntervalSince1970: 1_790_000_000)
        // With a .json sidecar: its exact segments.
        let a = try TranscriptWriter.save(transcript: Transcript(segments: segs), folder: dir, sessionName: "A",
                                          start: start, end: start, durationSeconds: 3, showTimestamps: false)
        // .txt only: one segment per body line, [HH:MM:SS] read as the start.
        let b = dir.appendingPathComponent("b.txt")
        try "Session: B\nStart: x\n\n[00:00:03] Hello there.\n[00:01:05] Second line.\n".write(to: b, atomically: true, encoding: .utf8)

        let store = try Store(url: dbURL)
        let ra = try store.insert(SessionRecord(sessionName: "A", createdAt: "2026-10-02T09:00:00Z", transcriptFile: a.txtURL.path))
        let rb = try store.insert(SessionRecord(sessionName: "B", createdAt: "2026-10-02T10:00:00Z", transcriptFile: b.path))
        let rc = try store.insert(SessionRecord(sessionName: "C", createdAt: "2026-10-02T11:00:00Z", transcriptFile: "/missing.txt"))

        XCTAssertEqual(try store.importTranscriptFiles(), 2)
        XCTAssertEqual(texts(try store.segments(sessionId: XCTUnwrap(ra.id))), texts(segs))
        let fromTxt = try store.segments(sessionId: XCTUnwrap(rb.id))
        XCTAssertEqual(texts(fromTxt), ["Hello there.", "Second line."])
        XCTAssertEqual(fromTxt.map(\.tStartMs), [3000, 65000])
        XCTAssertEqual(try store.segmentCount(sessionId: XCTUnwrap(rc.id)), 0)
        XCTAssertEqual(try store.importTranscriptFiles(), 0, "already imported")
    }
}
