# SPEC-07 — Settings

**Status:** Not started · **Depends on:** SPEC-01 (+ features to configure) · **Full detail:** SPEC.md §15

## Goal
A Settings screen bound to the config store, applying changes live where safe.

## What should be done
- [ ] Settings UI grouped per SPEC.md §15: General, Audio, ASR, Caption, Window, Clipboard.
- [ ] Two-way bind every setting to the Config store (SPEC-01); persist on change.
- [ ] **Live-apply** font size and window opacity; other changes apply on next session.
- [ ] **"Restart to apply"** note for ASR model changes.
- [ ] Sensible input constraints (opacity 0.3–1.0 clamped, VAD 0–3, N ≥ 1, etc.).
- [ ] Transcript-folder picker validates writability.

## What is done
- Nothing built. Full settings table (defaults, types, notes) specified in SPEC.md §15. Clipboard/window settings default to safe values (auto-clipboard OFF).

## Blockers
- **B1** — Xcode. Depends on SPEC-01. Individual settings only matter once their feature exists (audio 02, ASR 03, caption 05, window/clipboard 08).

## Acceptance
- Every setting persists across restarts (a core product acceptance criterion).
- Font/opacity change immediately; model change shows the restart note.
- Out-of-range inputs are clamped/rejected, not crashy.
