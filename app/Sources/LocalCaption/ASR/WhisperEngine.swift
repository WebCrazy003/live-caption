import Foundation
import CoreML
import WhisperKit
import LocalCaptionKit

/// Dual-model WhisperKit engine (SPEC §8, decision B4 settled by on-device benchmark):
/// a fast **interim** model for provisional partials + an accurate **final** model for
/// committed captions. Both load once and stay resident.
///
/// Measured on this Mac (CPU+GPU, 7 s clip): tiny.en ≈ 456 ms, small.en ≈ 2.06 s,
/// large-v3-turbo ≈ 3.53 s — so the default is tiny.en partials + small.en finals; turbo
/// is available as an opt-in accuracy mode via the final-model setting.
final class WhisperEngine {
    private var interim: WhisperKit?
    private var final: WhisperKit?

    let interimName: String       // config-friendly, e.g. "tiny.en"
    let finalName: String         // e.g. "small.en" / "large-v3-turbo"
    let interimVariant: String    // repo folder, e.g. "openai_whisper-tiny.en"
    let finalVariant: String

    private let repo = "argmaxinc/whisperkit-coreml"
    private let sr = 16000.0

    private let finalOptions = DecodingOptions(
        task: .transcribe, language: "en",
        skipSpecialTokens: true, withoutTimestamps: true, windowClipTime: 0)
    private let interimOptions = DecodingOptions(task: .transcribe, language: "en",
        temperatureFallbackCount: 0, sampleLength: 128,
        skipSpecialTokens: true, withoutTimestamps: true, wordTimestamps: true,
        windowClipTime: 0)

    init(interimModel: String, finalModel: String) {
        self.interimName = interimModel
        self.finalName = finalModel
        self.interimVariant = WhisperEngine.variant(for: interimModel)
        self.finalVariant = WhisperEngine.variant(for: finalModel)
    }

    var finalLabel: String { finalName }
    var interimLabel: String { interimName }
    var isLoaded: Bool { interim?.tokenizer != nil && final?.tokenizer != nil }

    /// Map a config-friendly model name to its WhisperKit repo variant folder.
    static func variant(for name: String) -> String {
        switch name {
        case "tiny.en": return "openai_whisper-tiny.en"
        case "base.en": return "openai_whisper-base.en"
        case "small.en": return "openai_whisper-small.en"
        case "large-v3": return "openai_whisper-large-v3"
        case "large-v3-turbo": return "openai_whisper-large-v3-v20240930_turbo"
        case "distil-large-v3": return "distil-whisper_distil-large-v3"
        default: return name.contains("_") ? name : "openai_whisper-\(name)"
        }
    }

    private func localFolder(_ variant: String) -> URL {
        AppPaths.models.appendingPathComponent("models/\(repo)/\(variant)", isDirectory: true)
    }

    private func isDownloaded(_ variant: String) -> Bool {
        let fm = FileManager.default
        let f = localFolder(variant)
        guard fm.fileExists(atPath: f.appendingPathComponent("config.json").path) else { return false }
        for part in ["AudioEncoder.mlmodelc", "MelSpectrogram.mlmodelc", "TextDecoder.mlmodelc"] {
            var isDir: ObjCBool = false
            let ok = fm.fileExists(atPath: f.appendingPathComponent(part).path, isDirectory: &isDir)
            if !ok || !isDir.boolValue { return false }
        }
        return true
    }

