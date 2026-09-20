# Local Caption for Windows — Port Plan & Technical Specification

- **Version:** 1.3 (plan spec — nothing built yet)
- **Status:** Draft for review · **W1 + W5 closed** · remote-over-Jump-Desktop confirmed as
  a supported configuration (§4.7)
- **Target:** **ASUS ROG Zephyrus G15 (GA503QR)** — Ryzen 9 5900HS · RTX 3070 Laptop 8 GB ·
  32 GB RAM · Windows 11 Pro build 26200 · x64 (§0.5)
- **Source of truth for behaviour:** [`SPEC.md`](SPEC.md) (macOS v2.0) + [`specs/STATUS.md`](specs/STATUS.md) (what actually shipped)
- **Date:** 2026-09-20

> **Decisions already taken by the owner**
> 1. **Separate native Windows app.** Not a cross-platform rewrite. The macOS app in
>    `app/` is untouched. What is shared is the *specification, file formats, config
>    schema and test vectors* — not code.
> 2. **Full feature parity except the Live AI Summary** (SPEC-10 / "Key points"). That
>    panel is explicitly out of scope for Windows v1; the config key stays reserved so the
>    two platforms' `config.json` files remain interchangeable (§9.2).
>
> Decisions I made are tagged **[DECISION]**. Decisions that still need the owner are
> tagged **[NEEDS OWNER]** and collected in §18. **Do not start Phase 1 until W1 is closed.**

---

## 0. How to read this document

- §0.5 is the **target machine** and every decision its profile settled. W1 is closed.
- §1 says what is being ported and what "parity" means concretely.
- §2 is the **stack decision** with the alternatives I rejected and why.
- §3–§12 are the corrected, Windows-specific spec, layer by layer. Each section opens with
  the macOS mechanism it replaces, so it can be read next to `SPEC.md`.
- §13–§17 are budgets, testing, structure, phases and risks.
- §18 is the blocker list. **W1 gates everything.**

---

## 0.5 Target machine (W1 — CLOSED)

| | |
|---|---|
| Model | ASUS ROG Zephyrus G15 **GA503QR**, x64 |
| OS | Windows 11 Pro, build **26200** |
| CPU | **AMD Ryzen 9 5900HS**, 8 cores / 16 threads, Zen 3, AVX2 |
| dGPU | **NVIDIA GeForce RTX 3070 Laptop, 8 GB** — driver 555.97, **CUDA 12.5** |
| iGPU | AMD Radeon (Cezanne), hybrid/MUX with the dGPU |
| NPU | none (Zen 3 has no NPU) |
| RAM | 32 GB |
| Disk free | ~193 GB on C: |
| .NET | **runtime 10.0.12** (`Microsoft.NETCore.App` + `WindowsDesktop.App`) present; **no SDK** |
| Audio endpoints | Realtek HD Audio · AMD HD Audio (display) · NVIDIA HD Audio (HDMI) · **Jump Desktop Virtual Speaker** |

**What this profile decided:**

1. **CUDA is the backend.** An RTX 3070 with 8 GB is comfortably the strongest target this
   app has ever run on — stronger, for Whisper, than the Apple Silicon Mac it is ported
   from. Vulkan, OpenVINO and ARM64 are all off the table (§5.2).
2. **`large-v3-turbo` becomes the default final model, unquantized.** The macOS build
   settled on `small.en` because turbo cost ~3.5 s there. That constraint is gone; 8 GB of
   VRAM holds the f16 weights with room to spare, so Windows should ship **more accurate**
   transcripts than the Mac (§5.3).
3. **Turbo-only streaming is worth re-testing.** `SPEC.md` §22.2 left this open and the Mac
   answered "no, use a hybrid" on the hardware it had. This GPU may answer differently —
   Phase 0 must check (§5.4).
4. **.NET 10 LTS**, not .NET 8 — the runtime is already installed. Only the SDK needs
   adding on the dev machine (§2.1).
5. **Capture architecture changed.** Four render endpoints, one of them a remote-desktop
   virtual device that appears and disappears — and the owner has confirmed interviews will
   be taken **over Jump Desktop**. **Process loopback is now the default capture mode**
   (§4.1, §4.5), with endpoint loopback as the fallback and an endpoint picker for it
   (§4.6). Remote operation is a supported configuration with its own requirements (§4.7).
6. **ROG-specific power behaviour must be handled** — the MUX switch, Eco mode and
   Armoury Crate profiles can remove the dGPU at runtime (§5.8, §13).
7. **Sleep prevention and a live level meter are now requirements**, not polish — the
   machine is operated unattended over a remote session (§4.7.5–6).

---

## 1. Scope

### 1.1 What the Windows app must do

The same product: a native desktop app that shows **live captions of the audio you hear on
a call**, fully on-device, and auto-saves the transcript.

Feature-for-feature parity with the shipped macOS app (`specs/STATUS.md` phases 1–5):

| # | Capability | macOS status | Windows v1 |
|---|---|---|---|
| 1 | System/speaker audio capture, no microphone | ✅ ScreenCaptureKit | ✅ WASAPI loopback (§4) |
| 2 | Dual-model streaming ASR (fast partials + accurate finals) | ✅ WhisperKit | ✅ whisper.cpp (§5) |
| 3 | LocalAgreement-2 / RollingCaption stabilisation | ✅ | ✅ 1:1 logic port (§6) |
| 4 | VAD endpointing, hallucination + metadata filters | ✅ | ✅ 1:1 logic port (§6) |
| 5 | Session state machine, pause/resume, sample-based clock | ✅ | ✅ |
| 6 | Transcript `.txt` + `.json` sidecar (identical format) | ✅ | ✅ (§9.1) |
| 7 | Append-only crash-recovery journal + recover-on-launch | ✅ | ✅ (§9.4) |
| 8 | SQLite session metadata, list / open / rename / delete / search / sort | ✅ | ✅ (§9.3) |
| 9 | Versioned `config.json`, corrupt→backup+repair, atomic write | ✅ | ✅ (§9.2) |
| 10 | Live caption view: incremental, selectable, auto-scroll, jump-to-latest | ✅ | ✅ (§7.2) |
| 11 | Always-on-top, opacity 0.3–1.0 | ✅ | ⛔ **cut** — pointless over remote desktop (§7.3) |
| 11b | Window size/position memory | ✅ | ✅ (§7.3) |
| 12 | Clipboard: manual "Copy last N", auto-update at endpoints, write-only | ✅ | ✅ (§7.4) |
| 13 | Full Settings, persisted, live where safe | ✅ | ✅ |
| 14 | **Live AI Summary ("Key points")** | ✅ | ⛔ **out of scope** (§1.3) |
| 15 | Signed, warning-free install | ⛔ blocked (B2) | 🟡 see §11 |

### 1.2 Privacy invariants (unchanged, non-negotiable)

- No audio, transcript or metadata leaves the device at runtime. No telemetry.
- The **only** lifetime network use is the one-time speech-model download.
- Raw audio is discarded after inference and never persisted.
- The clipboard is **written, never read**.

These are acceptance criteria, verified with a network monitor (§17.13).

### 1.3 Explicit non-goals for Windows v1

- **Live AI Summary panel** (owner decision). See §16.7 for how it would be added later —
  the Windows path is a bundled `llama-server.exe` speaking the same OpenAI-compatible
  API the macOS `MLXServerEngine` already uses, so the prompt, parser (`SummaryCard`) and
  UI port over unchanged when it's wanted.
- Microphone / local-voice capture.
- Speaker diarization.
- Languages other than English.
- Cloud sync, accounts, auto-update telemetry.
- Windows 10, Intel-Mac-style universal builds, Linux.
- Saving or replaying raw audio.

---

## 2. Stack decision

### 2.1 [DECISION] .NET 8 + WPF, C#

| Layer | Choice |
|---|---|
| Runtime | **.NET 10 LTS**, self-contained, ReadyToRun (runtime 10.0.12 already on the target; SDK needed on the dev box) |
| UI | **WPF** (`net10.0-windows`) |
| Caption text control | **AvalonEdit** (virtualised, read-only) — fallback `RichTextBox` (§7.2) |
| Audio | **WASAPI loopback** via `NAudio` + targeted P/Invoke (§4) |
| ASR | **whisper.cpp** via **Whisper.net** + a runtime backend chosen at install/first-run (§5) |
| Database | **Microsoft.Data.Sqlite** (WAL) |
| JSON | `System.Text.Json` |
| Tests | xUnit |
| Installer | **Velopack** (or Inno Setup) — §11 |

### 2.2 Why

