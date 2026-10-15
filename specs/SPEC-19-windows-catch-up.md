# SPEC-19 — Windows catch-up: Mac features added since the port paused

**Status:** ⬜ Not started · **Written:** 2026-10-15, against `main` at `ff009a4` ·
**Follows:** SPEC-16 (parity up to `7a466d0`) and SPEC-17 (bring-up on the G15)

## 0. Why this spec exists

The Windows port paused on 2026-10-04 (`7a466d0`, then `e390f8e` / `8988d6f`). Since then the Mac
app gained six user-visible features. Only one Windows change was made after that: the
base-prompt edit (`ff009a4`). This spec lists what Windows still lacks and how to build each
piece. SPEC-16 tracks the older gaps; this one tracks only the new ones.

| Mac commit | Feature | Windows today |
|---|---|---|
| `35d33d4` | Record session audio (call or mic) — SPEC-16 C13 | ❌ not started; Store skips `v5_audio_file` |
| `e78ecc6` | Hide app windows from screen sharing | ❌ none |
| `e6fc3d0` | Minimal view | ❌ none |
| `e6fc3d0` | Answer style + answer-format suffix | ❌ none (key dropped on write) |
| `e6fc3d0` | Code & Mermaid panels, Code & Diagram window | 🟡 fenced code renders as a plain read-only box; no language, no Copy/Maximize, no diagrams |
| `74fd775` | Interview UI pass (header transport, no card footer, live minimal view, diagram pan/zoom) | ❌ none |
| `74fd775` | Appearance: System / Light / Dark | ✅ exists as `ui.theme`, but the Mac stores it as `window.theme` (key mismatch) |
| `45e2946` | Captions listen to one output device | ✅ exists (endpoint mode + per-app capture); `audio.output_device` meaning differs (see §2) |
| `e6fc3d0` | Accent mode removed | — nothing to do (never ported) |
| `3e2e74a` | Delete session deletes every linked interview | ✅ already done (`Store.Interviews(sessionId)`) |

**Not in scope:** anything already in SPEC-16 / SPEC-17. Those come first: none of the
Interview screens have run on Windows yet (SPEC-17 W2–W7 ⬜).

---

## 1. Order of work

Each step is one commit to `main`, built and tested on the Mac (`EnableWindowsTargeting=true`)
before it goes in, like SPEC-16.

| Step | What | Testable on the Mac? | Size |
|---|---|---|---|
| **S1** | Shared formats: config keys, `v5_audio_file` migration, answer-format prompt + shared vectors | ✅ fully (Core tests) | S |
| **S2** | Answer style wired into Ask / Regenerate / typed + Settings → Interview picker | ✅ logic; picker 🟡 | S |
| **S3** | Hide from screen sharing | 🟡 compiles only | S |
| **S4** | Interview UI pass (header transport, compact header, no card footer, no Privacy section) | 🟡 compiles only | M |
| **S5** | Code panels + Mermaid (WebView2) + Code & Diagram window with pan/zoom | 🟡 parser ✅, view compiles only | L |
| **S6** | Minimal view (Caption only and Interview) | 🟡 `LiveQuestion` ✅, view compiles only | M |
| **S7** | Record session audio (call / mic → AAC `.m4a`) | 🟡 file/path rules ✅, capture compiles only | L |
| **S8** | Verify everything on the G15; add the checks to SPEC-17 | ❌ Windows only | M |

**Recommendation:** do S1–S2 now (they are fully testable here and stop Windows from dropping Mac
settings). Draft S3–S7 on the Mac only if the G15 bring-up is still blocked; otherwise build
them on the G15 after SPEC-17 W2 passes, so new UI isn't stacked on UI that has never been seen.

---

## 2. S1 — Shared formats

### 2.1 Config keys (`windows/src/LocalCaption.Core/Data/Config.cs`)

