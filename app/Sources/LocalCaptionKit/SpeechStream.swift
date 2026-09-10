import Foundation

public struct CaptionWord: Equatable, Sendable {
    public var text: String
    public var start: Double
    public var end: Double
    public init(_ text: String, start: Double, end: Double) {
        self.text = text; self.start = start; self.end = end
    }
    var midpoint: Double { (start + end) / 2 }
    var normalized: String {
        text.lowercased().trimmingCharacters(in: .whitespacesAndNewlines)
            .trimmingCharacters(in: .punctuationCharacters)
    }
}

public struct SpeechRequest: Sendable {
    public let session: UUID
    public let utterance: Int
    public let startSample: Int
    public let endSample: Int
    public let audio: [Float]
    public let isFinal: Bool
    public let submittedAt: Double
    public init(session: UUID, utterance: Int, startSample: Int, audio: [Float],
                isFinal: Bool, submittedAt: Double = ProcessInfo.processInfo.systemUptime) {
        self.session = session; self.utterance = utterance; self.startSample = startSample
        self.endSample = startSample + audio.count; self.audio = audio
        self.isFinal = isFinal; self.submittedAt = submittedAt
    }
}

public enum SpeechOutcome: Sendable {
    case success(text: String, words: [CaptionWord], fallbacks: Int)
    case empty, filtered, cancelled, timedOut
    case failure(String)

    public var label: String {
        switch self {
        case .success: return "success"
        case .empty: return "empty"
        case .filtered: return "filtered"
        case .cancelled: return "cancelled"
        case .timedOut: return "timeout"
        case .failure: return "failure"
        }
    }
}

/// Pure frame/VAD logic, also used by replay tests. No inference or main-actor work.
/// Retains 200 ms of pre-roll during silence, never a whole silent utterance.
public struct SpeechSegmenter: Sendable {
    public struct Transition: Sendable {
        public let utterance: Int
        public let sample: Int
        public let started: Bool
    }
    public struct Tuning: Sendable {
        public var endpointMs: Int
        public var intervalMs: Int
        public var maxUtteranceS: Int
        public var threshold: Float
        public init(endpointMs: Int = 600, intervalMs: Int = 500,
                    maxUtteranceS: Int = 20, threshold: Float = 0.015) {
            self.endpointMs = max(100, endpointMs)
            self.intervalMs = max(100, intervalMs)
            self.maxUtteranceS = min(60, max(5, maxUtteranceS))
            self.threshold = threshold
        }
    }

    public let session: UUID
    public let tuning: Tuning
    public private(set) var totalSamples: Int
    private var nextUtterance = 0
    private var remainder: [Float] = []
    private var preRoll: [Float] = []
    private var utterance: [Float] = []
    private var startSample = 0
    private var silenceSamples = 0
    private var sinceInterim = 0
    private var transitions: [Transition] = []
    public init(session: UUID, tuning: Tuning = Tuning(), startSample: Int = 0) {
        self.session = session; self.tuning = tuning; self.totalSamples = startSample
    }

    public mutating func append(_ samples: [Float], now: Double) -> [SpeechRequest] {
        remainder.append(contentsOf: samples)
        var requests: [SpeechRequest] = []
        var offset = 0
        while remainder.count - offset >= 1600 {
            consume(Array(remainder[offset..<(offset + 1600)]), now: now, into: &requests)
            offset += 1600
        }
        remainder = Array(remainder.dropFirst(offset))
        return requests
    }

    /// Capture must stop before this is called. Includes the last sub-100 ms frame.
    public mutating func finish(now: Double) -> [SpeechRequest] {
        var requests: [SpeechRequest] = []
        if !remainder.isEmpty {
            let tail = remainder; remainder = []
            consume(tail, now: now, into: &requests)
        }
        if !utterance.isEmpty { finalize(now: now, into: &requests) }
        preRoll = []
        return requests
    }

    public mutating func drainTransitions() -> [Transition] {
        let events = transitions; transitions = []
        return events
    }

