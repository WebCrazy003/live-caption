import Foundation
import SwiftUI
import LocalCaptionKit

/// Drives live captioning: capture → VAD endpointing → interim/final decode → paragraphs.
///
/// This is the `StreamingOrchestrator` role from SPEC.md §20, decomposed out of the
/// `minimal/` god-object (`CaptionModel`). Behavior is unchanged from the proven minimal
/// app; the tuned VAD constants remain hardcoded here (wiring them to `Config.asr` is a
/// later refinement). Session lifecycle (start/pause/stop/save) is Phase 2 (SPEC-04).
@MainActor
final class StreamingOrchestrator: ObservableObject {
    @Published var paragraphs: [String] = []  // completed paragraphs
    @Published var current = ""               // paragraph currently being built
    @Published var hypothesis = ""            // interim (in-progress utterance)
    @Published var status = "Starting…"
    @Published var detail = ""
    @Published var downloadFraction = 0.0
    @Published var isDownloading = false
    @Published var isReady = false
    @Published var errorText: String?

    private let engine = WhisperEngine()
    private var capture: SystemAudioCapture?
    private let buffer = SampleBuffer()
    private var loopTask: Task<Void, Never>?
    private var totalBytes: Int64 = 0

    // download-speed tracking
    private var lastSample: Date?
    private var lastBytes: Int64 = 0
    private var speedText = ""

    // streaming params (proven values from the minimal app)
    private let sr = 16000.0
    private let hop = 1600                 // 100 ms
    private let interimHops = 5            // 500 ms
    private let endpointMs = 500           // finalize ~0.5s after speech stops
    private let silenceRMS: Float = 0.015  // higher = less likely to trigger on near-silence
    private let maxUtterS = 15.0
    private let interimTailS = 6.0         // interim decodes only the recent tail (stays fast)

    private enum Work { case interim([Float]); case finalize([Float]) }
    private var workCont: AsyncStream<Work>.Continuation?
    private var interimInFlight = false

    func retry() {
        errorText = nil; status = "Starting…"; detail = ""
        downloadFraction = 0; isDownloading = false; isReady = false
        Task { await start() }
    }

    func start() async {
        guard !isReady else { return }
        do {
            // 1) Download the model with live percent / MB / speed.
            status = "Preparing…"
            totalBytes = (try? await fetchTotalBytes()) ?? 0
            let totalStr = totalBytes > 0 ? byteStr(totalBytes) : "unknown size"
            status = "Downloading model (\(totalStr))…"
            let folder = try await engine.download { [weak self] frac in
                Task { @MainActor in self?.updateDownload(frac) }
            }
            isDownloading = false; downloadFraction = 1

            // 2) Load on CPU+GPU.
            status = "Loading model (CPU+GPU)…"; detail = ""; speedText = ""
            try await engine.load(folder: folder) { [weak self] s in
                Task { @MainActor in self?.detail = s }
            }

            // 3) Start capturing system audio (Screen Recording permission required).
            status = "Requesting Screen Recording permission…"; detail = ""
            let capture = SystemAudioCapture(
                onSamples: { [buffer] s in buffer.append(s) },
                onError: { [weak self] e in Task { @MainActor in self?.errorText = String(describing: e) } }
            )
            self.capture = capture
            try await capture.start()

            status = "Listening (system audio)…"; isReady = true
            startLoop()
        } catch {
            isDownloading = false; isReady = false
            status = "Failed"
            errorText = friendlyError(error)
        }
    }

    func stop() {
        loopTask?.cancel(); loopTask = nil
        workCont?.finish(); workCont = nil
        Task { [capture] in await capture?.stop() }
        capture = nil
        isReady = false
        status = "Stopped"
    }

    // MARK: streaming loop (VAD endpointing + interim/final)
    private func startLoop() {
        // Serial transcription worker — decodes one request at a time (WhisperKit isn't reentrant),
        // and runs OFF the VAD loop so endpoint detection is never blocked.
        let (stream, cont) = AsyncStream<Work>.makeStream()
        workCont = cont
        Task { @MainActor [weak self] in
            for await work in stream {
                guard let self else { break }
                switch work {
                case .interim(let audio):
                    self.hypothesis = await self.engine.transcribe(audio)
                    self.interimInFlight = false
                case .finalize(let audio):
                    let text = await self.engine.transcribe(audio)
                    if !text.isEmpty { self.appendFinal(text) }
                    self.hypothesis = ""
                }
            }
        }

        // VAD loop — real-time, never awaits transcription. Detects the pause immediately.
        loopTask = Task { @MainActor [weak self] in
            guard let self else { return }
            var frame: [Float] = []
            var utter: [Float] = []
            var hasSpeech = false
            var silenceMs = 0
            var sinceInterim = 0
            while !Task.isCancelled {
                frame.append(contentsOf: self.buffer.drain())
                while frame.count >= self.hop {
                    let h = Array(frame.prefix(self.hop)); frame.removeFirst(self.hop)
                    utter.append(contentsOf: h)
                    sinceInterim += 1
                    let level = Self.rms(h)
                    if level >= self.silenceRMS { hasSpeech = true; silenceMs = 0 }
                    else if hasSpeech { silenceMs += 100 }

                    // interim: bounded recent tail, at most one in flight
                    if hasSpeech, sinceInterim >= self.interimHops, silenceMs == 0, !self.interimInFlight {
                        sinceInterim = 0
                        self.interimInFlight = true
                        let n = min(utter.count, Int(self.interimTailS * self.sr))
                        self.workCont?.yield(.interim(Array(utter.suffix(n))))
                    }

                    // endpoint: hand off the utterance and reset immediately (no waiting)
                    if (hasSpeech && silenceMs >= self.endpointMs) || Double(utter.count) / self.sr >= self.maxUtterS {
                        self.workCont?.yield(.finalize(utter))
                        utter.removeAll(keepingCapacity: true)
                        hasSpeech = false; silenceMs = 0; sinceInterim = 0
                    }
                }
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
        }
    }

    /// Accumulate finalized utterances into a paragraph; break at ~100 words or ~4 sentences.
    private func appendFinal(_ text: String) {
        current = current.isEmpty ? text : current + " " + text
        if Filters.sentenceCount(current) >= 4 || Filters.wordCount(current) >= 100 {
            paragraphs.append(current)
            current = ""
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
