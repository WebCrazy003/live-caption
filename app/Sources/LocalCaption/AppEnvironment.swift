import Foundation
import SwiftUI
import LocalCaptionKit

/// App-wide dependencies: the config store and the sqlite session store.
///
/// Bootstraps the App Support directory tree, loads (repairing if needed) the config,
/// and opens the database. `config` is `@Published`; any mutation persists atomically,
/// which is what gives Settings its two-way binding + live persistence.
@MainActor
final class AppEnvironment: ObservableObject {
    @Published var config: Config {
        didSet {
            persist()
            codexPath.set(config.interview.codexPath)
        }
    }
    let store: Store

    /// Interview Assist (SPEC-11): the user's library, and the one Codex engine for this app run.
    lazy var library = InterviewLibrary()
    /// Where earlier builds kept interview folders (imported once into the database); injectable.
    var interviewsRoot = AppPaths.interviews

    /// The live session and its interview. Held here, not by the session screen, so opening a
    /// past session in the sidebar and coming back keeps both (recording, prep, conversation).
    lazy var session = SessionController(env: self)
    lazy var interview: InterviewController = {
        let i = InterviewController(env: self)
        i.transcriptSource = { [weak self] in
            self?.session.askSnapshot ?? (segments: [], interim: "", audioMs: 0)
        }
        return i
    }()
    lazy var codex = CodexService(engine: CodexAppServerEngine(codexPath: { [codexPath] in codexPath.get() }))
    private let codexPath = LockedValue("")

    /// True if the config on disk was corrupt and had to be repaired to defaults.
    let configWasRepaired: Bool
    private let configURL: URL?

    /// Leftover journals from a crash/quit, offered for recovery on launch (SPEC.md §9.3).
    @Published var pendingRecoveries: [RecoveredSession] = []

    init() {
        configURL = AppPaths.configFile
        _ = try? AppPaths.bootstrap()

        let loaded = Config.loadOrRepair(from: AppPaths.configFile)
        self.config = loaded.config
        self.configWasRepaired = loaded.repaired

        do {
            self.store = try Store()
        } catch {
            // Last resort so the UI still launches; a real disk failure is surfaced elsewhere.
            let fallback = URL(fileURLWithPath: NSTemporaryDirectory())
                .appendingPathComponent("localcaption-fallback.db")
            self.store = try! Store(url: fallback)
        }

        self.pendingRecoveries = Journal.pending()
        codexPath.set(config.interview.codexPath)
        importLegacyInterviews()
        sweepInterviews()
        if ((try? store.importTranscriptFiles()) ?? 0) > 0 {
            NotificationCenter.default.post(name: .sessionsChanged, object: nil)
        }
    }

    /// Interviews written as folders by earlier builds move into the database once; the folders
    /// are left as they were.
    func importLegacyInterviews() {
        let dirs = (try? FileManager.default.contentsOfDirectory(at: interviewsRoot, includingPropertiesForKeys: nil)) ?? []
        var imported = 0
        for dir in dirs where (try? store.importLegacyInterview(folder: dir)) == true { imported += 1 }
        if imported > 0 { NotificationCenter.default.post(name: .sessionsChanged, object: nil) }
    }

    /// On launch: answers, preps or summaries left `streaming`/`running` by a quit or crash
    /// are marked failed (SPEC-15 §History), so the UI never shows a spinner that can't finish.
    func sweepInterviews() {
        for var rec in (try? store.allInterviews()) ?? [] where rec.failInterruptedTurns() {
            try? store.saveInterview(rec)
        }
    }

    /// Link a recovered capture to the interview recorded alongside it (SPEC-15 §History).
    func linkRecoveredInterview(captureId: UUID, sessionId: Int64?, start: Date) {
        guard let sessionId else { return }
        for var rec in (try? store.allInterviews()) ?? []
        where rec.captureSessionUUID == captureId.uuidString && rec.sessionId == nil {
            rec.sessionId = sessionId
            rec.endedAt = rec.endedAt ?? TimeFormat.iso(Date())
            try? store.saveInterview(rec)
            try? store.markInterview(sessionId: sessionId)
            if let name = rec.sessionName(date: start) { try? store.rename(id: sessionId, to: name + " (recovered)") }
        }
    }

    // MARK: Sessions window → interview panel

    /// The first screen's mode choice has been made (owner, 2026-10-02); until then the main
    /// window shows the chooser.
    @Published var modeChosen = false