- **Closest structural match to the existing app.** Swift/SwiftUI → C#/WPF maps almost
  one-to-one: `ObservableObject`/`@Published` → `INotifyPropertyChanged`, `actor` →
  `Channel<T>` + a single-threaded consumer, `@MainActor` → `Dispatcher` affinity,
  `Task`/`async let` → `Task`/`async`. The 1,500-line pure-logic layer (§6) ports
  mechanically, which is where most of the app's hard-won correctness lives.
- **whisper.cpp is the only Whisper runtime that covers every plausible ASUS**: CUDA for
  NVIDIA, Vulkan for AMD/Intel iGPU, OpenVINO for Core Ultra NPU, plain AVX2 for CPU,
  ARM64 for a Snapdragon machine — selected at runtime without changing app code.
- **WPF over WinUI 3:** WPF has a decade of stable behaviour for the two things this app
  actually needs from the window manager — plain, well-behaved top-level windows with
  reliable multi-monitor frame restoration. WinUI 3 carries packaging constraints for no
  upside here. (The overlay/opacity interop that argued for WPF in v1.2 is now cut, §7.3 —
  WPF remains the choice on maturity and on AvalonEdit.)
- **Self-contained publish** means the user never installs a .NET runtime.

### 2.3 Alternatives considered and rejected

| Option | Why not |
|---|---|
| **Electron / Tauri + web UI** | The hard parts here are audio capture, a sample-accurate clock, and two resident ML contexts — all of which end up in a native sidecar anyway. You'd pay the IPC and bundling cost for a UI that is a list, a text pane and a settings form. |
| **Python + PySide6 + faster-whisper** | Fastest to a prototype and genuinely excellent on NVIDIA (CTranslate2). Rejected for the same reason v1 of the macOS plan was: packaging a 2 GB PyInstaller bundle with CUDA DLLs, and an interpreter that antivirus and SmartScreen both dislike. Keep it as the **Phase 0 benchmark harness** only (§5.4). |
| **C++/Qt** | Maximum control, ~3× the implementation time, no reuse from the Swift app. |
| **Avalonia (one UI for both platforms)** | The owner ruled out convergence. Also would mean re-testing the already-tuned macOS UI. |
| **Windows built-in `SpeechRecognizer` / Azure Speech** | Either obsolete and inaccurate, or cloud — breaks the core privacy promise. |
| **ONNX Runtime + DirectML running Whisper** | Works, but Whisper-on-ORT has weaker streaming ergonomics than whisper.cpp and no equivalent of its token-timestamp/DTW word alignment, which `RollingCaption` depends on. Kept as a fallback (§5.5). |

---

## 3. Platform mapping (quick reference)

| macOS (shipped) | Windows 11 equivalent | Risk |
|---|---|---|
| `ScreenCaptureKit` `SCStream` audio | **WASAPI process loopback** (default) / endpoint loopback (fallback) | Low–Medium — see the silence-gap trap (§4.3) and remote operation (§4.7) |
| Screen Recording TCC permission | **none required** | ✅ *simpler than macOS* |
| `excludesCurrentProcessAudio` | Process loopback — now the **primary** capture mode, targeting the meeting app (§4.5) | Medium — async activation, unvalidated on this box (W9) |
| `AVAudioConverter` 48k stereo → 16k mono F32 | WDL resampler + manual downmix (§4.4) | Low |
| WhisperKit (CoreML, ANE+GPU) | whisper.cpp **CUDA** via Whisper.net (§5) | Low — RTX 3070 has large headroom (§13) |
| `avgLogprob` / `noSpeechProb` / `compressionRatio` from WhisperKit | `no_speech_prob` native; avg-logprob derived from token probs; **compression ratio computed in-app** (§5.6) | Medium |
| WhisperKit `wordTimestamps` | whisper.cpp token timestamps + DTW alignment heads (§5.7) | Medium |
| `~/Library/Application Support/LocalCaption` | `%LOCALAPPDATA%\LocalCaption` (§9) | Low |
| GRDB | Microsoft.Data.Sqlite, same DDL (§9.3) | Low |
| `FileHandle.synchronize()` | `FileStream.Flush(flushToDisk: true)` (§9.4) | Low — easy to get wrong |
| `NSPasteboard` | `Clipboard.SetText` + retry on `COMException` (§7.4) | **Medium** — remote clipboard sync makes contention routine (§4.7.4) |
| `NSWindow.level = .floating` | — | **cut** (§7.3) |
| `NSWindow.alphaValue` | — | **cut** (§7.3) |
| `setFrameAutosaveName` | Manual frame persistence + monitor validation (§7.3) | Low |
| `NSTextView` suffix-only replace | AvalonEdit document append (§7.2) | Medium — 3 h sessions |
| codesign + notarize | Authenticode + SmartScreen reputation (§11) | Medium — cost/policy |
| `mlx_lm.server` (summary) | *(deferred)* `llama-server.exe`, same HTTP contract | n/a |

---

## 4. Audio capture — WASAPI loopback

**Replaces:** `app/Sources/LocalCaption/Audio/SystemAudioCapture.swift`.

### 4.1 Approach — two modes, process loopback first

**[DECISION, revised]** Ship **both** capture modes in v1, with **process loopback as the
default**:

| Mode | What it taps | When |
|---|---|---|
| **A. Process loopback** (default) | the meeting app's own render stream, before endpoint mixing | normal operation — pick Zoom/Teams/Chrome once |
| **B. Endpoint loopback** (fallback) | everything rendered to one output endpoint | when no process is selected, or process loopback fails |

v1.0 of this document had mode B as the only v1 path and left mode A as a "nice to have"
(old §4.5). **The owner's confirmation that interviews will be taken over Jump Desktop
inverts that**: in a remote session the endpoint routing is precisely the fragile part
(§4.7), and process loopback is *immune* to it — it captures what a process plays,
regardless of which device that audio ends up on, whether the default endpoint flips
mid-call, or whether a virtual driver behaves oddly under loopback.

It also fixes a real transcript-quality problem for free: Spotify, Slack pings and browser
tabs never enter the transcript.

Mode B remains implemented because it is the simpler, better-understood path and the
safety net if mode A misbehaves on this machine (W9).

**Mode B — endpoint loopback:** capture the audio being rendered to the selected (or
default) output endpoint:

```
IMMDeviceEnumerator.GetDefaultAudioEndpoint(eRender, eConsole)
  → IAudioClient.Initialize(SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK | EVENTCALLBACK, ...)
  → IAudioCaptureClient.GetBuffer()  →  float32 frames at the device mix format
```

This is a strict improvement over macOS: **no permission prompt, no virtual audio cable,
no Multi-Output Device, no setup instructions**. The whole "BlackHole / Screen Recording
permission" chapter of `SPEC.md` (§6.2, §17.2, C3, C4) simply disappears. The entire
"permission denied" UI state is deleted.

- Run the capture callback on a thread registered with MMCSS
  (`AvSetMmThreadCharacteristics("Pro Audio")`) so it is not descheduled under load.
- Use event-driven mode (`AUDCLNT_STREAMFLAGS_EVENTCALLBACK` + `SetEventHandle`), not a
  polling loop.
- Hand samples to a port of `CaptureBuffer` — the same bounded 5-second buffer with a
  latched overflow and exact lost-sample count. Behaviour on overflow is unchanged:
  surface the count, pause capture, finish the retained audio.

### 4.2 Device changes and failures

Implement `IMMNotificationClient`:
- `OnDefaultDeviceChanged(eRender, eConsole)` → tear down and restart the loopback client
  against the new endpoint, preserving the session and the sample clock.
- `OnDeviceStateChanged` → same.
- Any `AUDCLNT_E_DEVICE_INVALIDATED` from `GetBuffer` → same path, bounded retry every 2 s.

This maps onto the existing `onError` → `onCaptureMustPause` → `SessionController.pause()`
recovery, which already exists in the macOS design and must be preserved.

### 4.3 ⚠ The silence gap — the one real trap

**WASAPI loopback does not reliably deliver packets while nothing is playing.** On many
drivers the capture endpoint simply produces no data during silence. The macOS
`ScreenCaptureKit` stream, by contrast, delivers continuous buffers including silence.

This matters more than it looks, because the whole app derives time from sample count:
`SpeechSegmenter.totalSamples` drives the elapsed clock, the endpoint detector, the
`t_start_ms`/`t_end_ms` of every transcript segment, and `duration_seconds`. If silence
produces no samples, **the clock stops during pauses in speech** and every timestamp after
the first silence is wrong.

**[DECISION] Mitigate with both of:**

