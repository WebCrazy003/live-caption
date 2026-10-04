import Foundation
import GRDB

/// The interview tables (owner, 2026-10-02: "a local DB storing all interview session data").
/// One row per interview, one per turn, one per screenshot (PNG bytes) — in the same
/// `localcaption.db` as the sessions list. The DDL is part of the cross-platform contract
/// (SPEC-11 §SQLite); the Windows store creates the same tables.
enum InterviewTables {
    static func create(_ db: Database) throws {
        try db.execute(sql: """
            CREATE TABLE interviews (
                id TEXT PRIMARY KEY,
                session_id INTEGER REFERENCES sessions(id) ON DELETE SET NULL,
                name TEXT NOT NULL,
                created_at TEXT NOT NULL,
                started_at TEXT,
                ended_at TEXT,
                capture_session_uuid TEXT,
                engine TEXT NOT NULL,
                model TEXT NOT NULL,
                reasoning_effort TEXT NOT NULL,
                thread_id TEXT,
                cv_document_id TEXT,
                cv_title TEXT,
                cv_text TEXT,
                jd_text TEXT,
                instructions TEXT NOT NULL DEFAULT '',
                answer_length TEXT NOT NULL DEFAULT 'medium',
                skill_ids TEXT NOT NULL DEFAULT '[]',
                legacy_briefing TEXT,
                summary_status TEXT NOT NULL DEFAULT 'pending',
                summary_text TEXT,
                summary_completed_at TEXT,
                transcript TEXT
            );
            CREATE INDEX idx_interviews_session ON interviews(session_id);
            CREATE INDEX idx_interviews_capture ON interviews(capture_session_uuid);
            CREATE TABLE interview_turns (
                interview_id TEXT NOT NULL REFERENCES interviews(id) ON DELETE CASCADE,
                n INTEGER NOT NULL,
                kind TEXT NOT NULL,
                question TEXT NOT NULL,
                answer TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL,
                error TEXT,
                audio_from_ms INTEGER,
                audio_to_ms INTEGER,
                images TEXT NOT NULL DEFAULT '[]',
                asked_at TEXT NOT NULL,
                ttft_ms INTEGER,
                total_ms INTEGER,
                PRIMARY KEY (interview_id, n)
            );
            CREATE TABLE interview_images (
                interview_id TEXT NOT NULL REFERENCES interviews(id) ON DELETE CASCADE,
                name TEXT NOT NULL,
                turn_n INTEGER,
                png BLOB NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (interview_id, name)
            );
            """)
    }
}

extension Store {
    // MARK: Interviews

    /// Insert or update an interview and replace its turns, in one transaction. Screenshots are
    /// written separately (`addInterviewImage`) and are not touched here.
    public func saveInterview(_ r: InterviewRecord) throws {
        try dbQueue.write { db in
            try db.execute(sql: """
                INSERT INTO interviews (id, session_id, name, created_at, started_at, ended_at,
                    capture_session_uuid, engine, model, reasoning_effort, thread_id, cv_document_id,
                    cv_title, cv_text, jd_text, instructions, answer_length, skill_ids, legacy_briefing,
                    summary_status, summary_text, summary_completed_at, transcript,
                    candidate_name, company, interview_step)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(id) DO UPDATE SET
                    session_id = excluded.session_id, name = excluded.name, started_at = excluded.started_at,
                    ended_at = excluded.ended_at, capture_session_uuid = excluded.capture_session_uuid,
                    engine = excluded.engine, model = excluded.model, reasoning_effort = excluded.reasoning_effort,
                    thread_id = excluded.thread_id, cv_document_id = excluded.cv_document_id,
                    cv_title = excluded.cv_title, cv_text = excluded.cv_text, jd_text = excluded.jd_text,
                    instructions = excluded.instructions, answer_length = excluded.answer_length,
                    skill_ids = excluded.skill_ids, legacy_briefing = excluded.legacy_briefing,
                    summary_status = excluded.summary_status, summary_text = excluded.summary_text,
                    summary_completed_at = excluded.summary_completed_at, transcript = excluded.transcript,
                    candidate_name = excluded.candidate_name, company = excluded.company,
                    interview_step = excluded.interview_step
                """, arguments: [
                    r.id, r.sessionId, r.name, r.createdAt, r.startedAt, r.endedAt,
                    r.captureSessionUUID, r.engine, r.model, r.reasoningEffort, r.threadId,
                    r.setup.documentIds.first, r.setup.cvTitle, r.cvText, r.setup.jdTextInline,
                    r.setup.instructions, r.setup.answerLength, Self.json(r.setup.skillIds),
                    r.prep.briefing.isEmpty ? nil : r.prep.briefing,
                    r.summary.status.rawValue, r.summaryText, r.summary.completedAt, r.transcript,
                    r.setup.candidate, r.setup.company.isEmpty ? nil : r.setup.company, r.setup.step,
                ])
            try db.execute(sql: "DELETE FROM interview_turns WHERE interview_id = ?", arguments: [r.id])
            for t in r.turns {
                try db.execute(sql: """
                    INSERT INTO interview_turns (interview_id, n, kind, question, answer, status, error,
                        audio_from_ms, audio_to_ms, images, asked_at, ttft_ms, total_ms)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """, arguments: [
                        r.id, t.n, t.kind.rawValue, t.question, t.answer, t.status.rawValue, t.error,
                        t.audioFromMs, t.audioToMs, Self.json(t.images), t.askedAt, t.ttftMs, t.totalMs,
                    ])
            }
        }
    }

