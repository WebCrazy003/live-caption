import Foundation

/// Resolves and bootstraps the app's on-disk locations under Application Support.
///
/// Layout (SPEC.md §12, SPEC-00):
/// ```
/// ~/Library/Application Support/LocalCaption/
/// ├── transcripts/      final .txt (+ .json sidecar) — Phase 2
/// ├── journal/          crash-recovery .jsonl        — Phase 2
/// ├── recordings/       audio being recorded (`<session uuid>.m4a`), moved out on save
/// ├── models/           WhisperKit CoreML weights
/// ├── config.json       versioned app config
/// ├── localcaption.db   sqlite session metadata
/// └── interview/        Interview Assist library, records, Codex home (SPEC-11)
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
    public static var recordings: URL { root.appendingPathComponent("recordings", isDirectory: true) }
    public static var models: URL { root.appendingPathComponent("models", isDirectory: true) }
    public static var configFile: URL { root.appendingPathComponent("config.json") }
    public static var databaseFile: URL { root.appendingPathComponent("localcaption.db") }

    // Interview Assist (SPEC-11 §On-disk layout). Kept out of the transcript folder: it holds the CV.
    public static var interview: URL { root.appendingPathComponent("interview", isDirectory: true) }
    public static var interviewLibrary: URL { interview.appendingPathComponent("library", isDirectory: true) }
    public static var interviewLibraryIndex: URL { interviewLibrary.appendingPathComponent("index.json") }
    public static var interviewSkills: URL { interviewLibrary.appendingPathComponent("skills", isDirectory: true) }
    public static var interviewDocuments: URL { interviewLibrary.appendingPathComponent("documents", isDirectory: true) }
    public static var interviews: URL { interview.appendingPathComponent("interviews", isDirectory: true) }
    /// Codex's working directory. Must stay empty (SPEC-12 §Lockdown).
    public static var interviewWorkspace: URL { interview.appendingPathComponent("workspace", isDirectory: true) }
    /// Dedicated CODEX_HOME so the user's own Codex config never loads (SPEC-12 §Lockdown).
    public static var codexHome: URL { interview.appendingPathComponent("codex-home", isDirectory: true) }

    /// Create the directory tree on first launch. Idempotent.
    @discardableResult
    public static func bootstrap() throws -> URL {
        let fm = FileManager.default
        for dir in [root, transcripts, journal, recordings, models] {
            try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        }
        return root
    }
}
