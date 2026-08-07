import Foundation
import SwiftUI
import LocalCaptionKit

/// Streaming ASR engine: capture → VAD endpointing → interim/final decode.
///
/// Phase 2 split the model plumbing from the capture lifecycle so a session can be
/// explicitly started / paused / resumed / stopped, and finalized segments flow out via
/// `onFinal` (with sample-based, pause-aware timing) to the `SessionController`, which owns
/// the transcript. This object no longer accumulates paragraphs itself.
@MainActor
final class StreamingOrchestrator: ObservableObject {
    // Interim (provisional) tail + model/download status for the UI.
    @Published var hypothesis = ""
    @Published var status = "Preparing…"
    @Published var detail = ""
    @Published var downloadFraction = 0.0
    @Published var isDownloading = false
    @Published var modelReady = false
    @Published var errorText: String?

    /// Emitted for each finalized segment: (text, tStartMs, tEndMs), session-relative.
    var onFinal: ((String, Int, Int) -> Void)?

    private let engine = WhisperEngine()
    var modelLabel: String { engine.modelDisplayName }

    private var capture: SystemAudioCapture?
    private let buffer = SampleBuffer()
    private var loopTask: Task<Void, Never>?
    private var workerTask: Task<Void, Never>?
    private var totalBytes: Int64 = 0

    // download-speed tracking
    private var lastSample: Date?
    private var lastBytes: Int64 = 0
    private var speedText = ""

    // streaming params (proven values from the minimal app)
    private let sr = 16000.0
    private let hop = 1600                 // 100 ms
    private let samplesPerMs = 16
    private let interimHops = 5            // 500 ms
    private let endpointMs = 500           // finalize ~0.5s after speech stops
    private let silenceRMS: Float = 0.015
    private let maxUtterS = 15.0
    private let interimTailS = 6.0

    // Loop/VAD state (instance-scoped so pause/stop can flush the in-flight utterance).
    private var utter: [Float] = []
    private var hasSpeech = false
    private var silenceMs = 0
    private var sinceInterim = 0
    private var uttStartSample = 0
    private var totalSamples = 0
    private var interimInFlight = false

    private enum Work { case interim([Float]); case finalize([Float], Int, Int) }
    private var workCont: AsyncStream<Work>.Continuation?

    /// Milliseconds of audio consumed while recording (frozen during pause) — the session clock.
    var recordedMs: Int { totalSamples / samplesPerMs }

    // MARK: Model preparation (once)

    /// Download (if needed) and load the model. No capture. Idempotent.
    func prepareModel() async {
        guard !modelReady else { return }
        errorText = nil
        do {
            let folder: URL
            if engine.isModelDownloaded {
                status = "Loading cached model (\(engine.modelDisplayName))…"
                folder = engine.localModelFolder
            } else {
                status = "Preparing…"
                totalBytes = (try? await fetchTotalBytes()) ?? 0
                let totalStr = totalBytes > 0 ? byteStr(totalBytes) : "unknown size"
                status = "Downloading \(engine.modelDisplayName) (\(totalStr))…"
                folder = try await engine.download { [weak self] frac in
                    Task { @MainActor in self?.updateDownload(frac) }
                }
                isDownloading = false; downloadFraction = 1
            }
            status = "Loading \(engine.modelDisplayName) (CPU+GPU)…"; detail = ""; speedText = ""
            try await engine.load(folder: folder) { [weak self] s in
                Task { @MainActor in self?.detail = s }
            }
            detail = ""
            modelReady = true
            status = "Ready — \(engine.modelDisplayName)"
        } catch {
            isDownloading = false; modelReady = false
            status = "Failed"
            errorText = friendlyError(error)
        }
    }

    // MARK: Capture lifecycle

    /// Begin a fresh recording (resets the session clock). Requires the model be ready.
    func startCapture() async throws {
        totalSamples = 0
        resetUtteranceState()
        try await beginCapture()
    }

    /// Restart capture after a pause (keeps the session clock; resets VAD context).
    func resumeCapture() async throws {
        resetUtteranceState()
        try await beginCapture()
    }


    /// Pause: stop capture, flush the in-flight utterance. Returns a final segment if any.
    func pauseAndFinalize() async -> (String, Int, Int)? {
        await endCaptureAndFinalize()
    }

    /// Stop: identical teardown; the session is terminal afterward.
    func stopAndFinalize() async -> (String, Int, Int)? {
        await endCaptureAndFinalize()
    }

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
        // Stop the VAD loop and drain the transcription worker (queued finals get emitted).
        loopTask?.cancel(); await loopTask?.value; loopTask = nil
        await capture?.stop(); capture = nil
        workCont?.finish(); await workerTask?.value
        workerTask = nil; workCont = nil
        hypothesis = ""; interimInFlight = false

        // Flush a still-open utterance (e.g. paused mid-sentence).
        guard !utter.isEmpty, hasSpeech else { resetUtteranceState(); return nil }
        let audio = utter
        let s = uttStartSample / samplesPerMs
        let e = totalSamples / samplesPerMs
        resetUtteranceState()
        let text = await engine.transcribe(audio)
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

        // Serial transcription worker — one decode at a time, off the VAD loop.
        workerTask = Task { @MainActor [weak self] in
            for await work in stream {
                guard let self else { break }
                switch work {
                case .interim(let audio):
                    self.hypothesis = await self.engine.transcribe(audio)
                    self.interimInFlight = false
                case .finalize(let audio, let start, let end):
                    let text = await self.engine.transcribe(audio)
                    if !text.isEmpty { self.onFinal?(text, start, end) }
                    self.hypothesis = ""
                }
            }
        }

        // VAD loop — real-time; never awaits a transcription.
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
                        self.workCont?.yield(.finalize(self.utter, start, end))
                        self.resetUtteranceState()
                    }
                }
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
        }
    }

    // MARK: download helpers
    private func updateDownload(_ frac: Double) {
        isDownloading = true; downloadFraction = frac
        let pct = Int(frac * 100)
        if totalBytes > 0 {
            let done = Int64(Double(totalBytes) * frac)
            let now = Date()
            if let last = lastSample, now.timeIntervalSince(last) > 0.4 {
                let dt = now.timeIntervalSince(last)
                if dt > 0 { speedText = "  ·  \(byteStr(Int64(Double(done - lastBytes) / dt)))/s" }
                lastSample = now; lastBytes = done
            } else if lastSample == nil { lastSample = now; lastBytes = done }
            status = "Downloading model… \(pct)%"
            detail = "\(byteStr(done)) / \(byteStr(totalBytes))\(speedText)"
        } else { status = "Downloading model… \(pct)%" }
    }

    private func fetchTotalBytes() async throws -> Int64 {
        let urlStr = "https://huggingface.co/api/models/\(engine.repo)/tree/main/\(engine.modelName)?recursive=true"
        guard let url = URL(string: urlStr) else { return 0 }
        let (data, _) = try await URLSession.shared.data(from: url)
        guard let arr = try JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return 0 }
        return arr.reduce(Int64(0)) { acc, item in
            guard (item["type"] as? String) == "file",
                  let size = (item["size"] as? NSNumber)?.int64Value else { return acc }
            return acc + size
        }
    }

    private func byteStr(_ b: Int64) -> String { ByteCountFormatter.string(fromByteCount: b, countStyle: .file) }

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