1. **Silent-render keepalive.** Open a second `IAudioClient` on the same endpoint in
   *render* mode and write zero-valued frames. This keeps the audio engine pumping, so
   loopback keeps producing buffers. Cost: negligible; it renders silence.
2. **Device-position padding (the correctness guarantee).** `IAudioCaptureClient.GetBuffer`
   returns `u64DevicePosition` and sets `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY`. On each
   packet, compare the device position to the expected next position and **insert exactly
   that many zero samples** before appending the packet. The sample clock then stays true
   even if the keepalive fails or the engine stalls.

Also honour `AUDCLNT_BUFFERFLAGS_SILENT` — the buffer contents are undefined when it is
set; write zeros, do not copy.

**Applies to both capture modes, but differently.** Mode A (process loopback) is less prone
to the gap — a process that renders silence is still rendering — so mitigation 2 alone is
likely enough there. Mitigation 1 is a **mode-B-only** measure, and should be *skipped when
the target endpoint is a remote-desktop virtual device*: pushing keepalive silence into the
Jump Desktop speaker streams it over the network for no benefit. Gate it on the endpoint's
form factor / driver, and rely on mitigation 2 otherwise.

**Acceptance for this section:** play a 60-second clip with a 20-second silent gap in the
middle; the resulting segment timestamps must match wall-clock within ±100 ms.

### 4.4 Format normalisation

The mix format is whatever the endpoint reports — commonly 48 kHz / 2 ch / 32-bit float,
but **44.1 kHz and 6-channel are both realistic** on a laptop with an HDMI monitor or
Realtek surround drivers.

- Parse `WAVEFORMATEXTENSIBLE`; accept `KSDATAFORMAT_SUBTYPE_IEEE_FLOAT` and
  `KSDATAFORMAT_SUBTYPE_PCM` (16/24/32-bit) and normalise to float32.
- **Downmix** N channels → mono by averaging (not summing — avoid clipping).
- **Resample** to 16 kHz with `WdlResamplingSampleProvider` (handles non-integer ratios
  such as 44.1 k → 16 k; a naive decimate-by-3 would alias and cost WER).
- Output: `float[]` at 16 kHz mono — byte-identical contract to the macOS capture layer,
  so §6's logic port needs no changes.

### 4.5 Mode A — process loopback (the v1 default)

Build 26200 supports it comfortably (the API needs 20348+).

```
AUDIOCLIENT_ACTIVATION_PARAMS {
  ActivationType = ProcessLoopback,
  ProcessLoopbackParams = {
    TargetProcessId  = <meeting app PID>,
    ProcessLoopbackMode = INCLUDE_TARGET_PROCESS_TREE
  }
}
ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, IID_IAudioClient, params, handler)
  → IAudioClient.Initialize(SHARED, LOOPBACK | EVENTCALLBACK, ...)   // format must be supplied, not negotiated
  → IAudioCaptureClient.GetBuffer()
```

Implementation notes that will otherwise cost a day each:

- Activation is **asynchronous via a completion handler** — there is no
  `GetDefaultAudioEndpoint` equivalent. Wrap it in a `TaskCompletionSource`.
- There is **no mix format to query**. You *specify* the format in `Initialize`; request
  48 kHz / 2 ch / float32 and normalise as in §4.4.
- `INCLUDE_TARGET_PROCESS_TREE` matters for browsers: Google Meet's audio lives in a Chrome
  **audio-service child process**, not the PID you clicked. Target the main browser PID and
  let the tree inclusion catch the child.
- If the target process exits (the user restarts Teams), the stream dies. Treat it as a
  capture failure → auto-pause → offer re-selection, reusing `onCaptureMustPause`. Do not
  silently keep recording nothing.
- Silence behaves *better* here than on endpoint loopback: a process that renders silence
  still renders. The §4.3 device-position padding stays in place regardless.

**UI:** Settings → Audio gains a **source picker** listing processes that currently have an
audio session (`IAudioSessionManager2.GetSessionEnumerator` gives running sessions with
friendly names and icons — filter to those with a render stream). Persist as
`audio.capture_mode` + `audio.target_process` (§9.2). Remember by **executable name**, not
PID, and re-resolve at Start.

### 4.6 [DECISION] Output-endpoint picker — for mode B

The macOS app has no device selector because ScreenCaptureKit needs none. This laptop has
**four render endpoints**: Realtek (speakers/jack), AMD display audio, NVIDIA HDMI audio,
and a **Jump Desktop Virtual Speaker**.

In mode B, loopback follows the **default** endpoint. Plug in an HDMI monitor, pair
Bluetooth headphones, or connect a Jump Desktop session, and the default moves — mid-call.
§4.2's `OnDefaultDeviceChanged` handling is therefore not a rare edge case here; it is
routine, and must be tested as such.

**Settings → Audio gains an output-endpoint picker** with "Follow system default" as the
default. Persist as `audio.output_device` (endpoint ID, `null` = follow default; §9.2).

### 4.7 ⚠ Remote operation over Jump Desktop — confirmed use case

The owner has confirmed interviews will be taken **while connected to the G15 over Jump
Desktop**. The meeting app, the caption app and the ASR all run on the G15; the user
watches and listens from a remote client. This is now a first-class supported
configuration, not an accident of the device list, and it has five consequences.

**1. The default render endpoint flips twice per session.** Jump Desktop's virtual speaker
typically becomes the default while connected and reverts on disconnect — so connecting or
disconnecting *mid-recording* invalidates a mode-B stream. Mode A (§4.5) is immune, which
is the main reason it is now the default. Mode B must survive it via §4.2; test connect and
disconnect during an active recording.

**2. The meeting app must not be pinned to a specific device.** If Zoom is configured to
output to "Realtek Speakers" rather than "Default", then while remote the audio goes to
Realtek — the user hears nothing *and* mode B captures nothing from the virtual speaker.
Mode A sidesteps this entirely. For mode B, the in-app level meter (point 5) is what makes
the misconfiguration visible in seconds rather than after the interview.

**3. Virtual-driver loopback is unvalidated (W9).** Jump Desktop's speaker is a software
driver, not hardware. Loopback on it may fail to initialise, report an unexpected mix
format, or stall its device position. **Phase 0 must include a 20-line loopback probe run
against that endpoint while a Jump Desktop session is live**, checking: `Initialize`
succeeds · reported mix format · packets arrive during silence · `u64DevicePosition`
advances monotonically. This is cheap now and expensive in Phase 2.

**4. ⚠ Clipboard becomes hazardous — keep auto-update OFF.** "Copy last N" writes to the
**G15's** clipboard. Jump Desktop syncs clipboards, so it usually reaches the user's local
machine, but:

- `clipboard.auto_update` writes on *every speech endpoint* — every few seconds. Over a
  synced clipboard that continuously overwrites whatever the user copied locally. Copy an
  email address on your own machine, and three seconds later it's a caption fragment.
- Remote clipboard monitors hold the clipboard open, making the `COMException` in §7.4
  **likely rather than theoretical**. Raise the retry budget to **10 attempts over ~500 ms**
  and never let it throw into the session.
- Sync is event-driven and coalesces; rapid successive writes may not all propagate, so the
  clipboard is not a reliable transport here.

**[DECISION]** `clipboard.auto_update` stays **off by default** (as on macOS) and Settings
carries an explicit note about remote sessions. Manual "Copy last N" remains the primary
path. The `.txt`/`.json` transcript on the G15 is the durable artifact — recommend a synced
folder or a share for it rather than relying on clipboard round-trips.

**5. Add a live audio level meter + the active source name to the Active Session header.**
The single worst outcome of this whole setup is recording 40 minutes of silence because the
wrong process or endpoint was captured. A 20px VU bar fed from `SpeechSegmenter.rms` — it
is already computed per frame — plus the source name makes that impossible to miss. Cheap,
and the highest-value UI addition in the port.

**6. Prevent sleep while recording.** The user will not be touching the G15's keyboard.
Call `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED)`
on Start and clear it on Stop. A machine that sleeps at minute 30 of a 90-minute interview
is the worst failure mode this app has; this is three lines of P/Invoke. (Do **not** set
`ES_DISPLAY_REQUIRED` — the screen may sleep harmlessly.)

**Also worth knowing (not blocking):** Jump Desktop encodes the G15's screen, likely with
NVENC. NVENC is separate silicon from the CUDA cores, so it does not contend with Whisper
decoding — but caption text is small, and remote video compression is unkind to small text.
Recommend raising `caption.font_size` for remote use and enabling Jump Desktop's
highest-quality/lossless-text setting. This is also why the always-on-top overlay and
opacity features are cut (§7.3): translucent text over re-encoded video reads badly.