| Key | Mac default | Windows change |
|---|---|---|
| `audio.record_source` | `"off"` (`off` · `call` · `microphone`) | add; unknown value reads as `off` (as the Mac does) |
| `window.hide_from_screen_share` | `true` | add |
| `window.theme` | `"system"` (`system` · `light` · `dark`) | add. **Migration:** if `window.theme` is missing and `ui.theme` is set, copy it across on load; from then on read and write `window.theme` only. `ThemeManager`, the Settings picker and the `toggle_theme` shortcut switch to it. Stop writing `ui.theme`. |
| `interview.answer_style` | `"standard"` (`standard` · `easy_english` · `bullets` · `sentence_per_line`) | add; unknown → `standard` |
| `audio.output_device` | Mac: `""` = all sound, else a Core Audio UID | Windows already owns this key with a different ID scheme (MMDevice ID, `null` = default). Treat `""` as `null`, and an ID that matches no active device as the default device with a status note, never a capture failure. |

Keep alphabetical key order (SPEC-16 §7). Tests: extend `ConfigTests` for each default, each
unknown value, the `ui.theme` → `window.theme` move, and the `""` device.

### 2.2 Database: `v5_audio_file`

- Add `"v5_audio_file"` to `Store.MigrationIds` → `AddColumn("sessions", "audio_file", "TEXT")`.
- `SessionRecord.AudioFile` (nullable), read and written by `Store` and `SessionFiles`.
- Test: a Mac database at v5 opens, keeps `audio_file`, and Windows writes it back unchanged.

### 2.3 Answer-format prompt (`Core/Interview/InterviewPrompt.cs`)

Port `InterviewPrompt.withAnswerFormat` and `styleRule` (`app/Sources/LocalCaptionKit/InterviewPrompt.swift:116`)
word for word: the code/Mermaid fence rule always, plus the style rule unless Standard, appended
as `\n\nANSWER FORMAT: …`.

The Mac marks this "not in the shared golden vectors". **Change that:** add
`testdata/interview-prompt/answer-format.json` (one case per style, plus an Ask with images) and
assert it in both suites, so the two prompts cannot drift again (the `ba3bcbe` / `ff009a4`
pair shows they do).

---

## 3. S2 — Answer style

- `InterviewController`: wrap every Ask, Regenerate and typed message with
  `InterviewPrompt.WithAnswerFormat(message, config.Interview.AnswerStyle)`, reading the config at
  send time (Mac `InterviewController.swift:676`). Skill and preparation messages are not wrapped.
- Settings → Interview: **Answer style** picker (Standard · Easy English · Bullet points · One
  sentence per line) under Answer length; applies to the next answer.
- Tests in `LocalCaption.Interview.Tests`: the sent text carries the suffix for Ask / Regenerate
  / typed, not for skills; a style change between two asks is honoured.

---

## 4. S3 — Hide from screen sharing

- New `App/ScreenShareShield.cs`: `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` on
  every app window, `WDA_NONE` when off.
- Hook it once for all windows: `EventManager.RegisterClassHandler(typeof(Window), …)` on
  `SourceInitialized`, plus re-apply to open windows when the setting changes. Covers main,
  Settings, Sessions, dialogs, the Code & Diagram window and the screenshot overlay.
- **Windows 10 1809–1909** (before build 19041) has no `WDA_EXCLUDEFROMCAPTURE`. Use
  `WDA_MONITOR` (the window shows as black in captures) and say so under the toggle. Never fail.
- Settings → Window: **Hide from screen sharing** toggle (default on), live.
- Check on the G15: Teams/Zoom share, OBS, Win+Shift+S and the app's own F9 screenshot all
  leave the windows out; with it off they appear.

---

## 5. S4 — Interview UI pass (Mac `74fd775`)

In `MainWindow.Interview.cs`, `InterviewLayoutView`, `AnswerCard`, `InterviewSettingsPage`:

1. **Start / Pause / End in the header** in Interview mode (stage 2 only, not during
   preparation). The bottom transport bar stays for Caption only.
2. **Header falls back to icons when narrow** (each keeps its tooltip), so the 360-wide window
   (C11) still fits.
3. **Interview mode drops Auto-copy and Copy last N** from the quick bar.
4. **Answer cards drop the footer** (answer time, Copy, Regenerate). Only an error line shows.
   Regenerate stays in the controller; the Mac has no UI entry for it now either.
