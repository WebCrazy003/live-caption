import XCTest
@testable import LocalCaption
import LocalCaptionKit

/// The interview flow against a fake engine (SPEC-13 §Acceptance): library import, Prepare on a
/// new thread, extra prep turns on the same thread, re-prepare, and the recording hook.
@MainActor
final class InterviewFlowTests: XCTestCase {

    /// Records every call; answers each turn with `reply(text)`.
    final class FakeEngine: AnswerEngine, @unchecked Sendable {
        struct Sent { let threadId: String; let input: [CodexRPC.Input]; let effort: String }
        let notices: AsyncStream<EngineNotice>
        private let noticeSink: AsyncStream<EngineNotice>.Continuation
        private let lock = NSLock()
        private var _threads: [ThreadConfig] = []
        private var _sent: [Sent] = []
        private var _archived: [String] = []
        var reply: (String) -> [AnswerEvent] = { _ in [.delta("Brief"), .delta("ing"), .completed("Briefing\nREADY")] }

        init() { (notices, noticeSink) = AsyncStream.makeStream(of: EngineNotice.self) }
        var threads: [ThreadConfig] { lock.lock(); defer { lock.unlock() }; return _threads }
        var sent: [Sent] { lock.lock(); defer { lock.unlock() }; return _sent }
        var archived: [String] { lock.lock(); defer { lock.unlock() }; return _archived }

        func status() async -> EngineStatus { .ready(email: "me@example.com", plan: "plus") }
        func models() async throws -> [CodexRPC.Model] {
            [.init(id: "gpt-6-luna", displayName: "GPT-6-Luna", description: "", isDefault: false,
                   defaultEffort: "medium", efforts: ["low", "medium"], acceptsImages: true)]
        }
        func usage() async throws -> CodexRPC.Usage { .init(planType: "plus", windows: [.init(minutes: 300, usedPercent: 5, resetsAt: nil)]) }
        func startThread(_ cfg: ThreadConfig) async throws -> String {
            let n = lock.withLock { _threads.append(cfg); return _threads.count }
            return "thr\(n)"
        }
        func resumeThread(id: String, _ cfg: ThreadConfig) async throws {}
        func send(threadId: String, input: [CodexRPC.Input], effort: String) -> AsyncThrowingStream<AnswerEvent, Error> {
            lock.lock(); _sent.append(Sent(threadId: threadId, input: input, effort: effort)); lock.unlock()
            let text: String = { if case .text(let t) = input.first { return t }; return "" }()
            let events = [.started(turnId: "u")] + reply(text)
            return AsyncThrowingStream { c in events.forEach { c.yield($0) }; c.finish() }
        }
        func interrupt(threadId: String) async {}
        func archiveThread(id: String) async { lock.withLock { _archived.append(id) } }
        func startLogin() async throws -> LoginTicket { throw EngineError.other("n/a") }
        func cancelLogin(_ ticket: LoginTicket) async {}
        func shutdown() async {}
    }

    private var tmp: URL!
    private var env: AppEnvironment!
    private var engine: FakeEngine!

    override func setUp() async throws {
        tmp = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("lc-flow-\(UUID().uuidString)")
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

    private func write(_ text: String, _ name: String, in dir: URL) throws -> URL {
        let url = dir.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: url)
        return url
    }

    private func seedLibrary() throws -> (cv: String, jd: String, skill: String) {
        let src = tmp.appendingPathComponent("src")
        let cv = try env.library.importDocument(from: write("Jane Doe\r\nSwift, 8 years.", "Jane CV.txt", in: src), kind: .cv)
        let jd = try env.library.addPastedDocument(title: "Acme JD", text: "Senior iOS role.", kind: .jd)
        let skillDir = src.appendingPathComponent("coach")
        _ = try write("---\nname: Interview coach\n---\nUse STAR.", "SKILL.md", in: skillDir)
        _ = try write("Situation, Task, Action, Result.", "refs/star.md", in: skillDir)
        _ = try write("#!/bin/sh\necho hi", "run.sh", in: skillDir)
        let imported = try env.library.importSkill(from: skillDir)
        XCTAssertEqual(imported.ignored, ["run.sh"])
        XCTAssertEqual(imported.skill.files, ["SKILL.md", "refs/star.md"])
        return (cv.id, jd.id, imported.skill.id)
    }

    func testLibraryImportsNormaliseAndPersist() throws {
        let ids = try seedLibrary()
        XCTAssertEqual(env.library.text(of: ids.cv), "Jane Doe\nSwift, 8 years.")
        XCTAssertEqual(env.library.promptSkill(ids.skill)?.files.map(\.path), ["refs/star.md"])
        // A fresh library instance reads the same index from disk.
        let reloaded = InterviewLibrary(root: tmp.appendingPathComponent("library"))
        XCTAssertEqual(reloaded.documents.map(\.title).sorted(), ["Acme JD", "Jane CV"])
        XCTAssertEqual(reloaded.skills.map(\.title), ["Interview coach"])
        env.library.setText("Edited", of: ids.cv)
        XCTAssertEqual(env.library.text(of: ids.cv), "Edited")
    }