    private mutating func consume(_ frame: [Float], now: Double, into requests: inout [SpeechRequest]) {
        let speaking = Self.rms(frame) >= tuning.threshold
        let frameStart = totalSamples
        totalSamples += frame.count
        if utterance.isEmpty {
            guard speaking else {
                preRoll = Array((preRoll + frame).suffix(3200))
                return
            }
            startSample = frameStart - preRoll.count
            utterance = preRoll; preRoll = []
            transitions.append(Transition(utterance: nextUtterance, sample: frameStart, started: true))
        }
        utterance.append(contentsOf: frame)
        silenceSamples = speaking ? 0 : silenceSamples + frame.count
        sinceInterim += frame.count
        if speaking && sinceInterim >= tuning.intervalMs * 16 {
            sinceInterim = 0
            let tail = Array(utterance.suffix(96000))
            requests.append(SpeechRequest(session: session, utterance: nextUtterance,
                startSample: totalSamples - tail.count, audio: tail, isFinal: false, submittedAt: now))
        }
        if silenceSamples >= tuning.endpointMs * 16 || utterance.count >= tuning.maxUtteranceS * 16000 {
            finalize(now: now, into: &requests)
        }
    }

    private mutating func finalize(now: Double, into requests: inout [SpeechRequest]) {
        transitions.append(Transition(utterance: nextUtterance, sample: totalSamples, started: false))
        requests.append(SpeechRequest(session: session, utterance: nextUtterance,
            startSample: startSample, audio: utterance, isFinal: true, submittedAt: now))
        nextUtterance += 1; utterance = []; silenceSamples = 0; sinceInterim = 0
    }

    public static func rms(_ samples: [Float]) -> Float {
        guard !samples.isEmpty else { return 0 }
        return (samples.reduce(Float(0)) { $0 + $1 * $1 } / Float(samples.count)).squareRoot()
    }
}

/// Merge model word timings in session coordinates, never by the length of an old
/// prefix. The overlapping suffix is provisional and may be corrected by a new decode.
public struct RollingCaption: Sendable {
    public private(set) var words: [CaptionWord] = []
    public private(set) var lastEndSample = -1
    private var lastStartSample = -1
    public init() {}
    public var text: String { words.map(\.text).joined(separator: " ") }

    @discardableResult
    public mutating func update(_ incoming: [CaptionWord], startSample: Int, endSample: Int) -> Bool {
        guard endSample > lastEndSample, !incoming.isEmpty else { return false }
        let offset = Double(startSample) / 16000
        let duration = Double(endSample - startSample) / 16000
        let valid = incoming.filter {
            !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
            $0.start.isFinite && $0.end.isFinite && $0.start >= 0 &&
            $0.end >= $0.start && $0.start <= duration
        }.map { CaptionWord($0.text.trimmingCharacters(in: .whitespacesAndNewlines),
                            start: offset + $0.start, end: offset + min(duration, $0.end)) }
        guard !valid.isEmpty else { return false }

        if words.isEmpty || startSample == lastStartSample {
            // Same audio origin: permit corrections, including shorter hypotheses.
            let prefix = words.prefix { $0.end <= offset }
            words = Array(prefix) + valid
        } else {
            // Anchor on the first matching word within 350 ms. Time proximity
            // distinguishes real repeated phrases from duplicated overlap.
            var anchor: (old: Int, new: Int)?
            for (j, new) in valid.enumerated() {
                let candidates = words.indices.filter {
                    words[$0].normalized == new.normalized && !new.normalized.isEmpty &&
                    abs(words[$0].midpoint - new.midpoint) <= 0.35
                }
                if let i = candidates.min(by: {
                    abs(words[$0].midpoint - new.midpoint) < abs(words[$1].midpoint - new.midpoint)
                }) { anchor = (i, j); break }
            }
            if let anchor {
                // Keep the newer model's prefix where it contains words before the
                // anchor, preserving only the old words outside that covered range.
                let prefixCount = anchor.new == 0 ? anchor.old :
                    min(anchor.old, words.prefix { $0.midpoint < valid[0].start }.count)
                words = Array(words.prefix(prefixCount)) + valid
            } else {
                // No lexical anchor: the new window owns its time range. This is an
                // explicit provisional correction, not an invented textual overlap.
                words = Array(words.prefix { $0.midpoint < valid[0].start }) + valid
            }
        }
        lastStartSample = startSample; lastEndSample = endSample
        return true
    }
}
