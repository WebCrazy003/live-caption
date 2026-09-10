import XCTest
import Combine
import LocalCaptionKit
@testable import LocalCaption

@MainActor
final class CaptionObservationTests: XCTestCase {
    func testCaptionAndErrorPublishWithoutSessionClock() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let env = AppEnvironment(config: Config(), store: try Store(url: directory.appendingPathComponent("test.db")))
        let controller = SessionController(env: env)
        var notifications = 0
        let subscription = controller.objectWillChange.sink { notifications += 1 }
        controller.orchestrator.hypothesis = "fresh caption"
        XCTAssertEqual(notifications, 1)
        controller.orchestrator.errorText = "decode error"
        XCTAssertEqual(notifications, 2)
        XCTAssertEqual(controller.elapsed, "00:00:00")
        withExtendedLifetime(subscription) {}
    }

    func testSaveFailureCannotBeOverwrittenByStartingNewSession() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let blocked = directory.appendingPathComponent("not-a-directory")
        try Data([0]).write(to: blocked)
        var config = Config()
        config.general.transcriptFolder = blocked.path
        config.summary.enabled = false
        let env = AppEnvironment(config: config, store: try Store(url: directory.appendingPathComponent("test.db")))
        let controller = SessionController(env: env)
        // Inject a completed decode; this never starts capture or reads user files.
        try await controller.orchestrator.onFinal?("Keep this final speech.", 0, 1000)
        controller.phase = .failed
        controller.orchestrator.modelReady = true
        await controller.start()
        XCTAssertTrue(controller.hasTranscript)
        XCTAssertTrue(controller.hasUnsavedSession)
        await controller.stop()
        XCTAssertEqual(controller.phase, .failed)
        XCTAssertNotNil(controller.saveError)
        XCTAssertTrue(controller.hasTranscript)
        env.config.general.transcriptFolder = directory.appendingPathComponent("saved").path
        await controller.stop()
        XCTAssertEqual(controller.phase, .saved)
        XCTAssertFalse(controller.hasUnsavedSession)
        let url = try XCTUnwrap(controller.savedTxtURL)
        XCTAssertTrue(try String(contentsOf: url).contains("Keep this final speech."))
    }

    func testRepeatedAutomaticPauseRequestsFinishOnce() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let env = AppEnvironment(config: Config(), store: try Store(url: directory.appendingPathComponent("test.db")))
        let controller = SessionController(env: env)
        controller.phase = .recording
        let paused = expectation(description: "automatic pause completed")
        let subscription = controller.$phase.sink { if $0 == .paused { paused.fulfill() } }
        controller.orchestrator.onCaptureMustPause?()
        controller.orchestrator.onCaptureMustPause?()
        await fulfillment(of: [paused], timeout: 1)
        XCTAssertEqual(controller.phase, .paused)
        withExtendedLifetime(subscription) {}
    }
}
