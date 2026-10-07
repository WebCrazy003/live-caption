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
    /// What this session is recording to an audio file (`.off` when nothing is).
    @Published private(set) var recordingSource: Config.RecordSource = .off
    /// The audio file written by the last save, beside the transcript.
    @Published private(set) var savedAudioURL: URL?
    /// A recording problem. Never stops the captions.
    @Published var recordingIssue: String?
    /// Accent mode (SPEC-18): raw captions still waiting for their live correction, shown muted
    /// after the committed text.
    @Published private(set) var pendingRaw = ""
    /// Live correction stopped for this session (signed out, usage limit…). Captions go on raw.
    @Published var correctionIssue: String?
    /// The final pass on the last saved session: nil when none runs.
    @Published private(set) var finalPass: FinalPassState?

    enum FinalPassState: Equatable {
        case running, done
        case failed(String)
    }


    /// True once any final has been committed — gates the "Copy last N" button.
    var hasTranscript: Bool { !transcript.isEmpty }
    var hasUnsavedSession: Bool { journal != nil || (!transcript.isEmpty && phase != .saved && !showingSaved) }
    /// The transcript on screen is a saved session (just stopped, or opened). It stays saved when
    /// the speech engine reloads underneath it (the Standard ↔ Accent switch), which moves `phase`.
    private var showingSaved = false

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
    private var recorder: SessionAudioRecorder?
    private var terminateObserver: NSObjectProtocol?
    private var prepareTask: Task<Void, Never>?
    /// This session runs in Accent mode (fixed at Start).
    private(set) var isAccentSession = false
    private var corrector: LiveCorrector?
    /// Segments shown as committed text; the rest are `pendingRaw`. Corrections settle in order.
    private var settledCount = 0
    private var settledStarts: Set<Int> = []
    /// Secondary texts that arrived before their segment was journalled.
    private var earlySecondary: [Int: String] = [:]
    /// The models this session uses (`primary+secondary`), fixed at Start.
    private var sessionModels: String?
    /// Accent settings changed while recording: reload once the session is saved.
    private var reloadAfterSession = false

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
        orchestrator.onSecondary = { [weak self] start, text in self?.secondaryArrived(start, text) }
        orchestrator.onCaptureMustPause = { [weak self] in
            guard let self else { return }
            self.capturePauseRequested = true
            if !self.transitioning { self.finishTransition() }
        }
        // Quitting mid-session leaves the journal for recovery; close the audio file too, so the
        // recovered session keeps its recording (an unclosed .m4a can't be read).
        terminateObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { _ = self?.recorder?.finish() }
        }
    }

    deinit {
        if let terminateObserver { NotificationCenter.default.removeObserver(terminateObserver) }
    }

    // MARK: Lifecycle

    /// Get the speech engine for the current Standard/Accent choice ready.
    func prepare() async {
        prepareTask?.cancel()
        let task = Task { await self.prepareNow() }
        prepareTask = task
        await task.value
    }

    private func prepareNow() async {
        phase = .preparing
        if env.config.accent.enabled {
            guard let engine = makeRTXEngine() else {
                orchestrator.errorText = RTXError.notConfigured.localizedDescription
                phase = .failed
                return
            }
            await orchestrator.prepare(engine)
        } else {
            await orchestrator.prepareModel(interimModel: env.config.asr.interimModel,
                                            finalModel: env.config.asr.finalModel)
        }
        guard !Task.isCancelled else { return }
        if !orchestrator.modelReady { phase = .failed } else { phase = showingSaved ? .saved : .ready }
    }

    private func makeRTXEngine() -> RTXEngine? {
        let a = env.config.accent
        guard let url = a.agentURL else { return nil }
        return RTXEngine(client: RTXClient(baseURL: url, token: RTXToken.load()),
                         settings: .init(primary: a.primaryModel, secondary: a.secondaryModel.isEmpty ? nil : a.secondaryModel,
                                         bandpass: a.audioBandpass, level: a.audioLevel))
    }

    /// The Standard ↔ Accent switch, and reloading after Accent settings change. Only while
    /// nothing is recording or unsaved.
    var canSwitchSpeech: Bool { !transitioning && !hasUnsavedSession && ![.recording, .pausing, .paused, .saving].contains(phase) }

    func switchSpeech(accent: Bool) {
        guard canSwitchSpeech else { return }
        env.config.accent.enabled = accent
        reloadEngine()
    }

    /// Drop the engine and prepare again with the current settings. While a session runs, this
    /// waits until it is saved.
    func reloadEngine() {
        guard canSwitchSpeech else { reloadAfterSession = true; return }
        reloadAfterSession = false
        prepareTask?.cancel()
        orchestrator.unload()
        Task { await prepare() }
    }

    func retryPrepare() {
        guard !transitioning, !hasUnsavedSession, phase != .recording, phase != .paused, phase != .pausing,
              phase != .saving else { return }
        orchestrator.errorText = nil
        if orchestrator.modelReady { phase = .ready }
        else { Task { await prepare() } }
    }

    /// `name` overrides the default `<prefix><timestamp>` (Interview mode names its sessions).
    func start(name: String? = nil) async {
        guard !transitioning, !hasUnsavedSession, orchestrator.modelReady,
              phase == .ready || phase == .saved || phase == .failed else { return }
        transitioning = true
        defer { finishTransition() }
        sessionId = UUID()
        startDate = Date()
        isAccentSession = env.config.accent.enabled
        showingSaved = false
        let a = env.config.accent
        sessionModels = isAccentSession ? [a.primaryModel, a.secondaryModel].filter { !$0.isEmpty }.joined(separator: "+") : nil
        orchestrator.applyTuning(
            endpointSilenceMs: isAccentSession ? env.config.accent.endpointSilenceMs : env.config.asr.endpointSilenceMs,
            interimIntervalMs: env.config.asr.interimIntervalMs,
            maxUtteranceS: env.config.asr.maxUtteranceS,
            vadSensitivity: env.config.audio.vadSensitivity)
        sessionName = name ?? (env.config.general.sessionNamePrefix + TimeFormat.fileStamp(startDate))
        transcript = Transcript(); paragraphs = []; current = ""
        savedTxtURL = nil; saveError = nil; savedAudioURL = nil; recordingIssue = nil
        startCorrection()
        do { journal = try JournalWriter(sessionId: sessionId) }
        catch { saveError = "Could not create recovery journal: \(error.localizedDescription)"; phase = .failed; return }
        let source = env.config.audio.recordSource
        if source == .call { setRecorder(makeRecorder { try CallAudioRecorder(url: $0) }) }
        do { try await orchestrator.startCapture() }
        catch {
            // No processing loop starts on capture failure. Remove its empty
            // journal so permission retry can start a new session safely.
            await journal?.deleteFile(); journal = nil
            recorder?.discard(); setRecorder(nil)
            phase = .failed; return
        }
        // After capture is up, so the microphone file starts with the captions' clock.
        if source == .microphone, let mic = makeRecorder({ try MicrophoneRecorder(url: $0) }) {
            do { try await mic.start(); setRecorder(mic) }
            catch { mic.discard(); recordingIssue = error.localizedDescription }
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
        recorder?.pause()
        await orchestrator.pauseAndFinalize()
        elapsed = TimeFormat.clock(orchestrator.recordedMs / 1000)
        phase = .paused
    }

    func resume() async {
        guard !transitioning, phase == .paused || (phase == .failed && hasUnsavedSession) else { return }
        transitioning = true
        defer { finishTransition() }
        do { try await orchestrator.resumeCapture() } catch { phase = .failed; return }
        do { try recorder?.resume() } catch { recordingIssue = error.localizedDescription }
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
        recorder?.pause()
        await orchestrator.stopAndFinalize()
        await orchestrator.waitForSecondaries(timeout: 5)
        await finishCorrection()
        phase = await save() ? .saved : .failed
        showingSaved = phase == .saved
        if reloadAfterSession, phase == .saved { reloadEngine() }
    }

    // MARK: Audio recording

    /// A recorder writing to `recordings/<session uuid>.m4a`, or nil (with the issue shown) if
    /// it can't be created.
    private func makeRecorder<R: SessionAudioRecorder>(_ make: (URL) throws -> R) -> R? {
        do { return try make(SessionFiles.recordingURL(sessionId: sessionId)) }
        catch {
            recordingIssue = "Audio recording is off for this session: \(error.localizedDescription)"
            return nil
        }
    }

    private func setRecorder(_ new: SessionAudioRecorder?) {
        new?.onError = { [weak self] message in
            Task { @MainActor in self?.recordingIssue = message }
        }
        recorder = new
        orchestrator.callRecorder = new as? CallAudioRecorder
        recordingSource = new?.source ?? .off
    }

    /// Close the recording and move it beside the transcript (same base name as the `.txt`).
    /// A file that can't be moved stays where it is and is still linked to the session.
    private func saveRecording(sessionId id: Int64?, txtURL: URL?, folder: URL) {
        guard let recorder else { return }
        setRecorder(nil)
        guard recorder.finish() else { return }
        var url = recorder.url
        let base = txtURL?.deletingPathExtension().lastPathComponent ?? TimeFormat.fileStamp(startDate)
        do { url = try SessionFiles.placeRecording(url, folder: folder, base: base) }
        catch { recordingIssue = "The audio was saved, but couldn't be moved beside the transcript: \(error.localizedDescription)" }
        if let id { try? env.store.setAudioFile(id: id, path: url.path) }
        savedAudioURL = url
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
        if let corrector {
            corrector.add(number: transcript.segments.count, startMs: startMs, primary: text)
            if let alt = earlySecondary.removeValue(forKey: startMs) { secondaryArrived(startMs, alt) }
            updatePendingRaw()
        } else {
            if let alt = earlySecondary.removeValue(forKey: startMs) { secondaryArrived(startMs, alt) }
            settle(startMs)
        }
    }

    // MARK: Accent mode: secondary text and live correction (SPEC-18)

    private func secondaryArrived(_ startMs: Int, _ text: String) {
        let patch = SegmentPatch(tStartMs: startMs, altText: text)
        guard transcript.apply(patch) else { earlySecondary[startMs] = text; return }
        Task { try? await journal?.append(patch) }
        corrector?.setSecondary(startMs: startMs, text: text)
    }

    private func startCorrection() {
        settledCount = 0; settledStarts = []; earlySecondary = [:]
        pendingRaw = ""; correctionIssue = nil; finalPass = nil
        corrector = nil
        let a = env.config.accent
        guard isAccentSession, a.liveCorrection else { return }
        let c = LiveCorrector(engine: env.correctionEngine, options: .init(
            model: a.effectiveLiveModel, effort: a.liveEffort, vocabulary: a.vocabulary,
            twoSources: !a.secondaryModel.isEmpty))
        c.onResult = { [weak self] start, text in self?.liveCorrected(start, text) }
        c.onStopped = { [weak self] message in
            self?.correctionIssue = "Live correction stopped: \(message) Captions continue uncorrected."
        }
        corrector = c
    }

    private func finishCorrection() async {
        guard let corrector else { return }
        await corrector.drain(timeout: 10)
        await corrector.finish()
        self.corrector = nil
    }

    private func liveCorrected(_ startMs: Int, _ text: String?) {
        if let text {
            let patch = SegmentPatch(tStartMs: startMs, liveText: text)
            if transcript.apply(patch) { Task { try? await journal?.append(patch) } }
        }
        settle(startMs)
    }

    /// Mark a segment final for display; the settled prefix moves into the paragraphs.
    private func settle(_ startMs: Int) {
        settledStarts.insert(startMs)
        let segs = transcript.segments
        while settledCount < segs.count, settledStarts.contains(segs[settledCount].tStartMs) {
            addToParagraphs(segs[settledCount].bestText)
            settledCount += 1
        }
        updatePendingRaw()
    }

    private func updatePendingRaw() {
        pendingRaw = transcript.segments.dropFirst(settledCount).map(\.text).joined(separator: " ")
    }

    private func rebuildParagraphs() {
        paragraphs = []; current = ""
        transcript.segments.prefix(settledCount).forEach { addToParagraphs($0.bestText) }
    }

    // MARK: Clipboard (write-only; never reads — SPEC.md §9.4)

    var committedText: String { transcript.segments.map(\.bestText).joined(separator: " ") }

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

    /// Write the DB row with its caption segments, then the `.txt` + `.json` export; delete the
    /// journal. Returns false when the database write fails (the journal is kept, so the session
    /// stays recoverable); a failed export only warns.
    private func save() async -> Bool {
        if !current.isEmpty { paragraphs.append(current); current = "" }
        let end = Date()
        let duration = orchestrator.recordedMs / 1000
        let saved: SessionRecord
        do {
            let accent = env.config.accent
            let runsFinalPass = isAccentSession && accent.finalPass && !transcript.isEmpty
            saved = try env.store.insert(SessionRecord(
                sessionName: sessionName,
                createdAt: TimeFormat.iso(startDate),
                endedAt: TimeFormat.iso(end),
                durationSeconds: duration,
                speechMode: isAccentSession ? SessionRecord.accentSpeech : SessionRecord.standardSpeech,
                models: sessionModels,
                correctionStatus: runsFinalPass ? SessionRecord.Correction.running : SessionRecord.Correction.none),
                segments: transcript.segments)
        } catch {
            saveError = "Could not save the session: \(error.localizedDescription)"
            // Keep the journal (and the open recording) for a retry or recovery; quitting closes
            // the audio file so recovery can attach it.
            return false
        }
        savedSessionId = saved.id
        await journal?.deleteFile(); journal = nil
        saveError = nil
        savedTxtURL = nil
        let folder = URL(fileURLWithPath:
            (env.config.general.transcriptFolder as NSString).expandingTildeInPath)
        do {
            let result = try TranscriptWriter.save(
                transcript: transcript, folder: folder, sessionName: sessionName,
                start: startDate, end: end, durationSeconds: duration,
                showTimestamps: env.config.caption.showTimestamps)
            if let id = saved.id { try? env.store.setTranscriptFile(id: id, path: result.txtURL.path) }
            savedTxtURL = result.txtURL
        } catch {
            saveError = "Saved, but the transcript file export failed: \(error.localizedDescription)"
        }
        saveRecording(sessionId: saved.id, txtURL: savedTxtURL, folder: folder)
        NotificationCenter.default.post(name: .sessionsChanged, object: nil)
        if saved.correctionStatus == SessionRecord.Correction.running, let id = saved.id {
            runFinalPass(sessionId: id, segments: transcript.segments, txtURL: savedTxtURL,
                         name: sessionName, start: startDate, end: end, duration: duration)
        }
        return true
    }

    /// Run the final pass again on a saved Accent session (after a failure, or with new settings).
    func retryFinalPass(sessionId id: Int64) {
        guard let rec = try? env.store.fetch(id: id), rec.isAccent,
              let segments = try? env.store.segments(sessionId: id), !segments.isEmpty else { return }
        let start = TimeFormat.parseISO(rec.createdAt) ?? Date()
        runFinalPass(sessionId: id, segments: segments, txtURL: rec.transcriptFile.map { URL(fileURLWithPath: $0) },
                     name: rec.sessionName, start: start,
                     end: rec.endedAt.flatMap(TimeFormat.parseISO) ?? start, duration: rec.durationSeconds)
    }

    /// Accent mode's final pass (SPEC-18): runs after the raw save, so Stop never waits for it.
    /// Writes each segment's final text to the database, re-exports the `.txt`/`.json`, and
    /// updates the captions if this session is still on screen. Also Retry from Sessions.
    func runFinalPass(sessionId id: Int64, segments: [TranscriptSegment], txtURL: URL?,
                      name: String, start: Date, end: Date, duration: Int) {
        if savedSessionId == id { finalPass = .running }
        let a = env.config.accent
        let store = env.store
        let showTimestamps = env.config.caption.showTimestamps
        Task {
            do {
                try store.setCorrectionStatus(id: id, SessionRecord.Correction.running)
                let texts = try await FinalPass.run(segments: segments, engine: env.correctionEngine,
                                                    model: a.effectiveFinalModel, effort: a.finalEffort,
                                                    vocabulary: a.vocabulary)
                try store.setFinalTexts(sessionId: id, texts)
                try store.setCorrectionStatus(id: id, SessionRecord.Correction.done)
                var improved = Transcript(segments: segments)
                for (startMs, text) in texts { improved.apply(SegmentPatch(tStartMs: startMs, finalText: text)) }
                if let txtURL {
                    try? TranscriptWriter.write(transcript: improved, txtURL: txtURL, sessionName: name, start: start,
                                                end: end, durationSeconds: duration, showTimestamps: showTimestamps)
                }
                if savedSessionId == id && phase == .saved {
                    transcript = improved
                    settledCount = improved.segments.count
                    rebuildParagraphs()
                    finalPass = .done
                }
            } catch {
                try? store.setCorrectionStatus(id: id, SessionRecord.Correction.failed)
                if savedSessionId == id && phase == .saved { finalPass = .failed(error.localizedDescription) }
            }
            NotificationCenter.default.post(name: .sessionsChanged, object: nil)
        }
    }

    // MARK: Opening a saved session

    /// Nothing live or unsaved, and the speech model has settled — a saved session can be shown.
    var canOpenSaved: Bool {
        !transitioning && !hasUnsavedSession && [.ready, .saved, .failed].contains(phase)
    }

    /// Sessions → Open in interview panel: show a saved session here as if it had just been saved.
    /// Start then begins a new session, as usual.
    func openSaved(_ rec: SessionRecord, segments: [TranscriptSegment]) {
        guard canOpenSaved else { return }
        transcript = Transcript(segments: segments)
        paragraphs = []; current = ""
        segments.forEach { addToParagraphs($0.bestText) }
        if !current.isEmpty { paragraphs.append(current); current = "" }
        settledCount = segments.count; pendingRaw = ""; finalPass = nil
        isAccentSession = rec.isAccent
        sessionName = rec.sessionName
        startDate = TimeFormat.parseISO(rec.createdAt) ?? Date()
        elapsed = TimeFormat.clock(rec.durationSeconds)
        savedSessionId = rec.id
        savedTxtURL = rec.transcriptFile.map { URL(fileURLWithPath: $0) }
        savedAudioURL = rec.audioFile.map { URL(fileURLWithPath: $0) }
        saveError = nil; recordingIssue = nil
        phase = .saved
        showingSaved = true
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