---

## 5. Speech recognition

**Replaces:** `app/Sources/LocalCaption/ASR/WhisperEngine.swift`.

### 5.1 Engine

**whisper.cpp**, driven from C# through **Whisper.net**. Two `WhisperFactory` instances
loaded once and kept resident — one interim, one final — exactly as the macOS engine does,
and with the same hard rule that came out of the `e5ec3bd` fix:

> **The two decode lanes must never share mutable decoder state, including when the same
> model is selected for both roles.** `whisper_context` is not thread-safe. One processor
> per lane, one decode in flight per processor, ever.

### 5.2 Backend selection

W1 collapses this to two backends. Vulkan, OpenVINO and ARM64 are **not shipped** in v1.

| Backend | Role | Package |
|---|---|---|
| **CUDA 12** | primary — RTX 3070, driver 555.97 already supports CUDA 12.5 | `Whisper.net.Runtime.Cuda` |
| **CPU (AVX2)** | fallback — Zen 3, 8 cores; used when the dGPU is unavailable (§5.8) | `Whisper.net.Runtime` |

**[DECISION]** **Bundle both in the installer.** The earlier plan fetched CUDA on demand to
keep the download small for machines that couldn't use it; since the target is a known
CUDA machine with 193 GB free, that indirection buys nothing and costs a first-run failure
mode. Installer grows by the CUDA runtime DLLs (`cudart`, `cublas`, `cublasLt` — a few
hundred MB); acceptable.

