import XCTest
@testable import LocalCaptionKit

/// Runs the shared cross-platform vectors in `testdata/` (SPEC-WINDOWS.md §6.1) against the
/// macOS implementation. The Windows suite runs the *same files*, which is what makes the
/// parity claim checkable instead of aspirational.
///
/// These tests deliberately duplicate coverage that the hand-written suites already have.
/// That is the point: this file proves the vectors describe the reference behaviour, so a
/// disagreement on Windows means the port is wrong — not that the vector was invented.
final class ConformanceTests: XCTestCase {

    // MARK: - Vector loading

    private static let root: URL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()   // LocalCaptionKitTests
        .deletingLastPathComponent()   // Tests
        .deletingLastPathComponent()   // app
        .deletingLastPathComponent()   // repo root
        .appendingPathComponent("testdata", isDirectory: true)

    private func vectors<T: Decodable>(_ suite: String, as type: T.Type) throws -> [(name: String, value: T)] {
        let dir = Self.root.appendingPathComponent(suite, isDirectory: true)
        let files = try FileManager.default
            .contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "json" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        XCTAssertFalse(files.isEmpty, "no vectors found in testdata/\(suite)")
        return try files.map { ($0.lastPathComponent, try JSONDecoder().decode(T.self, from: Data(contentsOf: $0))) }
    }

    // MARK: - RollingCaption

    private struct RollingVector: Decodable {
        struct Word: Decodable { let text: String; let start: Double; let end: Double }
        struct Step: Decodable {
            let words: [Word]
            let startSample: Int
            let endSample: Int
            let expectAccepted: Bool
            enum CodingKeys: String, CodingKey {
                case words
                case startSample = "start_sample"
                case endSample = "end_sample"
                case expectAccepted = "expect_accepted"
            }
        }
        let steps: [Step]
        let expectText: String
        enum CodingKeys: String, CodingKey { case steps; case expectText = "expect_text" }
    }

    func testRollingCaptionVectors() throws {
        for (name, v) in try vectors("rolling", as: RollingVector.self) {
            var caption = RollingCaption()
            for (i, step) in v.steps.enumerated() {
                let incoming = step.words.map { CaptionWord($0.text, start: $0.start, end: $0.end) }
                let accepted = caption.update(incoming, startSample: step.startSample, endSample: step.endSample)
                XCTAssertEqual(accepted, step.expectAccepted, "\(name) step \(i): acceptance")
            }
            XCTAssertEqual(caption.text, v.expectText, "\(name): merged text")
        }
    }

    // MARK: - SpeechSegmenter

    private struct SegmenterVector: Decodable {
        struct Tuning: Decodable {
            let endpointMs: Int; let intervalMs: Int; let maxUtteranceS: Int; let threshold: Float
            enum CodingKeys: String, CodingKey {
                case endpointMs = "endpoint_ms"
                case intervalMs = "interval_ms"
                case maxUtteranceS = "max_utterance_s"
                case threshold
            }
        }
        struct Run: Decodable { let amplitude: Float; let count: Int }
        struct Request: Decodable, Equatable, CustomStringConvertible {
            let utterance: Int; let startSample: Int; let sampleCount: Int
            let endSample: Int; let isFinal: Bool
            enum CodingKeys: String, CodingKey {
                case utterance
                case startSample = "start_sample"
                case sampleCount = "sample_count"
                case endSample = "end_sample"
                case isFinal = "is_final"
            }
            var description: String {
                "#\(utterance) [\(startSample)+\(sampleCount)=\(endSample)]\(isFinal ? "F" : "i")"
            }
        }
        struct Transition: Decodable, Equatable {
            let utterance: Int; let sample: Int; let started: Bool
        }
        struct Step: Decodable {
            let op: String
            let now: Double?
            let runs: [Run]?
            let expectRequests: [Request]?
            let expectFinalRequests: [Request]?
            let expectFinalCount: Int?
            let expectInterimMaxSamples: Int?
            let expectTransitions: [Transition]?
            enum CodingKeys: String, CodingKey {
                case op, now, runs
                case expectRequests = "expect_requests"
                case expectFinalRequests = "expect_final_requests"
                case expectFinalCount = "expect_final_count"
                case expectInterimMaxSamples = "expect_interim_max_samples"
                case expectTransitions = "expect_transitions"
            }
        }
        let tuning: Tuning
        let steps: [Step]
        let expectTotalFinalSamples: Int?
        enum CodingKeys: String, CodingKey {
            case tuning, steps
            case expectTotalFinalSamples = "expect_total_final_samples"
        }
    }

