# LocalCaption — Implementation Status & Architecture

Native macOS app that live-captions **system/call audio** fully on-device (Swift/SwiftUI +
WhisperKit), and auto-saves transcripts. This document reflects what is actually built in
`app/`. The product spec is [`../SPEC.md`](../SPEC.md); the phase plan is
[`SEQUENCE.md`](SEQUENCE.md).

---

## Status at a glance

| Phase | Area | Status |
|------|------|--------|
| 1 | Foundation: nav shell, config, SQLite, App Support bootstrap | ✅ Done |
| 2 | Session lifecycle, transcript save (`.txt`+`.json`), crash-recovery journal | ✅ Done |
| 3 | Session list (open/rename/delete/search/sort) + full Settings | ✅ Done |
| 4 | Dual-model ASR hybrid, LocalAgreement-2, metadata filters | ✅ Done |
| 5 | Always-on-top overlay, opacity, window memory, clipboard | ✅ Done |
| 6 | Developer ID signing + notarization + DMG | ⛔ Blocked on Apple Developer account (B2) |
| 7 | Live AI Summary — on-device 1B LLM, right-side "Key points" card | ❌ Removed 2026-10-02 ([SPEC-10](SPEC-10-live-summary.md)) |
| 8 | Interview Assist — F8 sends the interviewer's words to a locked-down Codex (ChatGPT) thread prepared with CV/JD/skill; answers stream beside captions; summary + history | 🟢 Built ([SPEC-11](SPEC-11-interview-assist.md)–[15](SPEC-15-interview-ui-results.md)); real-call check pending |
| 9 | Accent mode — Standard ↔ Accent switch; two speech models on the user's RTX desktop (`rtx-agent/`), Codex correction live and after Stop | 🟡 Built ([SPEC-18](SPEC-18-accent-pipeline.md)); end-to-end replay passes (final 37.0% WER vs 46.9% Standard turbo); real-call check pending |

**Phases 1–5 and 8 are built.** The app builds, runs, captions, saves, recovers, is locally
signed, and has an opt-in **Interview mode** (Phase 8). The live AI summary ("Key points",
Phase 7) was removed on 2026-10-02. Remaining: notarized distribution (Phase 6, blocked on B2).

Verification: `swift build` clean; **35 unit tests** pass (`swift test`) covering the whole
data/logic layer. Live captioning verified manually.

---

## Key decisions (resolved blockers)

- **Capture = ScreenCaptureKit** (not BlackHole). Native system-audio capture; needs the
  **Screen Recording** permission, no third-party install, no microphone permission.
- **ASR = dual-model hybrid**, settled by an on-device benchmark (CPU+GPU, 7 s clip):
  tiny.en ≈ 456 ms, small.en ≈ 2.06 s, large-v3-turbo ≈ 3.53 s. → **tiny.en partials +
  small.en finals** by default; large-v3-turbo is an opt-in accuracy mode.
- **Build system = SwiftPM** (no Xcode project). Builds, bundles, and locally signs via
  `run.sh` with a stable self-signed identity (so macOS keeps the Screen Recording grant
  across rebuilds).
- **Accent mode (SPEC-18) is the second exception:** call audio goes to the user's own RTX
  desktop on the LAN (paired, token-authenticated, Private-network firewall rule), and transcript
  text goes to OpenAI through Codex for correction. Standard mode is unchanged.
- **On-device only — in Caption only mode.** The sole network use is the one-time model
  download. Interview mode (opt-in, behind a notice) sends the CV, JD, questions and optional
  screenshots to OpenAI through the `codex` child process; the app itself still makes no
  network calls (SPEC-11 §Privacy).

---

## Package layout

Two SwiftPM targets keep the testable logic separate from the UI/ML.

```
app/
├── Package.swift
├── run.sh                     build → bundle → sign → launch
├── scripts/setup-signing.sh   one-time stable signing identity
├── Sources/
│   ├── LocalCaptionKit/       ← pure data/logic library (unit-tested, no SwiftUI/WhisperKit)
│   ├── LocalCaption/          ← the SwiftUI app: audio, ASR, session, UI
│   └── Benchmark/             ← dev-only on-device ASR benchmark (not shipped)
└── Tests/LocalCaptionKitTests/
```

### `LocalCaptionKit` (library — 35 tests target this)

| File | Responsibility |
|------|----------------|
| `AppPaths` | Application Support layout + first-launch bootstrap |
| `Config` | Versioned JSON config (schema 2): load, merge-defaults, corrupt→backup+repair, atomic write |
| `SessionRecord` / `Store` | SQLite (GRDB) session metadata; WAL; migrations; CRUD + search/sort |
| `Transcript` / `TranscriptSegment` | In-memory transcript model + `.txt` formatting |
| `TranscriptWriter` | Writes `.txt` + `.json` sidecar with ` (2)` collision suffixes |
| `Journal` | Append-only `.jsonl` crash-recovery log + recovery scan |
| `SessionFiles` | Delete a transcript's `.txt` + `.json` together |
| `Filters` | Text cleanup, hallucination blocklist, metadata quality gate (§8.3) |
| `LocalAgreement` | LocalAgreement-2 interim stabilization |
| `Sentences` | Sentence split + last-N (for clipboard) |
| `TimeFormat` | Clock / filename / ISO date formatting |

### `LocalCaption` (executable — the app)