    public func interview(id: String) throws -> InterviewRecord? {
        try dbQueue.read { db in
            try Row.fetchOne(db, sql: "SELECT * FROM interviews WHERE id = ?", arguments: [id])
                .map { try Self.record($0, db) }
        }
    }

    public func interview(sessionId: Int64) throws -> InterviewRecord? {
        try dbQueue.read { db in
            try Row.fetchOne(db, sql: "SELECT * FROM interviews WHERE session_id = ? ORDER BY created_at DESC LIMIT 1",
                             arguments: [sessionId]).map { try Self.record($0, db) }
        }
    }

    /// Every interview linked to a session, newest first. Read it BEFORE deleting the session:
    /// `interviews.session_id` is `ON DELETE SET NULL`, so afterwards the link is gone.
    public func interviews(sessionId: Int64) throws -> [InterviewRecord] {
        try dbQueue.read { db in
            try Row.fetchAll(db, sql: "SELECT * FROM interviews WHERE session_id = ? ORDER BY created_at DESC, rowid DESC",
                             arguments: [sessionId]).map { try Self.record($0, db) }
        }
    }

    /// Every interview, newest first.
    public func allInterviews() throws -> [InterviewRecord] {
        try dbQueue.read { db in
            try Row.fetchAll(db, sql: "SELECT * FROM interviews ORDER BY created_at DESC, rowid DESC")
                .map { try Self.record($0, db) }
        }
    }

    /// Deletes the interview, its turns and its screenshots.
    public func deleteInterview(id: String) throws {
        _ = try dbQueue.write { db in try db.execute(sql: "DELETE FROM interviews WHERE id = ?", arguments: [id]) }
    }

    /// Mark a saved session as an interview (the link itself is `interviews.session_id`).
    public func markInterview(sessionId: Int64) throws {
        try dbQueue.write { db in
            try db.execute(sql: "UPDATE \(SessionRecord.databaseTableName) SET mode = ? WHERE id = ?",
                           arguments: [SessionRecord.interviewMode, sessionId])
        }
    }

    // MARK: Screenshots

    public func addInterviewImage(interviewId: String, name: String, turn: Int?, png: Data) throws {
        try dbQueue.write { db in
            try db.execute(sql: """
                INSERT OR REPLACE INTO interview_images (interview_id, name, turn_n, png, created_at)
                VALUES (?, ?, ?, ?, ?)
                """, arguments: [interviewId, name, turn, png, TimeFormat.iso(Date())])
        }
    }

    public func interviewImage(interviewId: String, name: String) throws -> Data? {
        try dbQueue.read { db in
            try Data.fetchOne(db, sql: "SELECT png FROM interview_images WHERE interview_id = ? AND name = ?",
                              arguments: [interviewId, name])
        }
    }

    public func interviewImageCount(interviewId: String) throws -> Int {
        try dbQueue.read { db in
            try Int.fetchOne(db, sql: "SELECT COUNT(*) FROM interview_images WHERE interview_id = ?",
                             arguments: [interviewId]) ?? 0
        }
    }

    // MARK: Legacy folders

