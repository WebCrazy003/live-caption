import Foundation
import SwiftUI
import LocalCaptionKit
import OSLog

/// Streaming ASR engine: capture → VAD endpointing → interim/final decode.
///
/// Dual-model (SPEC §8, B4): a fast interim model (tiny.en) drives provisional partials,
/// aligned by word timestamps; an accurate final model (small.en / turbo) produces
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

    /// Completion includes the durable journal acknowledgment.
    var onFinal: ((String, Int, Int) async throws -> Void)?
    var onSpeechEnded: ((String) -> Void)?
    var onFinalized: ((String) -> Void)?
    var onCaptureMustPause: (() -> Void)?

    private var engine: WhisperEngine?
    private(set) var interimName = ""
    private(set) var finalName = ""
    var modelLabel: String { finalName.isEmpty ? "" : "\(interimName) · \(finalName)" }
    private var capture: SystemAudioCapture?
    private var processor: CaptureProcessor?
    private var pipeline: CaptionPipeline?
    private var loopTask: Task<Void, Never>?
    private var endingTask: Task<Void, Never>?
    private var totalSamples = 0
    private var activeCaptureID: UUID?
    private var tuning = SpeechSegmenter.Tuning()
    private let logger = Logger(subsystem: "com.livecaption.app", category: "caption-latency")
    var recordedMs: Int { totalSamples / 16 }

    func applyTuning(endpointSilenceMs: Int, interimIntervalMs: Int,
                     maxUtteranceS: Int, vadSensitivity: Int) {
        let thresholds: [Float] = [0.030, 0.020, 0.015, 0.008]
        tuning = SpeechSegmenter.Tuning(endpointMs: endpointSilenceMs,
            intervalMs: interimIntervalMs, maxUtteranceS: maxUtteranceS,
            threshold: thresholds[max(0, min(3, vadSensitivity))])
    }

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
        await endingTask?.value
        totalSamples = 0
        try await beginCapture()
    }

    func resumeCapture() async throws {
        await endingTask?.value
        try await beginCapture()
    }

    func pauseAndFinalize() async { await endCaptureAndFinalize() }
    func stopAndFinalize() async { await endCaptureAndFinalize() }

    private func beginCapture() async throws {
        guard capture == nil, let engine else {
            throw NSError(domain: "StreamingOrchestrator", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "Capture is already active or the speech model is not ready."])
        }
        errorText = nil; detail = ""; hypothesis = ""
        let session = UUID()
        activeCaptureID = session
        let buffer = CaptureBuffer()
        let processor = CaptureProcessor(buffer: buffer,
            segmenter: SpeechSegmenter(session: session, tuning: tuning, startSample: totalSamples))
        self.processor = processor
        let pipeline = CaptionPipeline(session: session,
            interim: { await engine.transcribeInterim($0.audio) },
            final: { await engine.transcribeFinal($0.audio) })
        self.pipeline = pipeline
        pipeline.onHypothesis = { [weak self] text in
            self?.hypothesis = text
            self?.logger.info("caption_published session=\(session.uuidString, privacy: .public)")
        }
        pipeline.onFinal = { [weak self] text, start, end in
            try await self?.onFinal?(text, start, end)
        }
        pipeline.onSpeechEnded = { [weak self] in self?.onSpeechEnded?($0) }
        pipeline.onFinalized = { [weak self] in self?.onFinalized?($0) }
        pipeline.onIssue = { [weak self] issue in
            self?.errorText = issue
            self?.logger.error("caption_issue=\(issue, privacy: .public)")
        }
        pipeline.onOverload = { [weak self] in self?.onCaptureMustPause?() }
        pipeline.onCatchingUp = { [weak self] behind in
            self?.detail = behind ? "Live captions are catching up…" : ""
        }
        pipeline.onMetric = { [weak self] metric in
            self?.logger.info("session=\(metric.session.uuidString, privacy: .public) utterance=\(metric.utterance) window_start_ms=\(metric.windowStartMs) window_end_ms=\(metric.windowEndMs) final=\(metric.isFinal) queue_ms=\(metric.queueMs) decode_ms=\(metric.decodeMs) audio_lag_ms=\(metric.audioLagMs) outcome=\(metric.outcome, privacy: .public) fallbacks=\(metric.fallbacks)")
            self?.logger.info("final_queue_depth=\(metric.finalQueueDepth) final_backlog_ms=\(metric.finalBacklogMs)")
        }
        let capture = SystemAudioCapture(
            onSamples: { buffer.append($0) },
            onError: { [weak self] error in
                Task { @MainActor in
                    guard let self, self.activeCaptureID == session else { return }
                    self.errorText = String(describing: error)
                    self.onCaptureMustPause?()
                }
            })
        self.capture = capture
        do { try await capture.start() }
        catch {
            activeCaptureID = nil
            await capture.stop()
            self.capture = nil; self.processor = nil; self.pipeline = nil
            errorText = friendlyError(error)
            throw error
        }
        loopTask = Task { [weak self] in
            while !Task.isCancelled {
                let output = await processor.poll()
                guard let self else { return }
                self.receive(output)
                do { try await Task.sleep(nanoseconds: 50_000_000) } catch { break }
            }
        }
    }

    private func receive(_ output: CaptureProcessor.Output) {
        totalSamples = output.totalSamples
        pipeline?.advanceAudio(to: totalSamples)
        for event in output.transitions {
            logger.info("vad_started=\(event.started) utterance=\(event.utterance) sample=\(event.sample)")
        }
        if output.batch.oldestAgeMs > 200 || output.batch.callbackGapMs > 200 {
            logger.info("capture_age_ms=\(output.batch.oldestAgeMs) callback_gap_ms=\(output.batch.callbackGapMs)")
        }
        for request in output.requests { pipeline?.submit(request) }
        if output.batch.droppedSamples > 0 {
            errorText = "Audio capture fell behind; \(output.batch.droppedSamples / 16) ms could not be captured. Pausing to finish the retained audio."
            logger.error("capture_dropped_samples=\(output.batch.droppedSamples)")
            onCaptureMustPause?()
        }
    }

    private func endCaptureAndFinalize() async {
        if let endingTask { await endingTask.value; return }
        let task = Task { @MainActor in
            // Stop delivery first, then drain every retained sample before closing
            // the lanes. Each resume creates a fresh buffer and generation.
            self.activeCaptureID = nil
            await self.capture?.stop(); self.capture = nil
            self.loopTask?.cancel(); await self.loopTask?.value; self.loopTask = nil
            if let processor = self.processor { self.receive(await processor.poll(finish: true)) }
            await self.pipeline?.finish()
            self.processor = nil; self.pipeline = nil
            self.hypothesis = ""; self.detail = ""
        }
        endingTask = task
        await task.value
        endingTask = nil
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

}
