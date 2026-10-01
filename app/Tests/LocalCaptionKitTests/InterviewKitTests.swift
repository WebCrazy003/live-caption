import XCTest
import GRDB
@testable import LocalCaptionKit

/// Interview Assist pieces that are not covered by shared vectors: records, library index,
/// slugs, skill files, folder names and the `v2_interview` migration (SPEC-11, SPEC-13).
final class InterviewKitTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lc-interview-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    // MARK: Record

    func testRecordRoundTripsWithSnakeCaseKeys() throws {
        var rec = InterviewRecord(name: "Acme — Senior iOS", createdAt: "2026-10-01T09:00:00Z",
                                  model: "gpt-6-luna", reasoningEffort: "low",
                                  setup: .init(company: "Acme", role: "Senior iOS", skillIds: ["s1"], documentIds: ["d1"]))
        rec.threadId = "thr_1"
        rec.turns.append(.init(n: 1, kind: .ask, question: "Tell me about yourself", audioFromMs: 0, audioToMs: 5_000,
                               images: ["attachments/1-1.png"], answer: "**Q:** …", status: .completed,
                               askedAt: "2026-10-01T09:05:00Z", ttftMs: 1_300, totalMs: 3_600))
        let folder = try InterviewFiles.makeFolder(in: dir, date: Date(timeIntervalSince1970: 0), name: rec.name)
        try InterviewFiles.write(rec, to: folder)

        XCTAssertEqual(try InterviewFiles.read(from: folder), rec)
        let json = try String(contentsOf: folder.appendingPathComponent("interview.json"))
        for key in ["\"schema_version\"", "\"thread_id\"", "\"audio_from_ms\"", "\"ttft_ms\"", "\"skill_ids\"", "\"extra_turns\""] {
            XCTAssertTrue(json.contains(key), "missing \(key)")
        }
        XCTAssertTrue(FileManager.default.fileExists(atPath: folder.appendingPathComponent("attachments").path))
        XCTAssertEqual(InterviewFiles.all(in: dir).map(\.record.id), [rec.id])
    }

    func testInterruptedTurnsFailOnRelaunch() {
        var rec = InterviewRecord(name: "x", createdAt: "t", model: "m", reasoningEffort: "low", setup: .init())
        rec.prep.status = .running
        rec.turns = [.init(n: 1, kind: .ask, question: "a", status: .completed, askedAt: "t"),
                     .init(n: 2, kind: .ask, question: "b", status: .streaming, askedAt: "t")]
        XCTAssertTrue(rec.failInterruptedTurns())
        XCTAssertEqual(rec.turns.map(\.status), [.completed, .failed])
        XCTAssertEqual(rec.turns[1].error, "app closed")
        XCTAssertEqual(rec.prep.status, .failed)
        XCTAssertEqual(rec.nextTurnNumber, 3)
        XCTAssertFalse(rec.failInterruptedTurns())
    }

    func testQAMarkdownExport() {
        var rec = InterviewRecord(name: "Acme — iOS", createdAt: "t", model: "m", reasoningEffort: "low",
                                  setup: .init(company: "Acme", role: "iOS"))
        rec.turns = [.init(n: 1, kind: .ask, question: "why us", audioToMs: 65_000, images: ["attachments/1-1.png"],
                           answer: "**Q:** Why us?\nBecause.", status: .completed, askedAt: "t"),
                     .init(n: 2, kind: .typed, question: "shorter", answer: "", status: .failed, error: "offline", askedAt: "t")]
        XCTAssertEqual(rec.qaMarkdown(), """
        # Acme — iOS

        ## 1. [00:01:05] why us

        _1 screenshot(s)_

        **Q:** Why us?
        Because.

        ## 2. You: shorter

        _(no answer)_

        _(failed: offline)_
        """)
    }

    // MARK: Folders

    func testFolderNameIsSafeOnBothPlatforms() {
        let d = Date(timeIntervalSince1970: 0)
        let name = InterviewFiles.folderName(date: d, name: "Acme: Senior/iOS  <Lead>?.. ")
        XCTAssertFalse(name.contains(where: { "/\\:*?\"<>|".contains($0) }))
        XCTAssertFalse(name.hasSuffix(".") || name.hasSuffix(" "))
        XCTAssertTrue(name.hasSuffix(" Acme- Senior-iOS -Lead--"), name)   // `>` and `?` → `-`; trailing `..` dropped
    }

    func testFolderCollisionGetsSuffix() throws {
        let d = Date(timeIntervalSince1970: 0)
        let a = try InterviewFiles.makeFolder(in: dir, date: d, name: "Same")
        let b = try InterviewFiles.makeFolder(in: dir, date: d, name: "Same")
        XCTAssertNotEqual(a, b)
        XCTAssertTrue(b.lastPathComponent.hasSuffix(" (2)"))
    }

    // MARK: Library

    func testSlugs() {
        XCTAssertEqual(LibrarySlug.make("CV 2026 — Final!"), "cv-2026-final")
        XCTAssertEqual(LibrarySlug.make("Résumé"), "resume")
        XCTAssertEqual(LibrarySlug.make("***"), "item")
        XCTAssertEqual(LibrarySlug.make("cv", taken: ["cv", "cv-2"]), "cv-3")
    }

    func testLibraryIndexPreservesUnknownKeysAndRepairsCorruption() throws {
        let url = dir.appendingPathComponent("library/index.json")
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(#"{"schema_version":1,"documents":[],"skills":[],"future_key":42}"#.utf8).write(to: url)
        var idx = InterviewLibraryIndex.load(from: url)
        idx.documents.append(.init(slug: "cv", kind: .cv, title: "CV", original: "original.pdf", chars: 10, addedAt: "t"))
        try idx.write(to: url)
        let raw = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any]
        XCTAssertEqual(raw?["future_key"] as? Int, 42)
        XCTAssertEqual(InterviewLibraryIndex.load(from: url).documents.map(\.slug), ["cv"])

        try Data("{ not json".utf8).write(to: url)
        XCTAssertEqual(InterviewLibraryIndex.load(from: url), InterviewLibraryIndex())
        let backups = try FileManager.default.contentsOfDirectory(atPath: url.deletingLastPathComponent().path)
            .filter { $0.hasPrefix("index.json.bak-") }
        XCTAssertEqual(backups.count, 1)
    }

    func testSkillFrontMatterAndFilePartition() {
        let fm = SkillFile.frontMatter("---\nname: \"Interview coach\"\ndescription: STAR answers\n---\n# Body")
        XCTAssertEqual(fm.name, "Interview coach")
        XCTAssertEqual(fm.description, "STAR answers")
        XCTAssertNil(SkillFile.frontMatter("# No front matter").name)

        let p = SkillFile.partition(["SKILL.md", "z.txt", "refs/a.md", "run.sh", ".hidden/x.md", "img.png"])
        XCTAssertEqual(p.included, ["refs/a.md", "z.txt"])
        XCTAssertEqual(p.ignored, ["img.png", "run.sh"])
    }

    // MARK: Prompt cap

    func testSummaryKeepsTheLast15000Words() {
        let words = (1...15_010).map { "w\($0)" }.joined(separator: " ")
        let msg = InterviewPrompt.summary(transcript: words)
        XCTAssertFalse(msg.contains("w10 "))
        XCTAssertTrue(msg.contains("w11 "))
        XCTAssertTrue(msg.contains("w15010\n"))
    }

    // MARK: Store migration

    func testV2InterviewMigrationDefaultsExistingRowsToCaption() throws {
        let url = dir.appendingPathComponent("legacy.db")
        // A v1 database written before Interview Assist.
        let legacy = try DatabaseQueue(path: url.path)
        try legacy.write { db in
            try db.execute(sql: """
                CREATE TABLE sessions (id INTEGER PRIMARY KEY AUTOINCREMENT, session_name TEXT NOT NULL,
                  created_at TEXT NOT NULL, ended_at TEXT, duration_seconds INTEGER NOT NULL DEFAULT 0,
                  transcript_file TEXT);
                CREATE INDEX idx_sessions_created ON sessions(created_at);
                CREATE TABLE grdb_migrations (identifier TEXT NOT NULL PRIMARY KEY);
                INSERT INTO grdb_migrations VALUES ('v1_sessions');
                INSERT INTO sessions (session_name, created_at) VALUES ('Old call', '2026-01-01T00:00:00Z');
                """)
        }
        try legacy.close()

        let store = try Store(url: url)
        let old = try XCTUnwrap(store.all().first)
        XCTAssertEqual(old.mode, "caption")
        XCTAssertNil(old.interviewDir)

        let new = try store.insert(SessionRecord(sessionName: "Acme", createdAt: "2026-10-01T00:00:00Z"))
        try store.setInterview(id: try XCTUnwrap(new.id), dir: "/tmp/acme")
        XCTAssertEqual(try store.all(mode: "interview").map(\.sessionName), ["Acme"])
        XCTAssertEqual(try store.all(mode: "caption").map(\.sessionName), ["Old call"])
        XCTAssertEqual(try store.fetch(id: try XCTUnwrap(new.id))?.interviewDir, "/tmp/acme")
    }
}
