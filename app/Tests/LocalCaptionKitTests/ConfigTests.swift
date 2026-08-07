import XCTest
@testable import LocalCaptionKit

final class ConfigTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lc-config-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private var configURL: URL { dir.appendingPathComponent("config.json") }

    func testMissingFileWritesDefaults() throws {
        let (cfg, repaired) = Config.loadOrRepair(from: configURL)
        XCTAssertFalse(repaired)
        XCTAssertEqual(cfg, Config())
        XCTAssertTrue(FileManager.default.fileExists(atPath: configURL.path))
    }

    func testRoundTripIdentical() throws {
        var cfg = Config()
        cfg.caption.fontSize = 24
        cfg.window.opacity = 0.5
        cfg.general.sessionNamePrefix = "Call "
        try cfg.write(to: configURL)

        let (reloaded, repaired) = Config.loadOrRepair(from: configURL)
        XCTAssertFalse(repaired)
        XCTAssertEqual(reloaded, cfg)
    }

    func testMissingKeyRestoresDefault() throws {
        // Only one key present; everything else must fall back to defaults.
        let partial = #"{ "schema_version": 2, "caption": { "font_size": 30 } }"#
        try partial.data(using: .utf8)!.write(to: configURL)

        let (cfg, repaired) = Config.loadOrRepair(from: configURL)
        XCTAssertFalse(repaired, "valid-but-partial JSON should merge defaults, not repair")
        XCTAssertEqual(cfg.caption.fontSize, 30)
        XCTAssertEqual(cfg.caption.autoScroll, Config.Caption().autoScroll)      // default true
        XCTAssertEqual(cfg.general.sessionNamePrefix, Config.General().sessionNamePrefix)
        XCTAssertEqual(cfg.asr.finalModel, Config.ASR().finalModel)
    }

    func testCorruptFileRepairsWithBackup() throws {
        try "{ this is not valid json".data(using: .utf8)!.write(to: configURL)

        let (cfg, repaired) = Config.loadOrRepair(from: configURL)
        XCTAssertTrue(repaired)
        XCTAssertEqual(cfg, Config())

        let backups = try FileManager.default
            .contentsOfDirectory(atPath: dir.path)
            .filter { $0.hasPrefix("config.json.bak-") }
        XCTAssertEqual(backups.count, 1, "a single timestamped backup should be written")

        // The replacement on disk must now be valid defaults.
        let (reloaded, repairedAgain) = Config.loadOrRepair(from: configURL)
        XCTAssertFalse(repairedAgain)
        XCTAssertEqual(reloaded, Config())
    }

    func testNoBlackHoleKeyInAudio() throws {
        // Regression guard for the B3 cleanup: audio has only vad_sensitivity.
        let data = try JSONEncoder().encode(Config())
        let json = String(data: data, encoding: .utf8)!
        XCTAssertFalse(json.contains("system_device"))
        XCTAssertTrue(json.contains("vad_sensitivity"))
    }
}
