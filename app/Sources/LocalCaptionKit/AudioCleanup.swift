import Foundation

/// Accent mode's per-utterance audio cleanup (SPEC-18 D4): an 80 Hz–7.5 kHz band-pass (room
/// rumble and hiss) and loudness levelling to a fixed RMS, so quiet speakers come up. 16 kHz
/// mono float samples in and out. Applied before an utterance is sent; Standard mode never uses it.
public enum AudioCleanup {
    public static let sampleRate = 16_000.0
    /// Target RMS for levelling (about −26 dBFS), the spike's `level()`.
    public static let targetRMS: Float = 0.05
    /// Gain is capped so near-silence is not blown up into noise.
    public static let maxGain: Float = 20

    public static func apply(_ samples: [Float], bandpass: Bool, level: Bool) -> [Float] {
        var out = samples
        if bandpass {
            out = Biquad.highPass(cutoff: 80).run(out)
            out = Biquad.lowPass(cutoff: 7_500).run(out)
        }
        if level { out = levelled(out) }
        return out
    }

    static func levelled(_ samples: [Float]) -> [Float] {
        let rms = SpeechSegmenter.rms(samples)
        guard rms > 1e-6 else { return samples }
        let gain = min(maxGain, targetRMS / rms)
        return samples.map { max(-1, min(1, $0 * gain)) }
    }

    /// Second-order IIR section (RBJ cookbook), Butterworth Q.
    struct Biquad {
        let b0, b1, b2, a1, a2: Float

        static func highPass(cutoff: Double) -> Biquad { make(cutoff: cutoff, high: true) }
        static func lowPass(cutoff: Double) -> Biquad { make(cutoff: cutoff, high: false) }

        private static func make(cutoff: Double, high: Bool) -> Biquad {
            let w = 2 * Double.pi * cutoff / AudioCleanup.sampleRate
            let alpha = sin(w) / (2 * 0.7071067811865476)
            let c = cos(w), a0 = 1 + alpha
            let b1 = high ? -(1 + c) : (1 - c)
            let b0 = high ? (1 + c) / 2 : (1 - c) / 2
            return Biquad(b0: Float(b0 / a0), b1: Float(b1 / a0), b2: Float(b0 / a0),
                          a1: Float(-2 * c / a0), a2: Float((1 - alpha) / a0))
        }

        func run(_ x: [Float]) -> [Float] {
            var y = [Float](repeating: 0, count: x.count)
            var x1: Float = 0, x2: Float = 0, y1: Float = 0, y2: Float = 0
            for i in x.indices {
                let v = b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
                x2 = x1; x1 = x[i]; y2 = y1; y1 = v
                y[i] = v
            }
            return y
        }
    }
}
