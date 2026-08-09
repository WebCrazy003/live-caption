# Local Caption — Build Sequence (v2)

Phases 1–5 are **built and shipping** in `../app/` (see [STATUS.md](STATUS.md)). This doc keeps
the original build order for history and tracks the **remaining** work: Phase 7 (spec 10, Live
AI Summary) and Phase 6 (spec 09, notarized distribution — blocked on B2).

Status legend: ✅ done · 🟢 mostly · 🟡 partial · 🔴 barely · ⬜ not started

---

## Current status

Phases 1–5 are **built** in `../app/` (see [STATUS.md](STATUS.md)); 35 unit tests pass. Only
distribution (09) and the new Live AI Summary (10) remain.

| Spec | Status | What's left (summary) |
|------|--------|-----------------------|
| [00 Foundation](SPEC-00-foundation.md) | ✅ | — |
| [01 Data Layer](SPEC-01-data-layer.md) | ✅ | — |
| [02 Audio Capture](SPEC-02-audio-capture.md) | ✅ | — |
| [03 ASR Engine](SPEC-03-asr-engine.md) | ✅ | real-audio WER pass (SPEC §19) |
| [04 Session & Transcript](SPEC-04-session-transcript.md) | ✅ | — |
| [05 Caption UI](SPEC-05-caption-ui.md) | ✅ | — |
| [06 Session List](SPEC-06-session-list.md) | ✅ | — |
| [07 Settings](SPEC-07-settings.md) | ✅ | — |
| [08 Window & Clipboard](SPEC-08-window-clipboard.md) | ✅ | auto-copy-on-selection (AppKit text view) |
| [09 Packaging](SPEC-09-packaging.md) | 🟡 | Developer ID + notarization, DMG — blocked on **B2** |
| [10 Live AI Summary](SPEC-10-live-summary.md) | ✅ | on-device 1B LLM (local mlx-lm server), "Key points" card every ~50 words; native MLX-Swift deferred |

**What the app already delivers:** ScreenCaptureKit system-audio capture → WhisperKit
dual-model hybrid (tiny.en + small.en) → decoupled real-time streaming with LocalAgreement-2
→ paragraphs, auto-scroll, metadata + hallucination filtering → session lifecycle, transcript
save (`.txt`+`.json`), crash recovery, session list, full Settings, always-on-top overlay,
and clipboard — in a locally-signed launchable app. See `../app/`.

---

## Remaining sequence

Phases 1–5 below are **complete** (kept for historical record). Remaining: **10** (Live AI
Summary) and **09** (ship).

```
Phase 1  ✅  00-finish  →  01                 foundation + data layer
Phase 2  ✅  04         →  05-finish          sessions/lifecycle + caption controls
Phase 3  ✅  06         ‖  07                  session list ‖ settings (parallel)
Phase 4  ✅  02-finish  ‖  03-finish          capture hardening ‖ ASR hybrid (parallel, independent)
Phase 5  ✅  08                               window + clipboard
Phase 7  ▶  10                               live AI summary (on-device MLX LLM)   ← next
Phase 6  ▶  09                               sign + notarize + ship                 (blocked on B2)
```

> **Phase 7 — Live AI Summary ([10](SPEC-10-live-summary.md)):** right-side "Key points" panel that
> adds a short easy-English card every ~50 words of speech, produced by an on-device MLX LLM,
> distilling captions into "what they want." Depends on 04 (finals) + 05 (layout) — both done — so
> it can start now, independent of the 09 distribution blocker.

### Phase 1 — Foundation & data
1. **00-finish** — navigation shell (List ↔ Active ↔ Settings), add GRDB, App Support dir bootstrap.
2. **01** — config store (versioned JSON) + SQLite `sessions` table + migrations.

### Phase 2 — Sessions become real
3. **04** — state machine (start/pause/resume/stop), **save transcript `.txt`**, crash-recovery journal, recovery-on-launch.
4. **05-finish** — session header (elapsed time, status pill) + controls (Pause/Resume/Stop/Settings, font ±).

### Phase 3 — Management & config (parallel)
5. **06** — Session List: create/open(read-only)/rename/delete/search/sort.
6. **07** — Settings UI bound to config (model, transcript folder, VAD, paragraph size…).

### Phase 4 — Quality (parallel, independent)
7. **02-finish** — device-disconnect auto-recovery, explicit bounded (drop-oldest) buffer.
8. **03-finish** — dual-model hybrid (tiny partials + finals), LocalAgreement-2 stabilization, metadata-based filters (`no_speech_prob`/`avg_logprob`/`compression_ratio`).

### Phase 5 — Polish
9. **08** — always-on-top, opacity, window size/position memory; clipboard copy-last-N (+ optional auto-copy).

### Phase 6 — Ship
10. **09** — hardened runtime, Developer ID signing + notarization, DMG.

---

## Critical path
```
00 → 01 → 04 → 05 → 06 → 09
```
07, 02-finish, 03-finish, and 08 hang off this spine and can run in parallel.

## Blockers
- ✅ **B1** (Xcode) — resolved, installed.
- ✅ **B3** (capture method) — resolved, ScreenCaptureKit.
- ⛔ **B2** (Apple Developer account) — still needed for Phase 6 notarization only.
- ✅ **B4** (hybrid vs single model) — resolved, dual-model hybrid shipped.
- ✅ **B5** (journal encryption, JSON sidecar, etc.) — resolved in Phase 2.
- **B6–B8** (Phase 7 / spec 10) — local-LLM memory pressure, latency vs 10 s cadence, summary
  quality on real speech. See [SPEC-10](SPEC-10-live-summary.md).

## Fastest-value shortcut
If you want immediate usefulness before the full foundation: pull **04's "save transcript
to `.txt`"** forward — it works on today's single-window app and makes it usable for a real
interview, then return to Phase 1.
