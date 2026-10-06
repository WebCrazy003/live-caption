import XCTest
import WhisperKit
import LocalCaptionKit
@testable import LocalCaption

/// Opt-in end-to-end replay of Accent mode (SPEC-18 build step 2): a recording goes through the
/// app's segmenter, caption pipeline and `RTXEngine` against a real RTX agent, then the live
/// corrector and the final pass through a real Codex. Writes what each stage produced, one line
/// per segment (`[mm:ss] text`), for scoring against a reference.
///
///   LOCALCAPTION_ACCENT_WAV=<16 kHz wav>  LOCALCAPTION_RTX=<host[:port]>  LOCALCAPTION_RTX_TOKEN=<token>
///   LOCALCAPTION_ACCENT_OUT=<folder>  [LOCALCAPTION_ACCENT_SPEED=1]  [LOCALCAPTION_ACCENT_FINAL=1]
///   [LOCALCAPTION_ACCENT_VOCAB="Claude, Cowork"]
///   swift test --filter AccentReplayTests
@MainActor
final class AccentReplayTests: XCTestCase {
    func testReplayThroughRTXAndCodex() async throws {
        let env = ProcessInfo.processInfo.environment
        guard let wav = env["LOCALCAPTION_ACCENT_WAV"], let address = env["LOCALCAPTION_RTX"],
              let token = env["LOCALCAPTION_RTX_TOKEN"], let out = env["LOCALCAPTION_ACCENT_OUT"] else {
            throw XCTSkip("Set LOCALCAPTION_ACCENT_WAV, LOCALCAPTION_RTX, LOCALCAPTION_RTX_TOKEN and LOCALCAPTION_ACCENT_OUT")
        }
        let speed = Double(env["LOCALCAPTION_ACCENT_SPEED"] ?? "1") ?? 1
        let vocabulary = env["LOCALCAPTION_ACCENT_VOCAB"] ?? ""
        let cfg = Config.Accent()
        let audio = try AudioProcessor.loadAudioAsFloatArray(fromPath: wav)
        let url = try XCTUnwrap(Config.Accent.agentURL(address))
        let engine = RTXEngine(client: RTXClient(baseURL: url, token: token), settings: .init(
            primary: cfg.primaryModel, secondary: cfg.secondaryModel, bandpass: cfg.audioBandpass, level: cfg.audioLevel))
        try await engine.prepare(onStatus: { print("ACCENT status \($0)") }, onDownload: { _, _ in })
        defer { engine.shutdown() }

        let codex = CodexAppServerEngine(codexPath: { "" },
                                         workspace: FileManager.default.temporaryDirectory.appendingPathComponent("accent-replay"))
        let corrector = LiveCorrector(engine: codex, options: .init(
            model: cfg.effectiveLiveModel, effort: cfg.liveEffort, vocabulary: vocabulary, twoSources: true))
        var transcript = Transcript()
        var shownAt: [Int: Double] = [:]
        var correctionDelay: [Double] = []
        var captionDelay: [Double] = []
        let began = ProcessInfo.processInfo.systemUptime
        let clock = { (ProcessInfo.processInfo.systemUptime - began) * speed }   // audio-time seconds

        corrector.onResult = { start, text in
            if let text { transcript.apply(SegmentPatch(tStartMs: start, liveText: text)) }
            if let shown = shownAt[start] { correctionDelay.append(clock() - shown) }
        }
        engine.onSecondary = { start, text in
            Task { @MainActor in
                transcript.apply(SegmentPatch(tStartMs: start, altText: text))
                corrector.setSecondary(startMs: start, text: text)
            }
        }
        let session = UUID()
        var tuning = SpeechSegmenter.Tuning()
        tuning.endpointMs = cfg.endpointSilenceMs
        var segmenter = SpeechSegmenter(session: session, tuning: tuning)
        let pipeline = CaptionPipeline(session: session,
                                       interim: { await engine.transcribeInterim($0) },
                                       final: { await engine.transcribeFinal($0) })
        pipeline.onIssue = { print("ACCENT issue \($0)") }
        pipeline.onFinal = { text, start, end in
            transcript.append(TranscriptSegment(text: text, tStartMs: start, tEndMs: end, createdAt: ""))
            shownAt[start] = clock()
            captionDelay.append(clock() - Double(end) / 1000)
            corrector.add(number: transcript.segments.count, startMs: start, primary: text)
        }

        var offset = 0
        while offset < audio.count {
            let end = min(offset + 1600, audio.count)
            let delay = Double(end) / 16000 / speed - (ProcessInfo.processInfo.systemUptime - began)
            if delay > 0 { try? await Task.sleep(nanoseconds: UInt64(delay * 1e9)) }
            for r in segmenter.append(Array(audio[offset..<end]), now: ProcessInfo.processInfo.systemUptime) {
                pipeline.submit(r)
            }
            pipeline.advanceAudio(to: segmenter.totalSamples)
            offset = end
        }
        for r in segmenter.finish(now: ProcessInfo.processInfo.systemUptime) { pipeline.submit(r) }
        await pipeline.finish()
        try? await Task.sleep(nanoseconds: 3_000_000_000)      // let the last secondaries land
        await corrector.drain(timeout: 30)
        await corrector.finish()

        var finalTexts: [Int: String] = [:]
        if env["LOCALCAPTION_ACCENT_FINAL"] == "1" {
            let t = ProcessInfo.processInfo.systemUptime
            finalTexts = try await FinalPass.run(segments: transcript.segments, engine: codex,
                                                 model: cfg.effectiveFinalModel, effort: cfg.finalEffort,
                                                 vocabulary: vocabulary)
            print("ACCENT final_pass_s=\(Int(ProcessInfo.processInfo.systemUptime - t)) lines=\(finalTexts.count)")
        }
        await codex.shutdown()

        let dir = URL(fileURLWithPath: out)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        func write(_ name: String, _ text: (TranscriptSegment) -> String?) throws {
            let lines = transcript.segments.map { s in
                "[\(String(format: "%02d:%02d", s.tStartMs / 60000, s.tStartMs / 1000 % 60))] \(text(s) ?? "")"
            }
            try (lines.joined(separator: "\n") + "\n").write(to: dir.appendingPathComponent(name), atomically: true, encoding: .utf8)
        }
        try write("primary.txt") { $0.text }
        try write("secondary.txt") { $0.altText }
        try write("live.txt") { $0.liveText ?? $0.text }
        if !finalTexts.isEmpty { try write("final.txt") { finalTexts[$0.tStartMs] ?? $0.text } }

        func q(_ v: [Double], _ p: Double) -> String {
            let s = v.sorted(); return s.isEmpty ? "-" : String(format: "%.2f", s[min(s.count - 1, Int(p * Double(s.count)))])
        }
        let corrected = transcript.segments.filter { $0.liveText != nil }.count
        print("ACCENT segments=\(transcript.segments.count) with_secondary=\(transcript.segments.filter { $0.altText != nil }.count) live_corrected=\(corrected)")
        print("ACCENT caption_after_speech_end_s p50=\(q(captionDelay, 0.5)) p90=\(q(captionDelay, 0.9))")
        print("ACCENT correction_after_caption_s p50=\(q(correctionDelay, 0.5)) p90=\(q(correctionDelay, 0.9))")
        XCTAssertFalse(transcript.segments.isEmpty)
        XCTAssertGreaterThan(corrected, transcript.segments.count / 2)
    }
}
