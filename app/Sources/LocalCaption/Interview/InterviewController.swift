import Foundation
import SwiftUI
import AppKit
import LocalCaptionKit

/// One interview (SPEC-13–15): the setup draft, the Prepare step, and every turn on the
/// interview's single Codex thread. Persists `interview.json` after every state change.
@MainActor
final class InterviewController: ObservableObject {
    enum PrepState: Equatable {
        case notPrepared, preparing, ready, failed(String)
        var isReady: Bool { self == .ready }
    }

    /// The Prepare panel's form (SPEC-13 §Prepare panel).
    struct Draft: Equatable {
        var name = ""
        var company = ""
        var role = ""
        var cvId: String?
        var jdId: String?
        var jdPaste = ""
        var usePastedJD = false
        var noteIds: Set<String> = []
        var skillIds: Set<String> = []
        var instructions = ""
        var answerLength: Config.Interview.AnswerLength = .medium
        var model = ""
        var effort = "low"

        var displayName: String {
            let n = name.trimmingCharacters(in: .whitespaces)
            if !n.isEmpty { return n }
            let parts = [company, role].map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            return parts.isEmpty ? "Interview" : parts.joined(separator: " — ")
        }
    }

    /// Prep message size limit (SPEC-13): over this, Prepare is disabled.
    static let maxPrepChars = 150_000

    @Published var draft: Draft
    @Published private(set) var prepState: PrepState = .notPrepared
    @Published private(set) var record: InterviewRecord?
    @Published private(set) var briefing = ""
    @Published private(set) var streamingTurn: Int?
    @Published private(set) var status: String?

    private(set) var folder: URL?
    private var preparedDraft: Draft?
    private var recordingStart: (uuid: UUID, date: Date)?

    let env: AppEnvironment
    var library: InterviewLibrary { env.library }
    var codex: CodexService { env.codex }
    private var engine: AnswerEngine { env.codex.engine }

    init(env: AppEnvironment) {
        self.env = env
        self.draft = Self.initialDraft(env: env)
    }

    // MARK: Draft

    /// Prefill from the most recent interview, else from config (SPEC-13 §Prepare panel).
    private static func initialDraft(env: AppEnvironment) -> Draft {
        let cfg = env.config.interview
        var d = Draft(instructions: cfg.customInstructions, answerLength: cfg.answerLength,
                      model: cfg.effectiveModel, effort: cfg.reasoningEffort)
        let lib = env.library
        if let last = InterviewFiles.all(in: env.interviewsRoot).first?.record {
            d.company = last.setup.company
            d.role = last.setup.role
            let ids = Set(last.setup.documentIds)
            d.cvId = lib.documents(of: .cv).first { ids.contains($0.id) }?.id
            d.skillIds = Set(last.setup.skillIds.filter { lib.skill($0) != nil })
        }
        if d.cvId == nil { d.cvId = lib.documents(of: .cv).last?.id }
        return d
    }

    var setupChangedSinceReady: Bool {
        guard prepState.isReady || prepState == .preparing, let p = preparedDraft else { return false }
        return p != draft
    }

    var jobDescriptionText: String {
        draft.usePastedJD ? draft.jdPaste : (draft.jdId.map(library.text(of:)) ?? "")
    }

    func promptSetup() -> InterviewPrompt.Setup {
        InterviewPrompt.Setup(
            company: draft.company, role: draft.role, instructions: draft.instructions,
            skills: library.skills.filter { draft.skillIds.contains($0.id) }.compactMap { library.promptSkill($0.id) },
            cv: draft.cvId.map(library.text(of:)) ?? "",
            jobDescription: jobDescriptionText,
            notes: library.documents(of: .notes).filter { draft.noteIds.contains($0.id) }
                .map { .init(title: $0.title, text: library.text(of: $0.id)) })
    }

    var prepMessage: String { InterviewPrompt.prepMessage(promptSetup()) }
    var prepChars: Int { prepMessage.count }
    var oversizedSkills: [String] {
        library.skills.filter { draft.skillIds.contains($0.id) && $0.chars > SkillFile.maxChars }.map(\.title)
    }

    var canPrepare: Bool {
        codex.isReady && prepState != .preparing && prepChars <= Self.maxPrepChars
    }

    // MARK: Prepare

    func prepare() async {
        guard canPrepare else { return }
        let snapshot = draft
        await discardUnstarted()                 // a re-prepare starts a new thread

        let model = codex.resolvedModel(snapshot.model)
        var rec = InterviewRecord(
            name: snapshot.displayName, createdAt: TimeFormat.iso(Date()), model: model,
            reasoningEffort: snapshot.effort,
            setup: .init(company: snapshot.company, role: snapshot.role,
                         skillIds: Array(snapshot.skillIds).sorted(),
                         documentIds: ([snapshot.cvId, snapshot.usePastedJD ? nil : snapshot.jdId].compactMap { $0 }
                                       + Array(snapshot.noteIds).sorted()),
                         jdTextInline: snapshot.usePastedJD ? snapshot.jdPaste : nil,
                         instructions: snapshot.instructions,
                         answerLength: snapshot.answerLength.rawValue))
        if let start = recordingStart {
            rec.startedAt = TimeFormat.iso(start.date)
            rec.captureSessionUUID = start.uuid.uuidString
        }
        do {
            folder = try InterviewFiles.makeFolder(in: env.interviewsRoot, date: Date(), name: rec.name)
        } catch {
            prepState = .failed("Could not create the interview folder: \(error.localizedDescription)")
            return
        }
        rec.prep.status = .running
        record = rec
        preparedDraft = snapshot
        briefing = ""
        prepState = .preparing
        persist()

        do {
            let thread = try await engine.startThread(ThreadConfig(
                model: model, baseInstructions: InterviewPrompt.baseInstructions(length: snapshot.answerLength)))
            record?.threadId = thread
            persist()
        } catch {
            failPrep(error.localizedDescription); return
        }
        await runPrepTurn(message: InterviewPrompt.prepMessage(promptSetup()))
    }

