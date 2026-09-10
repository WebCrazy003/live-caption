import Foundation

public struct CaptionMetric: Sendable {
    public let session: UUID
    public let utterance: Int
    public let windowStartMs: Int
    public let windowEndMs: Int
    public let isFinal: Bool
    public let queueMs: Int
    public let decodeMs: Int
    public let audioLagMs: Int
    public let finalQueueDepth: Int
    public let finalBacklogMs: Int
    public let outcome: String
    public let fallbacks: Int
}

/// Two independent serial lanes. Only pending interim snapshots are coalesced;
/// final requests remain ordered. All model access goes through these lanes.
@MainActor
public final class CaptionPipeline {
    public typealias Decode = @Sendable (SpeechRequest) async -> SpeechOutcome
    public var onHypothesis: ((String) -> Void)?
    public var onFinal: ((String, Int, Int) async throws -> Void)?
    public var onSpeechEnded: ((String) -> Void)?
    public var onFinalized: ((String) -> Void)?
    public var onIssue: ((String) -> Void)?
    public var onOverload: (() -> Void)?
    public var onCatchingUp: ((Bool) -> Void)?
    public var onMetric: ((CaptionMetric) -> Void)?
    public private(set) var metrics: [CaptionMetric] = []
    public private(set) var pendingInterim: SpeechRequest?
    public private(set) var finalQueue: [SpeechRequest] = []
    public private(set) var session: UUID
    public private(set) var hypothesis = ""

    private let interimDecode: Decode
    private let finalDecode: Decode
    private let now: @Sendable () -> Double
    private let sleep: @Sendable (Double) async throws -> Void
    private let interimBudget: Double
    private let backlogSeconds: Double
    private let backlogCount: Int
    private var interimTask: Task<Void, Never>?
    private var finalTask: Task<Void, Never>?
    private var activeInterim: Task<SpeechOutcome, Never>?
    private var captions: [Int: RollingCaption] = [:]
    private var completedThrough = -1
    private var lastFinalEnqueued = -1
    private var latestSample = 0
    private var queuedFinalSamples = 0
    private var overloadSignaled = false
    private var closing = false

    public init(session: UUID, interimBudget: Double = 2, backlogSeconds: Double = 40,
                backlogCount: Int = 16,
                now: @escaping @Sendable () -> Double = { ProcessInfo.processInfo.systemUptime },
                sleep: @escaping @Sendable (Double) async throws -> Void = {
                    try await Task.sleep(nanoseconds: UInt64($0 * 1_000_000_000))
                }, interim: @escaping Decode, final: @escaping Decode) {
        self.session = session; self.interimBudget = interimBudget
        self.backlogSeconds = backlogSeconds; self.backlogCount = backlogCount
        self.now = now; self.sleep = sleep
        self.interimDecode = interim; self.finalDecode = final
    }

    public func submit(_ request: SpeechRequest) {
        guard request.session == session, !closing else { return }
        latestSample = max(latestSample, request.endSample)
        if request.isFinal {
            guard request.utterance > lastFinalEnqueued else { return }
            lastFinalEnqueued = request.utterance
            onSpeechEnded?(text(through: request.utterance))
            finalQueue.append(request)
            queuedFinalSamples += request.audio.count
            if !overloadSignaled && (Double(queuedFinalSamples) / 16000 >= backlogSeconds ||
                                    finalQueue.count >= backlogCount) {
                overloadSignaled = true
                onIssue?("Transcription is falling behind. Pausing capture to finish the recorded speech.")
                onOverload?()
            }
            startFinalWorker()
        } else {
            guard request.utterance > completedThrough else { return }
            // New requests never invalidate an advancing in-flight result.
            if pendingInterim == nil || request.endSample > pendingInterim!.endSample {
                pendingInterim = request
            }
            startInterimWorker()
        }
    }

    public func advanceAudio(to sample: Int) { latestSample = max(latestSample, sample) }

    /// Caller stops and drains capture before finishing. Finals include persistence
    /// acknowledgment. No next session/model call may begin until this returns.
    public func finish() async {
        closing = true
        pendingInterim = nil
        activeInterim?.cancel()
        await interimTask?.value
        await finalTask?.value
        onCatchingUp?(false)
    }

