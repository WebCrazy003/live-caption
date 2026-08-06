# Spike results — real-time captioning engine

**Machine:** MacBook, Apple Silicon (arm64), macOS 14.7.6, unified memory.
**Clip:** 8.41s, `say`-generated, 16kHz mono, known ground truth (clean, single speaker).
**Streaming sim:** 100ms feed hop, interim decode every 500ms, 600ms endpoint, whole-utterance window (naive — a real impl bounds the window, so these are conservative upper bounds).

## Numbers

| Engine / model | Compute | Batch RTF | Interim decode (budget 500ms) | Final perceived latency | Accuracy (clean clip) |
|---|---|---|---|---|---|
| faster-whisper `large-v3-turbo` | **CPU** (int8) | 0.55× | **4,744 ms avg** ❌ | **~5,800 ms** ❌ | perfect |
| mlx-whisper `large-v3-turbo` | **Apple GPU** | 0.19× | **1,459 ms avg** ⚠️ | **~2,190 ms** ⚠️ | perfect |
| faster-whisper `tiny.en` | **CPU** | 0.04× | **222 ms avg** ✅ | **~908 ms** ✅ | perfect |

## Key findings

1. **The draft's exact plan (faster-whisper `large-v3-turbo` on CPU) is unusable for live captioning.**
   ~4.7 s per decode, ~5.8 s perceived latency. Blocker confirmed with hard numbers.

2. **The cost is intrinsic to Whisper, not tuning.** Every decode pays for Whisper's
   **30-second padded mel encoder** regardless of chunk length — a 0.5 s window costs
   almost the same as an 8 s window. There is a per-decode *floor*, independent of audio length:
   - CPU floor (large-v3-turbo): **~4.7 s**
   - Apple GPU floor (large-v3-turbo): **~1.4 s**

3. **Moving Whisper onto Apple-Silicon acceleration (MLX/WhisperKit) is ~3× faster** and
   makes **accurate finals** viable (~1.5 s decode → ~2.1 s perceived with proper endpointing).
   Still above 500 ms, so not for interim partials at large-v3-turbo.

4. **A small model hits the interim budget easily** — `tiny.en` decodes in ~220 ms and
   finals in ~900 ms, even on CPU.

## Conclusion (evidence-backed architecture)

**Dual-model hybrid on Apple-Silicon acceleration:**
- **Interim / provisional captions:** small model (`tiny.en`/`base.en`) — sub-300 ms, keeps up with the 500 ms cadence.
- **Final captions:** `large-v3-turbo` on GPU/ANE — accurate, ~2 s perceived.
- **Engine:** WhisperKit (native, ANE+GPU, built-in confirmed-token streaming) in the shipping app; MLX is the Python-side proxy proven here. **Not** CPU faster-whisper.

## Caveats
- Single clean TTS clip, one speaker, one machine — validates **latency/RTF** (architectural, robust), **not** real-world WER. Real interview audio needs a WER pass on noisy multi-speaker recordings; `tiny.en` accuracy will drop on noise (acceptable — it's the throwaway interim; the final is turbo).
- Naive whole-window re-decode. WhisperKit's bounded/streaming decode should beat the MLX numbers here.
- WhisperKit native spike was **blocked**: Command Line Tools SwiftPM is broken on this machine (manifest link failure); building the native app needs full Xcode installed.
