import Foundation
import GRDB

/// SQLite session-metadata store (SPEC.md §12.3). WAL mode; migrations via GRDB's
/// `DatabaseMigrator` (the idiomatic equivalent of the spec's `PRAGMA user_version`
/// versioning — GRDB records applied migrations in its own bookkeeping table).
public final class Store {
    private let dbQueue: DatabaseQueue

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

    /// Rename edits metadata only — it does NOT rename the transcript file (SPEC.md §10, C9).
    public func rename(id: Int64, to name: String) throws {
        try dbQueue.write { db in
            try db.execute(
                sql: "UPDATE \(SessionRecord.databaseTableName) SET session_name = ? WHERE id = ?",
                arguments: [name, id]
            )
        }
    }

    /// Delete the DB row only. Removing the transcript file is a separate, confirmed step (SPEC-06).
    public func delete(id: Int64) throws {
        _ = try dbQueue.write { db in
            try SessionRecord.deleteOne(db, key: id)
        }
    }

    public func fetch(id: Int64) throws -> SessionRecord? {
        try dbQueue.read { db in try SessionRecord.fetchOne(db, key: id) }
    }

    /// List sessions with optional name search and sort (backs SPEC-06).
    public func all(sort: SessionSort = .createdDesc, search: String? = nil) throws -> [SessionRecord] {
        try dbQueue.read { db in
            var request = SessionRecord.all()
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
