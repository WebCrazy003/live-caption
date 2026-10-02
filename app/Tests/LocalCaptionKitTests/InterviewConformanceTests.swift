import XCTest
@testable import LocalCaptionKit

/// Interview Assist's shared vectors (SPEC-11 §Windows compatibility contract): `testdata/hotkey`,
/// `testdata/ask`, `testdata/interview-prompt`, `testdata/library` and `testdata/records`
/// (specs/SPEC-16 §3.1). The Windows suite asserts the same files.
/// (The `interview` config vectors run with the other config vectors in `ConformanceTests`.)
final class InterviewConformanceTests: XCTestCase {

    private static let root: URL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent()
        .deletingLastPathComponent().deletingLastPathComponent()
        .appendingPathComponent("testdata", isDirectory: true)

    private func vectors<T: Decodable>(_ suite: String, as type: T.Type) throws -> [(name: String, value: T)] {
        let dir = Self.root.appendingPathComponent(suite, isDirectory: true)
        let files = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "json" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        XCTAssertFalse(files.isEmpty, "no vectors found in testdata/\(suite)")
        return try files.map { ($0.lastPathComponent, try JSONDecoder().decode(T.self, from: Data(contentsOf: $0))) }
    }

    // MARK: - Hotkey

    private struct HotkeyVector: Decodable {
        struct Case: Decodable { let input: String; let expect: String?; let error: String? }
        let cases: [Case]
    }

    private func kind(_ e: Hotkey.ParseError) -> String {
        switch e {
        case .empty: return "empty"
        case .unknownModifier: return "unknown_modifier"
        case .unknownKey: return "unknown_key"
        case .missingKey: return "missing_key"
        case .needsModifier: return "needs_modifier"
        }
    }

    func testHotkeyVectors() throws {
        for (file, v) in try vectors("hotkey", as: HotkeyVector.self) {
            for c in v.cases {
                switch Hotkey.parse(c.input) {
                case .success(let hk):
                    XCTAssertEqual(hk.description, c.expect, "\(file): \"\(c.input)\" canonical form")
                    XCTAssertNil(c.error, "\(file): \"\(c.input)\" should be invalid")
                    XCTAssertEqual(Hotkey.parse(hk.description), .success(hk), "\(file): canonical form re-parses")
                case .failure(let e):
                    XCTAssertNil(c.expect, "\(file): \"\(c.input)\" should be valid, got \(e)")
                    if let expected = c.error { XCTAssertEqual(kind(e), expected, "\(file): \"\(c.input)\" error") }
                    XCTAssertEqual(Hotkey.resolve(c.input), .default, "\(file): invalid resolves to F8")
                }
            }
        }
    }

    // MARK: - AskSelection

    private struct AskVector: Decodable {
        struct Seg: Decodable {
            let text: String; let tStartMs: Int; let tEndMs: Int
            enum CodingKeys: String, CodingKey { case text; case tStartMs = "t_start_ms"; case tEndMs = "t_end_ms" }
        }
        struct Mark: Decodable, Equatable {
            let audioMs: Int; let interimWasSent: Bool
            enum CodingKeys: String, CodingKey { case audioMs = "audio_ms"; case interimWasSent = "interim_was_sent" }
        }
        struct Input: Decodable {
            let segments: [Seg]; let interim: String; let mode: String; let sentences: Int?
            let mark: Mark?; let pressMs: Int; let maxWords: Int
            enum CodingKeys: String, CodingKey {
                case segments, interim, mode, sentences, mark
                case pressMs = "press_ms"
                case maxWords = "max_words"
            }
        }
        struct Expect: Decodable {
            let text: String; let mark: Mark; let fromMs: Int; let toMs: Int
            enum CodingKeys: String, CodingKey { case text, mark; case fromMs = "from_ms"; case toMs = "to_ms" }
        }
        let input: Input
        let expect: Expect
    }

