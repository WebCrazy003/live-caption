# SPEC-03 — ASR Engine & Streaming

**Status:** **Architecture validated by spike** · **Depends on:** SPEC-00 (frames from SPEC-02) · **Full detail:** SPEC.md §1A, §8

## Goal
Turn a live 16 kHz audio stream into low-latency interim captions + accurate final
captions, fully on-device, using WhisperKit. This is the product's core.

## What should be done
- [ ] Load **two WhisperKit models once, resident:** interim `tiny.en`/`base.en`, final `large-v3-turbo` (ANE+GPU).
- [ ] **Two-track streaming (SPEC.md §8.2):**
  - Interim: every ~500 ms decode current window with interim model → provisional text.
  - Final: on VAD endpoint (silence ≥ 600 ms) or `max_utterance_s` → decode with `large-v3-turbo`.
- [ ] **LocalAgreement-2** stabilization: commit only tokens agreed by last two hypotheses; never rewrite committed tokens.
- [ ] **Endpointing/VAD:** energy VAD; bound decode window (≤ ~28 s) so cost stays flat.
- [ ] **Hallucination/repetition filters (SPEC.md §8.3):** `no_speech_prob > 0.6`, `avg_logprob < -1.0`, `compression_ratio > 2.4`, blocklist ("Thank you." etc.), consecutive-repetition suppression.
- [ ] **Model acquisition (SPEC.md §8.4):** first-run download w/ progress; offline after; optional bundled-model build; enforce no runtime network.
- [ ] Emit caption events to UI as queued signals (SPEC.md §9.1 model).

## What is done — validated by the Python spike (`../spike/`)
- **Architecture proven.** Dual-model hybrid measured on this Apple Silicon Mac:
  - Interim (`tiny.en`): **~220 ms** ✅ (under the 500 ms budget).
  - Final (`large-v3-turbo`, Apple GPU/MLX proxy): **~1.5 s decode → ~2.0 s perceived** ✅.
  - **CPU faster-whisper rejected:** ~4.7 s/decode, ~5.8 s perceived (SPEC.md §1A).
- **Whisper 30 s-encoder floor** characterized (~1.4 s GPU / ~4.7 s CPU) — the reason sub-1 s finals are impossible; interim-only is sub-second.
- **Filters validated live:** silence "Thank you." and `come come come` repetition loops were produced *and then suppressed* by the `no_speech_prob` + blocklist + `compression_ratio` gates.
- **Long-window pathology observed:** with no endpoint, windows grew to the cap → 13–16 s decodes + repetition. Confirms endpointing + window cap are mandatory.
- Reference implementations: `../spike/bench.py`, `../spike/live.py`, `../spike/RESULTS.md`.

## Blockers
- **B1** — Xcode (WhisperKit is a Swift package).
- **B4** — decide **hybrid vs turbo-only**: WhisperKit's built-in confirmed-token streaming may make the separate interim model unnecessary. Re-measure WhisperKit on the ANE before finalizing (may beat the ~2 s MLX proxy).

## Acceptance
- Live interim partials < ~300 ms; finals ~2 s p50 (< 3 s p90); committed tokens never rewritten.
- 5-min silent input → no junk captions (filters hold).
- No runtime network calls (verified with a network monitor / offline mode).
- On a clean fixture, final transcript matches ground truth within WER tolerance (spike scored perfect on clean digital audio).
