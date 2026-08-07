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
}
