import Foundation
import PDFKit
import AppKit
import LocalCaptionKit

/// The user's interview library — skills, CVs, JDs and notes (SPEC-13 §Library), stored under
/// `interview/library/` as SPEC-11 §On-disk layout describes. Files are copied in on import;
/// the originals are never referenced again.
@MainActor
final class InterviewLibrary: ObservableObject {
    @Published private(set) var index: InterviewLibraryIndex
    @Published var lastError: String?

    private let root: URL
    private var indexURL: URL { root.appendingPathComponent("index.json") }
    private var skillsDir: URL { root.appendingPathComponent("skills", isDirectory: true) }
    private var documentsDir: URL { root.appendingPathComponent("documents", isDirectory: true) }

    init(root: URL = AppPaths.interviewLibrary) {
        self.root = root
        self.index = InterviewLibraryIndex.load(from: root.appendingPathComponent("index.json"))
    }

    var documents: [InterviewLibraryIndex.Document] { index.documents }
    var skills: [InterviewLibraryIndex.Skill] { index.skills }
    func documents(of kind: InterviewLibraryIndex.DocumentKind) -> [InterviewLibraryIndex.Document] {
        index.documents.filter { $0.kind == kind }
    }
    func document(_ id: String) -> InterviewLibraryIndex.Document? { index.documents.first { $0.id == id } }
    func skill(_ id: String) -> InterviewLibraryIndex.Skill? { index.skills.first { $0.id == id } }
    /// The newest skill with this slug (SPEC-13 §Skill steps finds steps by slug).
    func skill(slug: String) -> InterviewLibraryIndex.Skill? { index.skills.last { $0.slug == slug } }

    private func save() {
        do { try index.write(to: indexURL) } catch { lastError = "Could not save the library: \(error.localizedDescription)" }
    }

    // MARK: Documents

