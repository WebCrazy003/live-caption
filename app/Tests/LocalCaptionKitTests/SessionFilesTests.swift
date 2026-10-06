import XCTest
@testable import LocalCaptionKit

final class SessionFilesTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lc-files-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: dir) }

    func testDeletesTxtAndJsonSidecar() throws {
        let txt = dir.appendingPathComponent("s.txt")
        let json = dir.appendingPathComponent("s.json")
        try "t".write(to: txt, atomically: true, encoding: .utf8)
        try "{}".write(to: json, atomically: true, encoding: .utf8)

        let removed = SessionFiles.deleteTranscript(atTxtPath: txt.path)
        XCTAssertEqual(removed.count, 2)
        XCTAssertFalse(FileManager.default.fileExists(atPath: txt.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: json.path))
    }

    func testMissingSidecarIsFine() throws {
        let txt = dir.appendingPathComponent("s.txt")
        try "t".write(to: txt, atomically: true, encoding: .utf8)
        let removed = SessionFiles.deleteTranscript(atTxtPath: txt.path)
        XCTAssertEqual(removed.count, 1)
        XCTAssertFalse(FileManager.default.fileExists(atPath: txt.path))
    }
}

final class RecordingFilesTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lc-rec-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: dir) }

    func testRecordingURLIsNamedBySession() {
        let id = UUID()
        let url = SessionFiles.recordingURL(sessionId: id, directory: dir)
        XCTAssertEqual(url.lastPathComponent, "\(id.uuidString).m4a")
    }

    func testPlaceRecordingMovesBesideTranscriptAndAvoidsCollisions() throws {
        let folder = dir.appendingPathComponent("out", isDirectory: true)
        let first = dir.appendingPathComponent("a.m4a")
        let second = dir.appendingPathComponent("b.m4a")
        try Data("1".utf8).write(to: first)
        try Data("2".utf8).write(to: second)

        let placed1 = try SessionFiles.placeRecording(first, folder: folder, base: "2026-10-06_10-00-00")
        let placed2 = try SessionFiles.placeRecording(second, folder: folder, base: "2026-10-06_10-00-00")
        XCTAssertEqual(placed1.lastPathComponent, "2026-10-06_10-00-00.m4a")
        XCTAssertEqual(placed2.lastPathComponent, "2026-10-06_10-00-00 (2).m4a")
        XCTAssertFalse(FileManager.default.fileExists(atPath: first.path))
        XCTAssertEqual(try Data(contentsOf: placed2), Data("2".utf8))
    }

    func testDeleteAudio() throws {
        let url = dir.appendingPathComponent("s.m4a")
        try Data("x".utf8).write(to: url)
        XCTAssertTrue(SessionFiles.deleteAudio(atPath: url.path))
        XCTAssertFalse(SessionFiles.deleteAudio(atPath: url.path))
    }

    func testTranscriptBaseSkipsAnExistingRecording() throws {
        try Data("x".utf8).write(to: dir.appendingPathComponent("stamp.m4a"))
        XCTAssertEqual(TranscriptWriter.resolveBase(folder: dir, stamp: "stamp"), "stamp (2)")
    }
}
