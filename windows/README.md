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

## Packaging

```powershell
build\package.ps1 -Version 1.0.0 -CudaDirectory <folder with cublas64_*, cudart64_*>
```

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
- **Window shortcuts need `InputBindings`, not command gestures.** AvalonEdit's text area
  handles keys first, so a `RoutedCommand` carrying a `KeyGesture` never fires while the
  captions have focus. `Ctrl+.` was silently dead until this was found by pressing it.

## Deliberate divergences from macOS

There are four, and they are spec decisions rather than drift —
[`STATUS.md`](STATUS.md#deliberate-divergences-from-macos) lists them with the reason for
each. `config.json` stays interchangeable in both directions regardless.
