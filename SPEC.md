# Local Caption for Interviews (macOS) — Technical Specification

- **Version:** 2.0 (native WhisperKit rewrite; system-audio only)
- **Status:** Draft for review
- **Platform:** macOS 14 Sonoma or later, **Apple Silicon only**
- **Date:** 2026-08-06

> **Changes from v1.x**
> 1. **ASR engine → WhisperKit (native Swift).** Faster-Whisper on CPU is measurably
>    unusable for live captioning (see §1A, spike results). The app is now native
>    Swift/SwiftUI using WhisperKit (Apple Neural Engine + GPU).
> 2. **Microphone capture removed.** The app captures **system (speaker) audio only** —
>    i.e. the remote call participants. The local user's own voice is not transcribed.
>    All mic-device selection, mic enable/disable, and dual-source labelling are gone.
> 3. Latency targets replaced with **measured numbers** from the on-device spike.

---

## 0. How to read this document

Section **1** is the adversarial review of the original draft plus the **measured
evidence** that forced the engine change. Sections **2+** are the corrected spec.

Decisions I made are tagged **[DECISION]**. Decisions that still need the product owner
are tagged **[NEEDS OWNER]** and collected in §22. Do not start implementation until §22
is closed.

---

## 1. Challenge: what was wrong in the draft

Severity: **S1** = breaks a core promise · **S2** = significant risk · **S3** = clarification.

### 1A. The headline: Faster-Whisper cannot do live captioning on CPU — proven

The draft specified Faster-Whisper `large-v3-turbo`, `int8`, "real-time, latency < 1s".
We built a spike and measured it on this exact machine (Apple Silicon, macOS 14.7).

| Engine / model | Compute | Interim decode (budget 500 ms) | Final perceived latency | Accuracy* |
|---|---|---|---|---|
| faster-whisper `large-v3-turbo` | **CPU (int8)** | **4,744 ms** ❌ | **~5,800 ms** ❌ | perfect |
| mlx `large-v3-turbo` | **Apple GPU** | 1,459 ms ⚠️ | ~2,190 ms ⚠️ | perfect |
| faster-whisper `tiny.en` | CPU | **222 ms** ✅ | ~908 ms ✅ | perfect |

\* on a clean single-speaker clip — validates latency, not real-world WER.

**Why CPU fails, and why it's intrinsic:** Whisper's encoder always processes a
**30-second padded mel window regardless of chunk length**, so a 0.5 s chunk costs almost
as much as an 8 s chunk. There is a per-decode *floor* independent of audio length:
**~4.7 s on CPU, ~1.4 s on the Apple GPU** for `large-v3-turbo`. You cannot chunk under it.

**Consequences that drove the rewrite:**
- CPU `large-v3-turbo` (the literal draft) → ~5.8 s latency. **Rejected.**
- Apple-Silicon acceleration cuts the floor ~3×, making **accurate finals** viable (~2 s).
- A **small model** trivially meets the interim budget (`tiny.en` ~220 ms).
- ⇒ **Dual-model hybrid on Apple-Silicon acceleration** (WhisperKit): small model for
  interim partials, `large-v3-turbo` for finals. See §8.

Full method and logs: `spike/RESULTS.md`, `spike/bench.py`.

### 1B. Remaining challenges

