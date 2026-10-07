import XCTest
@testable import LocalCaptionKit

/// SPEC-18 Kit pieces: config group, segment patches and recovery, v6 store columns,
/// correction prompt/checks, audio cleanup, RTX protocol.
final class AccentKitTests: XCTestCase {

    // MARK: Config

    func testAccentDefaultsAndMergeDefault() throws {
        let json = #"{"schema_version": 2, "accent": {"enabled": true, "rtx_address": "10.0.0.5"}}"#
        let cfg = try JSONDecoder().decode(Config.self, from: Data(json.utf8))
        XCTAssertTrue(cfg.accent.enabled)
        XCTAssertEqual(cfg.accent.primaryModel, "parakeet-tdt-0.6b-v2")
        XCTAssertEqual(cfg.accent.secondaryModel, "whisper-large-v3")
        XCTAssertTrue(cfg.accent.audioBandpass && cfg.accent.audioLevel)
        XCTAssertEqual(cfg.accent.effectiveFinalModel, "gpt-6.1-sol")
        XCTAssertEqual(cfg.accent.finalEffort, "high")
        let none = try JSONDecoder().decode(Config.self, from: Data(#"{"schema_version": 2}"#.utf8))
        XCTAssertEqual(none.accent, Config.Accent())
    }

    func testAccentRoundTrips() throws {
        var cfg = Config()
        cfg.accent.vocabulary = "Claude, Cowork"
        cfg.accent.secondaryModel = ""
        let back = try JSONDecoder().decode(Config.self, from: JSONEncoder().encode(cfg))
        XCTAssertEqual(back.accent, cfg.accent)
    }

    func testAgentURL() {
        XCTAssertEqual(Config.Accent.agentURL("172.20.101.43")?.absoluteString, "http://172.20.101.43:8765")
        XCTAssertEqual(Config.Accent.agentURL(" desktop.local:9000 ")?.absoluteString, "http://desktop.local:9000")
        XCTAssertEqual(Config.Accent.agentURL("http://10.0.0.2:8765/hello")?.absoluteString, "http://10.0.0.2:8765")
        XCTAssertNil(Config.Accent.agentURL(""))
    }

    // MARK: Segments, patches, journal

    func testBestTextAndPatch() {
        var t = Transcript()
        t.append(TranscriptSegment(text: "I don't use C cloud", tStartMs: 1000, tEndMs: 2000, createdAt: "x"))
        XCTAssertEqual(t.segments[0].bestText, "I don't use C cloud")
        XCTAssertTrue(t.apply(SegmentPatch(tStartMs: 1000, altText: "I don't use Cowork")))
        XCTAssertTrue(t.apply(SegmentPatch(tStartMs: 1000, liveText: "I don't use Claude")))
        XCTAssertEqual(t.segments[0].bestText, "I don't use Claude")
        XCTAssertTrue(t.apply(SegmentPatch(tStartMs: 1000, finalText: "I don't use Cowork")))
        XCTAssertEqual(t.segments[0].bestText, "I don't use Cowork")
        XCTAssertEqual(t.segments[0].altText, "I don't use Cowork")
        XCTAssertFalse(t.apply(SegmentPatch(tStartMs: 5, liveText: "nobody")))
        XCTAssertEqual(t.body(showTimestamps: false), "I don't use Cowork")
    }

    func testStandardSegmentJSONIsUnchanged() throws {
        let seg = TranscriptSegment(text: "hi", tStartMs: 0, tEndMs: 1, createdAt: "c")
        let keys = try JSONSerialization.jsonObject(with: JSONEncoder().encode(seg)) as! [String: Any]
        XCTAssertEqual(Set(keys.keys), ["id", "text", "t_start_ms", "t_end_ms", "created_at"])
    }

    func testJournalRecoversPatches() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("lc-j-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: dir) }
        let journal = try Journal(sessionId: UUID(), directory: dir)
        try journal.append(TranscriptSegment(text: "pay stack", tStartMs: 0, tEndMs: 900, createdAt: "c"))
        try journal.append(TranscriptSegment(text: "next", tStartMs: 1000, tEndMs: 1900, createdAt: "c"))
        try journal.append(SegmentPatch(tStartMs: 0, altText: "Paystack"))
        try journal.append(SegmentPatch(tStartMs: 0, liveText: "Paystack"))
        let rec = try XCTUnwrap(Journal.pending(in: dir).first)
        XCTAssertEqual(rec.segments.map(\.bestText), ["Paystack", "next"])
        XCTAssertEqual(rec.segments[0].text, "pay stack")
        XCTAssertEqual(rec.segments[0].altText, "Paystack")
    }

    // MARK: Store v6

    func testStoreKeepsAccentColumns() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("lc-v6-\(UUID().uuidString).db")
        defer { for s in ["", "-wal", "-shm"] { try? FileManager.default.removeItem(atPath: url.path + s) } }
        let store = try Store(url: url)
        let segs = [TranscriptSegment(text: "cloud", tStartMs: 0, tEndMs: 1, createdAt: "c",
                                      altText: "Claude", liveText: "Claude"),
                    TranscriptSegment(text: "plain", tStartMs: 10, tEndMs: 11, createdAt: "c")]
        let rec = try store.insert(SessionRecord(sessionName: "a", createdAt: "2026-10-07T00:00:00Z",
                                                 speechMode: SessionRecord.accentSpeech,
                                                 models: "parakeet-tdt-0.6b-v2+whisper-large-v3"), segments: segs)
        let id = try XCTUnwrap(rec.id)
        try store.setFinalTexts(sessionId: id, [0: "Claude Cowork"])
        try store.setCorrectionStatus(id: id, "done")
        let back = try store.segments(sessionId: id)
        XCTAssertEqual(back.map(\.bestText), ["Claude Cowork", "plain"])
        XCTAssertEqual(back[0].altText, "Claude")
        XCTAssertNil(back[1].altText)
        let fetched = try XCTUnwrap(try store.fetch(id: id))
        XCTAssertTrue(fetched.isAccent)
        XCTAssertEqual(fetched.correctionStatus, "done")
        XCTAssertEqual(fetched.models, "parakeet-tdt-0.6b-v2+whisper-large-v3")
    }

    // MARK: Correction prompt

    func testInstructionsAndTurnFormat() {
        let ins = CorrectionPrompt.instructions(twoSources: true, vocabulary: "Claude, Cowork,\nPaystack")
        XCTAssertTrue(ins.contains("P and W"))
        XCTAssertTrue(ins.hasSuffix("Vocabulary (names and terms likely to come up): Claude, Cowork, Paystack"))
        let turn = CorrectionPrompt.turn(
            [.init(number: 7, primary: "I don't use C cloud", secondary: "I don't use Cloud\tnow"),
             .init(number: 8, primary: "pay stack", secondary: nil)],
            context: [(number: 6, text: "So now we're here.")])
        XCTAssertEqual(turn, """
            Already corrected (context only, do not repeat):
            6\tSo now we're here.

            New lines to correct:
            7\tP: I don't use C cloud
            7\tW: I don't use Cloud now
            8\tP: pay stack
            """)
    }

    func testParseReply() {
        let got = CorrectionPrompt.parse("7\tI don't use Cowork.\n  junk line\n8\t\n9 no tab\n")
        XCTAssertEqual(got, [7: "I don't use Cowork.", 8: ""])
    }

    func testCheckRejectsInventionAndEmpty() {
        let line = CorrectionPrompt.Line(number: 1, primary: "I don't use C cloud", secondary: "I don't use Cloud")
        let vocab = "Claude, Cowork"
        XCTAssertEqual(CorrectionPrompt.check("I don't use Cowork.", line: line, vocabulary: vocab),
                       .accept("I don't use Cowork."))
        XCTAssertEqual(CorrectionPrompt.check("I don't use [?].", line: line, vocabulary: ""),
                       .accept("I don't use [?]."))
        XCTAssertEqual(CorrectionPrompt.check("[non-English]", line: line, vocabulary: ""),
                       .accept("[non-English]"))
        XCTAssertEqual(CorrectionPrompt.check("", line: line, vocabulary: vocab), .reject(reason: "empty"))
        XCTAssertEqual(CorrectionPrompt.check("We should ship the payments feature on Friday morning", line: line,
                                              vocabulary: vocab), .reject(reason: "unsupported words"))
    }

    // MARK: Audio cleanup

    private func sine(_ hz: Double, amp: Float = 0.5, seconds: Double = 1) -> [Float] {
        (0..<Int(16_000 * seconds)).map { amp * Float(sin(2 * .pi * hz * Double($0) / 16_000)) }
    }

    private func rms(_ x: [Float]) -> Float { (x.reduce(0) { $0 + $1 * $1 } / Float(x.count)).squareRoot() }

    func testBandpassCutsRumbleKeepsSpeech() {
        let rumble = AudioCleanup.apply(sine(30), bandpass: true, level: false)
        let voice = AudioCleanup.apply(sine(1_000), bandpass: true, level: false)
        XCTAssertLessThan(rms(Array(rumble.dropFirst(4_000))), 0.2 * rms(sine(30)))   // 2nd order: about −17 dB at 30 Hz
        XCTAssertGreaterThan(rms(Array(voice.dropFirst(4_000))), 0.9 * rms(sine(1_000)))
    }

    func testLevellingRaisesQuietSpeechAndLeavesSilence() {
        let quiet = AudioCleanup.apply(sine(500, amp: 0.005), bandpass: false, level: true)
        XCTAssertEqual(rms(quiet), AudioCleanup.targetRMS, accuracy: 0.005)
        let silence = [Float](repeating: 0, count: 1_000)
        XCTAssertEqual(AudioCleanup.apply(silence, bandpass: true, level: true), silence)
        let off = sine(500)
        XCTAssertEqual(AudioCleanup.apply(off, bandpass: false, level: false), off)
    }

    // MARK: RTX protocol

    func testDecodeAgentReplies() throws {
        let status = try JSONDecoder().decode(RTXProtocol.Status.self, from: Data("""
            {"state": "downloading", "model": "whisper-large-v3", "progress": 0.456, "message": "",
             "loaded": {"primary": null, "secondary": null}, "vram_used_mb": 4696}
            """.utf8))
        XCTAssertEqual(status.line, "Downloading whisper-large-v3 · 45%")
        XCTAssertFalse(status.isReady(primary: "a", secondary: nil))
        let reply = try JSONDecoder().decode(RTXProtocol.TranscribeReply.self, from: Data("""
            {"id": "3", "primary": {"text": "Yeah, I was", "ms": 120,
             "words": [{"word": "Yeah,", "start": 0.48, "end": 0.8}]}}
            """.utf8))
        XCTAssertEqual(reply.primary?.words?.first, RTXProtocol.Word(word: "Yeah,", start: 0.48, end: 0.8))
        XCTAssertNil(reply.secondary)
    }

    func testHelloPairingFlag() throws {
        let open = try JSONDecoder().decode(RTXProtocol.Hello.self, from: Data(#"""
            {"name": "PC", "version": "1.0.0", "gpu": "RTX", "vram_total_mb": 24564, "paired": false, "pairing_required": false}
            """#.utf8))
        XCTAssertFalse(open.needsPairing)
        let old = try JSONDecoder().decode(RTXProtocol.Hello.self, from: Data(#"""
            {"name": "PC", "version": "1.0.0", "gpu": "RTX", "vram_total_mb": 24564, "paired": true}
            """#.utf8))
        XCTAssertTrue(old.needsPairing)
    }

    // MARK: Streaming words

    func testStableWordsGrowAsReadsAgree() {
        var s = StableWords()
        XCTAssertEqual(s.update("I was").stable, "")
        var r = s.update("I was just watching")
        XCTAssertEqual(r.stable, "I was"); XCTAssertEqual(r.tail, "just watching")
        r = s.update("I was just watching what Victor")
        XCTAssertEqual(r.stable, "I was just watching"); XCTAssertEqual(r.tail, "what Victor")
        r = s.update("I was just watching what, Victor's doing")
        XCTAssertEqual(r.stable, "I was just watching what,", "punctuation doesn't break agreement")
    }

    func testStableWordsDoNotFlickerBackButResetWhenTheReadChanges() {
        var s = StableWords()
        _ = s.update("pay stack now")
        XCTAssertEqual(s.update("pay stack now please").stable, "pay stack now")
        XCTAssertEqual(s.update("pay stack now please").stable, "pay stack now please")
        // The final took that utterance; the next one starts fresh.
        XCTAssertEqual(s.update("so then").stable, "")
        s.reset()
        XCTAssertEqual(s.update("").tail, "")
    }

    func testPCM16() {
        let data = RTXProtocol.pcm16([0, 1, -1, 2])
        let values = data.withUnsafeBytes { Array($0.bindMemory(to: Int16.self)) }.map { Int16(littleEndian: $0) }
        XCTAssertEqual(values, [0, 32767, -32767, 32767])
    }
}
