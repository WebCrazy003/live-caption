import Foundation
import SwiftUI
import AppKit
import LocalCaptionKit

/// Session state machine (SPEC.md §11): `IDLE → RECORDING ⇄ PAUSED → STOPPING → SAVED`.
/// Owns the in-memory transcript, the crash-recovery journal, the elapsed clock, and the
/// save-on-stop step (`.txt` + `.json` + DB row). Drives the streaming orchestrator.
@MainActor
final class SessionController: ObservableObject {
    enum Phase: Equatable {
        case preparing, ready, recording, paused, saving, saved, failed
    }

    @Published var phase: Phase = .preparing
    @Published var sessionName = ""
    @Published var paragraphs: [String] = []   // committed finals, grouped for display
    @Published var current = ""                 // building paragraph
    @Published var elapsed = "00:00:00"
    @Published var savedTxtURL: URL?
    @Published var saveError: String?
    @Published var justCopied = false

    // Live AI summary (SPEC-10): a growing list of "Key points" cards, one per ~100-word block.
    @Published var summaries: [SummaryCard] = []
    @Published var summarizing = false        // a generation is in flight
    @Published var summaryUnavailable = false // local model server not reachable

    /// True once any final has been committed — gates the "Copy last N" button.
    var hasTranscript: Bool { !transcript.isEmpty }

    let orchestrator = StreamingOrchestrator()

    private let env: AppEnvironment
    private var transcript = Transcript()
    private var journal: Journal?
    private var sessionId = UUID()
    private var startDate = Date()
    private var clockTask: Task<Void, Never>?

    // Summary trigger state (SPEC-10). Counts committed final words; at the threshold it
    // summarizes the accumulated block and resets. Independent of the paragraph counter.
    private var summaryEngine: SummaryEngine?
    private var summaryBuffer = ""
    private var summaryWords = 0
    private var summaryCardSeq = 0
    private var summaryFlushPending = false

    var displayName: String { sessionName.isEmpty ? "New Session" : sessionName }

    init(env: AppEnvironment) {
        self.env = env
        orchestrator.onFinal = { [weak self] text, start, end in
            self?.ingestFinal(text, start, end)
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
        orchestrator.errorText = nil
        if orchestrator.modelReady { phase = .ready }
        else { Task { await prepare() } }
    }

    func start() async {
        guard orchestrator.modelReady, phase == .ready || phase == .saved || phase == .failed else { return }
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
        resetSummaryState()
        journal = try? Journal(sessionId: sessionId)
        do { try await orchestrator.startCapture() } catch { phase = .failed; return }
        phase = .recording
        startClock()
    }

    func pause() async {
        guard phase == .recording else { return }
        stopClock()
        if let f = await orchestrator.pauseAndFinalize() { ingestFinal(f.0, f.1, f.2) }
        flushSummary()   // summarize the tail of the utterance before we freeze
        phase = .paused
    }

    func resume() async {
        guard phase == .paused else { return }
        do { try await orchestrator.resumeCapture() } catch { phase = .failed; return }
        phase = .recording
        startClock()
    }

    func stop() async {
        guard phase == .recording || phase == .paused else { return }
        phase = .saving
        stopClock()
        if let f = await orchestrator.stopAndFinalize() { ingestFinal(f.0, f.1, f.2) }
        flushSummary()   // final card for the whole call; does not block the save below
        phase = save() ? .saved : .failed
    }

    // MARK: Transcript ingestion

    private func ingestFinal(_ text: String, _ startMs: Int, _ endMs: Int) {
        let seg = TranscriptSegment(text: text, tStartMs: startMs, tEndMs: endMs,
                                    createdAt: TimeFormat.iso(Date()))
        transcript.append(seg)
        try? journal?.append(seg)          // crash-safety: on disk before anything else
        addToParagraphs(text)
        accumulateForSummary(text)         // SPEC-10 word-count trigger
        if env.config.clipboard.autoUpdate { copyLastN() }   // write-only, opt-in
    }

    // MARK: Clipboard (write-only; never reads — SPEC.md §9.4)

    private var committedText: String { transcript.segments.map(\.text).joined(separator: " ") }

    /// Copy the last N completed sentences to the clipboard (N from Settings).
    func copyLastN() {
        let text = Sentences.lastN(committedText, n: env.config.clipboard.recentSentences)
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

    // MARK: Live AI summary (SPEC-10)

    /// Fresh summary state for a new session.
    private func resetSummaryState() {
        summaries = []; summaryBuffer = ""; summaryWords = 0
        summaryCardSeq = 0; summarizing = false; summaryFlushPending = false; summaryUnavailable = false
        guard env.config.summary.enabled else { summaryEngine = nil; return }
        let engine = MLXServerEngine(serverURL: env.config.summary.serverURL,
                                     model: env.config.summary.model)
        summaryEngine = engine
        // Non-blocking availability check so the panel can show a quiet note if the server is down.
        Task { @MainActor [weak self] in self?.summaryUnavailable = !(await engine.probe()) }
    }

    /// Add a committed final to the pending block; summarize once it reaches the word threshold.
    private func accumulateForSummary(_ text: String) {
        guard env.config.summary.enabled, summaryEngine != nil else { return }
        let t = text.trimmingCharacters(in: .whitespaces)
        guard !t.isEmpty else { return }
        summaryBuffer = summaryBuffer.isEmpty ? t : summaryBuffer + " " + t
        summaryWords += Filters.wordCount(t)
        if summaryWords >= env.config.summary.wordsPerSummary, !summarizing {
            dispatchSummary()
        }
    }

    /// Summarize the tail (any remaining words) on pause/stop. Never blocks the save path.
    private func flushSummary() {
        guard env.config.summary.enabled, summaryEngine != nil, !summaryBuffer.isEmpty else { return }
        summaryFlushPending = true
        if !summarizing { summaryFlushPending = false; dispatchSummary() }
    }

    /// Send the accumulated block to the engine; reset the buffer. Callers decide *when*
    /// (threshold or flush); if more text piled up during generation, re-dispatch on completion.
    private func dispatchSummary() {
        guard let engine = summaryEngine, !summaryBuffer.isEmpty else { return }
        let chunk = summaryBuffer
        let id = summaryCardSeq
        let maxBullets = env.config.summary.maxBullets
        summaryBuffer = ""; summaryWords = 0; summaryCardSeq += 1
        summarizing = true
        Task { @MainActor [weak self] in
            guard let self else { return }
            do {
                let card = try await engine.summarize(chunk: chunk, id: id, maxBullets: maxBullets)
                if !card.isEmpty { self.summaries.append(card) }
                self.summaryUnavailable = false
            } catch {
                self.summaryUnavailable = true
            }
            self.summarizing = false
            // Merge-while-busy: enough new words arrived during generation, or a flush is pending.
            if !self.summaryBuffer.isEmpty,
               self.summaryWords >= self.env.config.summary.wordsPerSummary || self.summaryFlushPending {
                self.summaryFlushPending = false
                self.dispatchSummary()
            }
        }
    }

    // MARK: Save

    /// Write `.txt` + `.json` and the DB row; delete the journal. Returns false on failure
    /// (journal is kept so the session stays recoverable).
    private func save() -> Bool {
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
            _ = try? env.store.insert(rec)
            journal?.deleteFile(); journal = nil
            savedTxtURL = result.txtURL
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
