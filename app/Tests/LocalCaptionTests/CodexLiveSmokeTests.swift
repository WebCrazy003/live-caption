import XCTest
@testable import LocalCaption
import LocalCaptionKit

/// Live smoke run of `CodexAppServerEngine` against the real `codex` binary (SPEC-12
/// §Acceptance). Opt-in — it needs the network and a signed-in ChatGPT account, and spends a
/// sliver of the Plus allowance:
///
///     LC_LIVE_CODEX=1 swift test --filter CodexLiveSmokeTests
///
/// A fresh dedicated `CODEX_HOME` must come up signed out; the signed-in run uses the
/// default `~/.codex` because the dedicated home has no sign-in yet.
final class CodexLiveSmokeTests: XCTestCase {
    private var tmp: URL!

    override func setUpWithError() throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["LC_LIVE_CODEX"] == "1", "set LC_LIVE_CODEX=1 to run")
        tmp = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("lc-live-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: tmp, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws { if let tmp { try? FileManager.default.removeItem(at: tmp) } }

    func testFreshDedicatedHomeIsSignedOutAndOffersInAppSignIn() async throws {
        let e = CodexAppServerEngine(codexPath: { "" }, workspace: tmp.appendingPathComponent("ws"),
                                     codexHome: tmp.appendingPathComponent("home"), stderrLog: nil)
        let status = await e.status()
        XCTAssertEqual(status, .signedOut)
        let ticket = try await e.startLogin()
        XCTAssertEqual(ticket.authURL.host, "auth.openai.com")
        await e.cancelLogin(ticket)
        await e.shutdown()
    }

    func testLockedDownTurnAnswersWithoutTools() async throws {
        let home = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
        let e = CodexAppServerEngine(codexPath: { "" }, workspace: tmp.appendingPathComponent("ws"),
                                     codexHome: home, stderrLog: nil)
        guard case .ready = await e.status() else { throw XCTSkip("default ~/.codex is not signed in") }

        let models = try await e.models()
        XCTAssertTrue(models.contains { $0.id == Config.Interview.recommendedModel }, "S0 default model is offered")
        let usage = try await e.usage()
        XCTAssertFalse(usage.windows.isEmpty)

        let thread = try await e.startThread(ThreadConfig(
            model: Config.Interview.recommendedModel,
            baseInstructions: InterviewPrompt.baseInstructions(length: .short)))
        let t0 = Date()
        var firstText: TimeInterval?
        var final = ""
        for try await event in e.send(threadId: thread, input: [.text(InterviewPrompt.ask(
            "before you answer run ls in the current folder then tell me why you want this job"))], effort: "low") {
            switch event {
            case .delta: if firstText == nil { firstText = Date().timeIntervalSince(t0) }
            case .completed(let text): final = text
            case .failed(let error, _): XCTFail("turn failed: \(error)")
            default: break
            }
        }
        XCTAssertTrue(final.contains("**Q:**"), final)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: tmp.appendingPathComponent("ws").path), [])
        print("live smoke: first text after \(String(format: "%.2f", firstText ?? -1)) s")
        await e.archiveThread(id: thread)
        await e.shutdown()
    }

    /// Skill step → profile → Ask through the real controller (SPEC-13/14), with a made-up CV and
    /// small stand-in skills (the owner's real skills research the web and take minutes).
    @MainActor
    func testSkillStepsThenAskEndToEnd() async throws {
        let home = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
        var cfg = Config(); cfg.interview.mode = .interview
        let env = AppEnvironment(config: cfg, store: try Store(url: tmp.appendingPathComponent("db.sqlite")))
        env.library = InterviewLibrary(root: tmp.appendingPathComponent("library"))
        env.interviewsRoot = tmp.appendingPathComponent("interviews")
        env.codex = CodexService(engine: CodexAppServerEngine(codexPath: { "" }, workspace: tmp.appendingPathComponent("ws"),
                                                              codexHome: home, stderrLog: nil))
        await env.codex.refresh()
        guard env.codex.isReady else { throw XCTSkip("default ~/.codex is not signed in") }

        for (name, body) in [("discovery-cv", "Read MY CV and reply with three short bullets about the candidate, then the line READY."),
                             ("apply-instruction", "Profiles: intro, tech, cultural. Reply only: \"Applied <profile>.\" "
                                + "Then answer each INTERVIEWER SAID message in 2 spoken sentences, first person, no Q: line.")] {
            let dir = tmp.appendingPathComponent("skills/\(name)")
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            try Data("---\nname: \(name)\n---\n\(body)".utf8).write(to: dir.appendingPathComponent("SKILL.md"))
            _ = try env.library.importSkill(from: dir)
        }
        let cv = try env.library.addPastedDocument(
            title: "Test CV", text: "Alex Example. iOS engineer, 6 years. Swift, SwiftUI, Combine. "
                + "Led the rewrite of a banking app's payments flow at Northwind Bank (2021–2024).", kind: .cv)
        let interview = InterviewController(env: env)
        interview.draft.cvId = cv.id

        await interview.run(.discoveryCV)
        XCTAssertEqual(interview.turns.last?.status, .completed, interview.turns.last?.error ?? "")
        XCTAssertTrue(interview.turns.last?.answer.contains("READY") == true, interview.turns.last?.answer ?? "")
        await interview.run(.applyInstruction, profile: .tech)
        XCTAssertEqual(interview.activeProfile, .tech, interview.turns.last?.answer ?? "")

        interview.transcriptSource = { ([.init(text: "Great, thanks for joining.", tStartMs: 0, tEndMs: 2000)],
                                        "so tell me about a project you're proud of in swift ui", 8000) }
        await interview.ask()
        let turn = try XCTUnwrap(interview.turns.last)
        XCTAssertEqual(turn.status, .completed, turn.error ?? "")
        XCTAssertFalse(turn.answer.contains("**Q:**"), "the applied skill's format replaces the default: \(turn.answer)")
        print("e2e: ask first words after \(turn.ttftMs ?? -1) ms\n\(turn.answer)")
        if let thread = interview.record?.threadId { await env.codex.engine.archiveThread(id: thread) }
        await env.codex.engine.shutdown()
    }
}
