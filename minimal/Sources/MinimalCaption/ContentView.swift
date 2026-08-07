import SwiftUI
import Foundation
import CoreML
import WhisperKit

@MainActor
final class CaptionModel: ObservableObject {
    @Published var paragraphs: [String] = []  // completed paragraphs
    @Published var current = ""               // paragraph currently being built
    @Published var hypothesis = ""            // interim (in-progress utterance)
    @Published var status = "Starting…"
    @Published var detail = ""
    @Published var downloadFraction = 0.0
    @Published var isDownloading = false
    @Published var isReady = false
    @Published var errorText: String?

    private var whisperKit: WhisperKit?
    private var capture: SystemAudioCapture?
    private let buffer = SampleBuffer()
    private var loopTask: Task<Void, Never>?

    private let modelName = "openai_whisper-small.en"
    private let repo = "argmaxinc/whisperkit-coreml"
    private var totalBytes: Int64 = 0

    // download-speed tracking
    private var lastSample: Date?
    private var lastBytes: Int64 = 0
    private var speedText = ""

    // streaming params
    private let sr = 16000.0
    private let hop = 1600                 // 100 ms
    private let interimHops = 5            // 500 ms
    private let endpointMs = 500           // finalize ~0.5s after speech stops
    private let silenceRMS: Float = 0.015   // higher = less likely to trigger on near-silence
    private let maxUtterS = 15.0
    private let interimTailS = 6.0         // interim decodes only the recent tail (stays fast)

    private enum Work { case interim([Float]); case finalize([Float]) }
    private var workCont: AsyncStream<Work>.Continuation?
    private var interimInFlight = false

    private lazy var decodeOptions = DecodingOptions(
        task: .transcribe, language: "en",
        skipSpecialTokens: true, withoutTimestamps: true
    )

    func retry() {
        errorText = nil; status = "Starting…"; detail = ""
        downloadFraction = 0; isDownloading = false; isReady = false
        Task { await start() }
    }