    private func startInterimWorker() {
        guard interimTask == nil else { return }
        interimTask = Task { [weak self] in
            guard let self else { return }
            while let request = self.pendingInterim, !self.closing {
                self.pendingInterim = nil
                guard request.utterance > self.completedThrough else { continue }
                let started = self.now()
                let decode = self.interimDecode
                let inference = Task { await decode(request) }
                self.activeInterim = inference
                let watchdog = Task { [weak self, sleep = self.sleep, budget = self.interimBudget] in
                    do { try await sleep(budget) } catch { return }
                    guard !Task.isCancelled else { return }
                    inference.cancel()
                    self?.onCatchingUp?(true)
                }
                let result = await inference.value
                watchdog.cancel()
                self.activeInterim = nil
                let expired = self.now() - started >= self.interimBudget
                let outcome: SpeechOutcome = expired ? .timedOut : result
                self.record(request, started: started, outcome: outcome)
                if !self.closing, request.utterance > self.completedThrough,
                   self.latestSample - request.endSample <= 3 * 16000,
                   case .success(_, let words, _) = outcome {
                    var caption = self.captions[request.utterance] ?? RollingCaption()
                    if caption.update(words, startSample: request.startSample, endSample: request.endSample) {
                        self.captions[request.utterance] = caption
                        self.publish()
                        self.onCatchingUp?(false)
                    } else if words.isEmpty {
                        self.onIssue?("The live model did not provide word timings. Waiting for final transcription.")
                    }
                } else if case .failure = outcome {
                    self.onCatchingUp?(true)
                }
            }
            self.interimTask = nil
        }
    }

    private func startFinalWorker() {
        guard finalTask == nil else { return }
        finalTask = Task { [weak self] in
            guard let self else { return }
            while !self.finalQueue.isEmpty {
                let request = self.finalQueue.removeFirst()
                let started = self.now()
                let outcome = await self.finalDecode(request)
                self.record(request, started: started, outcome: outcome)
                switch outcome {
                case .success(let text, _, _):
                    do { try await self.onFinal?(text, request.startSample / 16, request.endSample / 16) }
                    catch { self.onIssue?("Transcript could not be journaled: \(error.localizedDescription)") }
                case .empty, .filtered:
                    if !(self.captions[request.utterance]?.text ?? "").isEmpty {
                        self.onIssue?("Final transcription rejected speech at \(TimeFormat.stamp(ms: request.startSample / 16)). Temporary words were not saved.")
                    }
                case .failure, .cancelled, .timedOut:
                    self.onIssue?("Final transcription failed at \(TimeFormat.stamp(ms: request.startSample / 16)). This interval is missing from the saved transcript.")
                }
                self.completedThrough = request.utterance
                self.captions.removeValue(forKey: request.utterance)
                self.queuedFinalSamples -= request.audio.count
                self.publish()
                // Clipboard refresh occurs after retiring the matching temporary
                // text, so a late final cannot duplicate it or erase newer speech.
                self.onFinalized?(self.hypothesis)
            }
            self.finalTask = nil
        }
    }

    private func text(through utterance: Int = .max) -> String {
        captions.keys.sorted().filter { $0 <= utterance }.compactMap { captions[$0]?.text }
            .filter { !$0.isEmpty }.joined(separator: " ")
    }

    private func publish() {
        hypothesis = text()
        onHypothesis?(hypothesis)
    }

    private func record(_ request: SpeechRequest, started: Double, outcome: SpeechOutcome) {
        let fallbacks: Int
        if case .success(_, _, let count) = outcome { fallbacks = count } else { fallbacks = 0 }
        let metric = CaptionMetric(session: request.session, utterance: request.utterance,
            windowStartMs: request.startSample / 16, windowEndMs: request.endSample / 16,
            isFinal: request.isFinal,
            queueMs: Int(max(0, started - request.submittedAt) * 1000),
            decodeMs: Int(max(0, now() - started) * 1000),
            audioLagMs: max(0, latestSample - request.endSample) / 16,
            finalQueueDepth: finalQueue.count + (finalTask == nil ? 0 : 1),
            finalBacklogMs: queuedFinalSamples / 16,
            outcome: outcome.label, fallbacks: fallbacks)
        metrics.append(metric)
        if metrics.count > 256 { metrics.removeFirst(metrics.count - 256) }
        onMetric?(metric)
    }
}
