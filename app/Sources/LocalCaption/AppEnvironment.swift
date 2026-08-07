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
    @Published var config: Config { didSet { persist() } }
    let store: Store

    /// True if the config on disk was corrupt and had to be repaired to defaults.
    let configWasRepaired: Bool

    /// Leftover journals from a crash/quit, offered for recovery on launch (SPEC.md §9.3).
    @Published var pendingRecoveries: [RecoveredSession] = []

    init() {
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
    }

    private func persist() {
        try? config.write(to: AppPaths.configFile)
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
            _ = try? store.insert(rec)
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
