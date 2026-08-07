import Foundation

/// Resolves and bootstraps the app's on-disk locations under Application Support.
///
/// Layout (SPEC.md §12, SPEC-00):
/// ```
/// ~/Library/Application Support/LocalCaption/
/// ├── transcripts/      final .txt (+ .json sidecar) — Phase 2
/// ├── journal/          crash-recovery .jsonl        — Phase 2
/// ├── models/           WhisperKit CoreML weights
/// ├── config.json       versioned app config
/// └── localcaption.db   sqlite session metadata
/// ```
public enum AppPaths {
    /// `~/Library/Application Support/LocalCaption`
    public static let root: URL = {
        let base = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        return base.appendingPathComponent("LocalCaption", isDirectory: true)
    }()

    public static var transcripts: URL { root.appendingPathComponent("transcripts", isDirectory: true) }
    public static var journal: URL { root.appendingPathComponent("journal", isDirectory: true) }
    public static var models: URL { root.appendingPathComponent("models", isDirectory: true) }
    public static var configFile: URL { root.appendingPathComponent("config.json") }
    public static var databaseFile: URL { root.appendingPathComponent("localcaption.db") }

    /// Create the directory tree on first launch. Idempotent.
    @discardableResult
    public static func bootstrap() throws -> URL {
        let fm = FileManager.default
        for dir in [root, transcripts, journal, models] {
            try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        }
        return root
    }
}
