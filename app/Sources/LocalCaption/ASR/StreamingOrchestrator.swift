import Foundation
import SwiftUI
import LocalCaptionKit

/// Streaming ASR engine: capture → VAD endpointing → interim/final decode.
///
/// Dual-model (SPEC §8, B4): a fast interim model (tiny.en) drives provisional partials,
/// stabilized with LocalAgreement-2; an accurate final model (small.en / turbo) produces
/// committed captions on endpoint. Finalized segments flow out via `onFinal` with
/// sample-based, pause-aware timing to the `SessionController`.
@MainActor
final class StreamingOrchestrator: ObservableObject {
    @Published var hypothesis = ""
    @Published var status = "Preparing…"
    @Published var detail = ""
    @Published var downloadFraction = 0.0
    @Published var isDownloading = false
    @Published var modelReady = false
    @Published var errorText: String?

    /// Emitted for each finalized segment: (text, tStartMs, tEndMs), session-relative.
    var onFinal: ((String, Int, Int) -> Void)?

    /// Emitted as soon as VAD detects the end of an utterance, before the slower final
    /// model runs. The text is the latest interim caption currently visible in the UI.
    var onSpeechEnded: ((String) -> Void)?

    private var engine: WhisperEngine?
    private(set) var interimName = ""
    private(set) var finalName = ""
    var modelLabel: String { finalName.isEmpty ? "" : "\(interimName) · \(finalName)" }

    private var capture: SystemAudioCapture?
    private let buffer = SampleBuffer()
    private var loopTask: Task<Void, Never>?
    private var workerTask: Task<Void, Never>?

    // streaming params. Fixed frame geometry + tunables driven from Config (see applyTuning).
    private let sr = 16000.0
    private let hop = 1600                 // 100 ms
    private let samplesPerMs = 16
    private let interimTailS = 6.0
    private var interimHops = 5
    private var endpointMs = 600
    private var silenceRMS: Float = 0.015
    private var maxUtterS = 20.0

    /// Apply Config-driven tuning before a session starts.
    func applyTuning(endpointSilenceMs: Int, interimIntervalMs: Int,
                     maxUtteranceS: Int, vadSensitivity: Int) {
        endpointMs = max(100, endpointSilenceMs)
        interimHops = max(1, interimIntervalMs / 100)
        maxUtterS = Double(max(5, maxUtteranceS))
        switch max(0, min(3, vadSensitivity)) {
        case 0: silenceRMS = 0.030
        case 1: silenceRMS = 0.020
        case 2: silenceRMS = 0.015
        default: silenceRMS = 0.008
        }
    }

    // Loop/VAD state (instance-scoped so pause/stop can flush the in-flight utterance).
    private var utter: [Float] = []
    private var hasSpeech = false
    private var silenceMs = 0
    private var sinceInterim = 0
    private var uttStartSample = 0
    private var totalSamples = 0
    private var interimInFlight = false
    private var agreement = LocalAgreement()

    private enum Work { case interim([Float]); case finalize([Float], Int, Int) }
    private var workCont: AsyncStream<Work>.Continuation?

    var recordedMs: Int { totalSamples / samplesPerMs }

    // MARK: Model preparation (once)

    /// Download (if needed) and load both models. Idempotent.
    func prepareModel(interimModel: String, finalModel: String) async {
        guard !modelReady else { return }
        errorText = nil
        interimName = interimModel; finalName = finalModel
        let engine = WhisperEngine(interimModel: interimModel, finalModel: finalModel)
        self.engine = engine
        do {
            try await engine.prepare(
                onStatus: { [weak self] s in Task { @MainActor in self?.status = s } },
                onDownload: { [weak self] name, frac in
                    Task { @MainActor in self?.updateDownload(name, frac) }
                })
            isDownloading = false; downloadFraction = 1
            modelReady = true
            status = "Ready — \(interimModel) · \(finalModel)"
        } catch {
            isDownloading = false; modelReady = false
            status = "Failed"
            errorText = friendlyError(error)
        }
    }

    // MARK: Capture lifecycle

    func startCapture() async throws {
        totalSamples = 0
        resetUtteranceState()
        try await beginCapture()
    }

    func resumeCapture() async throws {
        resetUtteranceState()
        try await beginCapture()
    }

    func pauseAndFinalize() async -> (String, Int, Int)? { await endCaptureAndFinalize() }
    func stopAndFinalize() async -> (String, Int, Int)? { await endCaptureAndFinalize() }