    /// Import a `.pdf`, `.md` or `.txt` file. Returns the new document.
    @discardableResult
    func importDocument(from url: URL, kind: InterviewLibraryIndex.DocumentKind) throws -> InterviewLibraryIndex.Document {
        let text = try DocumentText.extract(from: url)
        let title = url.deletingPathExtension().lastPathComponent
        let slug = LibrarySlug.make(title, taken: Set(index.documents.map(\.slug)))
        let dir = documentsDir.appendingPathComponent(slug, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let original = "original." + url.pathExtension.lowercased()
        try FileManager.default.copyItem(at: url, to: dir.appendingPathComponent(original))
        try Data(text.utf8).write(to: dir.appendingPathComponent("text.txt"), options: .atomic)
        let doc = InterviewLibraryIndex.Document(slug: slug, kind: kind, title: title, original: original,
                                                 chars: text.count, addedAt: TimeFormat.iso(Date()))
        index.documents.append(doc)
        save()
        return doc
    }

    /// "Paste text" — a document with no original file.
    @discardableResult
    func addPastedDocument(title: String, text: String, kind: InterviewLibraryIndex.DocumentKind) throws -> InterviewLibraryIndex.Document {
        let clean = DocumentText.normalize(text)
        let name = title.trimmingCharacters(in: .whitespaces).isEmpty ? kind.label : title
        let slug = LibrarySlug.make(name, taken: Set(index.documents.map(\.slug)))
        let dir = documentsDir.appendingPathComponent(slug, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try Data(clean.utf8).write(to: dir.appendingPathComponent("text.txt"), options: .atomic)
        let doc = InterviewLibraryIndex.Document(slug: slug, kind: kind, title: name, original: "",
                                                 chars: clean.count, addedAt: TimeFormat.iso(Date()))
        index.documents.append(doc)
        save()
        return doc
    }

    /// The text that is actually sent (`text.txt`).
    func text(of id: String) -> String {
        guard let d = document(id) else { return "" }
        let url = documentsDir.appendingPathComponent(d.slug).appendingPathComponent("text.txt")
        return (try? String(contentsOf: url, encoding: .utf8)) ?? ""
    }

    /// Save the user's edits to the extracted text (SPEC-13: fixing extraction mistakes).
    func setText(_ text: String, of id: String) {
        guard let i = index.documents.firstIndex(where: { $0.id == id }) else { return }
        let url = documentsDir.appendingPathComponent(index.documents[i].slug).appendingPathComponent("text.txt")
        do {
            try Data(text.utf8).write(to: url, options: .atomic)
            index.documents[i].chars = text.count
            save()
        } catch { lastError = "Could not save the text: \(error.localizedDescription)" }
    }

    func update(_ doc: InterviewLibraryIndex.Document) {
        guard let i = index.documents.firstIndex(where: { $0.id == doc.id }) else { return }
        index.documents[i].title = doc.title
        index.documents[i].kind = doc.kind
        save()
    }

    func deleteDocument(_ id: String) {
        guard let d = document(id) else { return }
        try? FileManager.default.removeItem(at: documentsDir.appendingPathComponent(d.slug))
        index.documents.removeAll { $0.id == id }
        save()
    }

    // MARK: Skills

    struct SkillImport {
        let skill: InterviewLibraryIndex.Skill
        /// Files left out: Codex can't run or read them under the lockdown (SPEC-13).
        let ignored: [String]
    }

    /// Import a folder containing `SKILL.md`, or a single `.md` file.
    func importSkill(from url: URL) throws -> SkillImport {
        let fm = FileManager.default
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: url.path, isDirectory: &isDir) else { throw LibraryError.missing }

        var main: String
        var refs: [(path: String, text: String)] = []
        var ignored: [String] = []
        var fallbackTitle = url.deletingPathExtension().lastPathComponent
        if isDir.boolValue {
            let skillFile = url.appendingPathComponent(SkillFile.mainName)
            guard fm.fileExists(atPath: skillFile.path) else { throw LibraryError.noSkillFile }
            main = try DocumentText.readText(skillFile)
            let relative = Self.relativeFiles(in: url)
            let parts = SkillFile.partition(relative)
            ignored = parts.ignored
            refs = try parts.included.map { ($0, try DocumentText.readText(url.appendingPathComponent($0))) }
            fallbackTitle = url.lastPathComponent
        } else {
            guard url.pathExtension.lowercased() == "md" else { throw LibraryError.notMarkdown }
            main = try DocumentText.readText(url)
        }

        let title = SkillFile.frontMatter(main).name.flatMap { $0.isEmpty ? nil : $0 } ?? fallbackTitle
        let slug = LibrarySlug.make(title, taken: Set(index.skills.map(\.slug)))
        let dir = skillsDir.appendingPathComponent(slug, isDirectory: true)
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        try Data(main.utf8).write(to: dir.appendingPathComponent(SkillFile.mainName), options: .atomic)
        for r in refs {
            let dest = dir.appendingPathComponent(r.path)
            try fm.createDirectory(at: dest.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(r.text.utf8).write(to: dest, options: .atomic)
        }
        let chars = main.count + refs.reduce(0) { $0 + $1.text.count }
        let skill = InterviewLibraryIndex.Skill(slug: slug, title: title, files: [SkillFile.mainName] + refs.map(\.path),
                                                chars: chars, addedAt: TimeFormat.iso(Date()))
        index.skills.append(skill)
        save()
        return SkillImport(skill: skill, ignored: ignored)
    }

    /// The skill's definition as a skill step sends it (SPEC-13 §Skill message).
    func promptSkill(_ id: String) -> InterviewPrompt.Skill? {
        guard let s = skill(id) else { return nil }
        let dir = skillsDir.appendingPathComponent(s.slug)
        let main = (try? String(contentsOf: dir.appendingPathComponent(SkillFile.mainName), encoding: .utf8)) ?? ""
        let files = s.files.filter { $0 != SkillFile.mainName }.map { path in
            InterviewPrompt.Skill.File(path: path,
                                       text: (try? String(contentsOf: dir.appendingPathComponent(path), encoding: .utf8)) ?? "")
        }
        return InterviewPrompt.Skill(title: s.title, text: main, files: files)
    }

    func skillText(_ id: String) -> String {
        guard let p = promptSkill(id) else { return "" }
        return ([p.text] + p.files.map { "── \($0.path) ──\n\($0.text)" }).joined(separator: "\n\n")
    }

    func renameSkill(_ id: String, to title: String) {
        guard let i = index.skills.firstIndex(where: { $0.id == id }) else { return }
        index.skills[i].title = title
        save()
    }

    func deleteSkill(_ id: String) {
        guard let s = skill(id) else { return }
        try? FileManager.default.removeItem(at: skillsDir.appendingPathComponent(s.slug))
        index.skills.removeAll { $0.id == id }
        save()
    }

    private static func relativeFiles(in dir: URL) -> [String] {
        guard let e = FileManager.default.enumerator(at: dir, includingPropertiesForKeys: [.isRegularFileKey]) else { return [] }
        let base = dir.standardizedFileURL.path + "/"
        return e.compactMap { item -> String? in
            guard let u = item as? URL, (try? u.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile == true
            else { return nil }
            return u.standardizedFileURL.path.replacingOccurrences(of: base, with: "")
        }
    }

    enum LibraryError: LocalizedError {
        case missing, noSkillFile, notMarkdown
        var errorDescription: String? {
            switch self {
            case .missing: return "That file no longer exists."
            case .noSkillFile: return "That folder has no SKILL.md."
            case .notMarkdown: return "A single-file skill must be a .md file."
            }
        }
    }
}

extension InterviewLibraryIndex.DocumentKind {
    var label: String {
        switch self {
        case .cv: return "CV"
        case .jd: return "Job description"
        case .notes: return "Notes"
        }
    }
}

/// Text extraction for imported documents (SPEC-13 §Documents). What this returns is what gets
/// sent, so it is shown to the user and editable.
enum DocumentText {
    enum Failure: LocalizedError {
        case scannedPDF, unreadable, unsupported(String)
        var errorDescription: String? {
            switch self {
            case .scannedPDF: return "This PDF is a scanned image; paste the text instead."
            case .unreadable: return "Couldn't read that file."
            case .unsupported(let ext): return "“.\(ext)” files aren't supported — use PDF, Markdown or plain text, or paste the text."
            }
        }
    }

    static func extract(from url: URL) throws -> String {
        switch url.pathExtension.lowercased() {
        case "pdf":
            guard let doc = PDFDocument(url: url) else { throw Failure.unreadable }
            let pages = (0..<doc.pageCount).compactMap { doc.page(at: $0)?.string }
                .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                .filter { !$0.isEmpty }
            guard !pages.isEmpty else { throw Failure.scannedPDF }
            return normalize(pages.joined(separator: "\n\n"))
        case "md", "markdown", "txt", "text":
            return normalize(try readText(url))
        case let ext:
            throw Failure.unsupported(ext)
        }
    }

    /// UTF-8, falling back to Windows-1252.
    static func readText(_ url: URL) throws -> String {
        let data = try Data(contentsOf: url)
        if let s = String(data: data, encoding: .utf8) { return s }
        if let s = String(data: data, encoding: .windowsCP1252) { return s }
        throw Failure.unreadable
    }

    /// `\r\n` / `\r` → `\n`, trailing whitespace trimmed.
    static func normalize(_ s: String) -> String {
        s.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