    func start() async {
        do {
            // 1) Download the model with live percent / MB / speed.
            status = "Preparing…"
            totalBytes = (try? await fetchTotalBytes()) ?? 0
            let totalStr = totalBytes > 0 ? byteStr(totalBytes) : "unknown size"
            status = "Downloading model (\(totalStr))…"
            let folder = try await WhisperKit.download(variant: modelName, from: repo) { [weak self] progress in
                let frac = progress.fractionCompleted
                Task { @MainActor in self?.updateDownload(frac) }
            }
            isDownloading = false; downloadFraction = 1

            // 2) Load on CPU+GPU (skips the slow one-time ANE compilation).
            status = "Loading model (CPU+GPU)…"; detail = ""; speedText = ""
            let compute = ModelComputeOptions(
                melCompute: .cpuAndGPU, audioEncoderCompute: .cpuAndGPU,
                textDecoderCompute: .cpuAndGPU, prefillCompute: .cpuAndGPU
            )
            let wk = try await WhisperKit(WhisperKitConfig(
                modelFolder: folder.path, computeOptions: compute,
                verbose: true, logLevel: .info, prewarm: false, load: false, download: false
            ))
            self.whisperKit = wk
            wk.modelStateCallback = { [weak self] _, s in Task { @MainActor in self?.detail = "state: \(s)" } }
            try await wk.loadModels()
            guard wk.tokenizer != nil else {
                throw NSError(domain: "MinimalCaption", code: 1,
                              userInfo: [NSLocalizedDescriptionKey: "Tokenizer failed to load"])
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
                    self.hypothesis = await self.transcribe(audio)
                    self.interimInFlight = false
                case .finalize(let audio):
                    let text = await self.transcribe(audio)
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

    private func transcribe(_ audio: [Float]) async -> String {
        guard let wk = whisperKit, Double(audio.count) / sr > 0.2 else { return "" }
        do {
            let results = try await wk.transcribe(audioArray: audio, decodeOptions: decodeOptions)
            let text = Self.clean(results.map { $0.text }.joined(separator: " "))
            return Self.isHallucination(text) ? "" : text
        } catch { return "" }
    }

    /// Accumulate finalized utterances into a paragraph; break at ~100 words or ~4 sentences.
    private func appendFinal(_ text: String) {
        current = current.isEmpty ? text : current + " " + text
        if Self.sentenceCount(current) >= 4 || Self.wordCount(current) >= 100 {
            paragraphs.append(current)
            current = ""
        }
    }
    static func wordCount(_ s: String) -> Int { s.split(whereSeparator: { $0 == " " || $0 == "\n" }).count }
    static func sentenceCount(_ s: String) -> Int { s.reduce(0) { $1 == "." || $1 == "!" || $1 == "?" ? $0 + 1 : $0 } }

    /// Filter Whisper's silence hallucinations ("you you you", "Thank you.", etc.).
    static func isHallucination(_ text: String) -> Bool {
        let stem = text.lowercased().trimmingCharacters(in: .whitespaces)
            .trimmingCharacters(in: CharacterSet(charactersIn: ".!?,-"))
        if stem.isEmpty { return true }
        let block: Set<String> = [
            "you", "thank you", "thanks", "thanks for watching", "bye", "bye bye",
            "so", "the", "uh", "um", "you know", "thank you so much", "okay", "mm", "hmm", "yeah"
        ]
        if block.contains(stem) { return true }
        let words = stem.split(separator: " ").map(String.init)
        if words.count >= 3 {
            let unique = Set(words)
            if unique.count == 1 { return true }                                  // "you you you"
            if Double(unique.count) / Double(words.count) < 0.34 { return true }   // heavy repetition
        }
        return false
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
        let urlStr = "https://huggingface.co/api/models/\(repo)/tree/main/\(modelName)?recursive=true"
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
            return "Screen Recording permission is required.\n\nOpen System Settings ▸ Privacy & Security ▸ Screen Recording, enable “MinimalCaption”, then press Retry.\n\n(\(e))"
        }
        return e
    }

    static func rms(_ x: [Float]) -> Float {
        if x.isEmpty { return 0 }
        var s: Float = 0; for v in x { s += v * v }
        return (s / Float(x.count)).squareRoot()
    }

    /// Strip Whisper special/timestamp tokens (<|...|>) and [BLANK_AUDIO]; collapse whitespace.
    static func clean(_ s: String) -> String {
        var t = s.replacingOccurrences(of: "<\\|[^|]*\\|>", with: "", options: .regularExpression)
        t = t.replacingOccurrences(of: "[BLANK_AUDIO]", with: "")
        t = t.replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
        return t.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

struct ContentView: View {
    @StateObject private var model = CaptionModel()

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                if !model.isReady && model.errorText == nil { ProgressView().controlSize(.small) }
                Text(model.status).font(.headline)
                    .foregroundStyle(model.errorText == nil ? Color.primary : Color.red)
            }
            if model.isDownloading {
                ProgressView(value: model.downloadFraction).progressViewStyle(.linear)
            }
            if !model.detail.isEmpty {
                Text(model.detail).font(.caption).foregroundStyle(.secondary).monospacedDigit()
            }
            if let err = model.errorText {
                ScrollView {
                    Text(err).font(.callout).foregroundStyle(.red)
                        .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 140)
                Button("Retry") { model.retry() }
            }

            Divider()

            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 16) {   // paragraph spacing
                        ForEach(Array(model.paragraphs.enumerated()), id: \.offset) { _, p in
                            Text(p).font(.title3).lineSpacing(3).textSelection(.enabled)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        // building paragraph: finalized-so-far + dimmed interim, flowing together
                        if !model.current.isEmpty || !model.hypothesis.isEmpty {
                            (Text(model.current)
                             + Text(model.current.isEmpty ? model.hypothesis : " " + model.hypothesis)
                                .foregroundColor(.secondary))
                                .font(.title3).lineSpacing(3)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        if model.paragraphs.isEmpty && model.current.isEmpty && model.hypothesis.isEmpty && model.isReady {
                            Text("Play some audio…").font(.title3).foregroundStyle(.tertiary)
                        }
                        Color.clear.frame(height: 1).id("BOTTOM")   // scroll anchor
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .onChange(of: model.paragraphs.count) {
                    withAnimation(.easeOut(duration: 0.2)) { proxy.scrollTo("BOTTOM", anchor: .bottom) }
                }
                .onChange(of: model.current) { proxy.scrollTo("BOTTOM", anchor: .bottom) }
                .onChange(of: model.hypothesis) { proxy.scrollTo("BOTTOM", anchor: .bottom) }
            }
        }
        .padding()
        .task { await model.start() }
    }
}
