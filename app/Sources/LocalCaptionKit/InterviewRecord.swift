import Foundation

/// `interview.json` (SPEC-11 §On-disk layout, schema 1). One per interview, in
/// `interview/interviews/<yyyy-MM-dd HHmm> <name>/`. Written atomically after every state change,
/// so a crash loses at most the in-flight answer text; the Codex thread itself survives and is
/// resumable by `threadId`.
public struct InterviewRecord: Codable, Equatable, Identifiable, Sendable {
    public static let currentSchemaVersion = 1

    public enum Status: String, Codable, Sendable { case pending, running, done, failed, skipped }
    public enum TurnKind: String, Codable, Sendable { case ask, typed, quick, regenerate }
    public enum TurnStatus: String, Codable, Sendable { case streaming, completed, interrupted, failed }

    public struct Setup: Codable, Equatable, Sendable {
        public var company: String
        public var role: String
        public var skillIds: [String]
        public var documentIds: [String]
        public var jdTextInline: String?
        public var instructions: String
        public var answerLength: String
        public init(company: String = "", role: String = "", skillIds: [String] = [], documentIds: [String] = [],
                    jdTextInline: String? = nil, instructions: String = "", answerLength: String = "medium") {
            self.company = company; self.role = role; self.skillIds = skillIds; self.documentIds = documentIds
            self.jdTextInline = jdTextInline; self.instructions = instructions; self.answerLength = answerLength
        }
        enum CodingKeys: String, CodingKey {
            case company, role, instructions
            case skillIds = "skill_ids"
            case documentIds = "document_ids"
            case jdTextInline = "jd_text_inline"
            case answerLength = "answer_length"
        }
    }

    public struct Prep: Codable, Equatable, Sendable {
        public var status: Status
        public var briefing: String
        public var extraTurns: Int
        public var completedAt: String?
        public init(status: Status = .pending, briefing: String = "", extraTurns: Int = 0, completedAt: String? = nil) {
            self.status = status; self.briefing = briefing; self.extraTurns = extraTurns; self.completedAt = completedAt
        }
        enum CodingKeys: String, CodingKey {
            case status, briefing
            case extraTurns = "extra_turns"
            case completedAt = "completed_at"
        }
    }

    public struct Turn: Codable, Equatable, Sendable, Identifiable {
        public var n: Int
        public var kind: TurnKind
        public var question: String
        public var audioFromMs: Int?
        public var audioToMs: Int?
        public var images: [String]
        public var answer: String
        public var status: TurnStatus
        public var error: String?
        public var askedAt: String
        public var ttftMs: Int?
        public var totalMs: Int?
        public var id: Int { n }
        public init(n: Int, kind: TurnKind, question: String, audioFromMs: Int? = nil, audioToMs: Int? = nil,
                    images: [String] = [], answer: String = "", status: TurnStatus = .streaming, error: String? = nil,
                    askedAt: String, ttftMs: Int? = nil, totalMs: Int? = nil) {
            self.n = n; self.kind = kind; self.question = question
            self.audioFromMs = audioFromMs; self.audioToMs = audioToMs; self.images = images
            self.answer = answer; self.status = status; self.error = error
            self.askedAt = askedAt; self.ttftMs = ttftMs; self.totalMs = totalMs
        }
        enum CodingKeys: String, CodingKey {
            case n, kind, question, images, answer, status, error
            case audioFromMs = "audio_from_ms"
            case audioToMs = "audio_to_ms"
            case askedAt = "asked_at"
            case ttftMs = "ttft_ms"
            case totalMs = "total_ms"
        }
    }

    public struct Summary: Codable, Equatable, Sendable {
        public var status: Status
        public var file: String?
        public var completedAt: String?
        public init(status: Status = .pending, file: String? = nil, completedAt: String? = nil) {
            self.status = status; self.file = file; self.completedAt = completedAt
        }
        enum CodingKeys: String, CodingKey {
            case status, file
            case completedAt = "completed_at"
        }
    }

    public var schemaVersion: Int
    public var id: String
    public var name: String
    public var sessionId: Int64?
    public var captureSessionUUID: String?
    public var createdAt: String
    public var startedAt: String?
    public var endedAt: String?
    public var engine: String
    public var model: String
    public var reasoningEffort: String
    public var threadId: String?
    public var setup: Setup
    public var prep: Prep
    public var turns: [Turn]
    public var summary: Summary

    public init(id: String = UUID().uuidString, name: String, createdAt: String, engine: String = "codex",
                model: String, reasoningEffort: String, setup: Setup) {
        self.schemaVersion = InterviewRecord.currentSchemaVersion
        self.id = id; self.name = name; self.sessionId = nil; self.captureSessionUUID = nil
        self.createdAt = createdAt; self.startedAt = nil; self.endedAt = nil
        self.engine = engine; self.model = model; self.reasoningEffort = reasoningEffort
        self.threadId = nil; self.setup = setup; self.prep = Prep(); self.turns = []; self.summary = Summary()
    }