| # | Sev | Draft assumption | Problem | Resolution |
|---|-----|------------------|---------|------------|
| C1 | **S1** | faster-whisper streams in real time | See §1A — false on CPU | **WhisperKit native**, dual-model hybrid (§8) |
| C2 | **S1** | Transcript held only in RAM; 3 h+ sessions | A crash at hour 2:59 loses everything | Append-only **crash-recovery journal** (§9.3) |
| C3 | **S2** | System audio "uses BlackHole"; pick the device | Selecting BlackHole as output means the user **stops hearing the call**; needs a **Multi-Output Device** | §6.2 specifies the Multi-Output setup + in-app guidance |
| C4 | **S2** | (Now) capture speaker only, no mic | **macOS still requires the microphone TCC permission to read the BlackHole *input* device** — dropping "mic" must not drop the permission | §17 keeps audio-input permission, reframed (not physical-mic) |
| C5 | **S2** | Native macOS app implied | Native Swift app must be **codesigned + notarized + hardened runtime** or Gatekeeper blocks it | §17.3 (far simpler than the Python-bundling path) |
| C6 | **S2** | "100% offline" | Whisper weights (~1.5 GB) download on first run; offline only *after* | §8.4: offline after model present; optional bundled-model build |
| C7 | **S3** | Whisper transcribes continuously | Whisper **hallucinates on silence** ("Thank you.", subtitle credits) | §8.3 VAD gating + logprob/no-speech/compression thresholds |
| C8 | **S3** | Devices selected freely | WhisperKit wants **16 kHz mono**; capture device is 48 kHz | §6.3 resampling/downmix via AVAudioConverter |
| C9 | **S3** | Delete / rename / open session | File-vs-metadata semantics unspecified | §10 defines all three |
| C10 | **S3** | config.json; "invalid config" handled | No schema version / migration | §12.2 adds versioning + repair |
| C11 | **S3** | Always-on-top during interviews | May be **captured in a screen share** | §14 warns; content-share exclusion is future |

**Note — the old C2 (dual mic+system with no diarization = unreadable transcript) is
resolved by scope:** capturing **only** system audio yields a single stream, so no
speaker labels are needed. Multiple remote participants are not separated (diarization is
a non-goal); this is acceptable for an interview transcript.

---

## 2. Overview

Local Caption is a native macOS app that produces **real-time captions of the audio you
hear on a call**, fully on-device. It captures **system (speaker) audio** via BlackHole,
transcribes locally with **WhisperKit** on the Apple Neural Engine + GPU, and shows a
live, scrolling transcript in an always-on-top window.

**No audio or transcript data ever leaves the device.** The only network use in the
product's lifetime is the one-time model download (or none, in a bundled-model build).

The local user's own microphone is **not** captured — the transcript is of the remote
participant(s).

---

## 3. Goals & non-goals

### 3.1 Goals
- Fully local, on-device speech recognition (no runtime network).
- Real-time captions of system/call audio with measured, honest latency (§18).
- Simple session management and automatic transcript saving.
- Crash-resilient long sessions (3 h+).
- Persistent, migratable settings.

### 3.2 Non-goals (v1)
- **Microphone / local-voice capture.**
- Speaker diarization (single mixed system stream, no per-speaker labels).
- AI summaries / local LLM.
- Languages other than English.
- Cloud sync / any server.
- Windows / Linux / Intel Macs.
- Saving or replaying raw audio (discarded after inference).

---

## 4. Primary use case & persona

**Persona — interviewer on video calls.** Runs Zoom/Meet/Teams; wants a live, private,
auto-saved transcript **of the candidate's answers** (the remote audio), without
transcribing their own questions. Accuracy of the saved transcript matters.

**Primary flow:** launch → model loaded → New Session → (BlackHole + Multi-Output already
configured) → Start → candidate speaks → captions appear live → Stop → transcript
auto-saved → back to list.

---

## 5. Application workflow

1. Launch; splash while WhisperKit models load.
2. Load & validate config (§12.2).
3. Load WhisperKit models **once** (interim + final), or trigger first-run download (§8.4).
4. Check environment: **audio-input permission** (§17.2) and **BlackHole presence** (§6.2).
5. Show **Session List**.
6. User creates a session (name optional → auto-named).
7. User confirms the system-audio (BlackHole) device.
8. **Start** → capture + streaming ASR begin.
9. Interim + final captions render live.
10. **Pause / Resume** (in-flight utterance finalized on pause).
11. **Stop** → finalize transcript, write file + DB row, delete journal.
12. Return to Session List.
13. Any prior session can be **opened read-only**.

---

## 6. Audio subsystem (system audio only)

### 6.1 Capture
- **Framework:** `AVAudioEngine` (AVFoundation), tapping the **BlackHole input device**.
- Single source. No microphone, no mixing, no source labels.
- `installTap` on the input node → buffers → converted (§6.3) → fed to the ASR pipeline.
- **Device-disconnect recovery:** if the capture device errors/disappears, show a
  non-blocking banner and retry every 2 s (bounded); audio during the gap is lost (documented).