    /// Import an `interviews/<folder>` written by earlier builds (interview.json + summary.md +
    /// cv.txt + attachments/*.png). Returns false when it was already imported or unreadable.
    /// The folder is left untouched.
    @discardableResult
    public func importLegacyInterview(folder: URL) throws -> Bool {
        guard var rec = try? InterviewFiles.read(from: folder), try interview(id: rec.id) == nil else { return false }
        let read = { (name: String) in try? String(contentsOf: folder.appendingPathComponent(name), encoding: .utf8) }
        if rec.summaryText == nil { rec.summaryText = read(InterviewFiles.summaryName) }
        if rec.cvText == nil { rec.cvText = read(InterviewFiles.cvName) }
        if let sessionId = rec.sessionId, try fetch(id: sessionId) == nil { rec.sessionId = nil }
        try saveInterview(rec)
        for t in rec.turns {
            for (i, rel) in t.images.enumerated() {
                guard let png = try? Data(contentsOf: folder.appendingPathComponent(rel)) else { continue }
                try addInterviewImage(interviewId: rec.id, name: Self.imageName(turn: t.n, index: i + 1, legacyPath: rel),
                                      turn: t.n, png: png)
            }
        }
        // Image references become database names.
        rec.turns = rec.turns.map { t in
            var t = t
            t.images = t.images.enumerated().map { Self.imageName(turn: t.n, index: $0.offset + 1, legacyPath: $0.element) }
            return t
        }
        try saveInterview(rec)
        if let sessionId = rec.sessionId { try markInterview(sessionId: sessionId) }
        return true
    }

    /// `<turn>-<index>.png` — the name a screenshot has in `interview_images`.
    public static func imageName(turn: Int, index: Int, legacyPath: String? = nil) -> String {
        legacyPath.map { ($0 as NSString).lastPathComponent } ?? "\(turn)-\(index).png"
    }

    // MARK: Mapping

    private static func json(_ list: [String]) -> String {
        String(decoding: (try? JSONEncoder().encode(list)) ?? Data("[]".utf8), as: UTF8.self)
    }

    private static func list(_ s: String?) -> [String] {
        (s.flatMap { try? JSONDecoder().decode([String].self, from: Data($0.utf8)) }) ?? []
    }

    private static func record(_ row: Row, _ db: Database) throws -> InterviewRecord {
        let id: String = row["id"]
        let cvDoc: String? = row["cv_document_id"]
        var r = InterviewRecord(
            id: id, name: row["name"], createdAt: row["created_at"], engine: row["engine"],
            model: row["model"], reasoningEffort: row["reasoning_effort"],
            setup: .init(skillIds: list(row["skill_ids"]), documentIds: cvDoc.map { [$0] } ?? [],
                         jdTextInline: row["jd_text"], instructions: row["instructions"],
                         answerLength: row["answer_length"]))
        r.setup.cvTitle = row["cv_title"]
        r.setup.candidate = row["candidate_name"]
        r.setup.company = row["company"] ?? ""
        r.setup.step = row["interview_step"]
        r.sessionId = row["session_id"]
        r.startedAt = row["started_at"]
        r.endedAt = row["ended_at"]
        r.captureSessionUUID = row["capture_session_uuid"]
        r.threadId = row["thread_id"]
        r.cvText = row["cv_text"]
        r.summaryText = row["summary_text"]
        r.transcript = row["transcript"]
        if let briefing: String = row["legacy_briefing"] { r.prep = .init(status: .done, briefing: briefing) }
        r.summary = .init(status: InterviewRecord.Status(rawValue: row["summary_status"]) ?? .pending,
                          completedAt: row["summary_completed_at"])
        r.turns = try Row.fetchAll(db, sql: "SELECT * FROM interview_turns WHERE interview_id = ? ORDER BY n",
                                   arguments: [id]).map { t in
            InterviewRecord.Turn(
                n: t["n"], kind: InterviewRecord.TurnKind(rawValue: t["kind"]) ?? .typed, question: t["question"],
                audioFromMs: t["audio_from_ms"], audioToMs: t["audio_to_ms"], images: list(t["images"]),
                answer: t["answer"], status: InterviewRecord.TurnStatus(rawValue: t["status"]) ?? .failed,
                error: t["error"], askedAt: t["asked_at"], ttftMs: t["ttft_ms"], totalMs: t["total_ms"])
        }
        return r
    }
}