    func testPrepareOpensOneLockedThreadAndStreamsTheBriefing() async throws {
        let ids = try seedLibrary()
        let interview = InterviewController(env: env)
        interview.draft.company = "Acme"; interview.draft.role = "Senior iOS"
        interview.draft.cvId = ids.cv; interview.draft.jdId = ids.jd; interview.draft.skillIds = [ids.skill]
        interview.draft.answerLength = .short
        XCTAssertTrue(interview.canPrepare)

        await interview.prepare()

        XCTAssertEqual(interview.prepState, .ready)
        XCTAssertEqual(interview.briefing, "Briefing\nREADY")
        XCTAssertEqual(engine.threads.count, 1)
        XCTAssertEqual(engine.threads[0].baseInstructions, InterviewPrompt.baseInstructions(length: .short))
        XCTAssertEqual(engine.threads[0].model, "gpt-6-luna")
        let prep = try XCTUnwrap(engine.sent.first)
        XCTAssertEqual(prep.effort, "medium", "prep uses prep_reasoning_effort")
        XCTAssertEqual(prep.input, [.text(interview.prepMessage)])
        guard case .text(let text) = prep.input[0] else { return XCTFail() }
        XCTAssertTrue(text.contains("INTERVIEW SKILL: Interview coach"))
        XCTAssertTrue(text.contains("SKILL FILE: refs/star.md"))
        XCTAssertTrue(text.hasSuffix(InterviewPrompt.prepTaskWithSkill))

        // On disk.
        let folder = try XCTUnwrap(interview.folder)
        let rec = try InterviewFiles.read(from: folder)
        XCTAssertEqual(rec.threadId, "thr1")
        XCTAssertEqual(rec.prep.status, .done)
        XCTAssertEqual(rec.setup.documentIds, [ids.cv, ids.jd])
        XCTAssertEqual(rec.name, "Acme — Senior iOS")
    }

    func testExtraPrepTurnsGoToTheSameThread() async throws {
        let interview = InterviewController(env: env)
        interview.draft.company = "Acme"
        await interview.prepare()
        engine.reply = { _ in [.delta("Sure"), .completed("Sure — here are three more.")] }

        let status = await interview.runTurn(kind: .typed, text: "Also prepare system design", question: "Also prepare system design")

        XCTAssertEqual(status, .completed)
        XCTAssertEqual(engine.sent.map(\.threadId), ["thr1", "thr1"])
        XCTAssertEqual(engine.sent.last?.effort, "low", "turns use reasoning_effort")
        let rec = try InterviewFiles.read(from: XCTUnwrap(interview.folder))
        XCTAssertEqual(rec.prep.extraTurns, 1)
        XCTAssertEqual(rec.turns.first?.answer, "Sure — here are three more.")
        XCTAssertEqual(rec.turns.first?.status, .completed)
        XCTAssertNotNil(rec.turns.first?.ttftMs)
    }

    func testPreparingAgainReplacesAnUnstartedInterview() async throws {
        let interview = InterviewController(env: env)
        interview.draft.company = "Acme"
        await interview.prepare()
        let first = try XCTUnwrap(interview.folder)
        interview.draft.role = "Lead"
        XCTAssertTrue(interview.setupChangedSinceReady)

        await interview.prepare()

        XCTAssertFalse(FileManager.default.fileExists(atPath: first.path), "the unstarted draft is deleted")
        XCTAssertEqual(engine.archived, ["thr1"], "and its thread archived")
        XCTAssertEqual(interview.record?.threadId, "thr2")
        XCTAssertFalse(interview.setupChangedSinceReady)
    }

    func testStartedInterviewIsKeptAndLinkedToTheCapture() async throws {
        let interview = InterviewController(env: env)
        let uuid = UUID()
        interview.recordingStarted(uuid: uuid, at: Date())      // Start before Prepare is allowed
        await interview.prepare()
        await interview.discardUnstarted()
        let rec = try InterviewFiles.read(from: XCTUnwrap(interview.folder))
        XCTAssertEqual(rec.captureSessionUUID, uuid.uuidString)
        XCTAssertNotNil(rec.startedAt)
    }

    func testFailedPrepCanBeRetriedOnTheSameThread() async throws {
        engine.reply = { _ in [.failed(.network("offline"), partial: "")] }
        let interview = InterviewController(env: env)
        await interview.prepare()
        guard case .failed = interview.prepState else { return XCTFail("expected failure") }
        engine.reply = { _ in [.completed("READY")] }
        await interview.retryPrepare()
        XCTAssertEqual(interview.prepState, .ready)
        XCTAssertEqual(engine.threads.count, 1, "retry reuses the thread")
    }
}
