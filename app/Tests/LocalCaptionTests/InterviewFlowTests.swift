import XCTest
@testable import LocalCaption
import LocalCaptionKit

/// The interview flow against a fake engine (SPEC-13 §Acceptance): library import, the CV upload,
/// the manual skill steps on one thread, profiles, and the coach opening on Start.
@MainActor
final class InterviewFlowTests: XCTestCase {

    /// Records every call; answers each turn with `reply(text)`.
    final class FakeEngine: AnswerEngine, @unchecked Sendable {
        struct Sent { let threadId: String; let input: [CodexRPC.Input]; let effort: String; let model: String? }
        let notices: AsyncStream<EngineNotice>
        private let noticeSink: AsyncStream<EngineNotice>.Continuation
        private let lock = NSLock()
        private var _threads: [ThreadConfig] = []
        private var _sent: [Sent] = []
        private var _archived: [String] = []
        var reply: (String) -> [AnswerEvent] = { _ in [.delta("Brief"), .delta("ing"), .completed("Briefing\nREADY")] }
        /// When set, a turn whose text matches stays open until `release()` or `interrupt`.
        var holdIf: (String) -> Bool = { _ in false }
        private var held: AsyncThrowingStream<AnswerEvent, Error>.Continuation?
        private(set) var interrupts = 0
        private(set) var resumed: [String] = []
        private(set) var shutdowns = 0

        func release(with text: String = "Done") {
            let c = lock.withLock { () -> AsyncThrowingStream<AnswerEvent, Error>.Continuation? in
                defer { held = nil }; return held
            }
            c?.yield(.completed(text)); c?.finish()
        }

        init() { (notices, noticeSink) = AsyncStream.makeStream(of: EngineNotice.self) }
        var threads: [ThreadConfig] { lock.lock(); defer { lock.unlock() }; return _threads }
        var sent: [Sent] { lock.lock(); defer { lock.unlock() }; return _sent }
        var archived: [String] { lock.lock(); defer { lock.unlock() }; return _archived }

        func status() async -> EngineStatus { .ready(email: "me@example.com", plan: "plus") }
        func models() async throws -> [CodexRPC.Model] {
            [.init(id: "gpt-6-luna", displayName: "GPT-6-Luna", description: "", isDefault: false,
                   defaultEffort: "medium", efforts: ["low", "medium"], acceptsImages: true),
             .init(id: "gpt-6.1-sol", displayName: "GPT-6.1-Sol", description: "", isDefault: true,
                   defaultEffort: "low", efforts: ["low", "medium", "high"], acceptsImages: true)]
        }
        func usage() async throws -> CodexRPC.Usage { .init(planType: "plus", windows: [.init(minutes: 300, usedPercent: 5, resetsAt: nil)]) }
        var failStart: EngineError?
        func startThread(_ cfg: ThreadConfig) async throws -> String {
            if let failStart { throw failStart }
            let n = lock.withLock { _threads.append(cfg); return _threads.count }
            return "thr\(n)"
        }
        func resumeThread(id: String, _ cfg: ThreadConfig) async throws { lock.withLock { resumed.append(id) } }
        func send(threadId: String, input: [CodexRPC.Input], effort: String, model: String?) -> AsyncThrowingStream<AnswerEvent, Error> {
            lock.lock(); _sent.append(Sent(threadId: threadId, input: input, effort: effort, model: model)); lock.unlock()
            let text: String = { if case .text(let t) = input.first { return t }; return "" }()
            if holdIf(text) {
                return AsyncThrowingStream { c in
                    c.yield(.started(turnId: "u")); c.yield(.delta("Partial"))
                    lock.withLock { held = c }
                }
            }
            let events = [.started(turnId: "u")] + reply(text)
            return AsyncThrowingStream { c in events.forEach { c.yield($0) }; c.finish() }
        }
        func interrupt(threadId: String) async {
            let c = lock.withLock { () -> AsyncThrowingStream<AnswerEvent, Error>.Continuation? in
                interrupts += 1; defer { held = nil }; return held
            }
            c?.yield(.interrupted(partial: "Partial")); c?.finish()
        }
        func archiveThread(id: String) async { lock.withLock { _archived.append(id) } }
        func startLogin() async throws -> LoginTicket { throw EngineError.other("n/a") }
        func logout() async throws {}
        func cancelLogin(_ ticket: LoginTicket) async {}
        func shutdown() async { lock.withLock { shutdowns += 1 } }
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

