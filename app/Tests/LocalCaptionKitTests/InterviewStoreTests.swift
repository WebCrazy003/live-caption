import XCTest
@testable import LocalCaptionKit

/// The interview tables (owner, 2026-10-02: all interview session data in a local database).
final class InterviewStoreTests: XCTestCase {
    private var dir: URL!
    private var store: Store!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("lc-istore-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        store = try Store(url: dir.appendingPathComponent("db.sqlite"))
    }

    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: dir) }

    private func sample(id: String = UUID().uuidString) -> InterviewRecord {
        var r = InterviewRecord(id: id, name: "Senior iOS", createdAt: "2026-10-02T09:00:00Z", model: "gpt-6-luna",
                                reasoningEffort: "low",
                                setup: .init(skillIds: ["s1"], documentIds: ["cv1"], jdTextInline: "JD text",
                                             instructions: "Be brief.", answerLength: "short"))
        r.setup.cvTitle = "Jane CV"
        r.cvText = "Jane Doe\nSwift."
        r.threadId = "thr1"
        r.captureSessionUUID = "cap-1"
        r.turns = [
            .init(n: 1, kind: .skill, question: "/discovery-cv", answer: "Profile…", status: .completed, askedAt: "t1"),
            .init(n: 2, kind: .ask, question: "why us", audioFromMs: 0, audioToMs: 5000, images: ["2-1.png", "2-2.png"],
                  answer: "Because…", status: .completed, askedAt: "t2", ttftMs: 1300, totalMs: 3600),
        ]
        return r
    }

    func testRoundTripKeepsEverything() throws {
        var r = sample()
        try store.saveInterview(r)
        XCTAssertEqual(try store.interview(id: r.id), r)

        r.turns[1].answer = "Edited"
        r.turns.append(.init(n: 3, kind: .typed, question: "shorter", status: .streaming, askedAt: "t3"))
        r.summaryText = "## Overview\nGood."
        r.summary = .init(status: .done, completedAt: "t4")
        r.transcript = "Thanks for joining. Why us?"
        r.endedAt = "t5"
        try store.saveInterview(r)
        XCTAssertEqual(try store.interview(id: r.id), r, "an update replaces the turns and keeps the rest")
        XCTAssertEqual(try store.allInterviews().map(\.id), [r.id])
    }

    func testScreenshotsAreStoredAsBytes() throws {
        let r = sample()
        try store.saveInterview(r)
        let png = Data([0x89, 0x50, 0x4E, 0x47, 1, 2, 3])
        try store.addInterviewImage(interviewId: r.id, name: "2-1.png", turn: 2, png: png)
        XCTAssertEqual(try store.interviewImage(interviewId: r.id, name: "2-1.png"), png)
        XCTAssertNil(try store.interviewImage(interviewId: r.id, name: "missing.png"))
        try store.saveInterview(r)      // saving the record again leaves screenshots alone
        XCTAssertEqual(try store.interviewImageCount(interviewId: r.id), 1)
    }

    func testDeleteRemovesTurnsAndScreenshots() throws {
        let r = sample()
        try store.saveInterview(r)
        try store.addInterviewImage(interviewId: r.id, name: "2-1.png", turn: 2, png: Data([1]))
        try store.deleteInterview(id: r.id)
        XCTAssertNil(try store.interview(id: r.id))
        XCTAssertEqual(try store.interviewImageCount(interviewId: r.id), 0)
    }

    func testSessionLinkAndMode() throws {
        let session = try store.insert(SessionRecord(sessionName: "Interview 1", createdAt: "2026-10-02T09:00:00Z"))
        var r = sample()
        r.sessionId = session.id
        try store.saveInterview(r)
        try store.markInterview(sessionId: XCTUnwrap(session.id))
        XCTAssertEqual(try store.interview(sessionId: XCTUnwrap(session.id))?.id, r.id)
        XCTAssertEqual(try store.fetch(id: XCTUnwrap(session.id))?.mode, "interview")
        try store.delete(id: XCTUnwrap(session.id))
        XCTAssertNil(try store.interview(id: r.id)?.sessionId, "deleting the session keeps the interview, unlinked")
    }

    func testLegacyFolderIsImportedOnce() throws {
        var r = sample()
        r.turns[1].images = ["attachments/2-1.png"]
        r.cvText = nil
        let folder = try InterviewFiles.makeFolder(in: dir.appendingPathComponent("interviews"), date: Date(), name: r.name)
        try InterviewFiles.write(r, to: folder)
        try Data("Summary text".utf8).write(to: folder.appendingPathComponent("summary.md"))
        try Data("CV snapshot".utf8).write(to: folder.appendingPathComponent("cv.txt"))
        try Data([7, 7, 7]).write(to: folder.appendingPathComponent("attachments/2-1.png"))

        XCTAssertTrue(try store.importLegacyInterview(folder: folder))
        let imported = try XCTUnwrap(store.interview(id: r.id))
        XCTAssertEqual(imported.summaryText, "Summary text")
        XCTAssertEqual(imported.cvText, "CV snapshot")
        XCTAssertEqual(imported.turns[1].images, ["2-1.png"])
        XCTAssertEqual(try store.interviewImage(interviewId: r.id, name: "2-1.png"), Data([7, 7, 7]))
        XCTAssertFalse(try store.importLegacyInterview(folder: folder), "already imported")
        XCTAssertTrue(FileManager.default.fileExists(atPath: folder.path), "the folder is left alone")
    }
}
