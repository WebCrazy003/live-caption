import XCTest
import WhisperKit
import LocalCaptionKit
@testable import LocalCaption

/// Opt-in real-model replay. Does not open capture, launch a meeting, download
/// models, or touch the user's transcript/configuration. Uses existing model files.
final class EngineReplayTests: XCTestCase {
    func testEqualModelSelectionsCanDecodeIndependently() async throws {
        guard let path = ProcessInfo.processInfo.environment["LOCALCAPTION_REPLAY_WAV"] else {
            throw XCTSkip("Set LOCALCAPTION_REPLAY_WAV to run the on-device model replay")
        }
        let model = AppPaths.models.appendingPathComponent("models/argmaxinc/whisperkit-coreml/openai_whisper-tiny.en/config.json")
        guard FileManager.default.fileExists(atPath: model.path) else { throw XCTSkip("Requires cached tiny.en") }
        let audio = try AudioProcessor.loadAudioAsFloatArray(fromPath: path)
        guard audio.count >= 12 * 16000 else { throw XCTSkip("Requires at least 12 seconds of speech") }
        let engine = WhisperEngine(interimModel: "tiny.en", finalModel: "tiny.en")
        try await engine.prepare(onStatus: { _ in }, onDownload: { _, _ in })
        async let first = engine.transcribeInterim(Array(audio.prefix(96000)))
        async let last = engine.transcribeFinal(Array(audio.suffix(96000)))
        let (a, b) = await (first, last)
        guard case .success(let firstText, let words, _) = a,
              case .success(let lastText, _, _) = b else {
            return XCTFail("Independent same-model lanes failed: \(a.label), \(b.label)")
        }
        XCTAssertFalse(words.isEmpty)
        XCTAssertNotEqual(firstText, lastText, "Fixture must have different opening and closing speech")
    }

    func testLocalAudioReplay() async throws {
        guard let path = ProcessInfo.processInfo.environment["LOCALCAPTION_REPLAY_WAV"] else {
            throw XCTSkip("Set LOCALCAPTION_REPLAY_WAV to run the on-device model replay")
        }
        let root = AppPaths.models.appendingPathComponent("models/argmaxinc/whisperkit-coreml")
        for name in ["openai_whisper-tiny.en", "openai_whisper-small.en"] {
            guard FileManager.default.fileExists(atPath: root.appendingPathComponent(name + "/config.json").path) else {
                throw XCTSkip("Replay requires already-downloaded tiny.en and small.en")
            }
        }
        let audio = try AudioProcessor.loadAudioAsFloatArray(fromPath: path)
        guard audio.count >= 16000 else { return XCTFail("Replay fixture must contain at least one second of audio") }
        let engine = WhisperEngine(interimModel: "tiny.en", finalModel: "small.en")
        try await engine.prepare(onStatus: { _ in }, onDownload: { _, _ in })
        for count in [8000, 16000] {
            let short = await engine.transcribeInterim(Array(audio.prefix(count)))
            if case .success(let text, _, _) = short { print("REPLAY short_ms=\(count / 16) text=\(text)") }
            else { print("REPLAY short_ms=\(count / 16) outcome=\(short.label)") }
        }
        let shortFinal = await engine.transcribeFinal(Array(audio.prefix(16000)))
        if case .success(let text, _, _) = shortFinal { XCTAssertFalse(text.isEmpty) }
        else { XCTFail("One-second speech must be decoded, not skipped: \(shortFinal)") }
        let silence = await engine.transcribeInterim(Array(repeating: 0, count: 96000))
        switch silence {
        case .empty, .filtered: break
        default: XCTFail("Silence should not generate captions: \(silence)")
        }
        let window = Array(audio.prefix(96000))
        let started = ProcessInfo.processInfo.systemUptime
        let first = await engine.transcribeInterim(window)
        switch first {
        case .success(let text, let words, _):
            XCTAssertFalse(words.isEmpty, "Installed interim model must supply word alignment")
            print("REPLAY first_decode_ms=\(Int((ProcessInfo.processInfo.systemUptime - started) * 1000)) words=\(words.count) text=\(text)")
        default: XCTFail("Initial interim returned \(first)")
        }
        // Exercise simultaneous inference on the real independent instances.
        async let final = engine.transcribeFinal(audio)
        let began = ProcessInfo.processInfo.systemUptime
        let interim = await engine.transcribeInterim(window)
        print("REPLAY concurrent_interim_ms=\(Int((ProcessInfo.processInfo.systemUptime - began) * 1000)) outcome=\(interim.label)")
        let finalized = await final
        if case .success(let text, _, _) = finalized {
            XCTAssertFalse(text.isEmpty)
            print("REPLAY final_text=\(text)")
        } else { XCTFail("Final returned \(finalized)") }
        if case .success(_, let words, _) = interim { XCTAssertFalse(words.isEmpty) }
        else { XCTFail("Concurrent interim returned \(interim.label)") }
        if ProcessInfo.processInfo.environment["LOCALCAPTION_REPLAY_STREAM"] == "1" {
            await replay(audio, engine: engine, legacy: true)
            await replay(audio, engine: engine, legacy: false)
        }
    }