    /// The interview as stored in the database.
    private func saved(_ i: InterviewController) throws -> InterviewRecord {
        try XCTUnwrap(env.store.interview(id: XCTUnwrap(i.record?.id)))
    }

    private func write(_ text: String, _ name: String, in dir: URL) throws -> URL {
        let url = dir.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: url)
        return url
    }

    /// The owner's four skills, as importable folders.
    @discardableResult
    private func importSkills(_ names: [String] = InterviewController.Step.allCases.map(\.rawValue)) throws -> [String: String] {
        var ids: [String: String] = [:]
        for name in names {
            let dir = tmp.appendingPathComponent("src/\(name)")
            _ = try write("---\nname: \(name)\n---\n# /\(name)\nDo the \(name) step.", "SKILL.md", in: dir)
            ids[name] = try env.library.importSkill(from: dir).skill.id
        }
        return ids
    }

    private func importCV() throws -> String {
        try env.library.importDocument(from: write("Jane Doe\r\nSwift, 8 years.", "Jane CV.txt", in: tmp.appendingPathComponent("src")),
                                       kind: .cv).id
    }

    private var sentTexts: [String] {
        engine.sent.compactMap { if case .text(let t)? = $0.input.first { return t }; return nil }
    }

    // MARK: Library

    func testLibraryImportsNormaliseAndPersist() throws {
        let cv = try importCV()
        let skillDir = tmp.appendingPathComponent("src/coach")
        _ = try write("---\nname: Interview coach\n---\nUse STAR.", "SKILL.md", in: skillDir)
        _ = try write("Situation, Task, Action, Result.", "refs/star.md", in: skillDir)
        _ = try write("#!/bin/sh\necho hi", "run.sh", in: skillDir)
        let imported = try env.library.importSkill(from: skillDir)
        XCTAssertEqual(imported.ignored, ["run.sh"])
        XCTAssertEqual(imported.skill.files, ["SKILL.md", "refs/star.md"])
        XCTAssertEqual(env.library.text(of: cv), "Jane Doe\nSwift, 8 years.")
        let reloaded = InterviewLibrary(root: tmp.appendingPathComponent("library"))
        XCTAssertEqual(reloaded.documents.map(\.title), ["Jane CV"])
        XCTAssertEqual(reloaded.skills.map(\.slug), ["interview-coach"])
        XCTAssertThrowsError(try env.library.importDocument(from: write("x", "cv.docx", in: tmp), kind: .cv),
                             ".docx isn't supported any more")
    }

    func testUploadCVImportsAndSelectsIt() throws {
        let interview = InterviewController(env: env)
        XCTAssertNil(interview.draft.cvId)
        try interview.uploadCV(from: write("Alex Example\nKotlin.", "Alex.md", in: tmp.appendingPathComponent("src")))
        let id = try XCTUnwrap(interview.draft.cvId)
        XCTAssertEqual(env.library.document(id)?.kind, .cv)
        XCTAssertEqual(env.library.text(of: id), "Alex Example\nKotlin.")
    }

    // MARK: Skill steps

