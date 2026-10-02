import Foundation
import SwiftUI
import AppKit
import LocalCaptionKit
import Combine

/// Session state machine (SPEC.md §11): `IDLE → RECORDING ⇄ PAUSED → STOPPING → SAVED`.
/// Owns the in-memory transcript, the crash-recovery journal, the elapsed clock, and the
/// save-on-stop step (`.txt` + `.json` + DB row). Drives the streaming orchestrator.
@MainActor
final class SessionController: ObservableObject {
    enum Phase: Equatable {
        case preparing, ready, recording, pausing, paused, saving, saved, failed
    }

    @Published var phase: Phase = .preparing
    @Published var sessionName = ""
    @Published var paragraphs: [String] = []   // committed finals, grouped for display
    @Published var current = ""                 // building paragraph
    @Published var elapsed = "00:00:00"
    @Published var savedTxtURL: URL?
    /// The `sessions` row written by the last save (links an interview to it, SPEC-15).
    @Published private(set) var savedSessionId: Int64?
    @Published var saveError: String?
    @Published var justCopied = false


    /// True once any final has been committed — gates the "Copy last N" button.
    var hasTranscript: Bool { !transcript.isEmpty }
    var hasUnsavedSession: Bool { journal != nil || (!transcript.isEmpty && phase != .saved) }

    let orchestrator = StreamingOrchestrator()

    private let env: AppEnvironment
    private var transcript = Transcript()
    private var journal: JournalWriter?
    private var orchestratorObservation: AnyCancellable?
    private var transitioning = false
    private var capturePauseRequested = false
    private(set) var sessionId = UUID()
    private(set) var startDate = Date()
    private var clockTask: Task<Void, Never>?

    var displayName: String { sessionName.isEmpty ? "New Session" : sessionName }

    /// What an Ask reads at press time (SPEC-14): committed finals, the live interim line, and
    /// the pause-aware audio clock.
    var askSnapshot: (segments: [AskSelection.Segment], interim: String, audioMs: Int) {
        (transcript.segments.map(AskSelection.Segment.init), orchestrator.hypothesis, orchestrator.recordedMs)
    }

    init(env: AppEnvironment) {
        self.env = env
        orchestrator.onFinal = { [weak self] text, start, end in
            await self?.ingestFinal(text, start, end)
        }
        orchestrator.onSpeechEnded = { [weak self] interimText in
            guard let self, self.env.config.clipboard.autoUpdate else { return }
            self.copyLastN(includingInterim: interimText)
        }
        orchestrator.onFinalized = { [weak self] pendingText in
            guard let self, self.env.config.clipboard.autoUpdate else { return }
            self.copyLastN(includingInterim: pendingText)
        }
        // Nested ObservableObject changes must invalidate captions and status
        // immediately, independently of the elapsed-time clock.
        orchestratorObservation = orchestrator.objectWillChange.sink { [weak self] _ in
            self?.objectWillChange.send()
        }
        orchestrator.onCaptureMustPause = { [weak self] in
            guard let self else { return }
            self.capturePauseRequested = true
            if !self.transitioning { self.finishTransition() }
        }
    }

    // MARK: Lifecycle

    func prepare() async {
        phase = .preparing
        await orchestrator.prepareModel(interimModel: env.config.asr.interimModel,
                                        finalModel: env.config.asr.finalModel)
        phase = orchestrator.modelReady ? .ready : .failed
    }

    func retryPrepare() {
        guard !transitioning, !hasUnsavedSession, phase != .recording, phase != .paused, phase != .pausing,
              phase != .saving else { return }
        orchestrator.errorText = nil
        if orchestrator.modelReady { phase = .ready }
        else { Task { await prepare() } }
    }

    func start() async {
        guard !transitioning, !hasUnsavedSession, orchestrator.modelReady,
              phase == .ready || phase == .saved || phase == .failed else { return }
        transitioning = true
        defer { finishTransition() }
        sessionId = UUID()
        startDate = Date()
        orchestrator.applyTuning(
            endpointSilenceMs: env.config.asr.endpointSilenceMs,
            interimIntervalMs: env.config.asr.interimIntervalMs,
            maxUtteranceS: env.config.asr.maxUtteranceS,
            vadSensitivity: env.config.audio.vadSensitivity)
        sessionName = env.config.general.sessionNamePrefix + TimeFormat.fileStamp(startDate)
        transcript = Transcript(); paragraphs = []; current = ""
        savedTxtURL = nil; saveError = nil
        do { journal = try JournalWriter(sessionId: sessionId) }
        catch { saveError = "Could not create recovery journal: \(error.localizedDescription)"; phase = .failed; return }
        do { try await orchestrator.startCapture() }
        catch {
            // No processing loop starts on capture failure. Remove its empty
            // journal so permission retry can start a new session safely.
            await journal?.deleteFile(); journal = nil
            phase = .failed; return
        }
        phase = .recording
        startClock()
    }