### 6.2 BlackHole + Multi-Output setup (C3)
Capturing what you *hear* requires routing system output through BlackHole while still
hearing it — a **Multi-Output Device**.

- **Detection** (launch + each start): enumerate Core Audio devices; find one named `BlackHole`.
- **If absent:** show install instructions (Homebrew / installer); **disable Start**; offer "Re-check".
- **Routing guidance (in-app):** user creates, in Audio MIDI Setup, a **Multi-Output
  Device** = [physical output] + [BlackHole], sets it as the system/meeting-app output,
  and selects **BlackHole** as the capture device in the app. The app **verifies** the
  selection but does not create the device in v1.
- Selected device persisted (`audio.system_device`).
- **[NEEDS OWNER]** Prefer **ScreenCaptureKit audio capture** (SCStream, macOS 13+) over
  BlackHole? It captures system audio **natively with no third-party install**, at the
  cost of a **Screen Recording** permission. Strong native alternative now that the app is
  Swift — see §22. Baseline remains BlackHole per the original draft.

### 6.3 Format normalization (C8)
- WhisperKit input = **16 kHz mono Float32**.
- BlackHole delivers 48 kHz stereo → **downmix to mono + resample to 16 kHz** via
  `AVAudioConverter`, off the audio-render thread.

### 6.4 Voice-activity detection
- Energy-based VAD (WhisperKit's built-in energy VAD, or a small Swift RMS gate),
  configurable sensitivity, used for endpointing and to gate the ASR against silence (C7).

---

## 7. (reserved)

---

## 8. Speech recognition — WhisperKit dual-model hybrid

### 8.1 Engine & models
- **Engine:** WhisperKit (CoreML) on **Apple Neural Engine + GPU**.
- **Two models, loaded once, resident:**
  - **Interim model:** `tiny.en` (or `base.en`) — fast provisional partials (~220 ms measured).
  - **Final model:** `large-v3-turbo` — accurate finals on endpoint (~1.5–2 s on-device).
- Language forced to **English**; task = transcribe.
- **[NEEDS OWNER]** WhisperKit's built-in streaming (confirmed-token windowing) may make a
  separate interim model unnecessary — validate `large-v3-turbo`-only streaming vs the
  hybrid on-device before finalizing (§22).

### 8.2 Two-track streaming
1. **Interim track** — while VAD reports speech, every **~500 ms** transcribe the current
   utterance window with the interim model → show *provisional* (dimmed) text. Stabilize
   with **LocalAgreement-2**: commit only tokens agreed by the last two hypotheses; keep
   the tail provisional so committed text never flickers.
2. **Final track** — when VAD sees end-of-utterance (silence ≥ `endpoint_silence_ms`,
   default **600 ms**) or the window hits `max_utterance_s` (default **20 s**), transcribe
   the full utterance with `large-v3-turbo` → emit a **final** caption replacing the provisional line.

Bound the decode window (e.g. last ≤ 28 s) so cost stays flat over long turns (C-perf).

### 8.3 Hallucination / junk suppression (C7)
Drop or blank a segment when any of: `noSpeechProb > 0.6` on a low-energy region;
`avgLogprob < -1.0`; `compressionRatio > 2.4` (repetition); or text matches a blocklist of
known Whisper silence-hallucinations ("Thank you.", "Thanks for watching.", trailing
subtitle credits). Apply consecutive-repetition suppression on committed tokens.

### 8.4 Model acquisition & offline (C6)
- Models downloaded on first run to Application Support; progress UI; network **only** here.
- On failure: retry / choose smaller final model / quit.
- **Bundled-model build (optional):** ships weights in the app → zero-network. Build flag.
- After models exist, **no network calls** at runtime.

### 8.5 Prerequisite for building (environment)
WhisperKit is a Swift package; **building requires full Xcode**. This machine currently
has only Command Line Tools, whose SwiftPM is broken (manifest link failure) — install
Xcode before implementation. (This blocked the native spike; the acceleration thesis was
proven via MLX instead.)

---

## 9. Transcript, captions, crash recovery

### 9.1 Model
A session transcript is an ordered list of **final segments**:

```jsonc
{
  "id": "uuid",
  "text": "final recognized text",
  "t_start_ms": 12345,   // session-relative
  "t_end_ms": 14210,
  "created_at": "2026-08-06T14:32:31Z"
}
```
Interim captions are **not** stored — a single provisional line updates until finalized.
No `source` field (single stream).

### 9.2 Live rendering
- Full session transcript, newest at bottom; finals **append**; provisional line below.
- **Auto-scroll** unless the user scrolled up (then show "Jump to latest"; resume at bottom).
- Selectable text, automatic wrapping, adjustable font size.
- Optional per-caption timestamps (Settings).
- **Incremental** rendering (append, not full-document rebuild) so long sessions stay fluid.

### 9.3 Crash-recovery journal (C2)
- Transcript authoritative in memory during the session.
- Each final segment also appended to `…/journal/<session_id>.jsonl` (append-only, batched fsync).
- Normal **Stop** → write clean transcript (§12.1), delete journal.
- Next launch: if journals remain → **"Recover unsaved session"** → rebuild + let user save.
- **[NEEDS OWNER]** Plaintext journal on disk during recording OK, or encrypt with a
  per-session Keychain key? (Never leaves device; deleted on clean stop.)

### 9.4 Clipboard features (defaults OFF — they overwrite the system clipboard)
- **Auto-update** (default OFF): on each final sentence, copy the last **N** completed
  sentences (default 10). Writes only; never reads the clipboard; UI indicator when active.
- **Auto-copy selection** (default OFF): selecting transcript text copies it.
- **Manual "Copy last N"** button — the recommended primary path, always available.

---

## 10. Session List screen

**Actions:** Create · Open (read-only) · Delete · Rename · Search · Sort.
**Columns:** Session Name · Created · Duration · Transcript file (reveal in Finder).

- **Create:** optional name; empty → `<prefix><YYYY-MM-DD_HH-mm-ss>` (prefix from Settings).
- **Open:** loads the saved `.txt` **read-only** (no re-transcription — raw audio isn't kept).
- **Rename (C9):** edits metadata only (`session_name`); does **not** rename the timestamped file.
- **Delete (C9):** removes the DB row; asks **"Also delete the transcript file?"** (default keep).
- **Search:** by name/date. **Sort:** name/created/duration, asc/desc.

---

## 11. Session lifecycle & state machine

States: `IDLE → RECORDING ⇄ PAUSED → STOPPING → SAVED`.

- **Start:** open capture device, start ASR, start monotonic elapsed clock.
- **Pause:** stop feeding audio; **finalize the in-flight utterance** (flush one final decode); freeze elapsed clock; keep transcript + journal.
- **Resume:** restart capture; clock resumes; VAD context reset.
- **Stop:** finalize in-flight utterance, stop capture + ASR, compute duration, write transcript (§12.1) + DB row (§12.3), delete journal, return to list.
- **Crash/quit while recording:** journal remains → recovery next launch (§9.3).
- **[DECISION]** `duration_seconds` excludes paused time.

---

## 12. Persistence

### 12.1 Transcript file
- Location: `general.transcript_folder`, default `~/Library/Application Support/LocalCaption/transcripts/`.
- Filename `YYYY-MM-DD_HH-mm-ss.txt` (start time); collision → append ` (2)`. UTF-8.

```
Session: Interview 2026-08-06 14:32
Start:   2026-08-06 14:32:18
End:     2026-08-06 15:04:02
Duration: 00:31:44

[00:00:03] Thanks for joining the interview today.
[00:00:07] Happy to be here.
...
```
Per-line timestamps optional (`caption.show_timestamps`). No speaker labels.
- **[NEEDS OWNER]** Also emit a machine-readable `.json` sidecar (trivial recovery/re-render)? Recommended.

### 12.2 Config (`config.json`, versioned — C10)
Location `~/Library/Application Support/LocalCaption/config.json`. On load: validate;
merge defaults for missing keys; on unparseable file, back up to `config.json.bak-<ts>`
and write defaults. Atomic writes (temp + rename).

```jsonc
{
  "schema_version": 2,
  "general": {
    "transcript_folder": "~/Library/Application Support/LocalCaption/transcripts",
    "session_name_prefix": "Interview "
  },
  "audio": {
    "system_device": null,        // BlackHole device UID
    "vad_sensitivity": 2          // 0..3
  },
  "asr": {
    "interim_model": "tiny.en",
    "final_model": "large-v3-turbo",
    "endpoint_silence_ms": 600,
    "interim_interval_ms": 500,
    "max_utterance_s": 20
  },
  "caption": { "font_size": 18, "auto_scroll": true, "show_timestamps": false },
  "window": {
    "always_on_top": true, "opacity": 1.0,
    "width": 480, "height": 640, "x": null, "y": null
  },
  "clipboard": { "auto_update": false, "recent_sentences": 10, "auto_copy_selection": false }
}
```
(Mic-related keys removed vs v1.)

### 12.3 SQLite (metadata only)
Location `…/localcaption.db`. Swift: GRDB or SQLite.swift. WAL mode.

```sql
CREATE TABLE IF NOT EXISTS sessions (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  session_name     TEXT    NOT NULL,
  created_at       TEXT    NOT NULL,
  ended_at         TEXT,
  duration_seconds INTEGER NOT NULL DEFAULT 0,
  transcript_file  TEXT
);
CREATE INDEX IF NOT EXISTS idx_sessions_created ON sessions(created_at);
```
Migrations keyed on `PRAGMA user_version`. Transcript text never stored in SQLite.

---

## 13. UI

- **Session List** (§10).
- **Active Session** — header (name · status pill ● Recording / ⏸ Paused · elapsed `HH:MM:SS`);
  **system-audio device selector** (BlackHole); caption area (§9.2); controls
  (Pause/Resume, Stop, Settings, Copy last N, font +/−).
- **Settings** — grouped per §15; live-applies font/opacity; model changes note "restart to apply".
- **Recovery prompt** — on launch if journals exist.
- **Explicit states to design:** empty list · models loading · model download · **audio-input permission not granted** · BlackHole missing · device disconnected · save failure · disk-full.
- **Keyboard:** ⌘N new · ⌘, settings · Space pause/resume · ⌘F search · ⌘C copy selection.

---

## 14. Window behavior

- Always-on-top (toggle), adjustable opacity **0.3–1.0** (clamped for legibility), movable, resizable.
- Remember size/position; validate the saved frame is on a connected display, else recenter.
- **Warning (C11):** an always-on-top overlay may be captured if the user screen-shares.
  Content-share exclusion (`NSWindow.sharingType = .none`) is a future item.

---

## 15. Settings reference

| Group | Setting | Type | Default | Notes |
|-------|---------|------|---------|-------|
| General | Transcript folder | path | App Support/transcripts | writable |
| General | Session name prefix | string | `Interview ` | auto-names |
| Audio | System device | device | none | BlackHole; Start disabled if absent |
| Audio | VAD sensitivity | 0–3 | 2 | endpointing/silence gate |
| ASR | Interim model | enum | tiny.en | fast partials |
| ASR | Final model | enum | large-v3-turbo | accurate finals; restart to apply |
| ASR | Endpoint silence (ms) | int | 600 | latency/segmentation |
| Caption | Font size | int | 18 | live |
| Caption | Auto-scroll | bool | true | |
| Caption | Show timestamps | bool | false | view + file |
| Window | Always on top | bool | true | |
| Window | Opacity | 0.3–1.0 | 1.0 | clamped |
| Window | Size / position | — | 480×640 / center | validated on load |
| Clipboard | Auto-update | bool | **false** | writes last N |
| Clipboard | Recent sentences (N) | int | 10 | |
| Clipboard | Auto-copy selection | bool | **false** | |

All settings persist across restarts.

---

## 16. (reserved)

---

## 17. Security, privacy, permissions, packaging

### 17.1 Privacy invariants
- No audio/transcript/metadata leaves the device, ever, at runtime. No telemetry.
- Only lifetime network use: one-time model download (skippable via bundled build).
- Raw audio discarded after inference (never persisted).

### 17.2 Permissions (C4 — important)
- **Microphone (audio-input) permission is REQUIRED even though no physical mic is used.**
  macOS gates **all** audio *input* devices — including BlackHole — behind the microphone
  TCC permission. Reading BlackHole without it fails.
- `Info.plist`: **`NSMicrophoneUsageDescription`** (word it as "to capture call/system audio for captioning").
- Request on first capture; handle **denied** gracefully (banner + link to System Settings; §13).
- BlackHole route needs **no** Screen Recording permission. *(If the ScreenCaptureKit
  alternative in §6.2 is chosen instead, that path needs Screen Recording permission and
  no microphone permission — a different trade-off.)*

### 17.3 Packaging / signing / notarization (C5)
- Standard Swift app build (Xcode). **Hardened Runtime** with entitlement
  `com.apple.security.device.audio-input`.
- **Codesign** (Developer ID) + **notarize** + staple; ship signed DMG.
- Far simpler than v1's Python-bundling path — no interpreter entitlement exceptions.

---

## 18. Performance & resource budgets (measured where possible)

| Metric | Target | Basis |
|--------|--------|-------|
| Cold startup (models present) | < 5 s to Session List | — |
| Model load | once, resident | WhisperKit prewarm |
| **Interim** partial latency | < ~300 ms | measured `tiny.en` = 222 ms avg |
| **Final** caption latency | p50 ≈ endpoint(600 ms) + decode(~1.5 s) ≈ **~2.1 s**; p90 < 3 s | measured GPU large-v3-turbo |
| Decode RTF (final model) | ~0.2× (5× realtime) | measured MLX proxy = 0.19× |
| Memory, 3 h session | stable; bounded buffers + capped window | requirement |
| UI | no main-thread block > 100 ms | requirement |
| Session length | ≥ 3 h | journal-backed |

**Honesty note:** the draft's "< 1 s final" is **not achievable** for `large-v3-turbo`
even on the Apple GPU — the encoder floor is ~1.4 s. Sub-second applies to **interim**
partials only. Finals land at ~2 s. WhisperKit on the ANE with streaming decode may
improve on the MLX proxy numbers — re-measure once Xcode is installed.

---

## 19. Testing strategy

- **Unit:** config load/validate/repair + migration; DB CRUD + migration; endpoint detection on synthetic silence/speech; hallucination-filter thresholds; sentence segmentation for clipboard; transcript formatting; filename collision.
- **Component (fixtures):** feed 16 kHz WAV fixtures through the pipeline; assert final text (WER tolerance) and that LocalAgreement never rewrites committed tokens.
- **Integration:** start→pause→resume→stop; journal write + crash-recovery (kill mid-session, assert restore); BlackHole-absent path; device-disconnect.
- **Perf/soak:** 3 h synthetic run — bounded RSS, no dropped-frame runaway.
- **Real-audio WER pass:** noisy, multi-speaker interview recordings (the spike used clean TTS — WER is not yet validated).
- **Manual:** permission granted/denied; always-on-top + opacity; screen-share capture check.

---

## 20. Project structure (Swift / SwiftUI)

```text
LocalCaption/
├── LocalCaptionApp.swift          # @main, app bootstrap
├── Package/                       # SwiftPM deps: WhisperKit, GRDB
├── UI/
│   ├── SessionListView.swift
│   ├── ActiveSessionView.swift
│   ├── SettingsView.swift
│   ├── RecoveryView.swift
│   └── CaptionView.swift          # incremental renderer
├── Audio/
│   ├── SystemAudioCapture.swift   # AVAudioEngine tap on BlackHole
│   ├── AudioConverter.swift       # 48k stereo -> 16k mono Float32
│   ├── BlackHole.swift            # detection + guidance
│   └── VAD.swift                  # energy VAD + endpointing
├── ASR/
│   ├── WhisperEngine.swift        # WhisperKit load (interim + final)
│   ├── StreamingOrchestrator.swift# two-track, LocalAgreement-2
│   └── Filters.swift              # hallucination/repetition suppression
├── Session/
│   ├── SessionLifecycle.swift     # state machine
│   ├── Transcript.swift           # in-memory model + file writer
│   ├── Journal.swift              # crash-recovery journal
│   └── Store.swift                # SQLite (GRDB)
├── Config/
│   ├── Config.swift               # schema + versioning + migration
├── Resources/                     # Info.plist (NSMicrophoneUsageDescription), assets
└── Tests/
    ├── Fixtures/                  # WAV + expected transcripts
    ├── Unit/  Integration/  Perf/
```

---

## 21. Acceptance criteria (measurable)

1. Create, open (read-only), rename, delete, search, sort sessions.
2. System (speaker) audio captured via BlackHole after Multi-Output setup; absence handled with a guided, non-crashing path.
3. Audio-input permission requested and required; denial handled gracefully (no crash).
4. WhisperKit runs `large-v3-turbo` (finals) + a small interim model **fully on-device**; no runtime network (verified with a network monitor).
5. Live captions: interim partials < ~300 ms; finals ~2 s p50 (< 3 s p90); committed tokens never rewritten.
6. Whisper silence-hallucinations suppressed (5-min silent soak → no junk captions).
7. Always-on-top + adjustable opacity; window frame restored and validated against connected displays.
8. Clipboard automation works as configured and is **off by default**; enabling it never reads the clipboard.
9. All settings persist; corrupt config repairs to defaults with a backup.
10. On Stop: `.txt` written (header + body); DB row recorded; journal deleted.
11. Killing the app mid-session leaves a recoverable journal; next launch recovers and can save.
12. A ≥ 3 h session completes with stable, bounded memory.
13. No audio or transcript data transmitted externally.
14. App is codesigned + notarized; launches without Gatekeeper warnings.

---

## 22. Open decisions requiring the product owner

1. **[NEEDS OWNER]** **BlackHole vs ScreenCaptureKit** for system audio. ScreenCaptureKit
   is native (no third-party install) but needs Screen Recording permission. BlackHole is
   the current baseline. (§6.2)
2. **[NEEDS OWNER]** **Hybrid vs turbo-only streaming** — validate on-device whether
   WhisperKit's confirmed-token streaming with `large-v3-turbo` alone is fast enough,
   making the separate `tiny.en` interim model unnecessary. (§8.1)
3. **[NEEDS OWNER]** Crash-recovery journal — plaintext or Keychain-encrypted? (§9.3)
4. **[NEEDS OWNER]** Emit a `.json` sidecar alongside `.txt`? (§12.1)
5. **[NEEDS OWNER]** Bundled-model installer variant (zero-network, larger) vs download-on-first-run? (§8.4)
6. **[NEEDS OWNER]** Confirm minimum OS = macOS 14 (WhisperKit) and Apple Silicon only.

---

## 23. Future enhancements (out of scope)

Microphone / dual-source with source labels · speaker diarization · multiple languages ·
in-session transcript search · keyword highlighting · export to Markdown/JSON/SRT · AI
summaries / local LLM · global keyboard shortcuts · screen-share window exclusion · auto
updates · Windows/Linux/Intel support · re-transcription from saved raw audio.

---

## 24. Glossary

- **Endpointing** — detecting utterance end via a silence gap to trigger a final decode.
- **Interim / provisional caption** — low-latency, unstable text before finalization.
- **Final caption** — stabilized text committed to the transcript.
- **LocalAgreement-2** — commit only tokens agreed by the two most recent hypotheses; keep the tail provisional.
- **RTF** — decode_time / audio_duration; < 1 = faster than real time.
- **Encoder floor** — Whisper's fixed ~30 s-padded encoder cost per decode, independent of chunk length (~1.4 s Apple GPU, ~4.7 s CPU for large-v3-turbo).
- **Multi-Output Device** — macOS virtual device fanning system audio to speakers **and** BlackHole so the user hears the call while it's captured.
- **WhisperKit** — Swift/CoreML Whisper running on Apple Neural Engine + GPU, with streaming.
- **TCC** — macOS privacy permission system (microphone gate covers all audio input devices).
- **Journal** — append-only on-disk log of finalized segments for crash recovery.
