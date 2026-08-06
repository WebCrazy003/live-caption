# SPEC-08 — Window Behavior & Clipboard

**Status:** Clipboard filters validated (spike) · **Depends on:** SPEC-04, SPEC-05 · **Full detail:** SPEC.md §9.4, §14

## Goal
Two polish feature areas: an always-on-top adjustable overlay window, and opt-in
clipboard automation.

## What should be done
### Window (SPEC.md §14)
- [ ] Always-on-top toggle (`NSWindow.level`).
- [ ] Adjustable opacity 0.3–1.0 (clamped so captions stay legible).
- [ ] Movable, resizable; **remember size + position**; on load, validate the frame is on a connected display, else recenter.
- [ ] One-time warning: an always-on-top overlay may be captured in a screen share.

### Clipboard (SPEC.md §9.4) — defaults OFF
- [ ] **Auto-update** (default OFF): on each final sentence, copy last **N** completed sentences (default 10). Writes only; never reads clipboard; visible indicator when active.
- [ ] **Auto-copy selection** (default OFF).
- [ ] **Manual "Copy last N"** button (primary path), always available.
- [ ] "Completed sentence" = final segment ending in terminal punctuation / at an endpoint.

## What is done
- Nothing built. Window + clipboard behavior specified in SPEC.md §9.4/§14.
- **Clipboard sentence/hallucination filtering validated in spike:** the `no_speech_prob` + blocklist + `compression_ratio` gates that decide what counts as a real "completed sentence" were proven live (`../spike/live.py`).

## Blockers
- **B1** — Xcode. Depends on SPEC-04 (sentences) and SPEC-05 (window/controls).

## Acceptance
- Always-on-top + opacity work; window frame restored and validated against displays.
- Clipboard automation works as configured and is **off by default**; enabling it never reads the clipboard; "Copy last N" always works.
