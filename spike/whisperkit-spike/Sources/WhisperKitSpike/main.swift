import Foundation
import WhisperKit

// ---------------------------------------------------------------------------
// WhisperKit streaming spike
//
// Measures the numbers that decide the ASR architecture for Local Caption:
//   1. Accuracy    - full-file transcript vs known text
//   2. Decode speed - real-time factor (RTF = decode_time / audio_duration)
//   3. Interim cadence - how long a partial (sliding-window) decode takes
//   4. Final latency - decode time for a full utterance window
//
// Usage:
//   WhisperKitSpike <audio.wav> [modelName]
//   modelName defaults to "base.en". Try "large-v3-turbo" for the real target.
// ---------------------------------------------------------------------------

let args = CommandLine.arguments
guard args.count >= 2 else {
    print("usage: WhisperKitSpike <audio.wav> [model=base.en]")
    exit(2)
}
let audioPath = args[1]
let modelName = args.count >= 3 ? args[2] : "base.en"

func stamp() -> String {
    let f = DateFormatter()
    f.dateFormat = "HH:mm:ss.SSS"
    return f.string(from: Date())
}
func rms(_ x: ArraySlice<Float>) -> Float {
    if x.isEmpty { return 0 }
    var s: Float = 0
    for v in x { s += v * v }
    return (s / Float(x.count)).squareRoot()
}

let sampleRate: Double = 16000

print("========================================================")
print("WhisperKit spike")
print("  model : \(modelName)")
print("  audio : \(audioPath)")
print("========================================================\n")

// ---- Load model -----------------------------------------------------------
print("[\(stamp())] loading WhisperKit model '\(modelName)' (first run downloads it)...")
let loadStart = Date()
let config = WhisperKitConfig(model: modelName, verbose: false, logLevel: .error, prewarm: true, load: true, download: true)
let whisperKit = try await WhisperKit(config)
print("[\(stamp())] model ready in \(String(format: "%.1f", Date().timeIntervalSince(loadStart)))s\n")

// ---- Load audio -----------------------------------------------------------
let samples = try AudioProcessor.loadAudioAsFloatArray(fromPath: audioPath)
let durationS = Double(samples.count) / sampleRate
print("[\(stamp())] loaded \(samples.count) samples = \(String(format: "%.2f", durationS))s of audio @ 16kHz mono\n")

let opts = DecodingOptions(task: .transcribe, language: "en", temperature: 0.0, usePrefillPrompt: true, skipSpecialTokens: true)

func decode(_ slice: [Float]) async -> (text: String, seconds: Double) {
    let t = Date()
    do {
        let results = try await whisperKit.transcribe(audioArray: slice, decodeOptions: opts)
        let text = results.map { $0.text }.joined().trimmingCharacters(in: .whitespacesAndNewlines)
        return (text, Date().timeIntervalSince(t))
    } catch {
        return ("<decode error: \(error)>", Date().timeIntervalSince(t))
    }
}

// ---- Phase 1: full-file accuracy + RTF ------------------------------------
print("---- PHASE 1: full-file decode (accuracy + RTF) --------------------")
let full = await decode(samples)
let rtf = full.seconds / durationS
print("  transcript : \(full.text)")
print("  decode time: \(String(format: "%.2f", full.seconds))s for \(String(format: "%.2f", durationS))s audio")
print("  RTF        : \(String(format: "%.2f", rtf))x  (<1.0 = faster than real time)\n")

// ---- Phase 2: streaming simulation ----------------------------------------
// Feed the file at real-time pace. Energy-based endpointing. Interim decode
// every 0.5s of speech; final decode when a ~600ms silence gap is seen.
print("---- PHASE 2: real-time streaming simulation ----------------------")
let hop = Int(0.1 * sampleRate)            // 100 ms feed hop
let interimEveryHops = 5                    // 500 ms interim cadence
let endpointSilenceMs = 600
let silenceRMS: Float = 0.006
let maxWindowS = 28.0

var fed = 0
var utterStart = 0
var hasSpeech = false
var silenceMs = 0
var hopsSinceInterim = 0
let t0 = Date()

func feedSleep(toAudioSeconds target: Double) async {
    let actual = Date().timeIntervalSince(t0)
    if target > actual {
        try? await Task.sleep(nanoseconds: UInt64((target - actual) * 1_000_000_000))
    }
}

while fed < samples.count {
    let next = min(fed + hop, samples.count)
    let frame = samples[fed..<next]
    let level = rms(frame)
    fed = next
    hopsSinceInterim += 1

    await feedSleep(toAudioSeconds: Double(fed) / sampleRate)

    if level >= silenceRMS {
        hasSpeech = true
        silenceMs = 0
    } else if hasSpeech {
        silenceMs += Int(Double(next - (next - hop)) / sampleRate * 1000)
    }

    let windowLenS = Double(fed - utterStart) / sampleRate
    let audioClock = Double(fed) / sampleRate

    // interim partial
    if hasSpeech, hopsSinceInterim >= interimEveryHops, silenceMs == 0 {
        hopsSinceInterim = 0
        let window = Array(samples[utterStart..<fed])
        let r = await decode(window)
        print("  [\(String(format: "%6.2f", audioClock))s] INTERIM (\(String(format: "%.0f", r.seconds * 1000))ms decode): \(r.text)")
    }

    // endpoint -> final
    if (hasSpeech && silenceMs >= endpointSilenceMs) || windowLenS >= maxWindowS {
        let window = Array(samples[utterStart..<fed])
        let r = await decode(window)
        let perceived = r.seconds * 1000
        print("  [\(String(format: "%6.2f", audioClock))s] >>> FINAL (decode \(String(format: "%.0f", perceived))ms, +\(endpointSilenceMs)ms endpoint wait = ~\(String(format: "%.0f", perceived + Double(endpointSilenceMs)))ms perceived): \(r.text)")
        utterStart = fed
        hasSpeech = false
        silenceMs = 0
        hopsSinceInterim = 0
    }
}

// flush trailing utterance
if hasSpeech, fed > utterStart {
    let r = await decode(Array(samples[utterStart..<fed]))
    print("  [\(String(format: "%6.2f", Double(fed)/sampleRate))s] >>> FINAL (flush, decode \(String(format: "%.0f", r.seconds * 1000))ms): \(r.text)")
}

print("\n[\(stamp())] done.")
