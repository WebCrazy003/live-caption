import Foundation
import GRDB

/// A row in the `sessions` table (SPEC.md §12.3). Its caption segments are in `session_segments`
/// (owner, 2026-10-02); the `.txt`/`.json` files are an export.
public struct SessionRecord: Codable, Equatable, Identifiable,
                             FetchableRecord, MutablePersistableRecord {
    public var id: Int64?
    public var sessionName: String
    /// ISO-8601 UTC string.
    public var createdAt: String
    public var endedAt: String?
    public var durationSeconds: Int
    public var transcriptFile: String?
    /// `caption` | `interview` (SPEC-11 §SQLite, migration `v2_interview`).
    public var mode: String
    /// The interview folder (absolute path) when `mode == "interview"`.
    public var interviewDir: String?
    /// The session's audio recording (absolute path), when recording was on (migration `v5_audio_file`).
    public var audioFile: String?

    public static let captionMode = "caption"
    public static let interviewMode = "interview"
    public var isInterview: Bool { mode == SessionRecord.interviewMode }

    public init(id: Int64? = nil,
                sessionName: String,
                createdAt: String,
                endedAt: String? = nil,
                durationSeconds: Int = 0,
                transcriptFile: String? = nil,
                mode: String = SessionRecord.captionMode,
                interviewDir: String? = nil,
                audioFile: String? = nil) {
        self.id = id
        self.sessionName = sessionName
        self.createdAt = createdAt
        self.endedAt = endedAt
        self.durationSeconds = durationSeconds
        self.transcriptFile = transcriptFile
        self.mode = mode
        self.interviewDir = interviewDir
        self.audioFile = audioFile
    }

    public static let databaseTableName = "sessions"

    enum CodingKeys: String, CodingKey {
        case id
        case sessionName = "session_name"
        case createdAt = "created_at"
        case endedAt = "ended_at"
        case durationSeconds = "duration_seconds"
        case transcriptFile = "transcript_file"
        case mode
        case interviewDir = "interview_dir"
        case audioFile = "audio_file"
    }

    /// GRDB column references for typed queries.
    enum Columns {
        static let id = Column(CodingKeys.id)
        static let sessionName = Column(CodingKeys.sessionName)
        static let createdAt = Column(CodingKeys.createdAt)
        static let durationSeconds = Column(CodingKeys.durationSeconds)
        static let mode = Column(CodingKeys.mode)
    }

    public mutating func didInsert(_ inserted: InsertionSuccess) {
        id = inserted.rowID
    }
}

/// Sort options for the session list (SPEC.md §10 / SPEC-06).
public enum SessionSort: String, CaseIterable, Sendable {
    case createdDesc, createdAsc
    case nameAsc, nameDesc
    case durationDesc, durationAsc
}