    func retryPrepare() async {
        guard case .failed = prepState else { return }
        if record?.threadId != nil, record?.prep.status == .failed {
            prepState = .preparing
            record?.prep.status = .running
            persist()
            await runPrepTurn(message: prepMessage)
        } else {
            await prepare()
        }
    }

    private func runPrepTurn(message: String) async {
        guard let thread = record?.threadId else { return }
        let effort = env.config.interview.prepReasoningEffort
        var text = ""
        for await event in streamEvents(engine.send(threadId: thread, input: [.text(message)], effort: effort)) {
            switch event {
            case .delta(let d): text += d; briefing = text
            case .completed(let full):
                briefing = full
                record?.prep = .init(status: .done, briefing: full, extraTurns: record?.prep.extraTurns ?? 0,
                                     completedAt: TimeFormat.iso(Date()))
                prepState = .ready
                persist()
            case .interrupted(let partial):
                failPrep("Preparation was interrupted.", partial: partial)
            case .failed(let e, let partial):
                failPrep(e.localizedDescription, partial: partial)
            case .started, .thinking, .slow:
                break
            }
        }
    }

    private func failPrep(_ message: String, partial: String = "") {
        if !partial.isEmpty { briefing = partial }
        record?.prep.status = .failed
        record?.prep.briefing = partial
        prepState = .failed(message)
        persist()
    }

    /// Remove a prepared interview that never started recording (SPEC-13: "deleted if never
    /// started") — its folder and its Codex thread.
    func discardUnstarted() async {
        guard let rec = record, rec.startedAt == nil, recordingStart == nil else { return }
        if let folder { try? FileManager.default.removeItem(at: folder) }
        if let thread = rec.threadId { await engine.archiveThread(id: thread) }
        record = nil; folder = nil; preparedDraft = nil; briefing = ""
        prepState = .notPrepared
    }

    // MARK: Recording hooks

    func recordingStarted(uuid: UUID, at date: Date) {
        recordingStart = (uuid, date)
        guard record != nil else { return }
        record?.startedAt = TimeFormat.iso(date)
        record?.captureSessionUUID = uuid.uuidString
        persist()
    }

    // MARK: Turns

    /// Turns shown in the Answers panel.
    var turns: [InterviewRecord.Turn] { record?.turns ?? [] }
    var isStreaming: Bool { streamingTurn != nil }

    /// Send one turn on the interview's thread and stream its answer into the record.
    /// Returns when the turn ends.
    @discardableResult
    func runTurn(kind: InterviewRecord.TurnKind, text: String, question: String,
                 images: [URL] = [], span: (from: Int, to: Int)? = nil,
                 onAccepted: (() -> Void)? = nil) async -> InterviewRecord.TurnStatus? {
        guard prepState.isReady, let thread = record?.threadId, var rec = record else { return nil }
        let n = rec.nextTurnNumber
        let relImages = images.map { "\(InterviewFiles.attachmentsName)/\($0.lastPathComponent)" }
        rec.turns.append(.init(n: n, kind: kind, question: question, audioFromMs: span?.from, audioToMs: span?.to,
                               images: relImages, askedAt: TimeFormat.iso(Date())))
        if rec.startedAt == nil, kind == .typed { rec.prep.extraTurns += 1 }
        record = rec
        streamingTurn = n
        persist()

        let t0 = Date()
        let input: [CodexRPC.Input] = [.text(text)] + images.map { .localImage(path: $0.path) }
        var final: InterviewRecord.TurnStatus = .failed
        for await event in streamEvents(engine.send(threadId: thread, input: input,
                                                    effort: env.config.interview.reasoningEffort)) {
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
    private var draining = false

    /// One turn to send, already built at press time.
    struct Request {
        var kind: InterviewRecord.TurnKind
        var text: String
        var question: String
        var images: [URL] = []
        var span: (from: Int, to: Int)?
        var onAccepted: (() -> Void)?
        /// For merging queued asks: the transcript text and screenshot count behind `text`.
        var askText = ""
    }

    /// The Ask hotkey / button.
    func ask() async {
        guard prepState.isReady else {
            status = "Still preparing…"
            NSSound.beep()
            return
        }
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
        guard !t.isEmpty, prepState.isReady else { return }
        await submit(Request(kind: .typed, text: t, question: t))
    }

    func sendQuick(_ prompt: Config.Interview.QuickPrompt) async {
        guard prepState.isReady else { return }
        await submit(Request(kind: .quick, text: prompt.text, question: prompt.label))
    }

    /// A different answer to the latest question (latest card only).
    func regenerate() async {
        guard prepState.isReady, let last = turns.last else { return }
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
                          span: r.span, onAccepted: r.onAccepted)
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
