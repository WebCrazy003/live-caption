import Foundation
import Darwin
import MLXLLM
import MLXLMCommon

// ---------------------------------------------------------------------------
// SPEC-10 live-summary spike
//
// Question: can a small on-device MLX LLM turn ~100 words of messy call
// transcript into a short, easy-English "what they want" card — fast enough
// and light enough to run beside the live ASR pipeline?
//
// Measures:
//   1. Model load time + memory footprint (phys_footprint delta)   → B6
//   2. Per-block generation latency + tokens/sec                    → B7
//   3. The actual summary text on 3 realistic chunks                → B8 (eyeball)
//
// Usage:
//   LLMSummarySpike [1b|3b]      (default 1b = Llama-3.2-1B-Instruct-4bit)
// First run downloads the model (~0.8 GB for 1B, ~1.8 GB for 3B).
// ---------------------------------------------------------------------------

func stamp() -> String {
    let f = DateFormatter(); f.dateFormat = "HH:mm:ss.SSS"
    return f.string(from: Date())
}

/// Whole-process resident memory (what Activity Monitor / Xcode call "Memory").
/// On Apple Silicon this includes MLX's unified-memory buffers, so it's the
/// honest B6 number for "does the LLM fit beside everything else".
func footprintMB() -> Double {
    var info = task_vm_info_data_t()
    var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<natural_t>.size)
    let kr = withUnsafeMutablePointer(to: &info) {
        $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
            task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
        }
    }
    return kr == KERN_SUCCESS ? Double(info.phys_footprint) / 1_048_576.0 : -1
}

func wordCount(_ s: String) -> Int {
    s.split(whereSeparator: { $0 == " " || $0 == "\n" }).count
}

// ---- Prompt (mirrors SPEC-10 §"Output format") ----------------------------

let systemPrompt = """
You help a non-native English speaker follow a live call. \
Read the transcript part and say, in very simple English (short words, short lines), \
what the other person wants. Lead with the main ask. Do NOT invent anything that is not \
in the text. If nothing important, say so.

Answer ONLY in this exact format:
MAIN: <one short line: the single most important ask/point>
- <short point in simple words>
- <short point in simple words>
WANT: <what they want you to do, or "-" if none>
"""

func userPrompt(_ chunk: String) -> String {
    "Transcript part:\n\"\(chunk)\"\n\nNow write the card."
}

// ---- Realistic ~100-word ASR-style chunks (no punctuation, run-on) --------
// These imitate small.en finals: lowercase, messy, one speaker (the client).

let chunks: [(label: String, text: String)] = [
    ("project scope (rambling → ask)", """
so yeah basically the thing is we launched the new site back in march and it was fine \
for a while but lately the checkout has been really slow especially on mobile and a few \
customers have complained on twitter which is not great for us um and we also want to add \
that new payment method the one everyone keeps asking about and honestly the whole thing \
feels a bit dated so what i really need from you is a proper estimate on how long it would \
take to fix the checkout speed first and then maybe we talk about the redesign after that
"""),
    ("billing complaint", """
i've been charged twice this month and i really don't understand why because i only have \
the one subscription the basic plan and i checked my bank and there are two charges on the \
fourth and the ninth both for the same amount and i already emailed support last week but \
nobody got back to me and i'm getting a little frustrated to be honest so i need someone to \
actually look at my account today refund the extra charge and tell me it won't happen again \
next month because otherwise i'm going to cancel
"""),
    ("jargon-heavy technical (easy-English + hallucination test)", """
right so our current stack is a monolith on kubernetes and the p99 latency on the api \
gateway has been creeping up and we think it's the n plus one queries in the orm plus we're \
not caching the auth tokens so every request hits the identity provider and the on call \
rotation is getting paged constantly so what we'd like is for you to come in do an audit of \
the hot paths maybe introduce a read replica and a redis layer and help us set some slos so \
the team stops firefighting every single night
""")
]

// ---- Run ------------------------------------------------------------------

let which = (CommandLine.arguments.dropFirst().first ?? "1b").lowercased()
let config: ModelConfiguration = (which == "3b") ? LLMRegistry.llama3_2_3B_4bit
                                                  : LLMRegistry.llama3_2_1B_4bit

print("========================================================")
print("SPEC-10 summary spike — model: \(which == "3b" ? "Llama-3.2-3B-Instruct-4bit" : "Llama-3.2-1B-Instruct-4bit")")
print("========================================================\n")

let baseMB = footprintMB()
print("[\(stamp())] baseline footprint: \(String(format: "%.0f", baseMB)) MB")
print("[\(stamp())] loading model (first run downloads weights)…")

let loadStart = Date()
var lastPct = -1
let container = try await LLMModelFactory.shared.loadContainer(configuration: config) { progress in
    let pct = Int(progress.fractionCompleted * 100)
    if pct != lastPct, pct % 10 == 0 {
        lastPct = pct
        print("[\(stamp())]   downloading weights… \(pct)%")
    }
}
let loadSecs = Date().timeIntervalSince(loadStart)
let loadedMB = footprintMB()
print("[\(stamp())] model ready in \(String(format: "%.1f", loadSecs))s")
print("[\(stamp())] footprint after load: \(String(format: "%.0f", loadedMB)) MB  (Δ \(String(format: "%.0f", loadedMB - baseMB)) MB for the model)\n")

var params = GenerateParameters(maxTokens: 120, temperature: 0.0)

var latencies: [Double] = []
var tokPerSec: [Double] = []
var peakMB = loadedMB

for (i, chunk) in chunks.enumerated() {
    print("──── block \(i + 1)/\(chunks.count): \(chunk.label)  (\(wordCount(chunk.text)) words) ────")

    let input = UserInput(chat: [
        .system(systemPrompt),
        .user(userPrompt(chunk.text)),
    ])

    let (output, info): (String, GenerateCompletionInfo?) = try await container.perform { context in
        let lmInput = try await context.processor.prepare(input: input)
        var out = ""
        var completion: GenerateCompletionInfo? = nil
        let stream = try MLXLMCommon.generate(input: lmInput, parameters: params, context: context)
        for await item in stream {
            switch item {
            case .chunk(let s): out += s
            case .info(let inf): completion = inf
            default: break
            }
        }
        return (out.trimmingCharacters(in: .whitespacesAndNewlines), completion)
    }

    peakMB = max(peakMB, footprintMB())

    print(output)
    if let info {
        let ms = info.generateTime * 1000
        latencies.append(ms)
        tokPerSec.append(info.tokensPerSecond)
        print(String(format: "  ⏱  %.0f ms gen · %.1f tok/s · %d out tokens · %.0f ms prompt",
                     ms, info.tokensPerSecond, info.generationTokenCount, info.promptTime * 1000))
    }
    print("")
}

func avg(_ xs: [Double]) -> Double { xs.isEmpty ? 0 : xs.reduce(0, +) / Double(xs.count) }

print("========================================================")
print("SUMMARY")
print("  model load        : \(String(format: "%.1f", loadSecs)) s")
print("  model memory (Δ)  : \(String(format: "%.0f", loadedMB - baseMB)) MB")
print("  peak footprint    : \(String(format: "%.0f", peakMB)) MB")
print("  gen latency (avg) : \(String(format: "%.0f", avg(latencies))) ms  per ~100-word block")
print("  throughput  (avg) : \(String(format: "%.1f", avg(tokPerSec))) tok/s")
print("  vs 100 words ≈ ~40s of speech → generation is \(String(format: "%.0f", avg(latencies)))ms, well inside the gap" )
print("========================================================")
print("[\(stamp())] done.")
