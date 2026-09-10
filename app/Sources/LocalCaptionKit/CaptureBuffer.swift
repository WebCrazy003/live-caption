import Foundation

/// Five-second handoff buffer. An overflow is latched and reported with an exact
/// lost-sample count; callers stop capture rather than silently accumulating audio.
public final class CaptureBuffer: @unchecked Sendable {
    public struct Batch: Sendable {
        public let samples: [Float]
        public let droppedSamples: Int
        public let oldestAgeMs: Int
        public let callbackGapMs: Int
    }
    private let lock = NSLock()
    private let capacity: Int
    private var data: [Float] = []
    private var dropped = 0
    private var oldestAt: Double?
    private var lastAppendAt: Double?
    private var maxGap = 0.0
    private var overflowed = false

    public init(capacity: Int = 5 * 16000) { self.capacity = max(1, capacity) }

    public func append(_ samples: [Float], now: Double = ProcessInfo.processInfo.systemUptime) {
        guard !samples.isEmpty else { return }
        lock.lock(); defer { lock.unlock() }
        if let previous = lastAppendAt { maxGap = max(maxGap, now - previous) }
        lastAppendAt = now
        if overflowed { dropped += samples.count; return }
        if oldestAt == nil { oldestAt = now }
        let available = max(0, capacity - data.count)
        data.append(contentsOf: samples.prefix(available))
        if samples.count > available {
            overflowed = true
            dropped += samples.count - available
        }
    }

    public func drain(now: Double = ProcessInfo.processInfo.systemUptime) -> Batch {
        lock.lock(); defer { lock.unlock() }
        let batch = Batch(samples: data, droppedSamples: dropped,
                          oldestAgeMs: Int(max(0, now - (oldestAt ?? now)) * 1000),
                          callbackGapMs: Int(maxGap * 1000))
        data = []; dropped = 0; oldestAt = nil; maxGap = 0
        return batch
    }
}

/// CPU framing and VAD run on this actor, independently of SwiftUI and decoding.
public actor CaptureProcessor {
    public struct Output: Sendable {
        public let requests: [SpeechRequest]
        public let totalSamples: Int
        public let batch: CaptureBuffer.Batch
        public let transitions: [SpeechSegmenter.Transition]
    }
    private let buffer: CaptureBuffer
    private var segmenter: SpeechSegmenter
    public init(buffer: CaptureBuffer, segmenter: SpeechSegmenter) {
        self.buffer = buffer; self.segmenter = segmenter
    }
    public func poll(finish: Bool = false) -> Output {
        let now = ProcessInfo.processInfo.systemUptime
        let batch = buffer.drain(now: now)
        var requests = segmenter.append(batch.samples, now: now)
        if finish { requests += segmenter.finish(now: now) }
        return Output(requests: requests, totalSamples: segmenter.totalSamples, batch: batch,
                      transitions: segmenter.drainTransitions())
    }
}