    enum CodingKeys: String, CodingKey {
        case id, name, engine, model, setup, prep, turns, summary
        case schemaVersion = "schema_version"
        case sessionId = "session_id"
        case captureSessionUUID = "capture_session_uuid"
        case createdAt = "created_at"
        case startedAt = "started_at"
        case endedAt = "ended_at"
        case reasoningEffort = "reasoning_effort"
        case threadId = "thread_id"
    }

    /// Next turn number (1-based).
    public var nextTurnNumber: Int { (turns.map(\.n).max() ?? 0) + 1 }

    /// On launch: any turn still `streaming` was cut off by a quit or crash (SPEC-15 §History).
    public mutating func failInterruptedTurns(reason: String = "app closed") -> Bool {
        var changed = false
        for i in turns.indices where turns[i].status == .streaming {
            turns[i].status = .failed; turns[i].error = reason; changed = true
        }
        if prep.status == .running { prep.status = .failed; changed = true }
        if summary.status == .running { summary.status = .failed; changed = true }
        return changed
    }
}

// MARK: - Folder + persistence

public enum InterviewFiles {
    public static let recordName = "interview.json"
    public static let summaryName = "summary.md"
    public static let attachmentsName = "attachments"

    /// `yyyy-MM-dd HHmm <name>`, made filesystem-safe on both platforms.
    public static func folderName(date: Date, name: String) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd HHmm"
        let safe = sanitize(name)
        return safe.isEmpty ? f.string(from: date) : "\(f.string(from: date)) \(safe)"
    }

    /// Replace characters Windows or macOS reject in a path component; collapse whitespace;
    /// cap at 80 characters; strip trailing dots/spaces (Windows).
    public static func sanitize(_ name: String) -> String {
        let bad = CharacterSet(charactersIn: "/\\:*?\"<>|").union(.controlCharacters)
        let replaced = name.unicodeScalars.map { bad.contains($0) ? "-" : String($0) }.joined()
        let collapsed = replaced.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        var out = String(collapsed.prefix(80))
        while let last = out.last, last == "." || last == " " { out.removeLast() }
        return out
    }

    /// Create a new, unique interview folder under `root` (` (2)`, ` (3)`… on collision).
    public static func makeFolder(in root: URL, date: Date, name: String) throws -> URL {
        let fm = FileManager.default
        try fm.createDirectory(at: root, withIntermediateDirectories: true)
        let base = folderName(date: date, name: name)
        var url = root.appendingPathComponent(base, isDirectory: true)
        var n = 2
        while fm.fileExists(atPath: url.path) {
            url = root.appendingPathComponent("\(base) (\(n))", isDirectory: true); n += 1
        }
        try fm.createDirectory(at: url.appendingPathComponent(attachmentsName, isDirectory: true),
                               withIntermediateDirectories: true)
        return url
    }

    public static func write(_ record: InterviewRecord, to folder: URL) throws {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        try enc.encode(record).write(to: folder.appendingPathComponent(recordName), options: .atomic)
    }

    public static func read(from folder: URL) throws -> InterviewRecord {
        try JSONDecoder().decode(InterviewRecord.self,
                                 from: Data(contentsOf: folder.appendingPathComponent(recordName)))
    }

    /// Every interview folder with a readable record, newest folder name first.
    public static func all(in root: URL) -> [(folder: URL, record: InterviewRecord)] {
        let dirs = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
        return dirs.sorted { $0.lastPathComponent > $1.lastPathComponent }
            .compactMap { dir in (try? read(from: dir)).map { (dir, $0) } }
    }
}

// MARK: - Library index

/// `interview/library/index.json` (SPEC-11, schema 1): the user's imported documents and skills.
public struct InterviewLibraryIndex: Codable, Equatable, Sendable {
    public static let currentSchemaVersion = 1

    public enum DocumentKind: String, Codable, CaseIterable, Sendable { case cv, jd, notes }

    public struct Document: Codable, Equatable, Identifiable, Sendable {
        public var id: String
        public var slug: String
        public var kind: DocumentKind
        public var title: String
        public var original: String
        public var chars: Int
        public var addedAt: String
        public init(id: String = UUID().uuidString, slug: String, kind: DocumentKind, title: String,
                    original: String, chars: Int, addedAt: String) {
            self.id = id; self.slug = slug; self.kind = kind; self.title = title
            self.original = original; self.chars = chars; self.addedAt = addedAt
        }
        enum CodingKeys: String, CodingKey {
            case id, slug, kind, title, original, chars
            case addedAt = "added_at"
        }
    }

    public struct Skill: Codable, Equatable, Identifiable, Sendable {
        public var id: String
        public var slug: String
        public var title: String
        public var files: [String]
        public var chars: Int
        public var addedAt: String
        public init(id: String = UUID().uuidString, slug: String, title: String, files: [String],
                    chars: Int, addedAt: String) {
            self.id = id; self.slug = slug; self.title = title; self.files = files
            self.chars = chars; self.addedAt = addedAt
        }
        enum CodingKeys: String, CodingKey {
            case id, slug, title, files, chars
            case addedAt = "added_at"
        }
    }

