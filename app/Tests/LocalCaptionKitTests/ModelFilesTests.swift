import XCTest
@testable import LocalCaptionKit

final class ModelFilesTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lc-model-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: dir) }

    private func write(_ relative: String) throws {
        let url = dir.appendingPathComponent(relative)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try "x".write(to: url, atomically: true, encoding: .utf8)
    }

    private func writeWholeModel() throws {
        try write("config.json")
        for part in ModelFiles.requiredParts {
            for file in ModelFiles.partFiles { try write("\(part)/\(file)") }
        }
    }

    func testWholeModelIsComplete() throws {
        try writeWholeModel()
        XCTAssertTrue(ModelFiles.isComplete(dir))
    }

    func testMissingFolderIsIncomplete() {
        XCTAssertFalse(ModelFiles.isComplete(dir.appendingPathComponent("absent")))
    }

    /// The interrupted-download shape: every directory exists, some weights never landed.
    func testMissingWeightsAreIncomplete() throws {
        try writeWholeModel()
        try FileManager.default.removeItem(at: dir.appendingPathComponent("MelSpectrogram.mlmodelc/weights"))
        XCTAssertFalse(ModelFiles.isComplete(dir))
    }

    func testMissingProgramIsIncomplete() throws {
        try writeWholeModel()
        try FileManager.default.removeItem(at: dir.appendingPathComponent("AudioEncoder.mlmodelc/model.mil"))
        XCTAssertFalse(ModelFiles.isComplete(dir))
    }

    func testPartialOptionalPartIsIncomplete() throws {
        try writeWholeModel()
        try write("TextDecoderContextPrefill.mlmodelc/coremldata.bin")
        XCTAssertFalse(ModelFiles.isComplete(dir))
    }
}