    func testStepsRunOnOneThreadAndSendEachDefinitionOnce() async throws {
        try importSkills()
        let interview = InterviewController(env: env)
        interview.draft.cvId = try importCV()
        interview.draft.jobDescription = "Senior iOS Engineer\nAcme, London."
        let skill = { (slug: String) in self.env.library.promptSkill(try! XCTUnwrap(self.env.library.skill(slug: slug)).id) }

        await interview.run(.discoveryCV)
        XCTAssertEqual(sentTexts.last, InterviewPrompt.skillMessage(
            command: "/discovery-cv", definition: skill("discovery-cv"),
            attachments: [.init(title: "MY CV", text: "Jane Doe\nSwift, 8 years.")]))
        XCTAssertEqual(engine.sent.last?.effort, "medium", "skill steps use prep_reasoning_effort")
        XCTAssertEqual(engine.threads.first?.baseInstructions, InterviewPrompt.baseInstructions(length: .medium))

        await interview.run(.discoveryCV)
        XCTAssertEqual(sentTexts.last, InterviewPrompt.skillMessage(
            command: "/discovery-cv", definition: nil,
            attachments: [.init(title: "MY CV", text: "Jane Doe\nSwift, 8 years.")]), "definition only the first time")

        await interview.run(.discoveryJD)
        XCTAssertTrue(sentTexts.last!.hasSuffix("JOB DESCRIPTION\nSenior iOS Engineer\nAcme, London.\n\n/discovery-jd"))
        XCTAssertEqual(interview.record?.name, "Senior iOS Engineer")

        XCTAssertNotNil(interview.blocker(.liveCoding), "live coding waits for Tech")
        await interview.run(.applyInstruction, profile: .tech)
        XCTAssertTrue(sentTexts.last!.hasSuffix("\n\n/apply-instruction tech"))
        XCTAssertEqual(interview.activeProfile, .tech)
        XCTAssertNil(interview.blocker(.liveCoding))
        await interview.run(.liveCoding)
        XCTAssertEqual(sentTexts.last, InterviewPrompt.skillMessage(command: "/live-coding-design",
                                                                   definition: skill("live-coding-design")))
        XCTAssertTrue(interview.liveCodingActive)

        await interview.run(.applyInstruction, profile: .cultural)
        XCTAssertEqual(sentTexts.last, "/apply-instruction cultural", "switching profile resends no definition")
        XCTAssertEqual(interview.activeProfile, .cultural)
        XCTAssertFalse(interview.liveCodingActive, "a new profile replaces live coding")

        XCTAssertEqual(engine.threads.count, 1)
        XCTAssertEqual(Set(engine.sent.map(\.threadId)), ["thr1"])
        let rec = try saved(interview)
        XCTAssertEqual(rec.turns.map(\.kind), Array(repeating: .skill, count: 6))
        XCTAssertEqual(rec.turns.map(\.question), ["/discovery-cv", "/discovery-cv", "/discovery-jd",
                                                   "/apply-instruction tech", "/live-coding-design", "/apply-instruction cultural"])
        XCTAssertTrue(interview.isDone(.discoveryCV) && interview.isDone(.discoveryJD))
    }

    func testStepBlockers() throws {
        let interview = InterviewController(env: env)
        XCTAssertEqual(interview.blocker(.discoveryCV), "Load the discovery-cv skill in Settings → Skills")
        XCTAssertFalse(interview.allSkillsLoaded)
        XCTAssertEqual(interview.missingSkills.count, 4)
        try importSkills()
        XCTAssertTrue(interview.allSkillsLoaded)
        XCTAssertEqual(interview.blocker(.discoveryCV), "Select or upload a CV")
        XCTAssertEqual(interview.blocker(.discoveryJD), "Paste the job description")
        XCTAssertNil(interview.blocker(.applyInstruction))
        XCTAssertEqual(interview.blocker(.liveCoding), "Apply the Tech profile first")
    }

    // MARK: Settings → Skills slots