| File | Responsibility |
|------|----------------|
| `LocalCaptionApp` / `AppEnvironment` | `@main`; owns config store + DB; recovery scan on launch |
| `Audio/SystemAudioCapture` | ScreenCaptureKit stream → 48k stereo → 16k mono Float32; feeds the call recording at 48k |
| `Audio/SessionAudioRecorder` | Optional audio recording (`CallAudioRecorder` / `MicrophoneRecorder`) → AAC `.m4a` |
| `ASR/WhisperEngine` | Dual resident WhisperKit models (interim + final); cache-aware download/load; metadata filtering |
| `ASR/StreamingOrchestrator` | VAD endpointing loop; interim/final routing; LocalAgreement-2; sample-based timing |
| `Session/SessionController` | State machine; owns transcript + journal + clock; save-on-stop; clipboard |
| `UI/*` | Root nav, session list, active session + controls, read-only viewer, settings, recovery, window overlay |

---

## How it works (data flow)

```mermaid
flowchart LR
  A[ScreenCaptureKit\nsystem audio] --> B[48k→16k mono\nAVAudioConverter]
  B --> C[SampleBuffer]
  C --> D[VAD loop\nenergy gate + endpointing]
  D -->|~500ms tail| E[tiny.en interim]
  D -->|endpoint / max| F[small.en final]
  E --> G[LocalAgreement-2\nstabilize] --> H[dimmed provisional line]
  F --> I[metadata + text filters] --> J[TranscriptSegment]
  J --> K[in-memory Transcript]
  J --> L[journal .jsonl fsync]
  K -->|Stop| M[.txt + .json + SQLite row]
  M -->|delete| L
```

- **Interim track:** every ~500 ms the recent tail is decoded by **tiny.en** (~0.45 s),
  stabilized by LocalAgreement-2, shown as a dimmed provisional line (never saved).
- **Final track:** on a silence endpoint (or max-utterance cap) the utterance is decoded by
  **small.en** (~2 s), filtered, appended as a committed segment, and journaled.
- **Timing** is sample-based and pause-aware (the clock freezes while paused).

### Session state machine (SPEC §11)

`READY → RECORDING ⇄ PAUSED → STOPPING → SAVED`. Pause finalizes the in-flight utterance and
freezes the clock; Stop finalizes, writes `.txt` + `.json` + a DB row, and deletes the
journal. `duration_seconds` excludes paused time.

---

## On-disk data (`~/Library/Application Support/LocalCaption/`)

```
config.json              versioned settings (schema 2)
localcaption.db          SQLite session metadata (WAL)
transcripts/             <start>.txt  +  <start>.json  (saved on Stop)
journal/<session>.jsonl  crash-recovery log (deleted on clean Stop)
recordings/<session>.m4a audio being recorded; moved beside the transcript on Stop
models/                  WhisperKit CoreML weights (downloaded once)
```

Caption text lives in SQLite (`session_segments`); the `.txt`/`.json` are an export. Audio
is discarded after inference unless **Record audio** is on (below). Nothing is transmitted
off-device.

### Audio recording (owner, 2026-10-06)

Settings → General → **Record audio**: Off (default) · Call audio · My microphone.

- **Call audio** is the ScreenCaptureKit stream itself, written at its native 48 kHz stereo
  before the 16 kHz downmix. **My microphone** records the default input device
  (`AVAudioRecorder`, 48 kHz mono) and needs the Microphone permission
  (`NSMicrophoneUsageDescription`; release builds sign with `LocalCaption.entitlements`).
  Captions always come from the call audio.
- AAC at 64 kbps per channel (~58 MB/h for call audio, ~29 MB/h for the mic).
- The file only grows while capturing, so pauses are cut out exactly as the transcript clock
  skips them: caption timestamps line up with the call recording.
- On Stop the file moves to the transcript folder with the `.txt`'s base name and is stored
  in `sessions.audio_file` (migration `v5_audio_file`, macOS only so far). Sessions → Play
  Audio / Reveal Audio; deleting a session's files deletes the audio too.
- Recording problems (no permission, no input device, encoder error) are reported under
  *Session issues* and never stop the captions.
- **Crash safety:** a quit mid-session closes the file, so Recover & Save attaches it. A
  crash leaves the `.m4a` without its header; recovery deletes it and saves the transcript
  only.

---

## Settings (persisted, live where safe)

General (transcript folder, name prefix, record audio) · Caption (font, auto-scroll, timestamps) · Audio
(VAD sensitivity) · ASR (endpoint silence, max utterance, interim/final model) · Window
(always-on-top, opacity) · Clipboard (auto-update, N, auto-copy selection). Font/opacity/
always-on-top apply live; ASR timing/VAD apply on next Start; model changes apply on next
Start (re-loads models).

---

## Known limitations / deferred

- **WER on real audio is unvalidated.** small.en vs large-v3-turbo was compared only on clean
  TTS; both were perfect there. Turbo is the opt-in for hard/noisy audio (SPEC §19 WER pass
  still outstanding).
- **Auto-copy-selection** (selecting text auto-copies) is not wired — it needs an AppKit text
  view rather than SwiftUI `Text`. Manual "Copy last N" and auto-update both work.
- **Not notarized** (Phase 6 / B2). Runs locally via a stable self-signed identity; would show
  a Gatekeeper prompt if copied to another Mac.
- **Model change requires restarting the session** (models load at Start).
- **Diarization / multi-language / mic captioning** are non-goals (SPEC §3.2). The mic can be
  *recorded* (above), not captioned.
- **Audio recording is one source at a time** (call *or* mic, not both mixed) and is lost
  if the app crashes mid-session (see Crash safety above).
