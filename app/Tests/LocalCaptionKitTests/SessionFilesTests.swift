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