    func testSkillSlotsTakeAnyFileNameAndReplace() throws {
        let src = tmp.appendingPathComponent("src")
        let first = try write("---\nname: My CV analyser\n---\nv1", "whatever.md", in: src)
        let loaded = try env.library.loadSkill(slot: "discovery-cv", from: first)
        XCTAssertEqual(loaded.skill.slug, "discovery-cv", "the slot name becomes the slug")
        XCTAssertEqual(env.library.promptSkill(loaded.skill.id)?.text, "---\nname: My CV analyser\n---\nv1")

        let second = try write("---\nname: discovery-cv\n---\nv2", "SKILL.md", in: src.appendingPathComponent("folder"))
        try env.library.loadSkill(slot: "discovery-cv", from: second.deletingLastPathComponent())
        XCTAssertEqual(env.library.skills.filter { $0.slug == "discovery-cv" }.count, 1, "replaced, not added")
        XCTAssertEqual(env.library.promptSkill(try XCTUnwrap(env.library.skill(slug: "discovery-cv")).id)?.text,
                       "---\nname: discovery-cv\n---\nv2")

        env.library.removeSkill(slot: "discovery-cv")
        XCTAssertNil(env.library.skill(slug: "discovery-cv"))
        let reloaded = InterviewLibrary(root: tmp.appendingPathComponent("library"))
        XCTAssertTrue(reloaded.skills.isEmpty)
    }

    // MARK: Start preparation

    private func readyDraft(_ interview: InterviewController, liveCoding: Bool = false) throws {
        interview.draft.cvId = try importCV()
        interview.draft.jobDescription = "Senior iOS Engineer"
        interview.draft.profile = .tech
        interview.draft.liveCoding = liveCoding
    }

    func testStartPreparationRunsThePlanInOrder() async throws {
        try importSkills()
        let interview = InterviewController(env: env)
        try readyDraft(interview, liveCoding: true)
        XCTAssertNil(interview.preparationBlocker)
        XCTAssertFalse(interview.isPrepared)

        await interview.startPreparation()

        XCTAssertEqual(interview.turns.map(\.question),
                       ["/discovery-cv", "/discovery-jd", "/apply-instruction tech", "/live-coding-design"])
        XCTAssertTrue(interview.isPrepared)
        XCTAssertNil(interview.preparationFailedAt)
        XCTAssertEqual(Set(engine.sent.map(\.effort)), ["medium"], "preparation uses the preparation effort")
        XCTAssertEqual(engine.threads.count, 1)
    }

    func testPreparationBlockers() throws {
        let interview = InterviewController(env: env)
        XCTAssertEqual(interview.preparationBlocker, "Load the 4 skills in Settings → Interview → Skills")
        try importSkills()
        XCTAssertEqual(interview.preparationBlocker, "Choose or upload a CV (①)")
        interview.draft.cvId = try importCV()
        XCTAssertEqual(interview.preparationBlocker, "Paste the job description (②)")
        interview.draft.jobDescription = "JD"
        XCTAssertEqual(interview.preparationBlocker, "Choose a mode (③)")
        interview.draft.profile = .intro
        interview.draft.liveCoding = true
        XCTAssertEqual(interview.preparationBlocker, "Live coding needs the Tech mode (③)")
        interview.draft.profile = .tech
        XCTAssertNil(interview.preparationBlocker)
    }

    func testPreparationStopsAtAFailureAndContinuesFromThere() async throws {
        try importSkills()
        let interview = InterviewController(env: env)
        try readyDraft(interview)
        engine.reply = { text in text.hasSuffix("/discovery-jd") ? [.failed(.network("offline"), partial: "")] : [.completed("ok")] }

        await interview.startPreparation()
        XCTAssertEqual(interview.preparationFailedAt, .discoveryJD)
        XCTAssertEqual(interview.turns.map(\.question), ["/discovery-cv", "/discovery-jd"], "stops at the failure")
        XCTAssertFalse(interview.isPrepared)

        engine.reply = { _ in [.completed("ok")] }
        await interview.startPreparation(resume: true)
        XCTAssertEqual(interview.turns.map(\.question), ["/discovery-cv", "/discovery-jd", "/discovery-jd", "/apply-instruction tech"],
                       "Continue resumes at the failed step, not from the start")
        XCTAssertTrue(interview.isPrepared)
        XCTAssertNil(interview.preparationFailedAt)
    }