    func testSegmenterVectors() throws {
        for (name, v) in try vectors("segmenter", as: SegmenterVector.self) {
            let tuning = SpeechSegmenter.Tuning(endpointMs: v.tuning.endpointMs,
                                                intervalMs: v.tuning.intervalMs,
                                                maxUtteranceS: v.tuning.maxUtteranceS,
                                                threshold: v.tuning.threshold)
            var segmenter = SpeechSegmenter(session: UUID(), tuning: tuning)
            var totalFinalSamples = 0

            for (i, step) in v.steps.enumerated() {
                let where_ = "\(name) step \(i) (\(step.op))"
                var produced: [SpeechRequest] = []

                switch step.op {
                case "append":
                    var samples: [Float] = []
                    for run in step.runs ?? [] {
                        samples.append(contentsOf: Array(repeating: run.amplitude, count: run.count))
                    }
                    produced = segmenter.append(samples, now: step.now ?? 0)
                case "finish":
                    produced = segmenter.finish(now: step.now ?? 0)
                case "drain_transitions":
                    let actual = segmenter.drainTransitions().map {
                        SegmenterVector.Transition(utterance: $0.utterance, sample: $0.sample, started: $0.started)
                    }
                    XCTAssertEqual(actual, step.expectTransitions ?? [], "\(where_): transitions")
                    continue
                default:
                    XCTFail("\(where_): unknown op"); continue
                }

                totalFinalSamples += produced.filter(\.isFinal).map(\.audio.count).reduce(0, +)
                let shape = { (r: SpeechRequest) in
                    SegmenterVector.Request(utterance: r.utterance, startSample: r.startSample,
                                            sampleCount: r.audio.count, endSample: r.endSample,
                                            isFinal: r.isFinal)
                }
                if let expected = step.expectRequests {
                    XCTAssertEqual(produced.map(shape), expected, "\(where_): requests")
                }
                if let expected = step.expectFinalRequests {
                    XCTAssertEqual(produced.filter(\.isFinal).map(shape), expected, "\(where_): final requests")
                }
                if let expected = step.expectFinalCount {
                    XCTAssertEqual(produced.filter(\.isFinal).count, expected, "\(where_): final count")
                }
                if let cap = step.expectInterimMaxSamples {
                    let widest = produced.filter { !$0.isFinal }.map(\.audio.count).max() ?? 0
                    XCTAssertLessThanOrEqual(widest, cap, "\(where_): interim window cap")
                }
            }

            if let expected = v.expectTotalFinalSamples {
                XCTAssertEqual(totalFinalSamples, expected, "\(name): every speech sample survives")
            }
        }
    }

    // MARK: - LocalAgreement

    private struct AgreementVector: Decodable {
        struct Step: Decodable {
            let op: String
            let hypothesis: [String]?
            let expectCommitted: [String]?
            let expectProvisional: [String]?
            enum CodingKeys: String, CodingKey {
                case op, hypothesis
                case expectCommitted = "expect_committed"
                case expectProvisional = "expect_provisional"
            }
        }
        let steps: [Step]
    }

    func testLocalAgreementVectors() throws {
        for (name, v) in try vectors("localagreement", as: AgreementVector.self) {
            var la = LocalAgreement()
            for (i, step) in v.steps.enumerated() {
                switch step.op {
                case "reset":
                    la.reset()
                case "update":
                    let r = la.update(step.hypothesis ?? [])
                    XCTAssertEqual(r.committed, step.expectCommitted ?? [], "\(name) step \(i): committed")
                    XCTAssertEqual(r.provisional, step.expectProvisional ?? [], "\(name) step \(i): provisional")
                default:
                    XCTFail("\(name) step \(i): unknown op \(step.op)")
                }
            }
        }
    }

    // MARK: - Filters

    private struct FilterVector: Decodable {
        struct Case: Decodable {
            let input: String?
            let expect: Bool?
            let expectText: String?
            let avgLogprob: Double?
            let noSpeechProb: Double?
            let compressionRatio: Double?
            let expectWords: Int?
            let expectSentences: Int?
            enum CodingKeys: String, CodingKey {
                case input, expect
                case avgLogprob = "avg_logprob"
                case noSpeechProb = "no_speech_prob"
                case compressionRatio = "compression_ratio"
                case expectWords = "expect_words"
                case expectSentences = "expect_sentences"
            }
            // `expect` is a Bool for the verdict suites and a String for `clean`.
            init(from decoder: Decoder) throws {
                let c = try decoder.container(keyedBy: CodingKeys.self)
                input = try c.decodeIfPresent(String.self, forKey: .input)
                expect = try? c.decodeIfPresent(Bool.self, forKey: .expect)
                expectText = try? c.decodeIfPresent(String.self, forKey: .expect)
                avgLogprob = try c.decodeIfPresent(Double.self, forKey: .avgLogprob)
                noSpeechProb = try c.decodeIfPresent(Double.self, forKey: .noSpeechProb)
                compressionRatio = try c.decodeIfPresent(Double.self, forKey: .compressionRatio)
                expectWords = try c.decodeIfPresent(Int.self, forKey: .expectWords)
                expectSentences = try c.decodeIfPresent(Int.self, forKey: .expectSentences)
            }
        }
        let kind: String
        let cases: [Case]
    }