    public var schemaVersion: Int
    public var documents: [Document]
    public var skills: [Skill]

    public init(documents: [Document] = [], skills: [Skill] = []) {
        self.schemaVersion = InterviewLibraryIndex.currentSchemaVersion
        self.documents = documents; self.skills = skills
    }

    enum CodingKeys: String, CodingKey {
        case documents, skills
        case schemaVersion = "schema_version"
    }

    public init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        schemaVersion = try c.decodeIfPresent(Int.self, forKey: .schemaVersion) ?? InterviewLibraryIndex.currentSchemaVersion
        documents = try c.decodeIfPresent([Document].self, forKey: .documents) ?? []
        skills = try c.decodeIfPresent([Skill].self, forKey: .skills) ?? []
    }

    /// Missing file → empty index. A corrupt file is backed up and replaced, like `config.json`.
    public static func load(from url: URL) -> InterviewLibraryIndex {
        guard let data = try? Data(contentsOf: url) else { return InterviewLibraryIndex() }
        if let idx = try? JSONDecoder().decode(InterviewLibraryIndex.self, from: data) { return idx }
        let backup = url.deletingLastPathComponent()
            .appendingPathComponent("index.json.bak-\(Config.backupStamp())")
        try? FileManager.default.copyItem(at: url, to: backup)
        return InterviewLibraryIndex()
    }

    /// Atomic write. Unknown top-level keys from a newer build are preserved.
    public func write(to url: URL) throws {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        var out = try JSONSerialization.jsonObject(with: try enc.encode(self)) as? [String: Any] ?? [:]
        if let old = try? Data(contentsOf: url),
           let existing = try? JSONSerialization.jsonObject(with: old) as? [String: Any] {
            for (k, v) in existing where out[k] == nil { out[k] = v }
        }
        let data = try JSONSerialization.data(withJSONObject: out, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url, options: .atomic)
    }
}

// MARK: - Slugs and skill files (shared rules, SPEC-13)

public enum LibrarySlug {
    /// Lowercase ASCII `[a-z0-9-]`, runs of anything else collapsed to one dash, no leading or
    /// trailing dash; `"item"` when nothing is left. A taken slug gets `-2`, `-3`…
    public static func make(_ title: String, taken: Set<String> = []) -> String {
        let folded = title.folding(options: [.diacriticInsensitive, .caseInsensitive, .widthInsensitive],
                                   locale: Locale(identifier: "en_US_POSIX")).lowercased()
        var out = ""
        var dash = false
        for ch in folded.unicodeScalars {
            if ("a"..."z").contains(ch) || ("0"..."9").contains(ch) {
                out.unicodeScalars.append(ch); dash = false
            } else if !dash, !out.isEmpty {
                out.append("-"); dash = true
            }
        }
        while out.hasSuffix("-") { out.removeLast() }
        if out.isEmpty { out = "item" }
        guard taken.contains(out) else { return out }
        var n = 2
        while taken.contains("\(out)-\(n)") { n += 1 }
        return "\(out)-\(n)"
    }
}

public enum SkillFile {
    public static let mainName = "SKILL.md"
    /// Per-skill size the Prepare panel warns above (SPEC-13).
    public static let maxChars = 60_000

    public struct FrontMatter: Equatable, Sendable {
        public var name: String?
        public var description: String?
    }

    /// Parse an optional leading `---` YAML block for `name` / `description` (single-line values,
    /// optional quotes). Anything else in the block is ignored.
    public static func frontMatter(_ text: String) -> FrontMatter {
        let lines = text.replacingOccurrences(of: "\r\n", with: "\n").split(separator: "\n", omittingEmptySubsequences: false)
        guard lines.first?.trimmingCharacters(in: .whitespaces) == "---" else { return FrontMatter() }
        var fm = FrontMatter()
        for line in lines.dropFirst() {
            let l = line.trimmingCharacters(in: .whitespaces)
            if l == "---" { break }
            guard let colon = l.firstIndex(of: ":") else { continue }
            let key = l[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            var value = l[l.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            if value.count >= 2, let f = value.first, let last = value.last, (f == "\"" || f == "'"), f == last {
                value = String(value.dropFirst().dropLast())
            }
            if key == "name" { fm.name = value }
            if key == "description" { fm.description = value }
        }
        return fm
    }

    /// Split a skill folder's relative paths into the reference files that get inlined (`.md`
    /// and `.txt`, excluding `SKILL.md`, sorted by path) and the ones that are ignored.
    public static func partition(_ relativePaths: [String]) -> (included: [String], ignored: [String]) {
        var included: [String] = [], ignored: [String] = []
        for p in relativePaths.sorted() {
            if p == mainName { continue }
            let ext = (p as NSString).pathExtension.lowercased()
            let hidden = p.split(separator: "/").contains { $0.hasPrefix(".") }
            if hidden { continue }
            if ext == "md" || ext == "txt" { included.append(p) } else { ignored.append(p) }
        }
        return (included, ignored)
    }
}
