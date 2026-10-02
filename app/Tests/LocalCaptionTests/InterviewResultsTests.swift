import XCTest
@testable import LocalCaption
import LocalCaptionKit

/// SPEC-15: ending an interview, the summary on the same thread, history reopen, the launch
/// sweep and crash-recovery linking.
@MainActor
final class InterviewResultsTests: XCTestCase {
    private typealias FakeEngine = InterviewFlowTests.FakeEngine

    private var tmp: URL!
    private var env: AppEnvironment!
    private var engine: FakeEngine!

    override func setUp() async throws {
        tmp = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("lc-results-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: tmp, withIntermediateDirectories: true)
        var cfg = Config()
        cfg.interview.mode = .interview
        env = AppEnvironment(config: cfg, store: try Store(url: tmp.appendingPathComponent("db.sqlite")))
        env.library = InterviewLibrary(root: tmp.appendingPathComponent("library"))
        env.interviewsRoot = tmp.appendingPathComponent("interviews")
        engine = FakeEngine()
        env.codex = CodexService(engine: engine)
        await env.codex.refresh()
    }

    override func tearDown() async throws { try? FileManager.default.removeItem(at: tmp) }

    private let summaryMarkdown = "## Overview\nGood.\n## Questions asked\n- Why us\n## Follow-ups\n-\n## Prepare next time\n-\n## Thank-you note\nThanks!"

    /// Prepare, start recording, ask once — ready for Stop.
    private func runInterview() async throws -> (InterviewController, Int64) {
        let interview = InterviewController(env: env)
        interview.transcriptSource = { ([], "why us", 3000) }
        interview.recordingStarted(uuid: UUID(), at: Date())
        await interview.ensureThread()
        engine.reply = { _ in [.completed("**Q:** Why us?\nBecause.")] }
        await interview.ask()
        let row = try env.store.insert(SessionRecord(sessionName: "Interview 1", createdAt: TimeFormat.iso(Date())))
        return (interview, try XCTUnwrap(row.id))
    }

    func testStopLinksTheSessionAndSummarizesOnTheSameThread() async throws {
        let (interview, rowId) = try await runInterview()
        engine.reply = { text in text.hasPrefix("INTERVIEW FINISHED") ? [.completed(self.summaryMarkdown)] : [] }

        await interview.sessionSaved(sessionId: rowId, transcript: "Thanks for joining. Why us?")

        let folder = try XCTUnwrap(interview.folder)
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("summary.md"), encoding: .utf8), summaryMarkdown)
        let rec = try InterviewFiles.read(from: folder)
        XCTAssertEqual(rec.sessionId, rowId)
        XCTAssertNotNil(rec.endedAt)
        XCTAssertEqual(rec.summary.status, .done)
        XCTAssertEqual(rec.summary.file, "summary.md")

        let summaryTurn = try XCTUnwrap(engine.sent.last)
        XCTAssertEqual(summaryTurn.threadId, "thr1", "prep, asks and summary share one thread")
        XCTAssertEqual(summaryTurn.input, [.text(InterviewPrompt.summary(transcript: "Thanks for joining. Why us?"))])
        XCTAssertEqual(summaryTurn.effort, "medium", "summary uses prep_reasoning_effort")
        XCTAssertEqual(engine.shutdowns, 1)

        let row = try XCTUnwrap(env.store.fetch(id: rowId))
        XCTAssertEqual(row.mode, "interview")
        XCTAssertEqual(row.interviewDir, folder.path)
        XCTAssertEqual(interview.summaryText, summaryMarkdown)
        XCTAssertTrue(interview.isFinished)
    }

    func testSummaryOffIsSkipped() async throws {
        env.config.interview.summarizeOnEnd = false
        let (interview, rowId) = try await runInterview()
        let sentBefore = engine.sent.count
        await interview.sessionSaved(sessionId: rowId, transcript: "x")
        XCTAssertEqual(engine.sent.count, sentBefore)
        XCTAssertEqual(interview.record?.summary.status, .skipped)
    }

    func testFailedSummaryCanBeGeneratedLater() async throws {
        let (interview, rowId) = try await runInterview()
        engine.reply = { _ in [.failed(.network("offline"), partial: "")] }
        await interview.sessionSaved(sessionId: rowId, transcript: "x")
        XCTAssertEqual(interview.record?.summary.status, .failed)
        XCTAssertNotNil(interview.summaryError)

        engine.reply = { _ in [.completed(self.summaryMarkdown)] }
        await interview.generateSummary(transcript: "x")
        XCTAssertEqual(interview.record?.summary.status, .done)
        XCTAssertNil(interview.summaryError)
    }

    func testReopeningFromHistoryResumesTheThreadBeforeSummarizing() async throws {
        env.config.interview.summarizeOnEnd = false
        let (live, rowId) = try await runInterview()
        await live.sessionSaved(sessionId: rowId, transcript: "x")
        let folder = try XCTUnwrap(live.folder)

        let reopened = try InterviewController(env: env, existing: folder)
        XCTAssertEqual(reopened.threadState, .open)
        XCTAssertEqual(reopened.record?.turns.count, 1)
        XCTAssertTrue(reopened.isFinished)

        engine.reply = { _ in [.completed(self.summaryMarkdown)] }
        await reopened.generateSummary(transcript: "x")
        XCTAssertEqual(engine.resumed, ["thr1"], "a reopened thread is resumed (lockdown re-applied) first")
        XCTAssertEqual(reopened.record?.summary.status, .done)

        // And the next reopen shows the saved summary without asking Codex.
        let again = try InterviewController(env: env, existing: folder)
        XCTAssertEqual(again.summaryText, summaryMarkdown)
    }

    func testLaunchSweepFailsCutOffTurns() async throws {
        let interview = InterviewController(env: env)
        await interview.ensureThread()
        engine.holdIf = { _ in true }
        let turn = Task { await interview.sendTyped("left hanging") }
        try await Task.sleep(nanoseconds: 100_000_000)
        let folder = try XCTUnwrap(interview.folder)
        XCTAssertEqual(try InterviewFiles.read(from: folder).turns.last?.status, .streaming)

        env.sweepInterviews()     // as on the next launch
        let rec = try InterviewFiles.read(from: folder)
        XCTAssertEqual(rec.turns.last?.status, .failed)
        XCTAssertEqual(rec.turns.last?.error, "app closed")
        engine.release()
        await turn.value
    }

    func testRecoveredCaptureIsLinkedToItsInterview() async throws {
        let interview = InterviewController(env: env)
        let capture = UUID()
        interview.recordingStarted(uuid: capture, at: Date())
        await interview.ensureThread()
        let row = try env.store.insert(SessionRecord(sessionName: "Recovered", createdAt: TimeFormat.iso(Date())))

        env.linkRecoveredInterview(captureId: capture, sessionId: row.id)

        let folder = try XCTUnwrap(interview.folder)
        let rec = try InterviewFiles.read(from: folder)
        XCTAssertEqual(rec.sessionId, row.id)
        XCTAssertNotNil(rec.endedAt)
        let linked = try XCTUnwrap(env.store.fetch(id: XCTUnwrap(row.id))?.interviewDir)
        XCTAssertEqual(URL(fileURLWithPath: linked).resolvingSymlinksInPath(), folder.resolvingSymlinksInPath())
    }
}
