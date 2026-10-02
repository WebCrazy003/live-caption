import Foundation
import GRDB

/// SQLite session-metadata store (SPEC.md §12.3). WAL mode; migrations via GRDB's
/// `DatabaseMigrator` (the idiomatic equivalent of the spec's `PRAGMA user_version`
/// versioning — GRDB records applied migrations in its own bookkeeping table).
public final class Store {
    /// Internal so the interview tables (`InterviewStore.swift`) share the one database.
    let dbQueue: DatabaseQueue

    public init(url: URL = AppPaths.databaseFile) throws {
        var config = Configuration()
        config.prepareDatabase { db in
            try db.execute(sql: "PRAGMA journal_mode = WAL")
        }
        dbQueue = try DatabaseQueue(path: url.path, configuration: config)
        try Store.migrator.migrate(dbQueue)
    }

    private static var migrator: DatabaseMigrator {
        var m = DatabaseMigrator()
        m.registerMigration("v1_sessions") { db in
            try db.create(table: SessionRecord.databaseTableName) { t in
                t.autoIncrementedPrimaryKey("id")
                t.column("session_name", .text).notNull()
                t.column("created_at", .text).notNull()
                t.column("ended_at", .text)
                t.column("duration_seconds", .integer).notNull().defaults(to: 0)
                t.column("transcript_file", .text)
            }
            try db.create(index: "idx_sessions_created",
                          on: SessionRecord.databaseTableName,
                          columns: ["created_at"])
        }
        // Interview Assist (SPEC-11 §SQLite). Existing rows become `caption`. The Windows Store
        // registers the same migration with the same names.
        m.registerMigration("v2_interview") { db in
            try db.alter(table: SessionRecord.databaseTableName) { t in
                t.add(column: "mode", .text).notNull().defaults(to: SessionRecord.captionMode)
                t.add(column: "interview_dir", .text)
            }
        }
        // Interview data lives in the database (owner, 2026-10-02): the record, every turn, the
        // CV/JD/summary/transcript text and the screenshots. Windows registers the same DDL.
        m.registerMigration("v3_interview_store") { db in try InterviewTables.create(db) }
        // Everything in the database (owner, 2026-10-02): each session's caption segments, and the
        // interviewee, company and interview step. The .txt/.json files stay as an export.
        m.registerMigration("v4_segments_and_details") { db in
            try db.execute(sql: """
                CREATE TABLE session_segments (
                    session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                    n INTEGER NOT NULL,
                    text TEXT NOT NULL,
                    t_start_ms INTEGER NOT NULL,
                    t_end_ms INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY (session_id, n)
                );
                ALTER TABLE interviews ADD COLUMN candidate_name TEXT;
                ALTER TABLE interviews ADD COLUMN company TEXT;
                ALTER TABLE interviews ADD COLUMN interview_step INTEGER;
                """)
        }
        return m
    }

    // MARK: CRUD

    /// Insert a session (called on Stop in Phase 2). Returns the record with its assigned id.
    @discardableResult
    public func insert(_ record: SessionRecord) throws -> SessionRecord {
        try dbQueue.write { db in
            var r = record
            try r.insert(db)
            return r
        }
    }

    /// Insert a session and its caption segments in one transaction.
    @discardableResult
    public func insert(_ record: SessionRecord, segments: [TranscriptSegment]) throws -> SessionRecord {
        try dbQueue.write { db in
            var r = record
            try r.insert(db)
            if let id = r.id { try Self.writeSegments(segments, sessionId: id, db) }
            return r
        }
    }

    /// A session's caption segments, in order.
    public func segments(sessionId: Int64) throws -> [TranscriptSegment] {
        try dbQueue.read { db in
            try Row.fetchAll(db, sql: "SELECT * FROM session_segments WHERE session_id = ? ORDER BY n",
                             arguments: [sessionId]).map { row in
                TranscriptSegment(text: row["text"], tStartMs: row["t_start_ms"], tEndMs: row["t_end_ms"],
                                  createdAt: row["created_at"])
            }
        }
    }

    public func segmentCount(sessionId: Int64) throws -> Int {
        try dbQueue.read { db in
            try Int.fetchOne(db, sql: "SELECT COUNT(*) FROM session_segments WHERE session_id = ?",
                             arguments: [sessionId]) ?? 0
        }
    }