    private func beginCapture() async throws {
        errorText = nil
        let capture = SystemAudioCapture(
            onSamples: { [buffer] s in buffer.append(s) },
            onError: { [weak self] e in Task { @MainActor in self?.errorText = String(describing: e) } }
        )
        self.capture = capture
        do {
            try await capture.start()   // requests Screen Recording; throws if denied
        } catch {
            self.capture = nil
            errorText = friendlyError(error)
            throw error
        }
        beginLoops()
    }

    private func endCaptureAndFinalize() async -> (String, Int, Int)? {
        loopTask?.cancel(); await loopTask?.value; loopTask = nil
        await capture?.stop(); capture = nil
        workCont?.finish(); await workerTask?.value
        workerTask = nil; workCont = nil
        hypothesis = ""; interimInFlight = false; agreement.reset()

        guard !utter.isEmpty, hasSpeech, let engine else { resetUtteranceState(); return nil }
        let audio = utter
        let s = uttStartSample / samplesPerMs
        let e = totalSamples / samplesPerMs
        resetUtteranceState()
        let text = await engine.transcribeFinal(audio)
        return text.isEmpty ? nil : (text, s, e)
    }

    private func resetUtteranceState() {
        utter.removeAll(keepingCapacity: true)
        hasSpeech = false; silenceMs = 0; sinceInterim = 0
    }

    // MARK: Loops

    private func beginLoops() {
        let (stream, cont) = AsyncStream<Work>.makeStream()
        workCont = cont

        workerTask = Task { @MainActor [weak self] in
            for await work in stream {
                guard let self, let engine = self.engine else { break }
                switch work {
                case .interim(let audio):
                    let text = await engine.transcribeInterim(audio)
                    let (committed, provisional) = self.agreement.update(
                        text.split(separator: " ").map(String.init))
                    self.hypothesis = (committed + provisional).joined(separator: " ")
                    self.interimInFlight = false
                case .finalize(let audio, let start, let end):
                    let text = await engine.transcribeFinal(audio)
                    if !text.isEmpty { self.onFinal?(text, start, end) }
                    self.hypothesis = ""
                    self.agreement.reset()
                }
            }
        }

        loopTask = Task { @MainActor [weak self] in
            guard let self else { return }
            var frame: [Float] = []
            while !Task.isCancelled {
                frame.append(contentsOf: self.buffer.drain())
                while frame.count >= self.hop {
                    let h = Array(frame.prefix(self.hop)); frame.removeFirst(self.hop)
                    if self.utter.isEmpty { self.uttStartSample = self.totalSamples }
                    self.utter.append(contentsOf: h)
                    self.totalSamples += self.hop
                    self.sinceInterim += 1
                    let level = Self.rms(h)
                    if level >= self.silenceRMS { self.hasSpeech = true; self.silenceMs = 0 }
                    else if self.hasSpeech { self.silenceMs += 100 }

                    if self.hasSpeech, self.sinceInterim >= self.interimHops,
                       self.silenceMs == 0, !self.interimInFlight {
                        self.sinceInterim = 0
                        self.interimInFlight = true
                        let n = min(self.utter.count, Int(self.interimTailS * self.sr))
                        self.workCont?.yield(.interim(Array(self.utter.suffix(n))))
                    }

                    if (self.hasSpeech && self.silenceMs >= self.endpointMs)
                        || Double(self.utter.count) / self.sr >= self.maxUtterS {
                        let start = self.uttStartSample / self.samplesPerMs
                        let end = self.totalSamples / self.samplesPerMs
                        self.onSpeechEnded?(self.hypothesis)
                        self.workCont?.yield(.finalize(self.utter, start, end))
                        self.resetUtteranceState()
                    }
                }
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
        }
    }

    // MARK: helpers
    private func updateDownload(_ name: String, _ frac: Double) {
        isDownloading = true
        downloadFraction = frac
        status = "Downloading \(name)… \(Int(frac * 100))%"
    }

    private func friendlyError(_ error: Error) -> String {
        let e = String(describing: error)
        if e.localizedCaseInsensitiveContains("declined") || e.localizedCaseInsensitiveContains("permission")
            || e.localizedCaseInsensitiveContains("TCC") || e.localizedCaseInsensitiveContains("not authorized") {
            return "Screen Recording permission is required.\n\nOpen System Settings ▸ Privacy & Security ▸ Screen Recording, enable “LocalCaption”, then press Retry.\n\n(\(e))"
        }
        return e
    }

    static func rms(_ x: [Float]) -> Float {
        if x.isEmpty { return 0 }
        var s: Float = 0; for v in x { s += v * v }
        return (s / Float(x.count)).squareRoot()
    }
}