    func testAskSelectionVectors() throws {
        for (file, v) in try vectors("ask", as: AskVector.self) {
            let i = v.input
            let mode: AskSelection.Mode = i.mode == "last_sentences" ? .lastSentences(i.sentences ?? 3) : .sinceLastAsk
            let r = AskSelection.select(
                segments: i.segments.map { AskSelection.Segment(text: $0.text, tStartMs: $0.tStartMs, tEndMs: $0.tEndMs) },
                interim: i.interim, mode: mode,
                mark: i.mark.map { AskSelection.Mark(audioMs: $0.audioMs, interimWasSent: $0.interimWasSent) },
                pressAudioMs: i.pressMs, maxWords: i.maxWords)
            XCTAssertEqual(r.text, v.expect.text, "\(file): text")
            XCTAssertEqual(r.mark, AskSelection.Mark(audioMs: v.expect.mark.audioMs,
                                                     interimWasSent: v.expect.mark.interimWasSent), "\(file): mark")
            XCTAssertEqual(r.fromMs, v.expect.fromMs, "\(file): from")
            XCTAssertEqual(r.toMs, v.expect.toMs, "\(file): to")
        }
    }

    // MARK: - InterviewPrompt

    private struct PromptVector: Decodable {
        struct SkillFile: Decodable { let path: String; let text: String }
        struct Definition: Decodable { let title: String; let text: String; let files: [SkillFile]? }
        struct Attachment: Decodable { let title: String; let text: String }
        struct Case: Decodable {
            let kind: String
            let length: String?
            let custom: String?
            let command: String?
            let definition: Definition?
            let attachments: [Attachment]?
            let text: String?
            let imageCount: Int?
            let transcript: String?
            let expect: String
            enum CodingKeys: String, CodingKey {
                case kind, length, custom, command, definition, attachments, text, transcript, expect
                case imageCount = "image_count"
            }
        }
        let cases: [Case]
    }

    func testInterviewPromptVectors() throws {
        for (file, v) in try vectors("interview-prompt", as: PromptVector.self) {
            for (n, c) in v.cases.enumerated() {
                let actual: String
                switch c.kind {
                case "base":
                    let length = try XCTUnwrap(Config.Interview.AnswerLength(rawValue: c.length ?? ""))
                    actual = InterviewPrompt.baseInstructions(length: length, custom: c.custom ?? "")
                case "skill":
                    actual = InterviewPrompt.skillMessage(
                        command: c.command ?? "",
                        definition: c.definition.map { d in
                            InterviewPrompt.Skill(title: d.title, text: d.text,
                                                  files: (d.files ?? []).map { .init(path: $0.path, text: $0.text) })
                        },
                        attachments: (c.attachments ?? []).map { .init(title: $0.title, text: $0.text) })
                case "ask":
                    actual = InterviewPrompt.ask(c.text ?? "", imageCount: c.imageCount ?? 0)
                case "regenerate":
                    actual = InterviewPrompt.regenerate
                case "summary":
                    actual = InterviewPrompt.summary(transcript: c.transcript ?? "")
                default:
                    XCTFail("\(file) case \(n): unknown kind \(c.kind)"); continue
                }
                XCTAssertEqual(actual, c.expect, "\(file) case \(n) (\(c.kind))")
            }
        }
    }

    // MARK: - Library (slugs, skill files)

    private struct LibraryVector: Decodable {
        struct Case: Decodable {
            // slug
            let title: String?
            let taken: [String]?
            let expect: String?
            // skill_files
            let op: String?
            let text: String?
            let expectName: String?
            let expectDescription: String?
            let paths: [String]?
            let expectIncluded: [String]?
            let expectIgnored: [String]?
            enum CodingKeys: String, CodingKey {
                case title, taken, expect, op, text, paths
                case expectName = "expect_name"
                case expectDescription = "expect_description"
                case expectIncluded = "expect_included"
                case expectIgnored = "expect_ignored"
            }
        }
        let kind: String
        let cases: [Case]
    }

