import Foundation
import WhisperKit
import LocalCaptionKit

// On-device ASR benchmark to settle B4 (turbo-only vs hybrid) with real numbers.
// Usage: swift run Benchmark <wav-path> [model ...]
// Measures per-model: load time, decode median/p90 latency, RTF, and prints the
// transcript + first-segment metadata (avgLogprob / noSpeechProb / compressionRatio).

func ms(since t0: DispatchTime) -> Double {
    Double(DispatchTime.now().uptimeNanoseconds - t0.uptimeNanoseconds) / 1e6
}

func ensureModel(_ variant: String, repo: String) async throws -> URL {
    let folder = AppPaths.models.appendingPathComponent("models/\(repo)/\(variant)", isDirectory: true)
    if FileManager.default.fileExists(atPath: folder.appendingPathComponent("config.json").path) {
        return folder
    }
    FileHandle.standardError.write(Data("  downloading \(variant)…\n".utf8))
    return try await WhisperKit.download(variant: variant, downloadBase: AppPaths.models, from: repo) { _ in }
}

let args = CommandLine.arguments
let wav = args.count > 1 ? args[1] : "/tmp/lc-bench/clip.wav"
let models = args.count > 2 ? Array(args[2...]) : [
    "openai_whisper-tiny.en",                       // interim candidate
    "openai_whisper-small.en",                      // current app model
    "openai_whisper-large-v3-v20240930_turbo",      // final candidate (turbo)
]

guard let audio = try? AudioProcessor.loadAudioAsFloatArray(fromPath: wav) else {
    print("ERROR: could not load audio at \(wav)"); exit(1)
}
let audioMs = Double(audio.count) / 16.0
print(String(format: "\nClip: %@  (%.1fs)\n", wav, audioMs / 1000))
print("model                                     load    decode(med/p90)     RTF     text")
print(String(repeating: "─", count: 96))

let opts = DecodingOptions(task: .transcribe, language: "en", skipSpecialTokens: true, withoutTimestamps: true)
let repo = "argmaxinc/whisperkit-coreml"
let runs = 5

for model in models {
    do {
        let folder = try await ensureModel(model, repo: repo)
        let compute = ModelComputeOptions(
            melCompute: .cpuAndGPU, audioEncoderCompute: .cpuAndGPU,
            textDecoderCompute: .cpuAndGPU, prefillCompute: .cpuAndGPU)
        let l0 = DispatchTime.now()
        let wk = try await WhisperKit(WhisperKitConfig(
            modelFolder: folder.path, computeOptions: compute,
            verbose: false, logLevel: .info, prewarm: false, load: false, download: false))
        try await wk.loadModels()
        let loadMs = ms(since: l0)

        _ = try? await wk.transcribe(audioArray: audio, decodeOptions: opts)  // warm-up

        var times: [Double] = []
        var text = ""
        var meta = ""
        for _ in 0..<runs {
            let t0 = DispatchTime.now()
            let res = try await wk.transcribe(audioArray: audio, decodeOptions: opts)
            times.append(ms(since: t0))
            text = res.map { $0.text }.joined(separator: " ").trimmingCharacters(in: .whitespaces)
            if let seg = res.first?.segments.first {
                meta = String(format: "avgLogprob %.2f  noSpeech %.2f  compRatio %.2f",
                              seg.avgLogprob, seg.noSpeechProb, seg.compressionRatio)
            }
        }
        let s = times.sorted()
        let median = s[s.count / 2]
        let p90 = s[min(s.count - 1, Int(Double(s.count) * 0.9))]
        let rtf = median / audioMs
        print(String(format: "%-40@  %5.0fms  %6.0f/%-6.0fms   %.2f×  %.1f×rt",
                     model as NSString, loadMs, median, p90, rtf, 1 / rtf))
        print("    \(meta)")
        print("    \"\(text)\"\n")
    } catch {
        print("\(model)\n    FAILED: \(error)\n")
    }
}