    func pause() async {
        guard !transitioning, phase == .recording else { return }
        transitioning = true
        defer { finishTransition() }
        phase = .pausing
        stopClock()
        await orchestrator.pauseAndFinalize()
        elapsed = TimeFormat.clock(orchestrator.recordedMs / 1000)
        phase = .paused
    }

    func resume() async {
        guard !transitioning, phase == .paused || (phase == .failed && hasUnsavedSession) else { return }
        transitioning = true
        defer { finishTransition() }
        do { try await orchestrator.resumeCapture() } catch { phase = .failed; return }
        phase = .recording
        startClock()
    }

    func stop() async {
        guard !transitioning, phase == .recording || phase == .paused ||
              (phase == .failed && hasUnsavedSession) else { return }
        transitioning = true
        defer { finishTransition() }
        phase = .saving
        stopClock()
        await orchestrator.stopAndFinalize()
        phase = await save() ? .saved : .failed
    }

    private func finishTransition() {
        transitioning = false
        guard capturePauseRequested else { return }
        capturePauseRequested = false
        if phase == .recording { Task { @MainActor [weak self] in await self?.pause() } }
    }

    // MARK: Transcript ingestion

    private func ingestFinal(_ text: String, _ startMs: Int, _ endMs: Int) async {
        let seg = TranscriptSegment(text: text, tStartMs: startMs, tEndMs: endMs,
                                    createdAt: TimeFormat.iso(Date()))
        do {
            guard let journal else { throw CocoaError(.fileWriteUnknown) }
            try await journal.append(seg)
        }
        catch {
            // Retain the decoded segment in memory for Stop/save; never pretend
            // that a failed disk write is durable or silently swallow the failure.
            saveError = "Recovery journal write failed: \(error.localizedDescription). Stop to save the transcript."
        }
        transcript.append(seg)
        addToParagraphs(text)
    }

    // MARK: Clipboard (write-only; never reads — SPEC.md §9.4)

    var committedText: String { transcript.segments.map(\.text).joined(separator: " ") }

    /// Copy the last N completed sentences to the clipboard (N from Settings).
    func copyLastN() {
        copyLastN(includingInterim: "")
    }

    /// At an endpoint, copy the latest interim immediately instead of waiting for the final
    /// model. Final completion refreshes it after the matching provisional is retired.
    private func copyLastN(includingInterim interimText: String) {
        let text = Sentences.lastN(committedText, appending: interimText,
                                   n: env.config.clipboard.recentSentences)
        guard !text.isEmpty else { return }
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
        flashCopied()
    }

    private func flashCopied() {
        justCopied = true
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 1_400_000_000)
            self?.justCopied = false
        }
    }

    /// Accumulate finals into a paragraph; break at ~4 sentences or ~100 words.
    private func addToParagraphs(_ text: String) {
        current = current.isEmpty ? text : current + " " + text
        if Filters.sentenceCount(current) >= 4 || Filters.wordCount(current) >= 100 {
            paragraphs.append(current); current = ""
        }
    }

    // MARK: Save

    /// Write `.txt` + `.json` and the DB row; delete the journal. Returns false on failure
    /// (journal is kept so the session stays recoverable).
    private func save() async -> Bool {
        if !current.isEmpty { paragraphs.append(current); current = "" }
        let end = Date()
        let duration = orchestrator.recordedMs / 1000
        let folder = URL(fileURLWithPath:
            (env.config.general.transcriptFolder as NSString).expandingTildeInPath)
        do {
            let result = try TranscriptWriter.save(
                transcript: transcript, folder: folder, sessionName: sessionName,
                start: startDate, end: end, durationSeconds: duration,
                showTimestamps: env.config.caption.showTimestamps)
            let rec = SessionRecord(
                sessionName: sessionName,
                createdAt: TimeFormat.iso(startDate),
                endedAt: TimeFormat.iso(end),
                durationSeconds: duration,
                transcriptFile: result.txtURL.path)
            savedSessionId = (try? env.store.insert(rec))?.id
            await journal?.deleteFile(); journal = nil
            savedTxtURL = result.txtURL
            saveError = nil
            NotificationCenter.default.post(name: .sessionsChanged, object: nil)
            return true
        } catch {
            saveError = "Could not save transcript: \(error.localizedDescription)"
            return false   // keep the journal for recovery
        }
    }

    // MARK: Clock (sample-based; frozen during pause)

    private func startClock() {
        clockTask?.cancel()
        clockTask = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                guard let self else { break }
                self.elapsed = TimeFormat.clock(self.orchestrator.recordedMs / 1000)
                try? await Task.sleep(nanoseconds: 500_000_000)
            }
        }
    }

    private func stopClock() {
        clockTask?.cancel(); clockTask = nil
        elapsed = TimeFormat.clock(orchestrator.recordedMs / 1000)
    }
}
