# Local Caption — Spec Index & Build Sequence

The full design lives in [`../SPEC.md`](../SPEC.md) (v2.0). This folder splits it into
**10 buildable sub-specs**. Each sub-spec states *what should be done*, *what is done*,
and *blockers*. Build in the sequence below.

**Product in one line:** native macOS (Swift/SwiftUI + WhisperKit) app that captions
**system/call audio** live, fully on-device, and auto-saves transcripts.

---

## Status at a glance

Updated after the **minimal app** (`../minimal/`, commit `832d85c`) — a partial vertical
slice of specs 02/03/05. Build order and remaining work: **[SEQUENCE.md](SEQUENCE.md)**.

Legend: ✅ done · 🟢 mostly · 🟡 partial · 🔴 barely · ⬜ not started

| # | Spec | Status | Remaining |
|---|------|--------|-----------|
| 00 | [Foundation & App Shell](SPEC-00-foundation.md) | 🟡 partial | nav shell, GRDB, App Support dirs |
| 01 | [Data Layer (Config + SQLite)](SPEC-01-data-layer.md) | ⬜ not started | all |
| 02 | [Audio Capture (system audio)](SPEC-02-audio-capture.md) | 🟢 mostly | disconnect recovery, bounded buffer |
| 03 | [ASR Engine & Streaming](SPEC-03-asr-engine.md) | 🟡 partial | hybrid, LocalAgreement-2, metadata filters |
| 04 | [Session Lifecycle & Transcript](SPEC-04-session-transcript.md) | 🔴 barely | state machine, save, journal, recovery |
| 05 | [Live Caption UI](SPEC-05-caption-ui.md) | 🟡 partial | header, controls (pause/resume/stop/font) |
| 06 | [Session List & Management](SPEC-06-session-list.md) | ⬜ not started | all |
| 07 | [Settings](SPEC-07-settings.md) | ⬜ not started | all |
| 08 | [Window & Clipboard](SPEC-08-window-clipboard.md) | ⬜ not started | all |
| 09 | [Packaging & Distribution](SPEC-09-packaging.md) | 🟡 partial | hardened runtime, Developer ID, notarize, DMG |

**Built:** the Python **spike** (`../spike/`) that proved the ASR architecture, and the
**minimal app** (`../minimal/`) — a working native system-audio live captioner. The
remaining sequence to the full spec is in **[SEQUENCE.md](SEQUENCE.md)**.

---

## Cross-cutting blockers (resolve these first)

| ID | Blocker | Blocks | Status |
|----|---------|--------|--------|
| **B1** | Full Xcode not installed | Every Swift spec | ✅ **Resolved** — Xcode 16.2 installed |
| **B3** | Capture method: BlackHole vs ScreenCaptureKit | 02, permissions | ✅ **Resolved** — ScreenCaptureKit |
| **B4** | ASR: hybrid vs single-model streaming | 03 | Single `small.en` works today; hybrid is Phase 4 |
| **B2** | Apple Developer account + Developer ID cert | 09 (notarization) | ⛔ Still needed for Phase 6 only |
| **B5** | Product decisions (journal encryption, JSON sidecar, bundled model, min-OS) — SPEC.md §22 | 04, 09 | Surface in Phase 2 |

No blockers remain for Phases 1–5.

---

## Build sequence

```mermaid
graph TD
  S00[00 Foundation & App Shell] --> S01[01 Data Layer]
  S00 --> S02[02 Audio Capture]
  S00 --> S03[03 ASR Engine]
  S02 --> S04[04 Session Lifecycle & Transcript]
  S03 --> S04
  S01 --> S04
  S03 --> S05[05 Live Caption UI]
  S04 --> S05
  S01 --> S06[06 Session List]
  S04 --> S06
  S01 --> S07[07 Settings]
  S04 --> S08[08 Window & Clipboard]
  S05 --> S08
  S05 --> S09[09 Packaging & Distribution]
  S06 --> S09
  S07 --> S09
  S08 --> S09
```

**Phase A — Scaffold:** `00` → then `01` (config + DB) alongside it.
**Phase B — Core pipeline:** `02` (audio) and `03` (ASR) in parallel → `04` (glue: lifecycle + transcript + journal).
**Phase C — UI:** `05` (caption view) → `06` (session list) and `07` (settings) in parallel.
**Phase D — Polish:** `08` (window + clipboard).
**Phase E — Ship:** `09` (sign, notarize, DMG).

**Critical path:** `00 → 03 → 04 → 05 → 09`. Get `03` (ASR) right first — it's the
product's core and the only part already de-risked.

**Recommended first milestone (vertical slice):** `00 + 02 + 03 + 04 + 05` = "audio in →
live captions on screen." Everything else (list, settings, window, clipboard, packaging)
layers onto that working core.
