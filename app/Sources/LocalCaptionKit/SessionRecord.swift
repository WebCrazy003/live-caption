import Foundation
import GRDB

/// A row in the `sessions` table (SPEC.md §12.3). Metadata only — transcript text
/// never lives in SQLite; it is written to a `.txt` file (Phase 2).
public struct SessionRecord: Codable, Equatable, Identifiable,
                             FetchableRecord, MutablePersistableRecord {
    public var id: Int64?
    public var sessionName: String
    /// ISO-8601 UTC string.
    public var createdAt: String
    public var endedAt: String?
    public var durationSeconds: Int
    public var transcriptFile: String?

    public init(id: Int64? = nil,
                sessionName: String,
                createdAt: String,
                endedAt: String? = nil,
                durationSeconds: Int = 0,
                transcriptFile: String? = nil) {
        self.id = id
        self.sessionName = sessionName
        self.createdAt = createdAt
        self.endedAt = endedAt
        self.durationSeconds = durationSeconds
        self.transcriptFile = transcriptFile
    }

    public static let databaseTableName = "sessions"

    enum CodingKeys: String, CodingKey {
        case id
        case sessionName = "session_name"
        case createdAt = "created_at"
        case endedAt = "ended_at"
        case durationSeconds = "duration_seconds"
        case transcriptFile = "transcript_file"
    }

    /// GRDB column references for typed queries.
    enum Columns {
        static let id = Column(CodingKeys.id)
        static let sessionName = Column(CodingKeys.sessionName)
        static let createdAt = Column(CodingKeys.createdAt)
        static let durationSeconds = Column(CodingKeys.durationSeconds)
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
