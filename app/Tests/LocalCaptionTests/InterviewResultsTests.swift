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

    /// The interview as stored in the database.
    private func saved(_ i: InterviewController) throws -> InterviewRecord {
        try XCTUnwrap(env.store.interview(id: XCTUnwrap(i.record?.id)))
    }

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

    func testEndLinksTheSessionAndSummarizesOnlyWhenAsked() async throws {
        let (interview, rowId) = try await runInterview()
        engine.reply = { text in text.hasPrefix("INTERVIEW FINISHED") ? [.completed(self.summaryMarkdown)] : [] }
        let sentBefore = engine.sent.count

        await interview.sessionSaved(sessionId: rowId, transcript: "Thanks for joining. Why us?")
        XCTAssertEqual(engine.sent.count, sentBefore, "ending never summarizes on its own")
        XCTAssertEqual(interview.record?.summary.status, .pending)

        await interview.generateSummary(transcript: "Thanks for joining. Why us?")

        let rec = try saved(interview)
        XCTAssertEqual(rec.summaryText, summaryMarkdown, "the summary is stored in the database")
        XCTAssertEqual(rec.transcript, "Thanks for joining. Why us?", "and the transcript")
        XCTAssertEqual(rec.sessionId, rowId)
        XCTAssertNotNil(rec.endedAt)
        XCTAssertEqual(rec.summary.status, .done)
        XCTAssertEqual(try env.store.interview(sessionId: rowId)?.id, rec.id)

        let summaryTurn = try XCTUnwrap(engine.sent.last)
        XCTAssertEqual(summaryTurn.threadId, "thr1", "prep, asks and summary share one thread")
        XCTAssertEqual(summaryTurn.input, [.text(InterviewPrompt.summary(transcript: "Thanks for joining. Why us?"))])
        XCTAssertEqual(summaryTurn.effort, "medium", "summary uses prep_reasoning_effort")

        let row = try XCTUnwrap(env.store.fetch(id: rowId))
        XCTAssertEqual(row.mode, "interview")
        XCTAssertEqual(interview.summaryText, summaryMarkdown)
        XCTAssertTrue(interview.isFinished)
    }

    func testFollowUpAfterEndGoesToTheSameThread() async throws {
        let (interview, rowId) = try await runInterview()
        await interview.sessionSaved(sessionId: rowId, transcript: "x")
        engine.reply = { _ in [.completed("Dear Alex, thank you…")] }
        await interview.sendFollowUp("Draft a thank-you email")
        XCTAssertEqual(engine.sent.last?.threadId, "thr1")
        XCTAssertEqual(interview.turns.last?.kind, .typed)
        XCTAssertEqual(interview.turns.last?.answer, "Dear Alex, thank you…")
        XCTAssertTrue(interview.isFinished)
    }

    func testFailedSummaryCanBeGeneratedLater() async throws {
        let (interview, rowId) = try await runInterview()
        engine.reply = { _ in [.failed(.network("offline"), partial: "")] }
        await interview.sessionSaved(sessionId: rowId, transcript: "x")
        await interview.generateSummary(transcript: "x")
        XCTAssertEqual(interview.record?.summary.status, .failed)
        XCTAssertNotNil(interview.summaryError)

        engine.reply = { _ in [.completed(self.summaryMarkdown)] }
        await interview.generateSummary(transcript: "x")
        XCTAssertEqual(interview.record?.summary.status, .done)
        XCTAssertNil(interview.summaryError)
    }

    func testReopeningFromHistoryResumesTheThreadBeforeSummarizing() async throws {
        let (live, rowId) = try await runInterview()
        await live.sessionSaved(sessionId: rowId, transcript: "x")
        let stored = try saved(live)

        let reopened = InterviewController(env: env, existing: stored)
        XCTAssertEqual(reopened.threadState, .open)
        XCTAssertEqual(reopened.record?.turns.count, 1)
        XCTAssertTrue(reopened.isFinished)

        engine.reply = { _ in [.completed(self.summaryMarkdown)] }
        await reopened.generateSummary(transcript: "x")
        XCTAssertEqual(engine.resumed, ["thr1"], "a reopened thread is resumed (lockdown re-applied) first")
        XCTAssertEqual(reopened.record?.summary.status, .done)

        // And the next reopen shows the saved summary without asking Codex.
        let again = InterviewController(env: env, existing: try saved(reopened))
        XCTAssertEqual(again.summaryText, summaryMarkdown)
    }

    func testLaunchSweepFailsCutOffTurns() async throws {
        let interview = InterviewController(env: env)
        await interview.ensureThread()
        engine.holdIf = { _ in true }
        let turn = Task { await interview.sendTyped("left hanging") }
        await waitUntilOnMain { interview.isStreaming }
        XCTAssertEqual(try saved(interview).turns.last?.status, .streaming)

        env.sweepInterviews()     // as on the next launch
        let rec = try saved(interview)
        XCTAssertEqual(rec.turns.last?.status, .failed)
        XCTAssertEqual(rec.turns.last?.error, "app closed")
        engine.release()
        await turn.value
    }

    func testRecoveredCaptureIsLinkedToItsInterview() async throws {
        let interview = InterviewController(env: env)
        interview.draft.candidate = "victor"
        interview.draft.company = "Peloton"
        interview.draft.step = 2
        let capture = UUID()
        let start = Date()
        interview.recordingStarted(uuid: capture, at: start)
        await interview.ensureThread()
        let row = try env.store.insert(SessionRecord(sessionName: "Recovered", createdAt: TimeFormat.iso(start)))

        env.linkRecoveredInterview(captureId: capture, sessionId: row.id, start: start)

        let rec = try saved(interview)
        XCTAssertEqual(rec.sessionId, row.id)
        XCTAssertNotNil(rec.endedAt)
        let session = try env.store.fetch(id: XCTUnwrap(row.id))
        XCTAssertEqual(session?.mode, "interview")
        XCTAssertEqual(session?.sessionName, "victor-Peloton-2-\(TimeFormat.day(start)) (recovered)")
    }

    // MARK: Sessions window → Open in interview panel (owner, 2026-10-02)

    func testOpenInPanelShowsTheSavedInterviewAndContinuesItsThread() async throws {
        let (finished, _) = try await runInterview()
        let segments = [TranscriptSegment(text: "Thanks for joining.", tStartMs: 0, tEndMs: 900, createdAt: "t1"),
                        TranscriptSegment(text: "Why us?", tStartMs: 1000, tEndMs: 1800, createdAt: "t2")]
        let row = try env.store.insert(SessionRecord(sessionName: "victor-Peloton-1-2026-10-02",
                                                     createdAt: TimeFormat.iso(Date()), durationSeconds: 95),
                                       segments: segments)
        let rowId = try XCTUnwrap(row.id)
        await finished.sessionSaved(sessionId: rowId, transcript: "Thanks for joining. Why us?")

        env.session.phase = .ready            // the speech model has loaded
        env.config.interview.mode = .caption
        XCTAssertNil(env.openInPanelBlocker)
        await env.openInPanel(sessionId: rowId)

        XCTAssertEqual(env.config.interview.mode, .interview)
        XCTAssertTrue(env.modeChosen, "opening from Sessions skips the first screen's mode choice")
        XCTAssertEqual(env.interview.record?.id, finished.record?.id)
        XCTAssertTrue(env.interview.isFinished)
        XCTAssertEqual(env.session.phase, .saved)
        XCTAssertEqual(env.session.sessionName, "victor-Peloton-1-2026-10-02")
        XCTAssertEqual(env.session.committedText, "Thanks for joining. Why us?")
        XCTAssertEqual(env.session.elapsed, "00:01:35")

        engine.reply = { _ in [.completed("Sure.")] }
        await env.interview.sendFollowUp("Draft a thank-you email")
        XCTAssertEqual(engine.resumed, [try XCTUnwrap(finished.record?.threadId)], "the saved thread is resumed first")
        XCTAssertEqual(env.interview.turns.last?.status, .completed)
    }

    func testOpenInPanelIsBlockedWhileTheModelLoads() {
        XCTAssertEqual(env.session.phase, .preparing)
        XCTAssertEqual(env.openInPanelBlocker, "Wait until the speech model is ready")
    }

    func testInterviewDetailsAreStoredAndNameTheSession() async throws {
        let interview = InterviewController(env: env)
        interview.draft.candidate = " victor "
        interview.draft.company = "Capital on Tap"
        interview.draft.step = 3
        await interview.ensureThread()
        let day = TimeFormat.day(Date())
        XCTAssertEqual(interview.sessionName(on: Date()), "victor-Capital on Tap-3-\(day)")
        var rec = try saved(interview)
        XCTAssertEqual(rec.setup.candidate, "victor")
        XCTAssertEqual(rec.setup.company, "Capital on Tap")
        XCTAssertEqual(rec.setup.step, 3)

        interview.draft.step = 4               // corrected in the form after the record exists
        interview.detailsChanged()
        rec = try saved(interview)
        XCTAssertEqual(rec.setup.step, 4)
    }
}