5. **Hide / Show captions** redraws at once (no layout pass lag).
6. **Settings → Interview loses the Privacy section** (the notice still shows once from the
   mode chooser).

---

## 6. S5 — Code and Mermaid panels

### 6.1 Parser (`LocalCaption.Interview/MarkdownBlocks.cs`)

`MarkdownBlock.Code(string Text)` becomes `Code(string Language, string Text, bool Closed)`:
fence tag lowercased, empty when untagged; `Closed` false while the answer is still streaming the
block. Unit tests: tagged, untagged, unclosed, `mermaid`, nested backticks.

### 6.2 Panel (`Answers/CodeBlockPanel.xaml`)

Replaces the plain code box in `MarkdownView`:

- Title bar: language name (or **Code**), **Diagram** for Mermaid; **Copy** (✓ for 1.5 s) and
  **Maximize**. Blue tint for code, purple for diagrams, from the theme dictionaries.
- Code: the existing read-only mono `TextBox` (selectable), font 0.9 × caption size.
- Mermaid: rendered only once `Closed`; until then, and if rendering fails, show the source with
  a small "diagram couldn't be drawn" line. A **Source / Diagram** toggle as on the Mac.

### 6.3 Mermaid rendering

- Add `Microsoft.Web.WebView2` to `LocalCaption.App`. Bundle the same `mermaid.min.js` 11.4.1 the
  Mac uses (link `app/Resources/mermaid.min.js` and its LICENSE from the csproj; no copy in the
  repo). No network: load a local HTML page, `securityLevel: 'strict'`, block navigation.
- One shared `CoreWebView2Environment`; user data folder under `AppPaths` (LocalAppData), never
  beside the exe.
- **Runtime missing** (some Windows 10 PCs): the panel shows source only with a note; the
  installer does not force WebView2. Decide in §10 whether `package.ps1` should bootstrap it.
- Cost: WebView2 is out-of-process; create the view only when a diagram block appears, and reuse it.

### 6.4 Code & Diagram window

New `CodeDiagramWindow` opened by Maximize: the block full-size, Copy, and for diagrams drag to
pan, Ctrl+wheel / touchpad pinch / + − buttons to zoom, double-click to fit (Mac `CodePanels.swift`).
Code uses a `ScrollViewer` with the font controls. Bounds remembered in `ui.code_window`.

---

## 7. S6 — Minimal view

- **Toggle:** a header button, `Ctrl+Shift+M` (new Windows-only key `shortcuts.toggle_minimal`),
  Esc to leave, and a ✕ button that appears when the pointer is over the window. Not persisted
  (the Mac resets it each launch). Disabled until a mode is chosen.
- **Shown only** when there are captions or answers: not on the mode chooser, the preparation
  stage or the finished-interview replay.
- **Hides** the custom title bar (`ChromeWindow`), header, sidebar, quick bar, transport bar and
  status strip. The window keeps its size; drag anywhere empty to move it.
- **Caption only:** the caption view alone.
- **Interview:** a single column: the questions and answers, the **live question** at the
  bottom while recording, then the coach box, hotkey hints and screenshot thumbnails.
  - Port `liveQuestion()` (Mac `InterviewController.swift:681`) to the Windows controller:
    `AskSelection.Select(..., mode: SinceLastAsk, mark, ...)` **without moving the mark**. On Ask,
    the live question becomes that turn's question. Unit-test that it never moves the mark.
  - Refresh it from the existing caption-change throttle, not per interim word.
- F8/F9 keep working in minimal view.

---

## 8. S7 — Record session audio (SPEC-16 C13)

Mac reference: `app/Sources/LocalCaption/Audio/SessionAudioRecorder.swift` and specs/STATUS.md
§Audio recording. Same behaviour, Windows plumbing:

