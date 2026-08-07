import Foundation
import CoreML
import WhisperKit
import LocalCaptionKit

/// Wraps WhisperKit: one-time model download (into App Support `models/`), model load
/// on CPU+GPU, and single-shot transcription of a PCM buffer.
///
/// This is the `WhisperEngine` role from SPEC.md §20 — the model plumbing, split out of
/// the streaming loop (`StreamingOrchestrator`). Still single-model (`small.en`); the
/// dual-model hybrid is Phase 4 (SPEC-03, B4).
final class WhisperEngine {
    private var whisperKit: WhisperKit?

    // Current model. Phase 4 swaps this to the tiny+turbo hybrid.
    let modelName = "openai_whisper-small.en"
    let repo = "argmaxinc/whisperkit-coreml"

    /// Human-friendly model name for the UI, e.g. "small.en".
    var modelDisplayName: String {
        modelName.replacingOccurrences(of: "openai_whisper-", with: "")
    }

    /// Where `download(downloadBase:)` lays the model out on disk:
    /// `<models>/models/<repo>/<variant>/`.
    var localModelFolder: URL {
        AppPaths.models.appendingPathComponent("models/\(repo)/\(modelName)", isDirectory: true)
    }

    /// True if a complete model is already cached on disk (→ load offline, no network).
    var isModelDownloaded: Bool {
        let fm = FileManager.default
        let f = localModelFolder
        guard fm.fileExists(atPath: f.appendingPathComponent("config.json").path) else { return false }
        for part in ["AudioEncoder.mlmodelc", "MelSpectrogram.mlmodelc", "TextDecoder.mlmodelc"] {
            var isDir: ObjCBool = false
            let ok = fm.fileExists(atPath: f.appendingPathComponent(part).path, isDirectory: &isDir)
            if !ok || !isDir.boolValue { return false }
        }
        return true
    }

    private let sr = 16000.0

    private let decodeOptions = DecodingOptions(
        task: .transcribe, language: "en",
        skipSpecialTokens: true, withoutTimestamps: true
    )

    var isLoaded: Bool { whisperKit?.tokenizer != nil }

    /// Download weights into `AppPaths.models` (network used ONLY here; offline after).
    func download(progress: @escaping (Double) -> Void) async throws -> URL {
        try await WhisperKit.download(
            variant: modelName,
            downloadBase: AppPaths.models,
            from: repo
        ) { p in progress(p.fractionCompleted) }
    }

    /// Load on CPU+GPU (skips the slow one-time ANE compilation), report state transitions.
    func load(folder: URL, onState: @escaping (String) -> Void) async throws {
        let compute = ModelComputeOptions(
            melCompute: .cpuAndGPU, audioEncoderCompute: .cpuAndGPU,
            textDecoderCompute: .cpuAndGPU, prefillCompute: .cpuAndGPU
        )
        let wk = try await WhisperKit(WhisperKitConfig(
            modelFolder: folder.path, computeOptions: compute,
            verbose: true, logLevel: .info, prewarm: false, load: false, download: false
        ))
        wk.modelStateCallback = { _, s in onState("state: \(s)") }
        try await wk.loadModels()
        guard wk.tokenizer != nil else {
            throw NSError(domain: "WhisperEngine", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "Tokenizer failed to load"])
        }
        self.whisperKit = wk
    }

    /// Transcribe a mono 16 kHz buffer → cleaned text ("" if too short / hallucinated).
    func transcribe(_ audio: [Float]) async -> String {
        guard let wk = whisperKit, Double(audio.count) / sr > 0.2 else { return "" }
        do {
            let results = try await wk.transcribe(audioArray: audio, decodeOptions: decodeOptions)
            let text = Filters.clean(results.map { $0.text }.joined(separator: " "))
            return Filters.isHallucination(text) ? "" : text
        } catch { return "" }
    }
}
