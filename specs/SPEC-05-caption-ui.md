# SPEC-05 — Live Caption UI

**Status:** Not started · **Depends on:** SPEC-03, SPEC-04 · **Full detail:** SPEC.md §9.2, §13

## Goal
The Active Session screen: render the live transcript with instant interim + committed
final captions, smoothly, over multi-hour sessions.

## What should be done
- [ ] **Active Session screen (SPEC.md §13):** header (name · status pill ● Recording / ⏸ Paused · elapsed `HH:MM:SS`), system-audio device selector, caption area, controls (Pause/Resume, Stop, Settings, Copy last N, font +/−).
- [ ] **Caption rendering (SPEC.md §9.2):**
  - Full transcript, newest at bottom; finals append; one provisional line below.
  - Provisional text visually distinct (dim/italic); never persisted.
  - **Auto-scroll** unless user scrolled up → "Jump to latest"; resume at bottom.
  - Selectable text, automatic wrapping, adjustable font size.
  - Optional per-caption timestamps.
- [ ] **Incremental rendering** (append, not full-document rebuild) so long sessions stay < 100 ms/update.
- [ ] Wire caption events from SPEC-03; wire controls to SPEC-04 state machine.
- [ ] States: models loading, model download progress, permission-denied, BlackHole-missing, device-disconnected banner.

## What is done
- Nothing built. Layout, scroll behavior, and update model specified in SPEC.md §9.2/§13.
- Spike demonstrated the live experience end-to-end (relayed to chat): instant interim words + ~2 s finals — this UI is the on-screen version of that.

## Blockers
- **B1** — Xcode. Depends on SPEC-03/04.

## Acceptance
- Live captions appear (interim < ~300 ms, finals ~2 s); committed text never flickers.
- Auto-scroll follows newest; manual scroll-up pauses it and shows "Jump to latest".
- Text is selectable; font size adjusts live.
- No main-thread stall > 100 ms during continuous captioning over a 3 h session.
