# Local Caption for Windows

Native Windows port of the macOS app in [`../app`](../app). The specification is
[`../SPEC-WINDOWS.md`](../SPEC-WINDOWS.md); this file only records **how to build it and
where the work currently stands**.

The two apps share no code — what they share is the specification, the file formats, the
config schema and the [conformance vectors](../testdata) (§6.1).

## Status

**See [`STATUS.md`](STATUS.md)** for current progress, what is verified versus merely
compiled, known gaps and the remaining work.

In short: **the application works.** Launch it, press Start, and captions appear live on the
GPU; Stop writes a timestamped transcript and lists the session; killing it mid-recording gets
the session offered back at the next launch. Every stage A1–B6 is built, and fourteen of
§17's twenty-one acceptance criteria are measured on the target machine. 110 tests green.

It runs the shipping pair — `tiny.en` + `large-v3-turbo` on CUDA, 1.8 GB of VRAM — at
339 ms interim and 426 ms final under contention, inside both §5.4 budgets. Getting there
needed a driver update; [`BENCH-RESULTS.md`](BENCH-RESULTS.md) §1 records why.

## Build and test

Needs the **.NET 10 SDK**. On the Mac, if you do not have it:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"
```

On the G15 (PowerShell), the runtime is already present and only the SDK is missing:

```powershell
& ([scriptblock]::Create((Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1))) -Channel 10.0 -InstallDir "$env:USERPROFILE\.dotnet"
```

Then, from this directory:

```bash
PATH="$HOME/.dotnet:$PATH" dotnet test
```

The macOS suite must stay green too — it runs the same vectors:

```bash
cd ../app && swift test
```

## Running the build you are working on

```powershell
build\release.ps1                                   # rebuild, install, shortcut
build\release.ps1 -CudaDirectory C:\cuda-redist\bin # first time on a new machine
build\release.ps1 -SkipPublish                      # install what publish/ already holds
```

Publishes, bundles the CUDA payload, mirrors the result into `%LOCALAPPDATA%\LocalCaptionBuild`
and points **Local Caption** on the desktop at it — no installer, no admin rights, and no
uninstall to undo. `-Run` starts it, `-NoShortcut` leaves the desktop alone, `-Destination`
puts it elsewhere.

It copies the build out of `publish/` rather than shortcutting to it because a shortcut into
the repository breaks: `publish.ps1` clears its output at the start of every build, so between
two rebuilds the icon points at nothing, and while the app is running from there the rebuild
has to refuse outright — which means closing the app, mid-call if that is when you noticed.
The third folder name is deliberate. `%LOCALAPPDATA%\LocalCaption` is the *data* (§9) and
`%LOCALAPPDATA%\LocalCaptionApp` belongs to the installer, which deletes it on install and on
uninstall; a build in either one would be a program in the transcript folder, or a build Setup
silently removes. All three read the same data, so an installed copy and a local build share
the models — downloaded once, not twice.

The install is mirrored, not merged: a DLL dropped from the build is dropped from the install,
because a stale native library that loads in preference to the right one is the failure this
project can least afford to debug. Two things are refused rather than half-done — installing
over a folder that holds files but no `LocalCaption.exe` (a mistyped `-Destination` should
cost an error, not a directory), and installing over a running copy.

## Packaging

**What to hand someone else:** `artifacts\LocalCaption-Setup-<version>.exe`, and nothing else. It is
self-contained (.NET included), needs no admin rights, installs to
`%LOCALAPPDATA%\LocalCaptionApp`, adds Start-menu and desktop shortcuts, and launches. The first
launch downloads the speech models (about 1.7 GB with an NVIDIA GPU, less without) with a
progress bar; after that the app never touches the network. Without a usable NVIDIA GPU it
picks smaller models and says so. Because the build is unsigned (§11), Windows shows "Windows
protected your PC" once — **More info ▸ Run anyway**. `LocalCaptionApp-win-Portable.zip` is the
same app with no installer: unzip anywhere and run. Both carry the `extension/` folder for
Settings ▸ Send. Install → run on CUDA → uninstall was verified end to end on the G15,
including that uninstalling leaves the data folder alone.

```powershell
build\package.ps1 -Version 1.0.0 -CudaDirectory <folder with cublas64_*, cudart64_*>
```

`build\publish.ps1` alone refreshes `publish/`; it now carries any CUDA DLLs already sitting
there across the rebuild rather than deleting them with the folder. What counts as the CUDA
payload, and how it gets beside the executable, lives once in `build\cuda.ps1` — all three
scripts dot-source it, because the three ways of getting it wrong all fail the same silent
way (CPU fallback, no message).

Publishes self-contained `win-x64` (§11), prunes the Linux, macOS and ARM native libraries
Whisper.net copies in regardless of RID, and packs a Velopack installer into `artifacts/` —
per-user, no admin, with delta updates. Unsigned by decision (§11, W4): click through
SmartScreen once and add a Defender exclusion.

**`-CudaDirectory` is not optional.** The CUDA redistributables (`cublas64_13`,
`cublasLt64_13`, `cudart64_13`) are in no NuGet package — they come from NVIDIA's redist
archives — and without them the GPU backend cannot load and Whisper.net drops to the CPU
without saying so. `nvcudart_hybrid64.dll` is different again: it ships with the *display
driver*, in `System32\DriverStore`, and the script copies it out for you.

## Layout

```text
windows/
├── LocalCaption.slnx              ← .NET 10 defaults to the .slnx solution format
├── src/
│   ├── LocalCaption.Core/         ← pure logic. NO WPF, NO Whisper.net, NO WASAPI.
│   │   ├── Audio/                 SpeechStream (segmenter, requests), CaptureBuffer
│   │   ├── Captions/              RollingCaption, LocalAgreement, CaptionPipeline,
│   │   │                          SerialDispatcher, Filters, SegmentQuality, Sentences
│   │   ├── Data/                  Config, Store, Journal, Files, SessionFiles
│   │   ├── Transcripts/           Transcript, TranscriptWriter, TimeFormat
│   │   └── AppPaths.cs
│   ├── LocalCaption.Asr/          ← whisper.cpp via Whisper.net; models, backend, word timings
│   ├── LocalCaption.Audio/        ← WASAPI capture (§4). Windows-only.
│   │                                WasapiCapture (thread, clock, packets), EndpointLoopbackCapture
│   │                                (mode B), ProcessLoopbackCapture (mode A, the v1 default),
│   │                                AudioNormalizer, SilentRenderKeepalive, DeviceWatcher,
│   │                                AudioSessions, SleepPrevention
│   ├── LocalCaption.Session/      ← StreamingOrchestrator, SessionController, AppEnvironment.
│   │                                The §8 lifecycle, the journal and crash recovery. Kept out
│   │                                of the WPF project so it can be driven from a console.
│   ├── LocalCaption.App/          ← WPF: the shipping application (§7). CaptionTextView is
│   │                                the §7.2 answer — AvalonEdit, suffix-only updates.
│   ├── LocalCaption.Bench/        ← the §5.4 B0 harness — decode latency (not shipped)
│   └── LocalCaption.Probe/        ← the §4.7.3 harness — raw WASAPI, the capture layer via
│                                    --capture, and a whole session via --session / --recover
│                                    (not shipped, Windows-only)
└── tests/
    ├── LocalCaption.Core.Tests/   ← reads ../../testdata, shared with macOS
    ├── LocalCaption.Asr.Tests/    ← runs without the native library
    └── LocalCaption.Audio.Tests/  ← format normalisation; no device needed
