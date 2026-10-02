import Foundation
import SwiftUI
import AppKit
import LocalCaptionKit

/// One interview (SPEC-13–15): the setup (CV, pasted JD), the skill steps the user runs by hand,
/// and every turn on the interview's single Codex thread. The thread opens on first need — a
/// skill step, an Ask, or Start. Persists `interview.json` after every state change.
@MainActor
final class InterviewController: ObservableObject {
    /// The owner's skill sequence (SPEC-13 §Skill steps), found in the library by slug.
    enum Step: String, CaseIterable, Identifiable {
        case discoveryCV = "discovery-cv"
        case discoveryJD = "discovery-jd"
        case applyInstruction = "apply-instruction"
        case liveCoding = "live-coding-design"
        var id: String { rawValue }
        var title: String {
            switch self {
            case .discoveryCV: return "Discovery CV"
            case .discoveryJD: return "Discovery JD"
            case .applyInstruction: return "Apply instruction"
            case .liveCoding: return "Live coding & design"
            }
        }
    }

    /// `apply-instruction` profiles. `cultural` is the skill's name for the behavioral profile.
    enum Profile: String, CaseIterable, Identifiable {
        case intro, tech, cultural
        var id: String { rawValue }
        var label: String {
            switch self {
            case .intro: return "Intro"
            case .tech: return "Tech"
            case .cultural: return "Behavioral"
            }
        }
    }

    enum ThreadState: Equatable { case none, opening, open, failed(String) }

    /// The preparation form (SPEC-13 §Preparation): what Start preparation will run.
    struct Draft: Equatable {
        var cvId: String?
        var jobDescription = ""
        /// ③ — chosen here, applied by Start preparation.
        var profile: Profile?
        /// ④ — optional.
        var liveCoding = false
    }

    @Published var draft: Draft
    @Published private(set) var threadState: ThreadState = .none
    @Published private(set) var record: InterviewRecord?
    @Published private(set) var streamingTurn: Int?
    @Published private(set) var status: String?

    private var recordingStart: (uuid: UUID, date: Date)?
    private var opening: Task<String?, Never>?

    let env: AppEnvironment
    var library: InterviewLibrary { env.library }
    var codex: CodexService { env.codex }
    private var engine: AnswerEngine { env.codex.engine }

    init(env: AppEnvironment) {
        self.env = env
        self.draft = Self.initialDraft(env: env)
    }

    /// Reopen a saved interview (history viewer) from the database: its record, and the ability to
    /// (re)generate the summary on the same thread — resumed first, since this run may not know it.
    init(env: AppEnvironment, existing rec: InterviewRecord) {
        self.env = env
        self.draft = Draft(cvId: rec.setup.documentIds.first, jobDescription: rec.setup.jdTextInline ?? "")
        self.record = rec
        self.threadState = rec.threadId == nil ? .none : .open
        self.needsResume = true
        self.summaryText = rec.summaryText ?? ""
    }

    /// The CV from the most recent interview, else the newest CV.
    private static func initialDraft(env: AppEnvironment) -> Draft {
        let lib = env.library
        var d = Draft()
        if let last = (try? env.store.allInterviews())?.first {
            let ids = Set(last.setup.documentIds)
            d.cvId = lib.documents(of: .cv).first { ids.contains($0.id) }?.id
            d.profile = last.activeProfile.flatMap(Profile.init(rawValue:))
            d.liveCoding = last.liveCodingActive
        }
        if d.cvId == nil { d.cvId = lib.documents(of: .cv).last?.id }
        return d
    }

    /// Briefing from a schema-1 record made with the old Prepare button (shown in history).
    var legacyBriefing: String { record?.prep.briefing ?? "" }

    // MARK: Thread

    var isOpen: Bool { threadState == .open }

    /// The interview's thread, opening it (and creating the record) on first need.
    @discardableResult
    func ensureThread() async -> String? {
        if let t = record?.threadId { return t }
        if let opening { return await opening.value }
        let task = Task { await self.openThread() }
        opening = task
        defer { opening = nil }
        return await task.value
    }

