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
    /// Where interview records live; injectable for tests.
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
        sweepInterviews()
    }

    /// On launch: answers, preps or summaries left `streaming`/`running` by a quit or crash
    /// are marked failed (SPEC-15 §History), so the UI never shows a spinner that can't finish.
    func sweepInterviews() {
        for (folder, var rec) in InterviewFiles.all(in: interviewsRoot) where rec.failInterruptedTurns() {
            try? InterviewFiles.write(rec, to: folder)
        }
    }

    /// Link a recovered capture to the interview recorded alongside it (SPEC-15 §History).
    func linkRecoveredInterview(captureId: UUID, sessionId: Int64?) {
        guard let sessionId else { return }
        for (folder, var rec) in InterviewFiles.all(in: interviewsRoot)
        where rec.captureSessionUUID == captureId.uuidString && rec.sessionId == nil {
            rec.sessionId = sessionId
            rec.endedAt = rec.endedAt ?? TimeFormat.iso(Date())
            try? InterviewFiles.write(rec, to: folder)
            try? store.setInterview(id: sessionId, dir: folder.path)
        }
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

    /// Rebuild a transcript from a leftover journal, save it (`.txt` + `.json` + DB row),
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

        if let result = try? TranscriptWriter.save(
            transcript: transcript, folder: folder, sessionName: name,
            start: start, end: end, durationSeconds: durationMs / 1000,
            showTimestamps: config.caption.showTimestamps) {
            let rec = SessionRecord(
                sessionName: name, createdAt: TimeFormat.iso(start), endedAt: TimeFormat.iso(end),
                durationSeconds: durationMs / 1000, transcriptFile: result.txtURL.path)
            let inserted = try? store.insert(rec)
            linkRecoveredInterview(captureId: session.sessionId, sessionId: inserted?.id)
            Journal.remove(at: session.url)
            pendingRecoveries.removeAll { $0.url == session.url }
            NotificationCenter.default.post(name: .sessionsChanged, object: nil)
        }
    }

    /// Discard a leftover journal without saving.
    func discard(_ session: RecoveredSession) {
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