```

Run it with `dotnet run --project src/LocalCaption.App -c Release`.

`LocalCaption.Core` must not take a dependency on WPF, Whisper.net or WASAPI. That
separation is what keeps this suite fast and a future convergence cheap. `SampleClock` lives
in `Core` despite describing a WASAPI behaviour, because it is pure arithmetic and its
failure mode — a stalled clock and a transcript that is quietly wrong — is one worth unit
testing. Every §15 project now exists.

## The shell

What is around the captions, as of the redesign. None of it changes what the app records or
how; it changes how little attention the app needs while a call is going on.

- **Title bar** — drawn by the app (`ChromeWindow`, over `WindowChrome`, so resize, Snap and
  the system menu are still Windows' own). It carries the sidebar toggle, the pin
  (keep-on-top), the light/dark toggle and Settings, because the title bar is the one strip
  that survives any window size.
- **Quick toolbar** — source, live model, final model, text size, copy-N, Follow, Auto-copy,
  detection sensitivity and compute backend. Every control saves as it changes. Changing the
  source, sensitivity or a model *while recording* pauses, applies it and resumes — the
  sentence in progress is finalised first and the journal is never at risk. A model swap
  costs the few seconds the load takes.
- **Status bar** — `LIVE` and `FINAL` decode times, `LAG` behind live audio, and `SPEED` as a
  multiple of realtime, smoothed, turning amber then red at the thresholds the pipeline
  itself acts on (2 s interim budget, 3 s staleness cut-off). Timeouts count: a decode
  abandoned at the budget is a floor on how slow the model is, not a number to leave out.
- **Sidebar** — collapsible (`Ctrl+B`), resizable, and it steps aside by itself below 700 px
  wide without changing the saved preference. The window now goes down to 400 × 320.
- **The last question** — `Copy question` (F8) takes everything since the speaker last paused,
  which is the unit an interview actually runs on; "last N sentences" cannot know where a
  question began. A turn is the run of segments separated by less than `send.turn_gap_ms`
  (2.5 s) of sample-clock silence (`Core/Captions/Turns.cs`), plus whatever is still being
  spoken. Bookmarks never count as speech.
- **Send to your answer tool** (Settings ▸ Send, `send.target`, off by default) — the same turn,
  delivered instead of copied, on F9:
  - `window` — switches to a chosen window, pastes, optionally presses Enter, switches back.
    Works with anything that has a message box; needs the chat to be the tab showing. Manual
    only, by design: it takes the foreground, and doing that unasked would be a hazard.
  - `address` — a loopback HTTP feed (`127.0.0.1:17653`, no CORS header, so closed to web
    pages). `GET /next?since=N` long-polls for new turns; `/latest` answers at once. The
    companion browser extension in [`extension/`](extension) reads it and types into an open
    ChatGPT / DeepSeek / Claude / Gemini / Copilot tab **without taking focus**, even while
    that tab is in the background. With `send.auto` each turn goes out by itself ~2 s after
    the speaker stops.
  - `file` — appends each turn to a file for a home-made tool to watch.
  The extension's page-side logic is tested against a mock chat page; it has **not** been run
  against the real sites from this repo, and chat sites change their markup. `window` does not
  depend on the page at all and is the fallback that cannot rot.
- **Click-through** (title bar, F7) — with pin and see-through, the captions sit over the call
  and clicks go to the call. Three ways back, because any one can be unavailable: the
  shortcut, the "click to exit" handle that floats over the title bar (its own window, so it
  still takes clicks), and activating the app from the taskbar or Alt+Tab.
- **Bookmark** (Ctrl+M, the star) — a `[★ bookmark 00:12:31]` segment, journalled and saved in
  order like any caption, and excluded from everything copied or sent.
- **Source picker** — entries say what they are (`Meeting ·`, `Browser ·`, `Virtual machine ·`):
  a call taken inside a VM reaches the host as `vmware-vmx` and nobody recognises that as
  their interview. "Everything this PC plays" remains the default and always works, VM
  included. `Auto` (`audio.capture_mode: auto`) captures the one recognised program that is
  playing when capture begins, and falls back to the whole device whenever that is not
  clear-cut — a wrong guess records silence, so it only guesses when there is nothing to guess.
- **Model downloads ask first** — picking a model that is not on disk shows its size and asks;
  the download then runs while recording continues on the old model, and the (seconds-long)
  swap happens only once the file is here. At launch, a configured-but-missing model is
  asked about rather than silently fetched — except on a first run, which has nothing to
  fall back to and just downloads.
- **Text size and see-through** — behind the `Aa` button in the title bar, so they cost the
  captions no room. See-through fades only the *grounds* (`window.opacity`, 0.1–1.0); text,
  icons and controls stay solid, the caption surface gives way more slowly than the rest, and
  below 75% the captions get a soft halo in the ground's colour. It is per-pixel DWM
  composition of the ordinary hardware-rendered window — **not** `AllowsTransparency`, which
  would make it a software-rendered layered window for good. See "Things worth knowing".
- **Themes** — `ui.theme`: `system` (follows Windows, live), `dark`, `light`. One resource
  dictionary is swapped at index 0 of the application's merged dictionaries; nothing reloads.
- **Shortcuts** — every action is rebindable in Settings ▸ Shortcuts, and each can be made
  **global** (`RegisterHotKey`) so it works while the meeting app has focus. None is global
  by default: a global hotkey takes its combination away from every other app. If Windows
  refuses one because it is taken, Settings says so and the key still works in-window.
- **Names and terms** (Settings ▸ Speech, `asr.vocabulary`) — a comma-separated list handed to
  the *final* model as whisper.cpp's initial prompt. Measured on synthesised speech with
  small.en: "Chiquamica Okonkwo … Ngoziata S. … Obena" became "Chukwuemeka Okonkwo … Ngozi
  Adaeze … Obinna". It biases spelling; it does not add a language — Whisper has no Igbo.
  Empty (the default) leaves decoding exactly as it was.
- **Final transcript care** (Settings ▸ Speech, `asr.final_beam_size`) — 0 is greedy decoding,
  the default and what §5 specified; 5 turns on beam search for the final lane only. Measured
  on the G15 with large-v3-turbo and clean synthesised speech: about 8% slower (418 → 459 ms)
  and no change in accuracy. It is there for noisy audio, not as a free upgrade. The same
  test showed the stock setup already gets Russian and Polish surnames right in English
  ("Kuznetsova", "Wisniewski", 6/6); only rarer Igbo first names come out phonetic.
- **Settings ▸ Appearance ▸ Create desktop shortcut** — adds `Local Caption.lnk` if there is
  none, and says so if there is. For copies that were never installed.
- **Settings ▸ System** — engine, backend actually loaded, speed, CPU/RAM/GPU, models on
  disk, paths. Read from the registry and one Win32 call; no WMI.

The privacy invariant (§1.2) still holds for the app itself: the only outbound connection is
the model download. The loopback feed listens on 127.0.0.1 and nothing else. What an answer
tool then does with a question is that tool's business and the user's choice.

Config keys added, all Windows-only and all defaulted so an older `config.json` loads
unchanged: `ui.theme`, `ui.pin_on_top`, `ui.sidebar_collapsed`, `ui.sidebar_width`,
`asr.vocabulary`, `asr.final_beam_size`, `audio.auto_gain`, the `send` group, `audio.capture_mode: auto`, and
`shortcuts.<action>.{keys, global}`. `window.opacity`, reserved by §7.3,
is now honoured; its default of 1.0 means nothing changes until the slider is touched. `window.always_on_top` stays reserved and unread (§7.3) —
the pin is a separate key precisely because that one defaults to `true`.

**`LOCALCAPTION_HOME`** points the app at a different data folder (config, models, journal,
transcripts, database). It exists for trying a build without handing it real data:

```powershell
$env:LOCALCAPTION_HOME = "$env:TEMP\lc-scratch"; .\publish\LocalCaption.exe
```

**The icon** is drawn by [`build/make-icon.ps1`](build/make-icon.ps1) — per size, snapped to
whole pixels — into `src/LocalCaption.App/Assets/`. Re-run it to change the mark.

## Things worth knowing before changing this code

- **The vectors are a contract.** `testdata/` is asserted by both platforms. If a change
  makes one fail, fix the code — do not edit the vector to match. See
  [`../testdata/README.md`](../testdata/README.md).
- **`RollingCaption` is a literal port.** §6 calls it "the subtlest code in the app — port
  literally, do not improve". Its tie-breaking matches Swift's `min(by:)` deliberately.
- **The pipeline takes no locks.** Serialisation comes from its
  `SynchronizationContext`. Adding a lock around an `await` there will deadlock the interim
  lane against a slow final — see the class remarks.
- **Culture and encoding are load-bearing.** `TimeFormat` pins `InvariantCulture` and
  `Files.Utf8NoBom` avoids .NET's BOM, both so transcripts stay byte-identical to the macOS
  output (§9.1) — that is how §17 verifies parity, with a plain diff.
- **`Flush(flushToDisk: true)`** in `Journal`. Plain `Flush()` does not reach the disk, and
  without it the crash-recovery journal is void on power loss (§9.4).
- **Share modes are not optional on Windows.** `Journal.Pending` opens with
  `FileShare.ReadWrite | FileShare.Delete` because the default denies write sharing and so
  throws against the journal's own live append handle — which the scan would swallow and
  report as "nothing to recover". Unix ignores share modes entirely, so the Mac cannot see
  this class of bug; assume any file both written and read needs the share set spelled out.
- **A GPU backend that fails to load is silent.** Whisper.net walks its runtime order and
  drops to the CPU library without throwing or logging, so `EngineInfo.Library` records what
  actually loaded and `FellBack` says whether it matches what was asked for (§5.2). Trust
  that field, never the requested backend.
- **The capture clock must start when the stream opens, not when audio does.** An endpoint
  that is silent from the beginning never sends a first packet, so a clock waiting for one
  never starts and the session silently captures nothing. Found on hardware, not in a test —
  `--capture --no-keepalive` is how to reproduce it.
- **The two capture modes have different clock guarantees.** Mode B gets device-position
  padding *and* the wall-clock rule; mode A gets only the wall clock, because process
  loopback reports `u64DevicePosition = 0` on every packet. Do not assume a correction that
  works in one exists in the other.
- **`TimeFormat.ParseIso` returns UTC.** Segments store `created_at` as ISO UTC (§9.1), so
  anything read back out of a journal needs `ToLocalTime()` before it is formatted — or a
  recovered session ends up headed, and named, hours from when it ran. The Swift original
  cannot hit this: `Date` carries no offset.
- **Only one copy may run.** A named mutex in `App.OnStartup` refuses the second and raises
  the first one's window. Two instances capture the same audio into two journals and two
  database rows — and before `Journal.Pending` learned the rule below, the second offered to
  "recover" the first's live session.
- **A journal someone still has open is a live session, not an orphan.** `Journal.Pending`
  skips files another handle holds, because a second copy of the app would otherwise offer to
  "recover" a recording in progress — and accepting deletes its only durable record. Use
  `Journal.Read` when you want a journal's contents regardless.
- **A session that cannot be saved keeps its journal.** `SessionController.Save` returns
  false rather than throwing, and the journal stays on disk so the next launch can still
  recover it. A full disk must not turn into a lost interview (§8).
- **The caption view replaces suffixes, never rebuilds.** §7.2 is explicit that rebuilding
  the document per update drops the selection and re-lays-out the whole transcript. If a
  change to `CaptionTextView.Update` starts assigning `Document.Text`, it is wrong.
- **§5.8 is not optional on this laptop.** Eco mode and the MUX switch remove the GPU at
  runtime. `AsrFallback` decides what to load without one, a run of decode failures is taken
  as the GPU going away, and the session pauses and rebuilds on the CPU rather than dying.
  Never let a GPU state change reach the user as a lost transcript.
- **whisper.cpp writes markup in two shapes.** `<|…|>` for model special tokens and
  `[_BEG_]` / `[_TT_100]` for its own internal ones — and token timestamps, which the interim
  lane needs for W2, make it emit the second kind. Both are dropped in `WordTimings`; anything
  new that parses tokens has to drop them too.
- **Window shortcuts must be matched before the focused control sees the key.** AvalonEdit's
  text area handles keys first, so a `RoutedCommand` carrying a `KeyGesture` never fires while
  the captions have focus — `Ctrl+.` was silently dead until this was found by pressing it.
  `ShortcutManager` matches in one tunnelling `PreviewKeyDown` on the window for that reason.
- **The engine may only be swapped while capture is stopped.** A `CaptionPipeline` holds the
  `WhisperEngine` for as long as it exists, and pausing is what disposes the pipeline. So a
  live model change is pause → `ReloadModelAsync` → resume (`SessionController.ReconfigureAsync`),
  the same shape as the §5.8 CPU fallback, and never a reload under a running stream.
- **WPF vetoes `WS_EX_LAYERED`, and gets the last word over `AddHook`.** Click-through to
  *other applications* needs `WS_EX_TRANSPARENT | WS_EX_LAYERED`, and WPF's render target
  clears the layered bit in `WM_STYLECHANGING` on any window it does not consider layered —
  after ordinary hooks have run. `ClickThrough` therefore subclasses the window natively
  (`GWLP_WNDPROC`), lets WPF answer, then restores the bits. Reading the style back from
  another process is how this was found; "it is set" and "it stuck" are different things.
- **Function keys may be global shortcuts; bare letters may not.** With the call in a VM,
  Ctrl+Alt is the VM's own release-the-keyboard combination, so the mid-call defaults are
  F7/F8/F9. Note that VMware and VirtualBox grab the keyboard at driver level while the
  guest has focus — no host hotkey of any kind fires then. The handle and the taskbar exist
  for exactly that case.
- **The speech gate is an absolute level, and loopback is as quiet as the speakers.** On this
  machine's Realtek device (and many others) endpoint loopback is taken after the volume
  control. At 34% volume a real session kept 32% of a talking-head video, in one-second
  crumbs, with almost no live text — live decoding only runs while speech is "detected". It
  looked exactly like a pipeline regression and was nothing of the kind. `Core/Audio/AutoGain`
  now sits between capture and the buffer: peak-following gain, never below unity, bit-for-bit
  transparent for audio that is already loud enough, frozen during silence, +30 dB at most,
  length-preserving so the sample clock cannot tell. Measured on real speech: detection at
  10% volume went from 0% to 94%, the same 11 phrases as at full volume. The status bar shows
  `BOOST +n dB` while it is working. **If captions ever go patchy again, look at BOOST and at
  the system volume before looking at the code.**
- **`new SpeechSegmenter.Tuning()` is all zeros**, not the constructor's defaults — it is a
  struct, and C# gives a struct a parameterless constructor that ignores default arguments.
  A threshold of 0 calls everything speech. The app always passes explicit values
  (`ApplyTuning`); tests must too.
- **Never stop "LocalCaption" by process name from a script.** The owner may be recording in
  the real app while a test build runs beside it (the single-instance mutex is per data
  folder for that reason). Track the PID you started. `publish.ps1` refuses to run over a
  copy that is executing from its output folder.
- **A window made topmost goes in front of every other topmost window.** The click-through
  exit handle was shown, and then the main window was made topmost, which put it over the
  handle — visible only once clicked. The handle is now *owned* by the main window (Windows
  keeps an owned window in front of its owner, whatever either does later) and is shown after
  the listeners that change `Topmost` have run.
- **WPF has no event for a sideways touchpad swipe.** `MouseWheel` is WM_MOUSEWHEEL only;
  WM_MOUSEHWHEEL arrives at the window and stops. The toolbar takes it from an `HwndSource`
  hook in `MainWindow.OnWindowMessage`.
- **Endpoint loopback level follows the master volume on this machine's Realtek device.** With
  the speakers turned down, speech can fall under the VAD's absolute RMS threshold and be
  missed entirely while the level meter barely moves. Raising DETECT to Highest, or capturing
  the app rather than the device, is the workaround; an automatic gain stage ahead of the
  segmenter would be the fix. Found when a test that had passed an hour earlier captured
  nothing — the pipeline was fine, the volume had been turned down.
- **Blue (`Live`, #64B9D3) means live, brass means actionable.** Blue is for things happening
  now — the provisional caption tail, the speed readouts, loading and downloading. Keep it off
  buttons and selections, or it stops meaning anything.
- **Removing a session never deletes files by default.** The dialog's tick-box is what deletes,
  and it deletes to the Recycle Bin (`Microsoft.VisualBasic.FileIO`), txt and json sidecar
  together. If a file cannot be deleted the list row is kept, since it is then the only thing
  still pointing at it.
- **UI Automation toggles a `ToggleButton` without raising `Click`.** Anything driven from a
  toggle listens to `Checked`/`Unchecked`, which the mouse, the keyboard and automation all
  raise.
- **See-through needs the frame *out* of the client area.** With `GlassFrameThickness = -1`
  (what gives a solid window its shadow and drawn border) a translucent brush reveals DWM's
  own black sheet and ghost caption buttons, not the desktop. `ChromeWindow.SeeThrough` swaps
  to a zero-glass `WindowChrome` only while something is actually translucent, and back at
  100%. It also needs `CompositionTarget.BackgroundColor = Transparent` and
  `DwmEnableBlurBehindWindow` with an empty region. All three, or it composites over black.
- **The installer's pack ID must never equal the data folder's name.** Velopack owns
  `%LOCALAPPDATA%\<packId>` and deletes it on uninstall and on reinstall. The data folder is
  `%LOCALAPPDATA%\LocalCaption`, so the pack ID is `LocalCaptionApp`. When they matched,
  uninstalling the app deleted every transcript.
- **Sliders fire `ValueChanged` during `InitializeComponent`**, while XAML coerces their
  initial value — before the window's fields exist. `MainWindow._syncing` starts `true` for
  that reason.
- **`Config` groups must compare by value.** The conformance vectors assert
  `written == reloaded` with record equality, and a `Dictionary` or `List` member compares by
  reference. That is why `shortcuts` is a property per action rather than a map.
- **A resource key names one thing.** `Themes/Dark.xaml` and `Light.xaml` hold brushes;
  `Controls.xaml` holds styles, under `Type.*`, `Button.*`, `Scroll.*` and so on. A style and a
  brush sharing a key (`Text.Muted` did, once) compiles, then throws at first layout with
  "'Style' is not a valid value for property 'Foreground'".

## Deliberate divergences from macOS

There are four, and they are spec decisions rather than drift —
[`STATUS.md`](STATUS.md#deliberate-divergences-from-macos) lists them with the reason for
each. `config.json` stays interchangeable in both directions regardless.
