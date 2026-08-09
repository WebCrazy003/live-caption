# Spike results — SPEC-10 on-device live summary (1B)

**Machine:** MacBook, Apple Silicon (arm64), macOS 14.6, unified memory.
**Model:** `mlx-community/Llama-3.2-1B-Instruct-4bit` (~0.7 GB weights, 4-bit).
**Runtime:** MLX. Input = 3 realistic ~100-word, ASR-style transcript blocks (one speaker,
no punctuation), summarized with the exact SPEC-10 prompt + output format.

> **How this was run:** the Swift/MLX path (`Package.swift`, `main.swift`) **compiles and
> runs** but dies at runtime with *"Failed to load the default metallib"* — `mlx-swift` ships
> no metallib build step for a bare `swift build`, so the Metal shader lib is never produced;
> it needs Xcode's build system. So, exactly as the ASR architecture was proven in Python
> (`../bench.py`) before the Swift app, the numbers below come from **Python `mlx-lm`** on the
> **same weights + same MLX runtime**. They transfer; only the harness differs.

## Numbers (B6 memory / B7 latency)

| Metric | Value | Verdict |
|---|---|---|
| Model load | **1.0 s** (from local cache) | ✅ |
| Resident memory (mx active) | **0.70 GB** weights | ✅ |
| Peak memory during generation | **1.04 GB** | ✅ |
| Gen latency / ~100-word block | **~1.2 s avg** (0.67–1.68 s) | ✅ |
| Throughput | **~35 tok/s** | ✅ |

- **B6 (memory) → green for 1B.** ~1 GB peak. Sits comfortably beside the two WhisperKit
  models (~0.3 GB) and a video-call app (1–2 GB) on a 16 GB Mac.
- **B7 (latency) → green.** 100 words ≈ ~40 s of speech; a ~1.2 s card fits trivially in the
  gap. No backlog risk; the spec's queue-of-one is ample.
- **B_GPU (contention) → still unmeasured.** This spike ran the LLM alone. The real question —
  does an LLM generation delay a concurrent WhisperKit final decode on the shared GPU — needs
  both running together, i.e. an in-app measurement.

## Quality (B8) — the real risk, confirmed

Raw output per block:

**Block 1 — rambling scope → ask** (main ask = estimate to fix checkout speed):
```
MAIN: Fix the checkout speed          ✅ correct main point
- It's slow on mobile                 ✅
- Customers complain on Twitter       ✅
WANT: - A proper estimate ...          ⚠️ content right, but format broke (nested "- -" lines)
```

**Block 2 — billing double-charge → refund** (main ask = refund the duplicate charge):
```
MAIN: I need help with my account     ❌ vague — LOST the actual point (charged twice → refund)
- I need my account to be refunded    ⚠️ degenerate repetition
- I need my account to be fixed
WANT: My account to be refunded / fixed / checked again   ❌ repetitive, missed "won't happen again / else I cancel"
```

**Block 3 — jargon-heavy technical → audit** (asks = audit hot paths, read replica, Redis, SLOs):
```
MAIN: Audit the hot paths                         ✅ correct
WANT: Introduce a read replica and a Redis layer  ✅ grounded, but dropped the SLO/on-call ask; still says "Redis" (jargon)
```

### Findings
1. **No hallucination.** Across all three it invented nothing — the biggest B8 fear didn't
   materialize at temperature 0 with the "don't invent" instruction.
2. **But quality is inconsistent — 1 of 3 substantially failed.** Block 2 collapsed into vague,
   repetitive filler and *lost the concrete ask* (refund the duplicate charge). For a user who
   can't verify against the audio, "I need help with my account" is useless.
3. **Output format is not reliably followed** — nested/broken bullets (block 1), empty then
   over-long WANT (block 2), truncated to 2 lines (block 3). A strict parser + normalization is
   mandatory; the raw text can't be shown as-is.
4. **"Easy English" is only prompt-deep** — it still emits "Redis", "read replica", "read
   replica". Confirms the spec caveat: the jargon the user least understands survives.

## Conclusion

- **1B is viable on cost (memory + speed)** — both are comfortably green, with lots of headroom.
- **1B is marginal on quality** — good enough on clear asks, degenerate on others, and format-
  unreliable. As-is it would sometimes mislead the exact user it's meant to help.
- **Next levers, in order:** (a) compare **3B** on the identical blocks (quality is the whole
  question; memory still fits per B6); (b) harden the prompt + add few-shot examples + a strict
  post-parser/normalizer; (c) measure **B_GPU** in-app (LLM + Whisper together).

## Caveats
- Single machine, 3 hand-written blocks, clean text (real ASR output is messier — quality floor,
  not ceiling). Not a WER/quality benchmark, a smoke test of the architecture.
- Python `mlx-lm` proxy for Swift `mlx-swift` — same runtime/weights; the app must build MLX via
  Xcode's build system (not bare `swift build`) to get the metallib. Note this for SPEC-10 impl.
- B_GPU (GPU contention with live ASR) not exercised here.