    func testFilterVectors() throws {
        for (name, v) in try vectors("filters", as: FilterVector.self) {
            for (i, c) in v.cases.enumerated() {
                let where_ = "\(name) case \(i)"
                switch v.kind {
                case "clean":
                    XCTAssertEqual(Filters.clean(c.input ?? ""), c.expectText, "\(where_): clean(\(c.input ?? ""))")
                case "hallucination":
                    XCTAssertEqual(Filters.isHallucination(c.input ?? ""), c.expect,
                                   "\(where_): isHallucination(\(c.input ?? ""))")
                case "low_quality":
                    XCTAssertEqual(Filters.isLowQuality(avgLogprob: c.avgLogprob ?? 0,
                                                        noSpeechProb: c.noSpeechProb ?? 0,
                                                        compressionRatio: c.compressionRatio ?? 0),
                                   c.expect, "\(where_): isLowQuality")
                case "counts":
                    XCTAssertEqual(Filters.wordCount(c.input ?? ""), c.expectWords, "\(where_): wordCount")
                    XCTAssertEqual(Filters.sentenceCount(c.input ?? ""), c.expectSentences, "\(where_): sentenceCount")
                default:
                    XCTFail("\(where_): unknown kind \(v.kind)")
                }
            }
        }
    }

    // MARK: - Sentences

    private struct SentenceVector: Decodable {
        struct Case: Decodable {
            let text: String
            let n: Int?
            let provisional: String?
            let expectList: [String]?
            let expectText: String?
            enum CodingKeys: String, CodingKey { case text, n, provisional, expect }
            init(from decoder: Decoder) throws {
                let c = try decoder.container(keyedBy: CodingKeys.self)
                text = try c.decode(String.self, forKey: .text)
                n = try c.decodeIfPresent(Int.self, forKey: .n)
                provisional = try c.decodeIfPresent(String.self, forKey: .provisional)
                expectList = try? c.decodeIfPresent([String].self, forKey: .expect)
                expectText = try? c.decodeIfPresent(String.self, forKey: .expect)
            }
        }
        let kind: String
        let cases: [Case]
    }

    func testSentenceVectors() throws {
        for (name, v) in try vectors("sentences", as: SentenceVector.self) {
            for (i, c) in v.cases.enumerated() {
                let where_ = "\(name) case \(i)"
                switch v.kind {
                case "split":
                    XCTAssertEqual(Sentences.split(c.text), c.expectList, "\(where_): split")
                case "last_n":
                    XCTAssertEqual(Sentences.lastN(c.text, n: c.n ?? 0), c.expectText, "\(where_): lastN")
                case "last_n_appending":
                    XCTAssertEqual(Sentences.lastN(c.text, appending: c.provisional ?? "", n: c.n ?? 0),
                                   c.expectText, "\(where_): lastN appending")
                default:
                    XCTFail("\(where_): unknown kind \(v.kind)")
                }
            }
        }
    }

    // MARK: - Config

    private struct ConfigVector: Decodable {
        let name: String
        let input: String?
        let roundTripOverrides: [String: JSONValue]?
        let expectRepaired: Bool?
        let expectBackupCount: Int?
        let expectFileExists: Bool?
        let expectReloadClean: Bool?
        let expectRewritten: Bool?
        let expect: [String: JSONValue]?
        let expectEncodedContains: [String]?
        let expectEncodedOmits: [String]?
        enum CodingKeys: String, CodingKey {
            case name, input, expect
            case roundTripOverrides = "round_trip_overrides"
            case expectRepaired = "expect_repaired"
            case expectBackupCount = "expect_backup_count"
            case expectFileExists = "expect_file_exists"
            case expectReloadClean = "expect_reload_clean"
            case expectRewritten = "expect_rewritten"
            case expectEncodedContains = "expect_encoded_contains"
            case expectEncodedOmits = "expect_encoded_omits"
        }
    }

