# SPEC-04 — Session Lifecycle & Transcript Persistence

**Status:** Not started · **Depends on:** SPEC-01, SPEC-02, SPEC-03 · **Full detail:** SPEC.md §9, §11, §12.1

## Goal
The glue: a session state machine, an in-memory transcript, crash-safe journaling, and
final transcript + metadata writing on stop.

## What should be done
- [ ] **State machine (SPEC.md §11):** `IDLE → RECORDING ⇄ PAUSED → STOPPING → SAVED`.
  - Start: open capture (02) + ASR (03), start monotonic elapsed clock.
  - Pause: stop feeding audio, **finalize in-flight utterance**, freeze clock.
  - Resume: restart capture, resume clock, reset VAD context.
  - Stop: finalize, compute duration (excludes paused time), write transcript + DB row, delete journal.
- [ ] **In-memory transcript model (SPEC.md §9.1):** ordered final segments (id, text, t_start_ms, t_end_ms). Interim not stored.
- [ ] **Crash-recovery journal (SPEC.md §9.3):** append each final segment to `journal/<session_id>.jsonl` (batched fsync). Delete on clean stop.
- [ ] **Recovery flow:** on launch, if journals remain → "Recover unsaved session" → rebuild transcript → let user save.
- [ ] **Transcript file writer (SPEC.md §12.1):** header (name/start/end/duration) + body; filename `YYYY-MM-DD_HH-mm-ss.txt`; collision → ` (2)`; optional per-line timestamps; UTF-8.
- [ ] Pre-write disk-space check; save-failure keeps journal + offers retry/alt folder.

## What is done
- Nothing built. State machine, transcript model, journal, and file format fully specified in SPEC.md §9/§11/§12.1.
- Spike confirmed the **failure mode this spec prevents**: no crash safety = total loss on a long session; and that endpointing drives when segments finalize.

## Blockers
- **B1** — Xcode. Depends on SPEC-02/03 existing.
- **B5** — journal plaintext vs Keychain-encrypted (SPEC.md §22.3); `.json` sidecar (§22.4). Neither blocks core build.

## Acceptance
- Start→pause→resume→stop produces a correct `.txt` + DB row; journal deleted.
- Kill app mid-session → next launch offers recovery and restores all finalized segments.
- Paused time excluded from `duration_seconds`.
- Save failure preserves the journal and does not lose data.
