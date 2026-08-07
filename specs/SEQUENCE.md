# Local Caption — Build Sequence (v2)

Updated after the **minimal app** was built (commit `832d85c`), which advanced specs
02/03/05 as a partial vertical slice. This is the remaining sequence to reach the full spec.

Status legend: ✅ done · 🟢 mostly · 🟡 partial · 🔴 barely · ⬜ not started

---

## Current status

| Spec | Status | What's left (summary) |
|------|--------|-----------------------|
| [00 Foundation](SPEC-00-foundation.md) | 🟡 | nav shell, GRDB dep, App Support dirs |
| [01 Data Layer](SPEC-01-data-layer.md) | ⬜ | config store + SQLite |
| [02 Audio Capture](SPEC-02-audio-capture.md) | 🟢 | disconnect recovery, bounded buffer |
| [03 ASR Engine](SPEC-03-asr-engine.md) | 🟡 | dual-model hybrid, LocalAgreement-2, metadata filters |
| [04 Session & Transcript](SPEC-04-session-transcript.md) | 🔴 | state machine, save `.txt`, journal, recovery |
| [05 Caption UI](SPEC-05-caption-ui.md) | 🟡 | session header, controls (pause/resume/stop/font) |
| [06 Session List](SPEC-06-session-list.md) | ⬜ | list/create/open/rename/delete/search/sort |
| [07 Settings](SPEC-07-settings.md) | ⬜ | settings UI bound to config |
| [08 Window & Clipboard](SPEC-08-window-clipboard.md) | ⬜ | always-on-top, opacity, clipboard |
| [09 Packaging](SPEC-09-packaging.md) | 🟡 | hardened runtime, Developer ID + notarization, DMG |

**What the minimal app already delivers:** ScreenCaptureKit system-audio capture →
WhisperKit `small.en` (CPU+GPU) → decoupled real-time streaming → paragraphs + auto-scroll
+ hallucination filtering, in a locally-signed launchable app. See `../minimal/`.

---

## Remaining sequence

```
Phase 1  ▶  00-finish  →  01                 foundation + data layer
Phase 2  ▶  04         →  05-finish          sessions/lifecycle + caption controls
Phase 3  ▶  06         ‖  07                  session list ‖ settings (parallel)
Phase 4  ▶  02-finish  ‖  03-finish          capture hardening ‖ ASR hybrid (parallel, independent)
Phase 5  ▶  08                               window + clipboard
Phase 6  ▶  09                               sign + notarize + ship
```

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
- **B4** (hybrid vs single model) — single `small.en` works today; hybrid is Phase 4 step 8.
- **B5** — minor product decisions (journal encryption, JSON sidecar) surface in Phase 2.

## Fastest-value shortcut
If you want immediate usefulness before the full foundation: pull **04's "save transcript
to `.txt`"** forward — it works on today's single-window app and makes it usable for a real
interview, then return to Phase 1.
