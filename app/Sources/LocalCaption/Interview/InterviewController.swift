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

    /// The setup form (SPEC-13 §Interview panel).
    struct Draft: Equatable {
        var cvId: String?
        var jobDescription = ""
    }

    @Published var draft: Draft
    @Published private(set) var threadState: ThreadState = .none
    @Published private(set) var record: InterviewRecord?
    @Published private(set) var streamingTurn: Int?
    @Published private(set) var status: String?

    private(set) var folder: URL?
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

    /// Reopen a saved interview (history viewer): its record, and the ability to (re)generate
    /// the summary on the same thread — resumed first, since this app run may not know it.
    init(env: AppEnvironment, existing folder: URL) throws {
        self.env = env
        let rec = try InterviewFiles.read(from: folder)
        self.draft = Draft(cvId: rec.setup.documentIds.first, jobDescription: rec.setup.jdTextInline ?? "")
        self.folder = folder
        self.record = rec
        self.threadState = rec.threadId == nil ? .none : .open
        self.needsResume = true
        self.summaryText = (try? String(contentsOf: folder.appendingPathComponent(InterviewFiles.summaryName),
                                        encoding: .utf8)) ?? ""
    }

    /// The CV from the most recent interview, else the newest CV.
    private static func initialDraft(env: AppEnvironment) -> Draft {
        let lib = env.library
        var d = Draft()
        if let last = InterviewFiles.all(in: env.interviewsRoot).first?.record {
            let ids = Set(last.setup.documentIds)
            d.cvId = lib.documents(of: .cv).first { ids.contains($0.id) }?.id
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
            folder = try InterviewFiles.makeFolder(in: env.interviewsRoot, date: Date(), name: rec.name)
        } catch {
            let message = "Could not create the interview folder: \(error.localizedDescription)"
            threadState = .failed(message); status = message
            return false
        }
        record = rec
        persist()
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
                if let folder { try? Data(text.utf8).write(to: folder.appendingPathComponent(InterviewFiles.cvName), options: .atomic) }
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

    /// Part ④ checkbox: on runs `/live-coding-design`; off re-applies the current profile, which
    /// (per the apply-instruction skill) replaces the live-coding activation.
    func setLiveCoding(_ on: Bool) async {
        if on {
            guard !liveCodingActive else { return }
            await run(.liveCoding)
        } else if liveCodingActive, let profile = activeProfile {
            await run(.applyInstruction, profile: profile)
        }
    }

    /// History and wrap-up: the CV and JD this interview used.
    var cvText: String {
        if let folder, let s = try? String(contentsOf: folder.appendingPathComponent(InterviewFiles.cvName), encoding: .utf8) {
            return s
        }
        return record?.setup.documentIds.first.map(library.text(of:)) ?? ""
    }
    var cvTitle: String? { record?.setup.cvTitle ?? record?.setup.documentIds.first.flatMap { library.document($0)?.title } }
    var jdText: String { record?.setup.jdTextInline ?? "" }

    /// After End interview: a follow-up prompt on the same thread (SPEC-15 §Ending the interview).
    func sendFollowUp(_ text: String) async { await sendTyped(text) }

    /// Setup → Upload…: import a CV file into the library and select it.
    func uploadCV(from url: URL) throws {
        let doc = try library.importDocument(from: url, kind: .cv)
        draft.cvId = doc.id
    }

    /// New Session: drop an interview that never started recording — folder and thread.
    func discardUnstarted() async {
        guard let rec = record, rec.startedAt == nil, recordingStart == nil else { return }
        if let folder { try? FileManager.default.removeItem(at: folder) }
        if let thread = rec.threadId { await engine.archiveThread(id: thread) }
        record = nil; folder = nil; threadState = .none
    }

    // MARK: End of interview (SPEC-15)

    @Published private(set) var summaryText = ""
    @Published private(set) var summarizing = false
    @Published private(set) var summaryError: String?
    private var needsResume = false

    var isFinished: Bool { record?.endedAt != nil }

    /// After End interview has saved the transcript: link the record to its session row and let a
    /// streaming answer finish (≤ 30 s). Never delays the save. Summarizing is the user's choice.
    func sessionSaved(sessionId: Int64?) async {
        guard record != nil else { return }
        record?.endedAt = TimeFormat.iso(Date())
        record?.sessionId = sessionId
        if let sessionId, let folder {
            try? env.store.setInterview(id: sessionId, dir: folder.path)
            NotificationCenter.default.post(name: .sessionsChanged, object: nil)
        }
        persist()

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
        guard let thread = record?.threadId, let folder, !summarizing else { return }
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
                                                    effort: env.config.interview.prepReasoningEffort)) {
            switch event {
            case .delta(let d): summaryText += d
            case .completed(let full):
                summaryText = full
                do {
                    try Data(full.utf8).write(to: folder.appendingPathComponent(InterviewFiles.summaryName), options: .atomic)
                    record?.summary = .init(status: .done, file: InterviewFiles.summaryName, completedAt: TimeFormat.iso(Date()))
                    persist()
                } catch {
                    failSummary("Could not save the summary: \(error.localizedDescription)")
                }
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
        record = nil; folder = nil; threadState = .none; summaryText = ""; summaryError = nil
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
                 images: [URL] = [], span: (from: Int, to: Int)? = nil, effort: String? = nil,
                 onAccepted: (() -> Void)? = nil) async -> InterviewRecord.TurnStatus? {
        guard let thread = await ensureThread(), var rec = record else { return nil }
        let n = rec.nextTurnNumber
        let relImages = images.map { "\(InterviewFiles.attachmentsName)/\($0.lastPathComponent)" }
        rec.turns.append(.init(n: n, kind: kind, question: question, audioFromMs: span?.from, audioToMs: span?.to,
                               images: relImages, askedAt: TimeFormat.iso(Date())))
        record = rec
        streamingTurn = n
        persist()

        let t0 = Date()
        let input: [CodexRPC.Input] = [.text(text)] + images.map { .localImage(path: $0.path) }
        var final: InterviewRecord.TurnStatus = .failed
        for await event in streamEvents(engine.send(threadId: thread, input: input,
                                                    effort: effort ?? env.config.interview.reasoningEffort)) {
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
    @Published private(set) var clipboardImageCount = 0
    private var clipboardChangeCount = -1
    private var mark: AskSelection.Mark?
    private var queued: Request?

    /// One turn to send, already built at press time.
    struct Request {
        var kind: InterviewRecord.TurnKind
        var text: String
        var question: String
        var images: [URL] = []
        var span: (from: Int, to: Int)?
        var onAccepted: (() -> Void)?
        /// Skill steps use `prep_reasoning_effort`; nil = `reasoning_effort`.
        var effort: String?
        /// For merging queued asks: the transcript text and screenshot count behind `text`.
        var askText = ""
    }

    /// The Ask hotkey / button.
    func ask() async {
        let cfg = env.config.interview
        let src = transcriptSource?() ?? (segments: [], interim: "", audioMs: 0)
        let sel = AskSelection.select(segments: src.segments, interim: src.interim, mode: cfg.askMode,
                                      mark: mark, pressAudioMs: src.audioMs, maxWords: cfg.clampedMaxWords)
        mark = sel.mark   // advances on every press, in both modes

        var snapshot: ClipboardImages.Snapshot?
        if cfg.includeClipboardImages {
            let s = ClipboardImages.read()
            if !s.images.isEmpty, !codex.acceptsImages(record?.model ?? cfg.effectiveModel) {
                status = "This model can't read images — sending the text only."
            } else if !s.images.isEmpty {
                snapshot = s
                if s.skipped > 0 { status = "\(s.skipped) image(s) left out (too large or over the limit of \(ClipboardImages.maxImages))." }
            }
        }
        let images = snapshot?.images.count ?? 0
        guard !sel.text.isEmpty || images > 0 else {
            status = "Nothing new since your last ask"
            return
        }
        guard await ensureThread() != nil else { NSSound.beep(); return }

        var urls: [URL] = []
        if let snapshot, let folder {
            let turn = record?.nextTurnNumber ?? 1   // a queued ask still becomes the next turn
            let offset = queued?.images.count ?? 0
            do {
                urls = try ClipboardImages.save(snapshot, turn: turn, to: folder.appendingPathComponent(InterviewFiles.attachmentsName))
                if offset > 0 {   // a merged queued ask keeps every file name unique
                    urls = try urls.enumerated().map { i, u in
                        let dest = u.deletingLastPathComponent().appendingPathComponent("\(turn)-\(offset + i + 1).png")
                        try? FileManager.default.removeItem(at: dest)
                        try FileManager.default.moveItem(at: u, to: dest)
                        return dest
                    }
                }
            } catch {
                status = "Couldn't save the screenshot: \(error.localizedDescription)"
                urls = []
            }
        }
        let clear = cfg.clearClipboardImagesAfterSend
        let accepted: (() -> Void)? = (snapshot != nil && clear && !urls.isEmpty) ? {
            if ClipboardImages.removeImages(after: snapshot!) { self.clipboardImageCount = 0 }
        } : nil
        await submit(Request(kind: .ask, text: InterviewPrompt.ask(sel.text, imageCount: urls.count),
                             question: sel.text.isEmpty ? "(screenshot)" : sel.text, images: urls,
                             span: (sel.fromMs, sel.toMs), onAccepted: accepted, askText: sel.text))
    }

    func sendTyped(_ text: String) async {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty else { return }
        await submit(Request(kind: .typed, text: t, question: t))
    }

    func sendQuick(_ prompt: Config.Interview.QuickPrompt) async {
        await submit(Request(kind: .quick, text: prompt.text, question: prompt.label))
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

    /// Ask-button badge: image count from clipboard *types* only, re-read when the clipboard changes.
    func refreshClipboardBadge() {
        guard env.config.interview.includeClipboardImages else { clipboardImageCount = 0; return }
        let pb = NSPasteboard.general
        guard pb.changeCount != clipboardChangeCount else { return }
        clipboardChangeCount = pb.changeCount
        clipboardImageCount = min(ClipboardImages.imageCount(pb), ClipboardImages.maxImages)
    }

    func stopStreaming() async {
        guard let thread = record?.threadId, isStreaming else { return }
        await engine.interrupt(threadId: thread)
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
        guard let rec = record, let folder else { return }
        do { try InterviewFiles.write(rec, to: folder) }
        catch { status = "Could not save the interview record: \(error.localizedDescription)" }
    }
}