    /// Ensure both models are on disk (download only what's missing) and load them resident.
    func prepare(onStatus: @escaping (String) -> Void,
                 onDownload: @escaping (String, Double) -> Void) async throws {
        let interimFolder = try await ensure(interimVariant, name: interimName, onStatus: onStatus, onDownload: onDownload)
        let finalFolder = try await ensure(finalVariant, name: finalName, onStatus: onStatus, onDownload: onDownload)

        onStatus("Loading \(interimName) + \(finalName)…")
        interim = try await loadModel(folder: interimFolder)
        // The two scheduling lanes must never share mutable decoder/cache state,
        // including when the same model is selected for both roles.
        final = try await loadModel(folder: finalFolder)

        guard interim?.tokenizer != nil, final?.tokenizer != nil else {
            throw NSError(domain: "WhisperEngine", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "Tokenizer failed to load"])
        }
        // Loading weights does not warm CoreML's first prediction. Do this before
        // capture so first-use compilation cannot consume the live decode budget.
        onStatus("Warming speech models…")
        let silence = [Float](repeating: 0, count: 32000)
        var warmFinal = finalOptions
        warmFinal.temperatureFallbackCount = 0
        for (model, options) in [(interim, interimOptions), (final, warmFinal)] {
            let outcome = await run(model, silence, options: options, budget: nil)
            if case .failure(let message) = outcome {
                throw NSError(domain: "WhisperEngine", code: 2,
                              userInfo: [NSLocalizedDescriptionKey: message])
            }
        }
    }

    private func ensure(_ variant: String, name: String,
                        onStatus: @escaping (String) -> Void,
                        onDownload: @escaping (String, Double) -> Void) async throws -> URL {
        if isDownloaded(variant) { return localFolder(variant) }
        onStatus("Downloading \(name)…")
        return try await WhisperKit.download(variant: variant, downloadBase: AppPaths.models, from: repo) { p in
            onDownload(name, p.fractionCompleted)
        }
    }

    private func loadModel(folder: URL) async throws -> WhisperKit {
        let compute = ModelComputeOptions(
            melCompute: .cpuAndGPU, audioEncoderCompute: .cpuAndGPU,
            textDecoderCompute: .cpuAndGPU, prefillCompute: .cpuAndGPU)
        let wk = try await WhisperKit(WhisperKitConfig(
            modelFolder: folder.path, computeOptions: compute,
            verbose: false, logLevel: .info, prewarm: false, load: false, download: false))
        try await wk.loadModels()
        return wk
    }

    func transcribeInterim(_ audio: [Float]) async -> SpeechOutcome {
        await run(interim, audio, options: interimOptions, budget: 2)
    }

    func transcribeFinal(_ audio: [Float]) async -> SpeechOutcome {
        await run(final, audio, options: finalOptions, budget: nil)
    }

    /// Final accuracy options/filters are retained; windowClipTime is zero because
    /// its default skips <=1 s utterances entirely. Interim decoding has a token
    /// ceiling and cooperative deadline; an outstanding CoreML call is never overlapped
    /// with a replacement decode on the same instance.
    private func run(_ wk: WhisperKit?, _ audio: [Float], options: DecodingOptions,
                     budget: Double?) async -> SpeechOutcome {
        guard let wk else { return .failure("Speech model is not loaded") }
        guard Double(audio.count) / sr > 0.2 else { return .empty }
        let deadline = budget.map { ProcessInfo.processInfo.systemUptime + $0 }
        do {
            try Task.checkCancellation()
            let results: [TranscriptionResult] = try await wk.transcribe(audioArray: audio, decodeOptions: options) { _ in
                if let deadline, ProcessInfo.processInfo.systemUptime >= deadline { return false }
                return nil
            }
            try Task.checkCancellation()
            if let deadline, ProcessInfo.processInfo.systemUptime >= deadline { return .timedOut }
            var kept: [String] = []
            var words: [CaptionWord] = []
            var rejected = false
            var fallbacks = 0
            for result in results {
                fallbacks += Int(result.timings.totalDecodingFallbacks)
                for seg in result.segments {
                    if Filters.isLowQuality(avgLogprob: Double(seg.avgLogprob),
                                            noSpeechProb: Double(seg.noSpeechProb),
                                            compressionRatio: Double(seg.compressionRatio)) {
                        rejected = true
                        continue
                    }
                    let cleaned = Filters.clean(seg.text)
                    if !cleaned.isEmpty {
                        kept.append(cleaned)
                        words += (seg.words ?? []).map {
                            CaptionWord(Filters.clean($0.word), start: Double($0.start), end: Double($0.end))
                        }
                    }
                }
            }
            let text = Filters.clean(kept.joined(separator: " "))
            if text.isEmpty { return rejected ? .filtered : .empty }
            if Filters.isHallucination(text) { return .filtered }
            return .success(text: text, words: words, fallbacks: fallbacks)
        } catch is CancellationError { return .cancelled }
        catch { return .failure(error.localizedDescription) }
    }
}