| | Mac | Windows |
|---|---|---|
| Call audio | ScreenCaptureKit stream at 48 kHz before downmix | tap the WASAPI loopback buffer (process or endpoint) in `StreamingOrchestrator` **before** `AudioNormalizer` downmixes to 16 kHz, on its own queue |
| Microphone | `AVAudioRecorder`, default input, 48 kHz mono | `WasapiCapture` on the default capture endpoint, 48 kHz mono |
| Encoder | AAC 64 kbps per channel, `.m4a` | Media Foundation `IMFSinkWriter` → AAC in MP4 (`.m4a`), same bitrate |
| Pause | file only grows while capturing | same: stop feeding samples while paused, so timestamps match the transcript |
| On Stop | move beside the `.txt`, same base name, `sessions.audio_file` | same |
| Quit mid-session | file closed; Recover & Save attaches it | same (finalise the sink writer in the shutdown path) |
| Crash | header-less file deleted by recovery | same |
| Errors | under *Session issues*, captions never stop | same; add a recording badge in the header |

Windows-specific risks:

- **Microphone privacy switch** (Settings → Privacy → Microphone → desktop apps) off →
  `E_ACCESSDENIED`; report it with a link to `ms-settings:privacy-microphone`.
- **Windows N editions** have no Media Foundation without the Media Feature Pack. Report it under
  Session issues and record nothing. Don't fall back to WAV (about 1 GB per hour).
- Loopback formats vary (float32, 44.1/48 kHz, more than 2 channels). Resample/downmix to 48 kHz stereo
  for the encoder.

UI: Settings → General → **Record audio** (Off · Call audio · My microphone). Sessions window
and sidebar context menu: **Play audio** (default player, `ShellExecute`) and **Show audio in
folder**. Deleting a session's files deletes the audio too (Recycle Bin, as the transcript).

Tests (Mac-runnable): file naming and move rules in `SessionFiles`, `audio_file` round trip,
recovery attach/delete decisions, pause-gap accounting on a fake sample source.

---

## 9. S8 — Verification on the G15

Append to SPEC-17 W3/W5 and `docs/WINDOWS-STATUS.md`:

1. Config: a Mac `config.json` with all four new keys loads; Windows writes them back; old
   `ui.theme` moves to `window.theme` once.
2. Answer style: each style visibly changes the next answer; a fenced code block and a Mermaid
   block render in panels; Copy and Maximize work; pan/zoom/fit in the window.
3. Mermaid with WebView2 removed (or a Windows 10 VM without it): source shown, no crash.
4. Screen share: Teams, Zoom, OBS, Snipping Tool exclude every app window; Windows 10 1809 VM
   shows black boxes instead, with the note in Settings.
5. Minimal view in both modes; Esc/✕/Ctrl+Shift+M; F8 during minimal view; live question
   becomes the asked question.
6. Audio: 10 minutes with a pause, call audio then mic; the `.m4a` plays and lines up with the
   caption timestamps; mic privacy off reports cleanly; Close mid-session → recovery attaches the
   file; kill the process → recovery deletes it; ARM64 build records.
7. Interview UI pass: header transport at 360 px wide; no card footer; no Privacy section.

---

## 10. Decisions for the owner

| # | Question | Proposed default |
|---|---|---|
| D1 | Move Windows' theme from `ui.theme` to the Mac's `window.theme`? | Yes, one-time copy on load |
| D2 | Accept WebView2 as a dependency for Mermaid? Bootstrap it in the installer? | Accept; don't bootstrap; source-only fallback |
| D3 | Pre-2004 Windows 10: black box (`WDA_MONITOR`) or no protection? | Black box, with a note |
| D4 | Minimal-view shortcut | `Ctrl+Shift+M`, user-changeable |
| D5 | Windows N without Media Feature Pack: no recording, or WAV fallback? | No recording, clear message |
| D6 | Build S3–S7 now on the Mac, or after SPEC-17 W2 on the G15? | S1–S2 now; the rest after W2 |
| D7 | Make the answer-format suffix a shared golden vector (changes a Mac test)? | Yes |

## 11. Done when

- S1–S7 committed with their tests green on the Mac and the G15.
- §9 checked on the G15 and recorded in SPEC-17's fault log.
- SPEC-16 C13 marked ✅; `docs/WINDOWS-STATUS.md` lists this spec's items with no 🟡 left.