    func testChangingTheModelSwitchesTheNextTurn() async throws {
        let interview = InterviewController(env: env)
        await interview.sendTyped("hello")
        XCTAssertNil(engine.sent.last?.model, "unchanged model → not resent")
        XCTAssertEqual(engine.threads.first?.model, Config.Interview.recommendedModel)

        env.config.interview.model = "gpt-6.1-sol"
        await interview.sendTyped("again")
        XCTAssertEqual(engine.sent.last?.model, "gpt-6.1-sol")
        XCTAssertEqual(interview.record?.model, "gpt-6.1-sol")
        await interview.sendTyped("once more")
        XCTAssertNil(engine.sent.last?.model, "Codex keeps it; no need to resend")
    }

    func testDiscoveryCVSnapshotsTheCVForHistory() async throws {
        try importSkills()
        let interview = InterviewController(env: env)
        let cv = try importCV()
        interview.draft.cvId = cv
        interview.draft.jobDescription = "Role X"
        await interview.run(.discoveryCV)
        await interview.run(.discoveryJD)
        env.library.deleteDocument(cv)                  // the library changes later…
        let reopened = InterviewController(env: env, existing: try saved(interview))
        XCTAssertEqual(reopened.cvText, "Jane Doe\nSwift, 8 years.", "…history still shows the CV the coach saw")
        XCTAssertEqual(reopened.cvTitle, "Jane CV")
        XCTAssertEqual(reopened.jdText, "Role X")
    }

    // MARK: The coach without any step

    func testStartOpensTheCoachWithoutAnySteps() async throws {
        env.config.interview.answerLength = .short
        env.config.interview.customInstructions = "Mention measurable results."
        let interview = InterviewController(env: env)
        interview.recordingStarted(uuid: UUID(), at: Date())
        let thread = await interview.ensureThread()      // joins the open started by Start
        XCTAssertEqual(thread, "thr1")
        XCTAssertEqual(engine.threads.count, 1)
        XCTAssertEqual(interview.threadState, .open)
        XCTAssertEqual(engine.threads[0].baseInstructions,
                       InterviewPrompt.baseInstructions(length: .short, custom: "Mention measurable results."))
        let rec = try saved(interview)
        XCTAssertNotNil(rec.startedAt)
        XCTAssertNotNil(rec.captureSessionUUID)
    }

    func testCoachUnavailableIsReportedAndRetryable() async throws {
        engine.failStart = .signedOut
        let interview = InterviewController(env: env)
        let first = await interview.ensureThread()
        XCTAssertNil(first)
        XCTAssertEqual(interview.threadState, .failed(EngineError.signedOut.localizedDescription))
        engine.failStart = nil
        let second = await interview.ensureThread()
        XCTAssertEqual(second, "thr1")
        XCTAssertEqual(interview.threadState, .open)
    }

    func testStartOverDiscardsAnUnstartedInterview() async throws {
        let interview = InterviewController(env: env)
        await interview.sendTyped("hello")
        let id = try XCTUnwrap(interview.record?.id)
        await interview.discardUnstarted()
        interview.resetForNewInterview()
        XCTAssertNil(try env.store.interview(id: id), "its rows are deleted")
        XCTAssertEqual(engine.archived, ["thr1"])
        XCTAssertEqual(interview.threadState, .none)
        XCTAssertTrue(interview.turns.isEmpty)
    }

    func testStartedInterviewIsNotDiscarded() async throws {
        let interview = InterviewController(env: env)
        interview.recordingStarted(uuid: UUID(), at: Date())
        await interview.ensureThread()
        await interview.discardUnstarted()
        XCTAssertNotNil(interview.record)
        XCTAssertNotNil(try saved(interview))
    }
}