    /// Minimal JSON scalar/array box so vectors can carry heterogeneous expected values.
    private enum JSONValue: Decodable, Equatable, CustomStringConvertible {
        case string(String), int(Int), double(Double), bool(Bool), null
        init(from decoder: Decoder) throws {
            let c = try decoder.singleValueContainer()
            if c.decodeNil() { self = .null }
            else if let b = try? c.decode(Bool.self) { self = .bool(b) }
            else if let i = try? c.decode(Int.self) { self = .int(i) }
            else if let d = try? c.decode(Double.self) { self = .double(d) }
            else { self = .string(try c.decode(String.self)) }
        }
        var description: String {
            switch self {
            case .string(let s): return s
            case .int(let i): return String(i)
            case .double(let d): return String(d)
            case .bool(let b): return String(b)
            case .null: return "null"
            }
        }
        var any: Any? {
            switch self {
            case .string(let s): return s
            case .int(let i): return i
            case .double(let d): return d
            case .bool(let b): return b
            case .null: return nil
            }
        }
    }

    /// Encode a `Config` and read a dotted, snake_cased key path out of it.
    private func value(_ config: Config, at path: String) throws -> Any? {
        let data = try JSONEncoder().encode(config)
        var node = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        let parts = path.split(separator: ".").map(String.init)
        for key in parts.dropLast() { node = node?[key] as? [String: Any] }
        return node?[parts.last ?? ""]
    }

    private func assertSame(_ actual: Any?, _ expected: Any?, _ message: String) {
        switch (actual, expected) {
        case (nil, nil): break
        case let (a as NSNumber, b as NSNumber): XCTAssertEqual(a, b, message)
        case let (a as String, b as String): XCTAssertEqual(a, b, message)
        default: XCTFail("\(message): \(String(describing: actual)) != \(String(describing: expected))")
        }
    }

    func testConfigVectors() throws {
        let defaults = Config()
        for (file, v) in try vectors("config", as: ConfigVector.self) {
            let dir = URL(fileURLWithPath: NSTemporaryDirectory())
                .appendingPathComponent("lc-conformance-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: dir) }
            let url = dir.appendingPathComponent("config.json")

            // Encoded-shape assertions need no file at all.
            if v.expectEncodedContains != nil || v.expectEncodedOmits != nil {
                let json = String(decoding: try JSONEncoder().encode(defaults), as: UTF8.self)
                for needle in v.expectEncodedContains ?? [] {
                    XCTAssertTrue(json.contains(needle), "\(file): encoded config should contain \(needle)")
                }
                for needle in v.expectEncodedOmits ?? [] {
                    XCTAssertFalse(json.contains(needle), "\(file): encoded config should omit \(needle)")
                }
                continue
            }

            // Round-trip vectors build a config from overrides, write it, and reload it.
            if let overrides = v.roundTripOverrides {
                var nested: [String: Any] = [:]
                for (path, boxed) in overrides {
                    let parts = path.split(separator: ".").map(String.init)
                    guard parts.count == 2, let value = boxed.any else { continue }
                    var group = nested[parts[0]] as? [String: Any] ?? [:]
                    group[parts[1]] = value
                    nested[parts[0]] = group
                }
                let seed = try JSONSerialization.data(withJSONObject: nested)
                let configured = try JSONDecoder().decode(Config.self, from: seed)
                try configured.write(to: url)
                let (reloaded, repaired) = Config.loadOrRepair(from: url)
                XCTAssertFalse(repaired, "\(file): a config we just wrote must reload cleanly")
                XCTAssertEqual(reloaded, configured, "\(file): write→read must be the identity")
                continue
            }

            if let input = v.input { try Data(input.utf8).write(to: url) }

            let (config, repaired) = Config.loadOrRepair(from: url)
            if let expected = v.expectRepaired {
                XCTAssertEqual(repaired, expected, "\(file): repaired flag")
            }
            if let expected = v.expectBackupCount {
                let backups = try FileManager.default.contentsOfDirectory(atPath: dir.path)
                    .filter { $0.hasPrefix("config.json.bak-") }
                XCTAssertEqual(backups.count, expected, "\(file): backup count")
            }
            if v.expectFileExists == true {
                XCTAssertTrue(FileManager.default.fileExists(atPath: url.path), "\(file): config written")
            }
            if v.expectRewritten == true {
                let (onDisk, _) = Config.loadOrRepair(from: url)
                XCTAssertEqual(onDisk.schemaVersion, Config.currentSchemaVersion,
                               "\(file): migration must be persisted, not just returned")
            }
            if v.expectReloadClean == true {
                let (_, repairedAgain) = Config.loadOrRepair(from: url)
                XCTAssertFalse(repairedAgain, "\(file): the repaired file must itself reload cleanly")
            }
            for (path, expected) in v.expect ?? [:] {
                let actual = try value(config, at: path)
                if case .string("$default") = expected {
                    assertSame(actual, try value(defaults, at: path), "\(file): \(path) should be the default")
                } else {
                    assertSame(actual, expected.any, "\(file): \(path)")
                }
            }
        }
    }
}