    func testLibraryVectors() throws {
        for (file, v) in try vectors("library", as: LibraryVector.self) {
            for (n, c) in v.cases.enumerated() {
                let at = "\(file) case \(n)"
                switch (v.kind, c.op) {
                case ("slug", _):
                    let title = try XCTUnwrap(c.title, at)
                    XCTAssertEqual(LibrarySlug.make(title, taken: Set(c.taken ?? [])), c.expect, "\(at): \"\(title)\"")
                case ("skill_files", "front_matter"):
                    let fm = SkillFile.frontMatter(try XCTUnwrap(c.text, at))
                    XCTAssertEqual(fm.name, c.expectName, "\(at): name")
                    XCTAssertEqual(fm.description, c.expectDescription, "\(at): description")
                case ("skill_files", "partition"):
                    let p = SkillFile.partition(try XCTUnwrap(c.paths, at))
                    XCTAssertEqual(p.included, c.expectIncluded, "\(at): included")
                    XCTAssertEqual(p.ignored, c.expectIgnored, "\(at): ignored")
                default:
                    XCTFail("\(at): unknown kind \(v.kind) / op \(c.op ?? "nil")")
                }
            }
        }
    }

    // MARK: - Records (session name, Q&A export)

    private struct RecordVector: Decodable {
        struct Turn: Decodable {
            let n: Int; let kind: String; let question: String
            let audioToMs: Int?; let images: [String]?; let answer: String?
            let status: String; let error: String?
            enum CodingKeys: String, CodingKey {
                case n, kind, question, images, answer, status, error
                case audioToMs = "audio_to_ms"
            }
        }
        struct Case: Decodable {
            // session_name
            let candidate: String?
            let company: String?
            let step: Int?
            let localTime: String?
            // qa_markdown
            let name: String?
            let role: String?
            let turns: [Turn]?
            let expect: String?
            enum CodingKeys: String, CodingKey {
                case candidate, company, step, name, role, turns, expect
                case localTime = "local_time"
            }
        }
        let kind: String
        let cases: [Case]
    }

    /// `2026-10-02T12:00:00` as a wall-clock time in this machine's time zone.
    private func localTime(_ s: String) throws -> Date {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = .current
        f.dateFormat = "yyyy-MM-dd'T'HH:mm:ss"
        return try XCTUnwrap(f.date(from: s), "bad local_time \(s)")
    }

    func testRecordVectors() throws {
        for (file, v) in try vectors("records", as: RecordVector.self) {
            for (n, c) in v.cases.enumerated() {
                let at = "\(file) case \(n)"
                switch v.kind {
                case "session_name":
                    let date = try localTime(try XCTUnwrap(c.localTime, at))
                    XCTAssertEqual(InterviewRecord.sessionName(candidate: c.candidate, company: c.company,
                                                               step: c.step, date: date), c.expect, at)
                case "qa_markdown":
                    var rec = InterviewRecord(name: try XCTUnwrap(c.name, at), createdAt: "t", model: "m",
                                              reasoningEffort: "low",
                                              setup: .init(company: c.company ?? "", role: c.role ?? ""))
                    rec.turns = try (c.turns ?? []).map { t in
                        InterviewRecord.Turn(
                            n: t.n, kind: try XCTUnwrap(InterviewRecord.TurnKind(rawValue: t.kind), at),
                            question: t.question, audioToMs: t.audioToMs, images: t.images ?? [],
                            answer: t.answer ?? "",
                            status: try XCTUnwrap(InterviewRecord.TurnStatus(rawValue: t.status), at),
                            error: t.error, askedAt: "t")
                    }
                    XCTAssertEqual(rec.qaMarkdown(), c.expect, at)
                default:
                    XCTFail("\(at): unknown kind \(v.kind)")
                }
            }
        }
    }
}