    /// Sessions saved by earlier builds keep their captions only in their transcript files: copy
    /// them in once — the `.json` sidecar's segments, else the `.txt` body line by line. Files are
    /// left as they are. Returns how many sessions were imported.
    @discardableResult
    public func importTranscriptFiles() throws -> Int {
        let missing = try dbQueue.read { db in
            try SessionRecord.fetchAll(db, sql: """
                SELECT * FROM sessions WHERE transcript_file IS NOT NULL
                AND id NOT IN (SELECT DISTINCT session_id FROM session_segments)
                """)
        }
        var imported = 0
        for rec in missing {
            guard let id = rec.id, let path = rec.transcriptFile,
                  let segs = TranscriptFileReader.segments(txtPath: path, createdAt: rec.createdAt), !segs.isEmpty
            else { continue }
            try dbQueue.write { db in try Self.writeSegments(segs, sessionId: id, db) }
            imported += 1
        }
        return imported
    }

    private static func writeSegments(_ segments: [TranscriptSegment], sessionId: Int64, _ db: Database) throws {
        for (i, s) in segments.enumerated() {
            try db.execute(sql: """
                INSERT OR REPLACE INTO session_segments (session_id, n, text, t_start_ms, t_end_ms, created_at)
                VALUES (?, ?, ?, ?, ?, ?)
                """, arguments: [sessionId, i + 1, s.text, s.tStartMs, s.tEndMs, s.createdAt])
        }
    }

    /// Rename edits metadata only — it does NOT rename the transcript file (SPEC.md §10, C9).
    public func rename(id: Int64, to name: String) throws {
        try dbQueue.write { db in
            try db.execute(
                sql: "UPDATE \(SessionRecord.databaseTableName) SET session_name = ? WHERE id = ?",
                arguments: [name, id]
            )
        }
    }

    /// Point a session at its exported `.txt`.
    public func setTranscriptFile(id: Int64, path: String) throws {
        try dbQueue.write { db in
            try db.execute(sql: "UPDATE \(SessionRecord.databaseTableName) SET transcript_file = ? WHERE id = ?",
                           arguments: [path, id])
        }
    }

    /// Delete the row and its caption segments. Removing the exported transcript file is a
    /// separate, confirmed step (SPEC-06).
    public func delete(id: Int64) throws {
        _ = try dbQueue.write { db in
            try SessionRecord.deleteOne(db, key: id)
        }
    }

    /// Mark a saved session as an interview and point it at its interview folder.
    public func setInterview(id: Int64, dir: String) throws {
        try dbQueue.write { db in
            try db.execute(
                sql: "UPDATE \(SessionRecord.databaseTableName) SET mode = ?, interview_dir = ? WHERE id = ?",
                arguments: [SessionRecord.interviewMode, dir, id]
            )
        }
    }

    public func fetch(id: Int64) throws -> SessionRecord? {
        try dbQueue.read { db in try SessionRecord.fetchOne(db, key: id) }
    }

    /// List sessions with optional name search and sort (backs SPEC-06).
    public func all(sort: SessionSort = .createdDesc, search: String? = nil,
                    mode: String? = nil) throws -> [SessionRecord] {
        try dbQueue.read { db in
            var request = SessionRecord.all()
            if let mode { request = request.filter(SessionRecord.Columns.mode == mode) }
            if let q = search?.trimmingCharacters(in: .whitespacesAndNewlines), !q.isEmpty {
                request = request.filter(SessionRecord.Columns.sessionName.like("%\(q)%"))
            }
            switch sort {
            case .createdDesc:  request = request.order(SessionRecord.Columns.createdAt.desc)
            case .createdAsc:   request = request.order(SessionRecord.Columns.createdAt.asc)
            case .nameAsc:      request = request.order(SessionRecord.Columns.sessionName.asc)
            case .nameDesc:     request = request.order(SessionRecord.Columns.sessionName.desc)
            case .durationDesc: request = request.order(SessionRecord.Columns.durationSeconds.desc)
            case .durationAsc:  request = request.order(SessionRecord.Columns.durationSeconds.asc)
            }
            return try request.fetchAll(db)
        }
    }

    public func count() throws -> Int {
        try dbQueue.read { db in try SessionRecord.fetchCount(db) }
    }
}
