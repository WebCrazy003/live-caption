import XCTest
@testable import LocalCaptionKit

/// Interview Assist's shared vectors (SPEC-11 §Windows compatibility contract): `testdata/hotkey`,
/// `testdata/ask` and `testdata/interview-prompt`. The Windows suite asserts the same files.
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
        struct Skill: Decodable { let title: String; let text: String; let files: [SkillFile]? }
        struct Note: Decodable { let title: String; let text: String }
        struct Setup: Decodable {
            let company: String?; let role: String?; let instructions: String?
            let skills: [Skill]?; let cv: String?; let jd: String?; let notes: [Note]?
        }
        struct Case: Decodable {
            let kind: String
            let length: String?
            let setup: Setup?
            let text: String?
            let imageCount: Int?
            let transcript: String?
            let expect: String
            enum CodingKeys: String, CodingKey {
                case kind, length, setup, text, transcript, expect
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
                    actual = InterviewPrompt.baseInstructions(length: length)
                case "prep":
                    let s = try XCTUnwrap(c.setup)
                    actual = InterviewPrompt.prepMessage(InterviewPrompt.Setup(
                        company: s.company ?? "", role: s.role ?? "", instructions: s.instructions ?? "",
                        skills: (s.skills ?? []).map { sk in
                            InterviewPrompt.Skill(title: sk.title, text: sk.text,
                                                  files: (sk.files ?? []).map { .init(path: $0.path, text: $0.text) })
                        },
                        cv: s.cv ?? "", jobDescription: s.jd ?? "",
                        notes: (s.notes ?? []).map { .init(title: $0.title, text: $0.text) }))
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
}