    /// The baseline isolates scheduling/assembly: the old serial queue and
    /// LocalAgreement consume the SAME model options and VAD frames as the fix.
    /// This is not a benchmark of an unmodified historical app binary.
    @MainActor
    private func replay(_ audio: [Float], engine: WhisperEngine, legacy: Bool) async {
        let session = UUID()
        var segmenter = SpeechSegmenter(session: session)
        let began = ProcessInfo.processInfo.systemUptime
        var publications: [Double] = []
        var lastText = ""
        var finals: [String] = []
        var interimBusy = false
        var agreement = LocalAgreement()
        var decodeMetrics: [CaptionMetric] = []
        let publish: (String) -> Void = { text in
            if !text.isEmpty && text != lastText {
                publications.append(ProcessInfo.processInfo.systemUptime - began)
            }
            lastText = text
        }
        let pipeline = CaptionPipeline(session: session,
            interim: { await engine.transcribeInterim($0.audio) },
            final: { await engine.transcribeFinal($0.audio) })
        pipeline.onHypothesis = publish
        pipeline.onFinal = { text, _, _ in finals.append(text) }
        pipeline.onMetric = { decodeMetrics.append($0) }
        pipeline.onIssue = { print("REPLAY issue=\($0)") }
        let (stream, continuation) = AsyncStream<SpeechRequest>.makeStream()
        let worker = Task { @MainActor in
            for await request in stream {
                let outcome = request.isFinal ? await engine.transcribeFinal(request.audio) :
                    await engine.transcribeInterim(request.audio)
                if request.isFinal {
                    if case .success(let text, _, _) = outcome { finals.append(text) }
                    publish(""); agreement.reset()
                } else {
                    if case .success(let text, _, _) = outcome {
                        let parts = agreement.update(text.split(separator: " ").map(String.init))
                        publish((parts.committed + parts.provisional).joined(separator: " "))
                    }
                    interimBusy = false
                }
            }
        }
        let submit: (SpeechRequest) -> Void = { request in
            if legacy {
                if !request.isFinal {
                    guard !interimBusy else { return }
                    interimBusy = true
                }
                continuation.yield(request)
            } else { pipeline.submit(request) }
        }
        var offset = 0
        while offset < audio.count {
            let end = min(offset + 1600, audio.count)
            let target = began + Double(end) / 16000
            let delay = target - ProcessInfo.processInfo.systemUptime
            if delay > 0 { try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000)) }
            for request in segmenter.append(Array(audio[offset..<end]), now: ProcessInfo.processInfo.systemUptime) {
                submit(request)
            }
            pipeline.advanceAudio(to: segmenter.totalSamples)
            offset = end
        }
        for request in segmenter.finish(now: ProcessInfo.processInfo.systemUptime) { submit(request) }
        continuation.finish()
        await worker.value
        await pipeline.finish()
        let gaps = zip(publications, publications.dropFirst()).map { $1 - $0 }.sorted()
        let p95 = gaps.isEmpty ? 0 : gaps[min(gaps.count - 1, Int(Double(gaps.count) * 0.95))]
        let mode = legacy ? "serial-prefix-baseline" : "independent-timed-windows"
        print("REPLAY mode=\(mode) publications=\(publications.count) first_ms=\(Int((publications.first ?? 0) * 1000)) gap_p95_ms=\(Int(p95 * 1000)) gap_max_ms=\(Int((gaps.last ?? 0) * 1000))")
        for metric in decodeMetrics {
            print("REPLAY metric final=\(metric.isFinal) queue_ms=\(metric.queueMs) decode_ms=\(metric.decodeMs) audio_lag_ms=\(metric.audioLagMs) outcome=\(metric.outcome)")
        }
        print("REPLAY mode=\(mode) final_text=\(finals.joined(separator: " "))")
        XCTAssertFalse(publications.isEmpty)
        XCTAssertFalse(finals.isEmpty)
    }
}