    private func openThread() async -> String? {
        guard record != nil || createRecord() else { return nil }
        threadState = .opening
        let cfg = env.config.interview
        let model = codex.resolvedModel(cfg.model)
        do {
            let thread = try await engine.startThread(ThreadConfig(
                model: model,
                baseInstructions: InterviewPrompt.baseInstructions(length: cfg.answerLength, custom: cfg.customInstructions)))
            record?.threadId = thread
            record?.model = model
            persist()
            threadState = .open
            return thread
        } catch {
            let message = (error as? EngineError)?.localizedDescription ?? error.localizedDescription
            threadState = .failed(message)
            status = message
            return nil
        }
    }

    private func createRecord() -> Bool {
        let cfg = env.config.interview
        var rec = InterviewRecord(
            name: Self.name(fromJD: draft.jobDescription), createdAt: TimeFormat.iso(Date()),
            model: codex.resolvedModel(cfg.model), reasoningEffort: cfg.reasoningEffort,
            setup: .init(documentIds: draft.cvId.map { [$0] } ?? [],
                         jdTextInline: draft.jobDescription.isEmpty ? nil : draft.jobDescription,
                         instructions: cfg.customInstructions, answerLength: cfg.answerLength.rawValue))
        if let start = recordingStart {
            rec.startedAt = TimeFormat.iso(start.date)
            rec.captureSessionUUID = start.uuid.uuidString
        }
        do {
            try env.store.saveInterview(rec)
        } catch {
            let message = "Could not save the interview: \(error.localizedDescription)"
            threadState = .failed(message); status = message
            return false
        }
        record = rec
        return true
    }