Probe at startup with `cudaGetDeviceCount` (or Whisper.net's own capability check), record
the result in the log, and surface the active backend in Settings so a silent fall back to
CPU is visible rather than merely felt.

`asr.backend` stays in config as `auto | cuda | cpu` so the CPU path can be forced for A/B
testing without rebuilding.

### 5.3 Models

whisper.cpp GGML/GGUF weights from `ggerganov/whisper.cpp` on Hugging Face, downloaded
once into `%LOCALAPPDATA%\LocalCaption\models\` — the same "network only for the one-time
model download" rule, and the same download-progress UI.

| Role | Model | File | Size | Note |
|---|---|---|---|---|
| Interim (default) | `tiny.en` | `ggml-tiny.en.bin` | ~75 MB | |
| Interim (alt) | `base.en` | `ggml-base.en.bin` | ~142 MB | try if tiny.en's WER annoys |
| **Final (default)** | **`large-v3-turbo`** | `ggml-large-v3-turbo.bin` | ~1.6 GB | **f16, not quantized** |
| Final (light) | `small.en` | `ggml-small.en.bin` | ~466 MB | CPU-fallback default (§5.8) |

**[DECISION] The Windows default final model is `large-v3-turbo` at f16, not `small.en`.**
The Mac chose `small.en` only because turbo cost ~3.5 s there (`specs/STATUS.md`). With
8 GB of VRAM there is no reason to quantize and no reason to settle for the smaller model:
turbo + tiny.en together occupy well under 3 GB, leaving headroom for a meeting app also
using the GPU. Confirm the latency in Phase 0 and fall back to `small.en` if it disappoints.

Keep the **config-friendly names identical to macOS** (`tiny.en`, `small.en`,
`large-v3-turbo`) so `config.json` stays portable; map name → filename in a table that is
the direct analogue of `WhisperEngine.variant(for:)`.

Prewarm after load by decoding 2 s of silence — the macOS engine does this because loading
weights does not warm the first prediction, and the same is true of whisper.cpp's first
GPU kernel compile. **Do not skip this**; it is the difference between a clean first
caption and a 3-second stall.

### 5.4 Phase 0: benchmark before you build ⭐

The macOS architecture was not guessed — it came from `spike/RESULTS.md` measuring real
decode times, which killed the original CPU plan outright. **Do the same on the ASUS
before writing app code.**

W1 means Phase 0 is no longer a survival question — CUDA on a 3070 will clear the budgets.
It is now a **tuning and configuration** question, and it has to answer three things:

1. **Is `large-v3-turbo` fast enough to be the default final model?** (§5.3 assumes yes.)
2. **Is turbo-only streaming viable — one model for both lanes?** `SPEC.md` §22.2 / B4 left
   this open; the Mac said no on its hardware. If turbo decodes a 6 s window in well under
   500 ms here, the whole dual-model architecture could collapse to one resident model,
   deleting the interim/final model split, the two-lane isolation rule and a class of bugs
   with it. **Measure before committing to the hybrid.**
3. **What does GPU contention cost?** The two lanes decode concurrently on one GPU while a
   meeting app may also be using it. Benchmark them *together*, not just in isolation —
   isolated numbers will flatter the design.

Deliverable: a console app, `LocalCaption.Bench`, that takes a 16 kHz mono WAV and prints:

```
backend    model              load_ms  decode_ms(p50/p90)  RTF   text
cuda       tiny.en                420        88 /   131   0.02  <...>
cuda       small.en               910       260 /   340   0.05  <...>
cuda       large-v3-turbo        1840       510 /   690   0.10  <...>
cpu        tiny.en                180       410 /   520   0.08  <...>
...
```

Run the full backend × model matrix. The two gates, taken from the shipped macOS design:

- **Interim budget: < 500 ms p90** (the partial cadence), *measured under concurrent final
  decoding*. If nothing meets it, see §5.5.
- **Final budget: < ~2 s p90** perceived (600 ms endpoint + decode). On this GPU expect to
  beat it comfortably — which is why question 2 above matters.

The output of this step **is** the answer to "which models are the Windows defaults", and
it replaces every latency number in §13 with a measured one. Commit it as
`windows/BENCH-RESULTS.md`, mirroring `spike/RESULTS.md`.

A throwaway Python `faster-whisper` script is a legitimate way to get a second data point
here — it is not shipped.

### 5.5 Contingency if the interim budget is missed (CPU-only machines)

**W1 makes this unlikely on the GPU path** — it now applies only to the CPU fallback
(§5.8), i.e. when the dGPU has been switched off.

Whisper always pays for its 30-second padded mel encoder, so a small chunk costs nearly as
much as a big one — the "encoder floor" documented in `SPEC.md` §1A. On the Ryzen 5900HS
with the dGPU disabled, `tiny.en` should land around 200–350 ms — workable — while
`small.en` finals land around 1.5–3 s and turbo is unusable.

**Fallback [NEEDS OWNER if triggered]:** replace *only the interim track* with a true
streaming transducer — `sherpa-onnx` with a streaming Zipformer English model, which is
genuinely incremental (tens of ms per chunk, no encoder floor) and emits word timings.
Finals stay on whisper.cpp for accuracy. This is a contained change: the interim lane is
already behind the `Decode` delegate in `CaptionPipeline`.

Cheaper knobs to try first: raise `interim_interval_ms` to 700–800 (the config key exists),
drop the interim window from 6 s to 3 s, use `greedy` with `best_of = 1`, and cap threads
to physical cores.

### 5.6 Metadata filters — a real porting gap

`Filters.isLowQuality(avgLogprob:noSpeechProb:compressionRatio:)` rejects garbage using
three signals. whisper.cpp does **not** hand all three over the way WhisperKit does:

| Signal | whisper.cpp | Action |
|---|---|---|
| `no_speech_prob` | ✅ exposed per segment | use directly |
| `avg_logprob` | ⚠ not exposed directly; token probabilities are | **derive**: mean of `log(p)` over the segment's tokens |
| `compression_ratio` | ❌ not exposed | **compute in-app**: `len(utf8) / len(gzip(utf8))`, the same definition OpenAI uses |

Keep the thresholds identical (`> 0.6`, `< -1.0`, `> 2.4`) and unit-test the two derived
values against fixtures so the Windows gate behaves like the macOS one.

### 5.7 Word timestamps — `RollingCaption` depends on them

`RollingCaption.update` merges interim hypotheses by **word midpoint in session
coordinates**. It needs per-word `start`/`end`, and degrades to "waiting for final
transcription" without them (there is an explicit `onIssue` for exactly this).

whisper.cpp options, in order of preference:
1. **DTW word timestamps** (`whisper_full_params.dtw_token_timestamps` with the model's
   alignment-heads preset). Presets exist for `tiny.en`, `base.en`, `small.en` and
   `large-v3-turbo`. Verify Whisper.net exposes this (`WithDtwAheads` / equivalent); if it
   does not, it is a small upstream addition or a direct P/Invoke.
2. **Token timestamps** (`token_timestamps = true`) grouped into words on leading-space
   token boundaries. Coarser, workable.

**Validate this in Phase 0**, not in Phase 3.

### 5.7.1 Fallback if word timings are unusable

`RollingCaption` exists because the interim track decodes a **sliding 6-second tail**
(`utterance.suffix(96000)`), so every hypothesis starts at a different point in the audio
and successive hypotheses share no common prefix. Merging them needs absolute word times.

`LocalAgreement` — already written and unit-tested — solves the same problem for
**fixed-origin** windows: decode from the *start of the utterance* every time, so each
hypothesis is a longer version of the last, and commit the common prefix of the most recent
two. Its own doc comment says exactly this: *"Requires a fixed audio origin. Moving windows
use `RollingCaption` instead."*

So the fallback is not "worse text merging" — it is **a different windowing strategy**,
already implemented. Its only cost is that the interim window grows to `max_utterance_s`
(20 s) instead of staying at 6 s.

**And that cost is small here, because of the encoder floor** (`SPEC.md` §1A): Whisper
always pads the mel to 30 s, so a 20 s window costs *the same encoder pass* as a 6 s one.
Only the autoregressive decoder grows — roughly 3× the tokens — so expect perhaps 1.5–2×
total. On a 3070 where `tiny.en` runs ~40–100 ms, that is still ~150 ms against a 500 ms
budget.

**Therefore:** if W2 fails, switch the interim track to fixed-origin windows +
`LocalAgreement`, and accept slightly different flicker behaviour. Measure both in Phase 0
and pick on evidence. This is the kind of trade-off the Mac could not afford and this
machine can.

### 5.8 ⚠ ROG-specific: the dGPU can vanish at runtime

The Zephyrus G15 has a **MUX switch and hybrid graphics**, and Armoury Crate power profiles
(Silent / Performance / Turbo) plus an **Eco mode** that disables the RTX 3070 entirely.
Unlike a desktop, the GPU this app depends on is user-switchable — often on battery, often
without the user connecting it to anything.

Required behaviour:

- Probe CUDA availability **at model load**, not once at install.
- If CUDA is unavailable, fall back to **CPU with `tiny.en` + `small.en`** (not turbo) and
  show a non-blocking banner: *"Running on CPU — captions will be slower. Enable the NVIDIA
  GPU for best results."*
- If CUDA disappears *mid-session* (Eco mode toggled, driver reset), treat it exactly like
  a capture failure: surface it, auto-pause, drain retained audio, let the user resume.
  The `onCaptureMustPause` path already does this — reuse it rather than inventing a
  second recovery mechanism.
- Never crash the session over a GPU state change. A dropped transcript at minute 40 of an
  interview is the worst failure this app can have.

---

## 6. The logic layer — port it 1:1, test it identically

**Replaces:** the whole `LocalCaptionKit` target — ~1,500 lines with 35 passing tests.

This is the highest-value, lowest-risk part of the port, and it should be done **first and
in isolation**, with no audio and no ML anywhere near it.

| Swift (`LocalCaptionKit`) | C# (`LocalCaption.Core`) | Notes |
|---|---|---|
| `SpeechSegmenter` | `SpeechSegmenter` | 100 ms frames, RMS gate, 200 ms pre-roll, endpoint + max-utterance. Pure struct → port as a `struct`/class with the same method shapes. |
| `RollingCaption` | `RollingCaption` | The 350 ms midpoint-anchor merge. **The subtlest code in the app — port literally, do not "improve".** |
| `LocalAgreement` | `LocalAgreement` | LocalAgreement-2 common-prefix commit. |
| `CaptionPipeline` | `CaptionPipeline` | Two serial lanes, interim coalescing, ordered finals, overload/backlog signalling, metrics. §6.2. |
| `CaptureBuffer` / `CaptureProcessor` | same | Bounded buffer + latched overflow; framing/VAD off the UI thread. |
| `Filters` | `Filters` | Plus the two derived metrics from §5.6. |
| `Sentences` | `Sentences` | Clipboard last-N, including the `appending:` provisional overload. |
| `Transcript` / `TranscriptWriter` | same | Byte-identical output (§9.1). |
| `Journal` / `JournalWriter` | same | Real fsync (§9.4). |
| `Config` | `Config` | Identical JSON keys and repair semantics (§9.2). |
| `Store` / `SessionRecord` | same | Identical DDL (§9.3). |
| `TimeFormat` | `TimeFormat` | `en-US` invariant culture — **do not** let the ASUS locale change `HH:mm:ss` or the ISO stamps. |
| `SummaryCard` / `SummaryPrompt` | *(skipped)* | Out of scope; port later verbatim. |

### 6.1 Cross-platform conformance vectors ⭐

**[DECISION]** Before porting, extract the macOS test cases into a platform-neutral
`testdata/` folder at the repo root:

```
testdata/
├── segmenter/*.json      frames in → expected requests/transitions out
├── rolling/*.json        hypothesis sequences → expected merged word list
├── filters/*.json        text + metadata → expected verdict
├── sentences/*.json      transcript + N → expected clipboard string
├── config/*.json         malformed configs → expected repaired config
└── audio/*.wav           16 kHz mono fixtures + expected transcripts
```

Both test suites read the same files. That is what makes "parity" a checkable claim rather
than an aspiration, and it catches the class of bug where a port is subtly different in a
way no one notices for three months.

### 6.2 Concurrency mapping

`CaptionPipeline` is `@MainActor` with two `Task` workers. In C#:

- Affine the pipeline object to the WPF `Dispatcher` (all mutation of `pendingInterim`,
  `finalQueue`, `captions`, `completedThrough` happens there).
- Each lane is a long-running consumer over a `Channel<SpeechRequest>`; the decode itself
  runs on a dedicated thread via `Task.Run` and marshals results back with
  `Dispatcher.InvokeAsync`.
- The interim watchdog (`interimBudget`, default 2 s) becomes a `CancellationTokenSource`
  with `CancelAfter`, passed into `WhisperProcessor`'s cancellation.
- `JournalWriter` (a Swift `actor`) becomes a class guarded by a `SemaphoreSlim(1,1)` —
  serial fsync, and `onFinal` still awaits durability before publishing, which is the
  invariant behind `9e237fc`/`b1743a7`.

Preserve the ordering guarantees exactly: **only pending interim snapshots coalesce;
finals never reorder and never drop.**

---

## 7. UI

**Replaces:** `app/Sources/LocalCaption/UI/*`.

### 7.1 Screens

Same shell: `NavigationSplitView` → a WPF `Grid` with a session-list pane and a detail
pane. Session List · Active Session · read-only Transcript Viewer · Settings window ·
Recovery dialog. Same states to design, **minus** the permission states (§4.1) and the
summary panel:

empty list · models loading · model download progress · device disconnected · capture
fell behind · transcription falling behind · save failure · disk full.

### 7.2 The caption view — the one UI risk

macOS uses a single `NSTextView` with **suffix-only replacement** so committed text is
never re-laid-out, selection survives updates, and the provisional tail renders dimmed.
Naïve WPF equivalents do not hold up: a `FlowDocument` with thousands of `Paragraph`s
degrades badly over a 3-hour session, and rebuilding it per update drops selection.

**[DECISION] Use AvalonEdit** (`ICSharpCode.AvalonEdit`), read-only:
- Virtualised rendering — designed for large documents, so hour-long transcripts stay fluid.
- `TextDocument.Replace(offset, length, text)` gives the same append-only update: replace
  only from the first differing offset, exactly as `CaptionTextView.updateContent` does.
- Selection spans the whole document and survives edits outside the selected range.
- A `DocumentColorizingTransformer` dims the provisional tail (offset ≥ committed length)
  without touching the document.
- Auto-scroll + "Jump to latest": track `TextArea.TextView.VisualLinesChanged` /
  scroll offset; stop following on user scroll-up or an active selection, resume on the
  jump button — same rules as today.

Fallback if AvalonEdit proves awkward: read-only `RichTextBox` with a capped visible
document (last ~500 paragraphs) while the full transcript stays in memory and in the
journal. Note the macOS app has the same latent unbounded-document issue; the Windows
implementation should be the better one.

### 7.3 Window behaviour

**[DECISION] Always-on-top and adjustable opacity are CUT from the Windows build.**

Both features exist on macOS so the caption window can float over the meeting window and
you can see the call through it. Neither survives the remote workflow: the meeting, the
captions and the compositing all happen on the G15, but the user is watching a compressed
video stream of that desktop from somewhere else. Translucent text over video, re-encoded
by Jump Desktop and shipped over a network, is strictly worse to read than two opaque
windows side by side — and the whole point of this app is reading text quickly.

Cutting them removes `WS_EX_LAYERED`, `SetLayeredWindowAttributes`, the `Topmost`
management, the 0.3 legibility clamp, and the screen-share capture warning from `SPEC.md`
§14 / C11. A plain, ordinary, resizable window.

**Config keys `window.always_on_top` and `window.opacity` stay reserved** in the schema
(unimplemented, hidden in Settings) so a `config.json` still round-trips with the macOS
app — the same treatment as the `summary` group (§9.2).

What remains:

- **Size/position memory:** persist `window.{width,height,x,y}` to `config.json` (the keys
  already exist). On restore, validate the frame intersects a connected monitor
  (`System.Windows.Forms.Screen.AllScreens` / `MonitorFromRect`) and re-centre otherwise —
  laptops get docked and undocked constantly, this will fire.
- Minimum window size matches the current macOS build (see `231c9ee` / `72c7c63`).

### 7.4 Clipboard

Write-only, never read. Same three behaviours: manual **Copy last N**, **auto-update** at
speech endpoints and on final refresh (default **off**), auto-copy-on-selection (default
off; AvalonEdit makes this easy, unlike the macOS build where it is still unimplemented —
a small parity *win*).

**Windows-specific hazard:** `Clipboard.SetText` throws `COMException`/
`ExternalException` when another process holds the clipboard open — common with RDP,
clipboard managers, and Office, and **routine** under Jump Desktop's clipboard sync
(§4.7.4). Wrap in a **retry with backoff — 10 attempts over ~500 ms** — and fail silently
into the existing "Copied" indicator staying off. An unhandled throw here would kill a
recording session; this is a one-line bug with a big blast radius.

**Remote-session caveat (§4.7.4):** `clipboard.auto_update` writes every few seconds and,
over a synced clipboard, overwrites whatever the user copied on their local machine. It
stays **off by default** and Settings carries an explicit note. The transcript file, not
the clipboard, is the durable artifact.

Must call from an **STA** thread (the UI thread) — `Dispatcher.Invoke`.

### 7.5 Keyboard

`Ctrl+N` new · `Ctrl+,` settings · `Space` pause/resume · `Ctrl+F` search · `Ctrl+C` copy
selection · `Ctrl+.` stop (matching the macOS `⌘.`).

---

## 8. Session lifecycle

Unchanged: `PREPARING → READY → RECORDING ⇄ PAUSED → SAVING → SAVED`, with `FAILED` and
the `pausing` ("Finishing speech…") transitional state.

All the hard-won behaviours from the git history must survive the port:
- Pause **finalises the in-flight utterance** and freezes the sample-based clock.
- `duration_seconds` excludes paused time.
- Capture-fell-behind and transcription-backlog both trigger an automatic pause that
  drains retained audio rather than dropping it silently.
- A failed journal write does **not** silently succeed — the segment is retained in memory
  and the user is told to Stop to save.
- Start is refused while an unsaved session exists; a failed capture start deletes its own
  empty journal so a retry is clean.

---

## 9. Persistence

### 9.1 Transcript files — byte-identical

Same header, same `[HH:MM:SS]` lines, same `.json` sidecar keys (`session_name`,
`started_at`, `ended_at`, `duration_seconds`, `segments`), same `YYYY-MM-DD_HH-mm-ss.txt`
naming with ` (2)` collision suffixes.

Two Windows specifics:
- **UTF-8 without BOM**, `\n` line endings (not `\r\n`) — keeps files diffable against the
  macOS output, which is how §17 verifies parity.
- Atomic write = write `*.tmp` then `File.Move(tmp, final, overwrite: true)`.

### 9.2 `config.json` — same schema, same repair

`%LOCALAPPDATA%\LocalCaption\config.json`, `schema_version: 2`, identical snake_case keys,
merge-defaults for missing keys, corrupt → `config.json.bak-<ts>` + defaults, atomic write.

`System.Text.Json` with `[JsonPropertyName]` and nullable backing fields for the
merge-default behaviour (C# has no direct `decodeIfPresent ?? default`; a
`JsonConverter` or nullable-with-coalesce per property is the idiomatic equivalent).

**[DECISION]** Keep the `summary` group in the schema with `enabled: false` as the Windows
default, so a `config.json` copied between a Mac and the ASUS round-trips without loss.
Hide the group in the Windows Settings UI.

Windows-only additions, appended (not renumbered):
```jsonc
"audio": {
  "capture_mode": "process",   // process (default) | endpoint            (§4.1)
  "target_process": null,      // executable name, e.g. "Teams.exe"; re-resolved at Start (§4.5)
  "output_device": null        // mode B only: endpoint ID; null = follow system default (§4.6)
},
"asr": {
  "backend": "auto",        // auto | cuda | cpu
  "threads": 0              // 0 = physical cores (8 on this machine)
}
```

### 9.3 SQLite

`%LOCALAPPDATA%\LocalCaption\localcaption.db`, WAL, **identical DDL** to `Store.swift`
(`sessions` table + `idx_sessions_created`). Migrations on `PRAGMA user_version`.
Transcript text never stored in the database.

### 9.4 Crash-recovery journal — get fsync right

`journal\<session_id>.jsonl`, one JSON line per final segment, deleted on clean Stop,
scanned on launch to offer recovery.

**`FileStream.Flush()` does not reach the disk.** Use `Flush(flushToDisk: true)` (which
calls `FlushFileBuffers`) — otherwise the journal's entire reason for existing is void on
a hard power loss. Test it: kill the process with `Stop-Process -Force` mid-session and
assert full recovery.

---

## 10. Settings

Same groups and defaults as `SPEC.md` §15, minus Audio→device (not needed) and minus the
summary group, plus `asr.backend` / `asr.threads`.

Live-applying: font size, auto-scroll, timestamps, clipboard. (No opacity or
always-on-top — cut, §7.3.)
Next-Start: VAD sensitivity, endpoint silence, max utterance, interim interval, models,
backend.

---

## 11. Packaging & distribution

**Replaces:** `specs/SPEC-09-packaging.md`. The macOS blocker was an Apple Developer
account; Windows has a looser but non-zero equivalent.

- **Publish:** `dotnet publish -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true`
  against **.NET 10**. No ARM64 target (W1: x64).
- **Dev prerequisite:** the target has the .NET 10 *runtime* but **no SDK** — install the
  .NET 10 SDK on whichever machine builds.
- **Installer:** **Velopack** — per-user install, no admin rights, delta auto-update,
  and it handles the native DLL payload cleanly. Alternative: Inno Setup for a plain
  single-file installer with no update channel.
- **Code signing (Authenticode).** Unsigned binaries get a SmartScreen "unrecognised app"
  interstitial and occasional Defender quarantine.
  - **[NEEDS OWNER]** For a single ASUS used by the owner, **no certificate is needed** —
    click through SmartScreen once and add a Defender exclusion for the install folder.
    That is the pragmatic v1 answer and it unblocks shipping immediately.
  - For distribution to anyone else: an **OV cert now requires a hardware token or cloud
    HSM** (since June 2023) and still builds SmartScreen reputation slowly; an **EV cert**
    gets reputation immediately and costs more. Decide only if/when the app goes wider.
- **Native payload:** `whisper.dll` + `ggml*.dll` for **both** the CUDA and CPU backends,
  plus the CUDA runtime DLLs, all bundled (§5.2). Verify nothing loads from outside the
  install directory — especially that it does not pick up a stray system-wide `cudart`.
- **Offline enforcement:** after models are present, the app makes **zero** network calls.
  Verify with Fiddler/Wireshark/`Get-NetTCPConnection` as an acceptance test.
- Models live in `%LOCALAPPDATA%`, not in Program Files — the installer stays small and an
  uninstall can optionally leave them.

---

## 12. Security & privacy on Windows

- No permissions are required at all for loopback capture. **Do not** request microphone
  access — Windows would show it in the privacy indicator and it is not needed.
- No elevation required, ever. Per-user install.
- No audio written to disk at any point.
- The journal is plaintext (same decision as macOS, B5) and deleted on clean Stop. It is
  under `%LOCALAPPDATA%`, i.e. the user's own profile.
- Nothing is sent anywhere; the app should function fully with the network adapter
  disabled once models are downloaded.

---

## 13. Performance budgets

**These remain estimates until Phase 0 measures the machine (§5.4)** — the macOS numbers
came from measurement and overturned the original plan, so treat the table below as a
sizing guide, nothing more. What W1 *does* settle is that the GPU path has large headroom.

**RTX 3070 Laptop, CUDA 12.5 — the normal path:**

| Model | Expected decode (6 s window) | Against budget |
|---|---|---|
| `tiny.en` interim | ~40–100 ms | ✅ 5–10× inside the 500 ms cadence |
| `small.en` final | ~150–300 ms | ✅ |
| **`large-v3-turbo` final** | ~250–550 ms | ✅ → perceived ≈ 600 ms endpoint + decode ≈ **0.9–1.2 s** |

If those hold, Windows finals land at roughly **half the macOS latency (~2.1 s) with a
larger, more accurate model** — the clearest win in the whole port.

**Ryzen 9 5900HS, dGPU disabled (Eco mode / §5.8 fallback):**

| Model | Expected decode | Verdict |
|---|---|---|
| `tiny.en` interim | ~200–350 ms | ✅ workable |
| `small.en` final | ~1.5–3 s | ⚠ acceptable, degraded |
| `large-v3-turbo` | ~6 s+ | ❌ not offered on CPU |

**VRAM budget (8 GB):** turbo f16 ≈ 1.6 GB + tiny.en ≈ 0.1 GB + CUDA context ≈ 0.5 GB
≈ **2.2 GB resident**, leaving ~5.8 GB for Zoom/Teams and the desktop compositor. No
pressure. **System RAM (32 GB) is a non-issue.**

Fixed budgets that do not depend on the hardware:

| Metric | Target |
|---|---|
| Cold start (models present) | < 5 s to the session list |
| Capture callback jitter | no gap > 200 ms (already instrumented as `callback_gap_ms`) |
| UI thread | no block > 100 ms |
| Memory, 3 h session | stable and bounded |
| Session length | ≥ 3 h |
| Idle CPU while recording silence | < 5 % (VAD gates decoding) |
| Sustained-load caveat | **ROG laptops throttle hard on battery and in Silent mode.** Run the 3 h soak in at least three states: mains + Performance, mains + Silent, **battery + Eco (dGPU off)** — the last exercises §5.8 |

---

## 14. Testing

1. **Unit (xUnit), against the shared vectors in §6.1** — the whole logic layer: segmenter,
   rolling caption, LocalAgreement, filters (including the two derived metrics), sentences,
   config repair/migration, transcript formatting, filename collision, store CRUD.
   *Target: at least the 35 cases the macOS suite has, plus the Windows-specific ones.*
2. **Audio-layer integration** — synthetic loopback: play a known WAV and assert the
   captured 16 kHz mono stream matches, in **both** capture modes. Includes the
   **silence-gap timestamp test** (§4.3), a mid-session **default-device-change** test, and
   a **target-process-exit** test (§4.5).
2b. **Remote-session suite (§4.7)** — run with a live Jump Desktop connection:
   connect *and* disconnect mid-recording (both modes) · meeting app pinned to Realtek
   while remote (mode A must still capture; mode B must visibly show a dead level meter) ·
   clipboard contention under active sync · confirm the machine does not sleep across a
   90-minute unattended session.
3. **Pipeline replay** — feed WAV fixtures through segmenter → pipeline with a stub decoder
   and assert final ordering, interim coalescing, backlog/overload signalling. Direct port
   of `EngineReplayTests` / `CaptionPipelineTests`.
4. **Crash recovery** — `Stop-Process -Force` mid-session; assert the journal recovers every
   fsync'd segment and the recovered transcript saves.
5. **Cross-platform parity** — run the same WAV fixture through both apps; the `.json`
   sidecars must agree on segment text and on timestamps within a stated tolerance.
6. **Soak** — 3 h continuous, memory and handle counts flat, no dropped-sample runaway.
7. **Real-audio WER pass** — noisy, multi-speaker recordings. This is still outstanding on
   macOS (`specs/STATUS.md`); doing it once on Windows fixtures benefits both.
8. **Manual** — side-by-side layout over a Teams/Zoom call,
   monitor unplug, headphone/Bluetooth switch mid-session, clipboard contention with a
   clipboard manager running.

---

## 15. Project structure

```text
windows/
├── LocalCaption.sln
├── BENCH-RESULTS.md                 ← Phase 0 output; the Windows spike/RESULTS.md
├── src/
│   ├── LocalCaption.Core/           ← pure logic, NO WPF, NO whisper.cpp (ports LocalCaptionKit)
│   │   ├── Audio/{CaptureBuffer,CaptureProcessor,SpeechSegmenter}.cs
│   │   ├── Captions/{CaptionPipeline,RollingCaption,LocalAgreement,Filters,Sentences}.cs
│   │   ├── Data/{Config,Store,SessionRecord,Journal,JournalWriter}.cs
│   │   ├── Transcripts/{Transcript,TranscriptWriter,TimeFormat}.cs
│   │   └── AppPaths.cs
│   ├── LocalCaption.Audio/          ← WASAPI loopback, resampler, device notifications
│   ├── LocalCaption.Asr/            ← Whisper.net engine, backend probe, model download
│   ├── LocalCaption.App/            ← WPF: views, view-models, SessionController, WindowInterop
│   └── LocalCaption.Bench/          ← Phase 0 console benchmark (not shipped)
├── tests/
│   ├── LocalCaption.Core.Tests/     ← reads ../../testdata (shared with macOS)
│   ├── LocalCaption.Audio.Tests/
│   └── LocalCaption.Integration.Tests/
└── build/                           ← publish + Velopack packaging scripts
testdata/                            ← NEW, repo root, shared by both platforms (§6.1)
```

`LocalCaption.Core` must not reference WPF or Whisper.net. That separation is what makes
the test suite fast and what keeps a future convergence cheap.

---

## 16. Build sequence

```
Phase 0  ▶  LocalCaption.Bench + loopback probe on the G15 ← GATE: answers W2, W7, W9; sets defaults
Phase 1  ▶  Core logic port + shared test vectors          (no UI, no audio, no ML)
Phase 2  ▶  WASAPI loopback + resampler                    ‖ parallel with Phase 1
Phase 3  ▶  Whisper.net engine + streaming orchestrator    → first live captions
Phase 4  ▶  Session lifecycle, transcript, journal, recovery
Phase 5  ▶  Caption UI, clipboard, source picker, level meter
Phase 6  ▶  Session list + settings
Phase 7  ▶  Packaging, soak, WER pass
```

**Critical path:** `0 → 1 → 3 → 4 → 5 → 7`. Phase 2 and Phase 6 hang off it.

**Phase 0 must not be skipped even though W1 is closed.** It no longer answers "can this
machine do it" — it answers **W2** (do usable word timestamps come out of Whisper.net?) and
**W7** (is the dual-model hybrid even needed on a 3070?). W7 in particular can *remove*
work from Phase 3, so measuring first is cheaper than building first.

**First milestone (vertical slice), end of Phase 3:** loopback audio in → live captions on
screen. Everything after that is layering, exactly as the macOS build went.

Rough effort, assuming one experienced .NET developer:

| Phase | Work | Estimate |
|---|---|---|
| 0 | Benchmark harness + measurement (W2 word timings, W7 turbo-only, GPU contention) **+ loopback probe incl. the Jump Desktop endpoint (W9)** | 2–3 days |
| 1 | Core logic port + vectors + tests | 4–6 days |
| 2 | WASAPI **process + endpoint** loopback, resampling, device changes, silence padding, sleep prevention | 5–7 days |
| 3 | ASR engine, CUDA+CPU backends, word timings, dGPU-loss fallback (§5.8), orchestrator | 5–8 days |
| 4 | Lifecycle, transcript, journal, recovery | 3–4 days |
| 5 | Caption view, clipboard, source picker + level meter (§4.5–4.7) | 4–6 days |
| 6 | Session list, settings | 3–4 days |
| 7 | Packaging, soak, WER, polish | 3–5 days |
|  | **Total** | **~6–8 weeks** |

The increase over v1.0 (~5–7 weeks) is the second capture mode, the remote-session
requirements and their test suite — the cost of making the confirmed Jump Desktop
workflow actually reliable rather than incidentally working.

### 16.7 Deferred: the Live AI Summary on Windows

When it is wanted, the port is small because the macOS design already isolates the LLM
behind an HTTP contract: bundle **`llama-server.exe`** (llama.cpp) with
`Llama-3.2-1B-Instruct-Q4_K_M.gguf`, launch it as a child process on `127.0.0.1`, and
point the existing `summary.server_url` / `summary.model` config at it. It speaks the same
OpenAI-compatible `/v1/chat/completions`, so `SummaryPrompt`, `SummaryCard.parse` (with
its ToDo gating) and the panel UI port over unchanged. Budget ~1 week including the
CUDA/CPU offload decision.

---

## 17. Acceptance criteria

1. Create, open read-only, rename, delete, search and sort sessions.
2. System audio is captured with **no permission prompt and no third-party driver**; the
   default-output-device change mid-session recovers without losing the session.
3. A 20-second silence gap mid-recording leaves every subsequent timestamp accurate to
   ±100 ms (§4.3).
4. Two Whisper models run fully on-device; **zero** network traffic after the models are
   present, verified with a network monitor.
5. Interim partials meet the p90 budget set by Phase 0; finals ≤ ~2 s p50 on the target
   machine; **committed text is never rewritten**.
6. Whisper silence-hallucinations suppressed — a 5-minute silent soak produces no captions.
7. The window frame is restored on launch and validated against currently connected
   monitors, re-centring when the saved frame is off-screen.
8. Clipboard automation works as configured, is **off by default**, never reads the
   clipboard, and survives a clipboard-contention retry without crashing.
9. All settings persist; a corrupt `config.json` repairs to defaults with a timestamped backup.
10. On Stop: `.txt` + `.json` written, DB row inserted, journal deleted.
11. `Stop-Process -Force` mid-session leaves a recoverable journal containing every
    fsync-acknowledged segment; next launch recovers and saves it.
12. A ≥ 3 h session completes with flat memory and handle counts, on battery and on mains.
13. No audio or transcript data is transmitted externally, ever.
14. The same WAV fixture produces matching transcripts on macOS and Windows within the
    stated tolerance (§14.5).
15. The installer completes without admin rights and the app launches on a clean Windows 11
    machine (SmartScreen interstitial acceptable per §11 unless signing is funded).
16. **Toggling the dGPU off mid-session** (Eco mode / MUX) does not lose the transcript:
    the session auto-pauses, reports the fallback, and resumes on CPU (§5.8).
17. Switching the default output endpoint mid-session — including **connecting and
    disconnecting a Jump Desktop session** — keeps capture on the intended source in both
    modes, with no lost transcript (§4.6, §4.7.1).
18. With the meeting app pinned to a non-default output device, **process-loopback mode
    still captures it correctly** (§4.7.2).
19. A 90-minute unattended remote session completes without the machine sleeping (§4.7.6).
20. The Active Session header shows the live source name and a responsive level meter;
    capturing the wrong source is visible within seconds (§4.7.5).
21. Clipboard writes survive sustained contention from a remote clipboard sync without
    throwing or interrupting the session (§7.4).

---

## 18. Blockers & open decisions

| ID | Blocker | Blocks | Status |
|----|---------|--------|--------|
| **W1** | Target-hardware profile of the ASUS | §5, §13, all phases | ✅ **CLOSED** 2026-09-20 — ROG Zephyrus G15, RTX 3070 8 GB, CUDA 12.5 (§0.5) |
| W2 | Word-timestamp support in Whisper.net (DTW alignment heads) | §5.7, RollingCaption fidelity | ⚠ Verify in Phase 0 — **has a strong fallback** (§5.7.1): fixed-origin windows + LocalAgreement-2, which the encoder floor makes affordable on this GPU |
| W3 | Interim budget / streaming-transducer fallback | §5.5 | ✅ **De-risked** by W1 — GPU path has 5–10× headroom; applies only to the CPU fallback |
| **W7** | **Hybrid vs turbo-only** — can one resident `large-v3-turbo` serve both lanes on this GPU? Re-opens `SPEC.md` §22.2 / B4 on better hardware | §5.4, whole ASR architecture | ▶ **Answered by Phase 0.** Could remove the dual-model split entirely |
| W4 | Code-signing certificate — needed only if the app goes beyond the owner's machine | §11 | **[NEEDS OWNER]**, non-blocking for v1 |
| W5 | Process-loopback app picker | §4.1, §4.5 | ✅ **CLOSED — in v1, as the default capture mode.** Owner confirmed remote use, which makes endpoint routing the fragile part |
| W6 | AvalonEdit vs RichTextBox for the caption view | §7.2 | Decide in Phase 5 from a spike |
| **W8** | dGPU removal at runtime (MUX / Eco / Armoury Crate) | §5.8 | ▶ Specified; must be implemented and soak-tested |
| **W9** | **Does WASAPI loopback work on the Jump Desktop Virtual Speaker?** Init, mix format, silence behaviour, device-position monotonicity | §4.7.3, mode B viability | ⚠ **Probe in Phase 0** — cheap now, expensive in Phase 2. Mode A is the mitigation if it fails |
| **W10** | Clipboard round-trip reliability under Jump Desktop sync | §4.7.4, §7.4 | ▶ Mitigated by design (auto-update off, 10× retry); confirm in the §14.2b suite |

### W1 — closed (profiled 2026-09-20)

The full profile is in §0.5. Summary of what it changed in this document:

| Was (v1.0, unknown hardware) | Now (v1.1, measured) |
|---|---|
| Four candidate backends, chosen by runtime probe | **CUDA + CPU fallback only** (§5.2) |
| CUDA/OpenVINO fetched on demand | **Both backends bundled** in the installer (§5.2) |
| Final model `small.en`, turbo as opt-in | **`large-v3-turbo` f16 is the default final model** (§5.3) |
| .NET 8 LTS | **.NET 10 LTS** — runtime already installed (§2.1) |
| Interim budget was a survival risk (§5.5) | De-risked; §5.5 applies only to the CPU fallback |
| No device picker (following macOS) | **Output-endpoint picker required** — 4 endpoints incl. a virtual one (§4.6) |
| Generic "laptops throttle" caveat | **MUX / Eco / Armoury Crate can remove the dGPU mid-session** (§5.8, §13) |
| ARM64 a live possibility | Ruled out — x64 |

**What did not change:** the silence-gap trap (§4.3), the derived `compression_ratio` /
`avg_logprob` (§5.6), word timestamps (§5.7), and the virtualised caption control (§7.2).
Those are properties of the platform and the libraries, not of the hardware, and they
remain the real engineering content of this port.

**One residual question for the owner, non-blocking:** the machine has a **Jump Desktop
Virtual Speaker**, which suggests it is sometimes driven remotely. If interviews will be
taken *while connected over Jump Desktop*, say so — it makes §4.6's endpoint picker a
Phase 5 requirement rather than a nicety, and adds a test case (capture must follow the
virtual endpoint, and must survive the session connecting and disconnecting mid-call).

---

## 19. Summary of differences from the macOS app

Things that get **simpler** on Windows:
- No Screen Recording permission, no TCC, no permission-denied UI, no BlackHole, no
  Multi-Output Device. The entire audio-setup chapter of `SPEC.md` evaporates.
- No notarisation gate. Distribution is unblocked on day one for the owner's own machine.
- Auto-copy-on-selection is trivial (the macOS build still has it unimplemented).
- **The overlay is cut** (§7.3) — no `Topmost` management, no layered-window alpha, no
  legibility clamp, and no screen-share capture warning. One fewer subsystem.
- **Process loopback has no macOS equivalent in the shipped app.** ScreenCaptureKit can
  filter by application, but the current build captures the whole display's audio;
  targeting just the meeting app keeps Spotify and Slack pings out of the transcript.
- **The RTX 3070 makes `large-v3-turbo` affordable for finals** — so the Windows build
  should be both *more accurate* and roughly *twice as fast* on finals as the shipped
  macOS app, which settled for `small.en` at ~2.1 s. This is the headline win.
- The hybrid architecture itself may turn out to be unnecessary here (W7) — one resident
  turbo model for both lanes would be simpler than anything on macOS.

Things that get **harder** on Windows:
- The loopback silence gap (§4.3) — invisible until timestamps are wrong, so it must be
  handled up front.
- `compression_ratio` and `avg_logprob` must be derived rather than read (§5.6).
- Word timestamps need DTW alignment heads rather than a built-in flag (§5.7).
- Hardware variance: a single Mac target becomes four plausible backends.
- A virtualised text control is required for long sessions where macOS got away with
  `NSTextView`.
- The GPU is **user-removable at runtime** (MUX switch, Eco mode) in a way no Mac GPU is,
  so the ASR engine needs a live fallback path the macOS app never needed (§5.8).
- Four render endpoints, one of them virtual and intermittent, where macOS had no device
  concept at all (§4.6).
- **The confirmed remote-desktop workflow adds a whole requirement class the macOS app
  never had** (§4.7): a second capture mode, sleep prevention, a level meter, clipboard
  contention handling, and a test suite that can only run against a live Jump Desktop
  session. This is the single largest scope difference from v1.0 of this plan.