    /// Why a saved session can't be opened in the main window right now, or nil when it can.
    var openInPanelBlocker: String? {
        if !session.canOpenSaved {
            return session.hasUnsavedSession ? "Stop the current session first" : "Wait until the speech model is ready"
        }
        if interview.isBusy { return "Wait for the current answer to finish" }
        return nil
    }

    /// Show a saved interview in the main window's interview panel (summary, follow-ups).
    /// A preparation that never started recording is discarded first.
    func openInPanel(sessionId: Int64) async {
        guard openInPanelBlocker == nil, let rec = try? store.fetch(id: sessionId),
              let saved = try? store.interview(sessionId: sessionId) else { return }
        if interview.hasUnstartedPreparation { await interview.discardUnstarted() }
        let segments = (try? store.segments(sessionId: sessionId)) ?? []
        config.interview.mode = .interview
        modeChosen = true
        interview.load(saved)
        session.openSaved(rec, segments: segments)
    }

    /// Explicit dependencies for previews/tests; does not read or persist user settings.
    init(config: Config, store: Store) {
        self.config = config; self.store = store
        configWasRepaired = false; configURL = nil
        codexPath.set(config.interview.codexPath)
    }

    private func persist() {
        if let configURL { try? config.write(to: configURL) }
    }

    // MARK: Crash recovery

    /// Rebuild a transcript from a leftover journal, save it (DB row + segments, `.txt` + `.json`),
    /// then remove the journal.
    func recover(_ session: RecoveredSession) {
        let segs = session.segments
        guard !segs.isEmpty else { discard(session); return }

        var transcript = Transcript()
        segs.forEach { transcript.append($0) }

        let start = session.startedAt ?? Date()
        let end = segs.last.flatMap { TimeFormat.parseISO($0.createdAt) } ?? start
        let durationMs = segs.map(\.tEndMs).max() ?? 0
        let name = config.general.sessionNamePrefix + TimeFormat.fileStamp(start) + " (recovered)"
        let folder = URL(fileURLWithPath:
            (config.general.transcriptFolder as NSString).expandingTildeInPath)

        let rec = SessionRecord(
            sessionName: name, createdAt: TimeFormat.iso(start), endedAt: TimeFormat.iso(end),
            durationSeconds: durationMs / 1000)
        if let inserted = try? store.insert(rec, segments: segs), let id = inserted.id {
            let result = try? TranscriptWriter.save(
                transcript: transcript, folder: folder, sessionName: name,
                start: start, end: end, durationSeconds: durationMs / 1000,
                showTimestamps: config.caption.showTimestamps)
            if let result { try? store.setTranscriptFile(id: id, path: result.txtURL.path) }
            recoverRecording(session.sessionId, sessionId: id, folder: folder,
                             base: result?.txtURL.deletingPathExtension().lastPathComponent
                                ?? TimeFormat.fileStamp(start))
            linkRecoveredInterview(captureId: session.sessionId, sessionId: id, start: start)
            Journal.remove(at: session.url)
            pendingRecoveries.removeAll { $0.url == session.url }
            NotificationCenter.default.post(name: .sessionsChanged, object: nil)
        }
    }

    /// The session's audio, if it was recording and the file was closed (a quit, or a failed
    /// save); a crash leaves an unreadable file, which is removed.
    private func recoverRecording(_ captureId: UUID, sessionId: Int64, folder: URL, base: String) {
        let url = SessionFiles.recordingURL(sessionId: captureId)
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        guard RecordingCheck.isPlayable(url) else { try? FileManager.default.removeItem(at: url); return }
        let placed = (try? SessionFiles.placeRecording(url, folder: folder, base: base)) ?? url
        try? store.setAudioFile(id: sessionId, path: placed.path)
    }

    /// Discard a leftover journal (and any recording of it) without saving.
    func discard(_ session: RecoveredSession) {
        try? FileManager.default.removeItem(at: SessionFiles.recordingURL(sessionId: session.sessionId))
        Journal.remove(at: session.url)
        pendingRecoveries.removeAll { $0.url == session.url }
    }
}

/// A value readable from any thread — lets the engine (an actor) read `interview.codex_path`
/// without touching the main-actor config.
final class LockedValue<T>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: T
    init(_ value: T) { self.value = value }
    func get() -> T { lock.lock(); defer { lock.unlock() }; return value }
    func set(_ v: T) { lock.lock(); value = v; lock.unlock() }
}