    /// "Interview", or the JD's first line (usually the role) — up to 60 characters.
    static func name(fromJD jd: String) -> String {
        let first = jd.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }
            .first { !$0.isEmpty } ?? ""
        return first.isEmpty ? "Interview" : String(first.prefix(60))
    }

    // MARK: Skill steps (SPEC-13)

    func skill(for step: Step) -> InterviewLibraryIndex.Skill? { library.skill(slug: step.rawValue) }

    /// Completed at least once on this thread.
    func isDone(_ step: Step) -> Bool {
        turns.contains { $0.kind == .skill && $0.status == .completed && $0.question.hasPrefix("/\(step.rawValue)") }
    }

    var activeProfile: Profile? { record?.activeProfile.flatMap(Profile.init(rawValue:)) }
    var liveCodingActive: Bool { record?.liveCodingActive ?? false }

    /// The four skills must be loaded in Settings → Skills before an interview (owner, 2026-10-02).
    var missingSkills: [Step] { Step.allCases.filter { skill(for: $0) == nil } }
    var allSkillsLoaded: Bool { missingSkills.isEmpty }

    /// Why a step can't run right now, or nil when it can.
    func blocker(_ step: Step) -> String? {
        if skill(for: step) == nil { return "Load the \(step.rawValue) skill in Settings → Skills" }
        switch step {
        case .discoveryCV: return draft.cvId == nil ? "Select or upload a CV" : nil
        case .discoveryJD:
            return draft.jobDescription.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "Paste the job description" : nil
        case .applyInstruction: return nil
        case .liveCoding: return activeProfile == .tech ? nil : "Apply the Tech profile first"
        }
    }

    /// Run one skill step as a turn on the thread.
    func run(_ step: Step, profile: Profile? = nil) async {
        guard blocker(step) == nil, let skill = skill(for: step) else { return }
        guard await ensureThread() != nil else { return }
        let slug = step.rawValue
        let command = profile.map { "/\(slug) \($0.rawValue)" } ?? "/\(slug)"
        let definition = (record?.skillsReceived.contains(slug) ?? false) ? nil : library.promptSkill(skill.id)
        var attachments: [InterviewPrompt.Attachment] = []
        switch step {
        case .discoveryCV:
            if let id = draft.cvId {
                let text = library.text(of: id)
                attachments.append(.init(title: "MY CV", text: text))
                if record?.setup.documentIds.contains(id) == false { record?.setup.documentIds.insert(id, at: 0) }
                // Snapshot it: history shows the CV the coach saw, whatever happens to the library.
                record?.setup.cvTitle = library.document(id)?.title
                record?.cvText = text
            }
        case .discoveryJD:
            attachments.append(.init(title: "JOB DESCRIPTION", text: draft.jobDescription))
            record?.setup.jdTextInline = draft.jobDescription
            if record?.name == "Interview" { record?.name = Self.name(fromJD: draft.jobDescription) }
        case .applyInstruction, .liveCoding:
            break
        }
        if let id = definitionSkillId(skill) { record?.setup.skillIds.append(id) }
        persist()
        await submit(Request(kind: .skill,
                             text: InterviewPrompt.skillMessage(command: command, definition: definition,
                                                                attachments: attachments),
                             question: command, effort: env.config.interview.prepReasoningEffort))
    }

    private func definitionSkillId(_ skill: InterviewLibraryIndex.Skill) -> String? {
        (record?.setup.skillIds.contains(skill.id) ?? true) ? nil : skill.id
    }

    // MARK: Start preparation (owner, 2026-10-02)

    @Published private(set) var preparing = false
    /// The step Start preparation stopped at, so Continue can resume there.
    @Published private(set) var preparationFailedAt: Step?

    /// What Start preparation runs, in order: ① ② ③ and, when ticked, ④.
    var preparationPlan: [(step: Step, profile: Profile?)] {
        var plan: [(Step, Profile?)] = [(.discoveryCV, nil), (.discoveryJD, nil), (.applyInstruction, draft.profile)]
        if draft.liveCoding { plan.append((.liveCoding, nil)) }
        return plan
    }

    /// Why Start preparation can't run yet, or nil when it can.
    var preparationBlocker: String? {
        if !allSkillsLoaded { return "Load the 4 skills in Settings → Interview → Skills" }
        if draft.cvId == nil { return "Choose or upload a CV (①)" }
        if draft.jobDescription.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return "Paste the job description (②)" }
        if draft.profile == nil { return "Choose a mode (③)" }
        if draft.liveCoding && draft.profile != .tech { return "Live coding needs the Tech mode (③)" }
        return nil
    }

    /// Every planned step has completed on this thread, with the chosen mode and live-coding state.
    var isPrepared: Bool {
        isDone(.discoveryCV) && isDone(.discoveryJD) && activeProfile == draft.profile
            && draft.profile != nil && liveCodingActive == draft.liveCoding
    }

    /// Run the plan in order; stop at the first step that doesn't complete. `resume` continues
    /// from the failed step instead of starting over.
    func startPreparation(resume: Bool = false) async {
        guard preparationBlocker == nil, !preparing else { return }
        preparing = true
        defer { preparing = false }
        var plan = preparationPlan
        if resume, let failed = preparationFailedAt, let i = plan.firstIndex(where: { $0.step == failed }) {
            plan = Array(plan[i...])
        }
        preparationFailedAt = nil
        for (step, profile) in plan {
            let before = turns.count
            await run(step, profile: profile)
            let turn = turns.count > before ? turns.last : nil
            guard turn?.status == .completed else {
                preparationFailedAt = step
                status = "\(step.title) didn't finish" + (turn?.error.map { ": \($0)" } ?? ".")
                return
            }
        }
        status = nil
    }

    /// Which part Start preparation is running now (for the panel's progress marks).
    var runningStep: Step? {
        guard isStreaming, let q = turns.last?.question, turns.last?.kind == .skill,
              let name = InterviewRecord.skillName(q) else { return nil }
        return Step(rawValue: name)
    }

    /// History and wrap-up: the CV and JD this interview used.
    var cvText: String { record?.cvText ?? record?.setup.documentIds.first.map(library.text(of:)) ?? "" }
    var cvTitle: String? { record?.setup.cvTitle ?? record?.setup.documentIds.first.flatMap { library.document($0)?.title } }
    var jdText: String { record?.setup.jdTextInline ?? "" }

    /// After End interview: a follow-up prompt on the same thread (SPEC-15 §Ending the interview).
    func sendFollowUp(_ text: String) async { await sendTyped(text) }

    /// Setup → Upload…: import a CV file into the library and select it.
    func uploadCV(from url: URL) throws {
        let doc = try library.importDocument(from: url, kind: .cv)
        draft.cvId = doc.id
    }

    /// Start over: drop an interview that never started recording — its rows and its thread.
    func discardUnstarted() async {
        guard let rec = record, rec.startedAt == nil, recordingStart == nil else { return }
        try? env.store.deleteInterview(id: rec.id)
        if let thread = rec.threadId { await engine.archiveThread(id: thread) }
        record = nil; threadState = .none; pendingImages = []
    }

    // MARK: End of interview (SPEC-15)

    @Published private(set) var summaryText = ""
    @Published private(set) var summarizing = false
    @Published private(set) var summaryError: String?
    private var needsResume = false

    var isFinished: Bool { record?.endedAt != nil }

    /// After End interview has saved the transcript: link the record to its session row and let a
    /// streaming answer finish (≤ 30 s). Never delays the save. Summarizing is the user's choice.
    func sessionSaved(sessionId: Int64?, transcript: String) async {
        guard record != nil else { return }
        record?.endedAt = TimeFormat.iso(Date())
        record?.sessionId = sessionId
        record?.transcript = transcript
        persist()
        if let sessionId {
            try? env.store.markInterview(sessionId: sessionId)
            NotificationCenter.default.post(name: .sessionsChanged, object: nil)
        }

        queued = nil
        let deadline = Date().addingTimeInterval(30)
        while isStreaming, Date() < deadline { try? await Task.sleep(nanoseconds: 200_000_000) }
        if isStreaming {
            await stopStreaming()
            let grace = Date().addingTimeInterval(2.5)
            while isStreaming, Date() < grace { try? await Task.sleep(nanoseconds: 50_000_000) }
        }

        // No automatic summary: End interview asks the user to summarize or send a follow-up.
    }

    /// The summary turn (SPEC-15 §Summary message) → `summary.md`. Retryable.
    func generateSummary(transcript: String) async {
        guard let thread = record?.threadId, !summarizing else { return }
        summarizing = true
        summaryError = nil
        summaryText = ""
        record?.summary.status = .running
        persist()
        defer { summarizing = false }

        if needsResume, let rec = record {
            let length = Config.Interview.AnswerLength(rawValue: rec.setup.answerLength) ?? .medium
            do {
                try await engine.resumeThread(id: thread, ThreadConfig(
                    model: rec.model,
                    baseInstructions: InterviewPrompt.baseInstructions(length: length, custom: rec.setup.instructions)))
                needsResume = false
            } catch {
                failSummary(error.localizedDescription); return
            }
        }

        let message = InterviewPrompt.summary(transcript: transcript)
        for await event in streamEvents(engine.send(threadId: thread, input: [.text(message)],
                                                    effort: env.config.interview.prepReasoningEffort,
                                                    model: modelSwitch())) {
            switch event {
            case .delta(let d): summaryText += d
            case .completed(let full):
                summaryText = full
                record?.summaryText = full
                record?.summary = .init(status: .done, completedAt: TimeFormat.iso(Date()))
                persist()
            case .interrupted:
                failSummary("The summary was interrupted.")
            case .failed(let e, _):
                failSummary(e.localizedDescription)
            case .started, .thinking, .slow:
                break
            }
        }
    }

    private func failSummary(_ message: String) {
        summaryError = message
        record?.summary.status = .failed
        persist()
    }

    /// The session screen starts a new recording after Results: start a fresh interview.
    func resetForNewInterview() {
        record = nil; threadState = .none; summaryText = ""; summaryError = nil; pendingImages = []
        mark = nil; queued = nil; recordingStart = nil; status = nil
        draft = Self.initialDraft(env: env)
    }

    // MARK: Recording hooks

    /// Start in Interview mode: link the capture and open the thread now, so the cold first turn
    /// is paid before the first question (SPEC-13 §The thread).
    func recordingStarted(uuid: UUID, at date: Date) {
        recordingStart = (uuid, date)
        if record != nil {
            record?.startedAt = TimeFormat.iso(date)
            record?.captureSessionUUID = uuid.uuidString
            persist()
        }
        Task { await ensureThread() }
    }

    // MARK: Turns

    /// Turns shown in the Answers panel.
    var turns: [InterviewRecord.Turn] { record?.turns ?? [] }
    var isStreaming: Bool { streamingTurn != nil }

    /// Send one turn on the interview's thread and stream its answer into the record.
    /// Returns when the turn ends.
    @discardableResult
    func runTurn(kind: InterviewRecord.TurnKind, text: String, question: String,
                 images pngs: [Data] = [], span: (from: Int, to: Int)? = nil, effort: String? = nil,
                 onAccepted: (() -> Void)? = nil) async -> InterviewRecord.TurnStatus? {
        guard let thread = await ensureThread(), var rec = record else { return nil }
        let n = rec.nextTurnNumber
        // Screenshots are kept in the database; Codex reads them from short-lived files.
        let names = pngs.indices.map { Store.imageName(turn: n, index: $0 + 1) }
        for (name, png) in zip(names, pngs) {
            try? env.store.addInterviewImage(interviewId: rec.id, name: name, turn: n, png: png)
        }
        let files = Self.writeOutbox(pngs)
        defer { files.forEach { try? FileManager.default.removeItem(at: $0) } }
        rec.turns.append(.init(n: n, kind: kind, question: question, audioFromMs: span?.from, audioToMs: span?.to,
                               images: names, askedAt: TimeFormat.iso(Date())))
        record = rec
        streamingTurn = n
        persist()

        let t0 = Date()
        let input: [CodexRPC.Input] = [.text(text)] + files.map { .localImage(path: $0.path) }
        var final: InterviewRecord.TurnStatus = .failed
        for await event in streamEvents(engine.send(threadId: thread, input: input,
                                                    effort: effort ?? env.config.interview.reasoningEffort,
                                                    model: modelSwitch())) {
            switch event {
            case .delta(let d):
                update(n) { t in
                    if t.ttftMs == nil { t.ttftMs = Int(Date().timeIntervalSince(t0) * 1000) }
                    t.answer += d
                }
            case .completed(let full):
                update(n) { t in t.answer = full; t.status = .completed; t.totalMs = Int(Date().timeIntervalSince(t0) * 1000) }
                final = .completed
            case .interrupted(let partial):
                update(n) { t in t.answer = partial; t.status = .interrupted; t.totalMs = Int(Date().timeIntervalSince(t0) * 1000) }
                final = .interrupted
            case .failed(let e, let partial):
                update(n) { t in
                    t.answer = partial; t.status = .failed; t.error = e.localizedDescription
                    t.totalMs = Int(Date().timeIntervalSince(t0) * 1000)
                }
                final = .failed
            case .slow:
                status = "Still thinking…"
            case .started:
                onAccepted?()
            case .thinking:
                break
            }
        }
        if streamingTurn == n { streamingTurn = nil }
        status = nil
        persist()
        return final
    }

    // MARK: Asking (SPEC-14)

    /// The live transcript at press time; set by the session screen.
    var transcriptSource: (() -> (segments: [AskSelection.Segment], interim: String, audioMs: Int))?
    private var mark: AskSelection.Mark?
    private var queued: Request?

    /// One turn to send, already built at press time.
    struct Request {
        var kind: InterviewRecord.TurnKind
        var text: String
        var question: String
        var images: [Data] = []
        var span: (from: Int, to: Int)?
        var onAccepted: (() -> Void)?
        /// Skill steps use `prep_reasoning_effort`; nil = `reasoning_effort`.
        var effort: String?
        /// For merging queued asks: the transcript text behind `text`.
        var askText = ""
    }

    // MARK: Screenshots (SPEC-14 §Screenshots)

    /// A screenshot waiting in the current prompt.
    struct PendingImage: Identifiable, Equatable {
        let id = UUID()
        let png: Data
    }

    /// Screenshots added to the current prompt; the next Ask or Send takes them all.
    @Published private(set) var pendingImages: [PendingImage] = []
    static let maxPendingImages = 10
    /// Where the clipboard is watched; injectable for tests.
    var pasteboard: NSPasteboard = .general
    private var seenChangeCount: Int?
    private var capturedChangeCount: Int?

    /// Called twice a second while the interview panel is on screen. With
    /// `include_clipboard_images` on, every new image on the clipboard — a ⌘⌃⇧4 screenshot —
    /// is added to the current prompt. What was already there when watching began is left alone.
    func pollClipboard() {
        let count = pasteboard.changeCount
        guard let seen = seenChangeCount else { seenChangeCount = count; return }
        guard count != seen else { return }
        seenChangeCount = count
        guard env.config.interview.includeClipboardImages else { return }
        let room = Self.maxPendingImages - pendingImages.count
        let snap = ClipboardImages.read(pasteboard)
        guard !snap.images.isEmpty else { return }
        guard room > 0 else { status = "The prompt already has \(Self.maxPendingImages) screenshots."; return }
        let added = snap.images.prefix(room).map { PendingImage(png: $0) }
        pendingImages += added
        capturedChangeCount = count
        status = "Screenshot added — \(pendingImages.count) in this prompt"
    }

    func removePending(_ id: UUID) { pendingImages.removeAll { $0.id == id } }
    func clearPending() { pendingImages = [] }

    /// Take the tray for a send: the images, plus a hook that clears the last screenshot from the
    /// clipboard once Codex accepts the turn (if that setting is on and nothing new was copied).
    private func takePending() -> (images: [Data], onAccepted: (() -> Void)?) {
        let images = pendingImages.map(\.png)
        pendingImages = []
        guard !images.isEmpty else { return ([], nil) }
        if !codex.acceptsImages(record?.model ?? env.config.interview.effectiveModel) {
            status = "This model can't read images — sending the text only."
            return ([], nil)
        }
        guard env.config.interview.clearClipboardImagesAfterSend, let captured = capturedChangeCount else { return (images, nil) }
        let pb = pasteboard
        return (images, { [weak self] in
            guard pb.changeCount == captured else { return }
            pb.clearContents()
            self?.seenChangeCount = pb.changeCount
        })
    }

    /// The Ask hotkey / button: the interviewer's latest words plus every pending screenshot.
    func ask() async {
        let cfg = env.config.interview
        let src = transcriptSource?() ?? (segments: [], interim: "", audioMs: 0)
        let sel = AskSelection.select(segments: src.segments, interim: src.interim, mode: cfg.askMode,
                                      mark: mark, pressAudioMs: src.audioMs, maxWords: cfg.clampedMaxWords)
        mark = sel.mark   // advances on every press, in both modes
        guard !sel.text.isEmpty || !pendingImages.isEmpty else {
            status = "Nothing new since your last ask"
            return
        }
        guard await ensureThread() != nil else { NSSound.beep(); return }
        let pending = takePending()
        await submit(Request(kind: .ask, text: InterviewPrompt.ask(sel.text, imageCount: pending.images.count),
                             question: sel.text.isEmpty ? "(screenshot)" : sel.text, images: pending.images,
                             span: (sel.fromMs, sel.toMs), onAccepted: pending.onAccepted, askText: sel.text))
    }

    /// The Send button: typed text plus every pending screenshot.
    func sendTyped(_ text: String) async {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty || !pendingImages.isEmpty else { return }
        let pending = takePending()
        let body = t.isEmpty ? "(see the attached screenshot(s))" : t
        let message = pending.images.isEmpty ? body : body + "\n(\(pending.images.count) screenshot(s) attached.)"
        await submit(Request(kind: .typed, text: message, question: t.isEmpty ? "(screenshot)" : t,
                             images: pending.images, onAccepted: pending.onAccepted))
    }


    /// A different answer to the latest question (latest card only).
    func regenerate() async {
        guard let last = turns.last(where: { $0.kind != .skill }) else { return }
        await submit(Request(kind: .regenerate, text: InterviewPrompt.regenerate,
                             question: "Another answer: \(last.question)"))
    }

    /// One turn at a time per thread; what happens to a new request while an answer is still
    /// streaming is `interview.busy_policy` (SPEC-14 §Busy policy).
    func submit(_ request: Request) async {
        if isStreaming {
            switch env.config.interview.busyPolicy {
            case .interrupt:
                await stopStreaming()
                let deadline = Date().addingTimeInterval(2.5)
                while isStreaming, Date() < deadline { try? await Task.sleep(nanoseconds: 50_000_000) }
            case .queue:
                queued = merge(queued, request)
                status = "Queued — sends when this answer finishes."
                return
            }
        }
        var next: Request? = request
        while let r = next {
            await runTurn(kind: r.kind, text: r.text, question: r.question, images: r.images,
                          span: r.span, effort: r.effort, onAccepted: r.onAccepted)
            next = queued
            queued = nil
        }
    }

    /// A further Ask merges into the queued one; anything else replaces it.
    private func merge(_ old: Request?, _ new: Request) -> Request {
        guard let old, old.kind == .ask, new.kind == .ask else { return new }
        let text = [old.askText, new.askText].filter { !$0.isEmpty }.joined(separator: " ")
        let images = old.images + new.images
        let both = [old.onAccepted, new.onAccepted].compactMap { $0 }
        return Request(kind: .ask, text: InterviewPrompt.ask(text, imageCount: images.count),
                       question: text.isEmpty ? "(screenshot)" : text, images: images,
                       span: (old.span?.from ?? new.span?.from ?? 0, new.span?.to ?? old.span?.to ?? 0),
                       onAccepted: both.isEmpty ? nil : { both.forEach { $0() } }, askText: text)
    }

    func stopStreaming() async {
        guard let thread = record?.threadId, isStreaming else { return }
        await engine.interrupt(threadId: thread)
    }

    /// The model picker changed since the thread started → send the new model with this turn
    /// (Codex keeps it for later turns). Nil when unchanged.
    private func modelSwitch() -> String? {
        let wanted = codex.resolvedModel(env.config.interview.model)
        guard let current = record?.model, wanted != current else { return nil }
        record?.model = wanted
        return wanted
    }

    /// Short-lived PNG files for Codex's `localImage` input; deleted when the turn ends.
    private static func writeOutbox(_ pngs: [Data]) -> [URL] {
        guard !pngs.isEmpty else { return [] }
        let dir = AppPaths.interview.appendingPathComponent("outbox", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return pngs.compactMap { png in
            let url = dir.appendingPathComponent("\(UUID().uuidString).png")
            return (try? png.write(to: url, options: .atomic)) == nil ? nil : url
        }
    }

    /// A screenshot stored with this interview, for thumbnails and "what was sent".
    func image(named name: String) -> NSImage? {
        guard let id = record?.id, let data = try? env.store.interviewImage(interviewId: id, name: name) else { return nil }
        return NSImage(data: data)
    }

    private func update(_ n: Int, _ change: (inout InterviewRecord.Turn) -> Void) {
        guard let i = record?.turns.firstIndex(where: { $0.n == n }) else { return }
        change(&record!.turns[i])
    }

    /// Engine events, with stream errors folded into `.failed`.
    private func streamEvents(_ stream: AsyncThrowingStream<AnswerEvent, Error>) -> AsyncStream<AnswerEvent> {
        AsyncStream { continuation in
            let task = Task {
                do { for try await e in stream { continuation.yield(e) } }
                catch { continuation.yield(.failed(.other(error.localizedDescription), partial: "")) }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    // MARK: Persistence

    func persist() {
        guard let rec = record else { return }
        do { try env.store.saveInterview(rec) }
        catch { status = "Could not save the interview record: \(error.localizedDescription)" }
    }
}
