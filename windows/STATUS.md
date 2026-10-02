# Local Caption for Windows — Implementation Status

Native Windows port of the macOS app in [`../app`](../app). This document reflects what is
actually built in `windows/`. The port plan is [`../SPEC-WINDOWS.md`](../SPEC-WINDOWS.md);
the product behaviour it targets is [`../SPEC.md`](../SPEC.md) and
[`../specs/STATUS.md`](../specs/STATUS.md).

**Last updated:** 2026-09-20 (driver updated to 616.92 — **B0 is complete and CUDA works**;
B1–B5 built and measured on the G15)

> **The app runs on the GPU.** Press Start and captions appear live; Stop writes a
> timestamped transcript and lists the session; killing it mid-recording gets the session
> offered back at the next launch. Running the shipping pair — `tiny.en` + `large-v3-turbo`
> on CUDA, 1.8 GB of VRAM, 82% GPU while decoding — it produces *"And so, my fellow
> Americans… Ask what you can do for your country."*

---

## Status at a glance

Following the Mac-first build order in §16.1. Stage A was written and tested on the Mac;
**B0 has now been run on the G15** — see [`BENCH-RESULTS.md`](BENCH-RESULTS.md).

| Stage | Area | Where | Status |
|---|---|---|---|
| **A1** | Core logic port + shared vectors + test suite | Mac | ✅ **Done** — and now green on Windows too |
| **A2** | `LocalCaption.Bench` harness | Mac | ✅ **Done** — executed on the G15 |
| **A3** | ASR engine wrapper, model/backend plumbing | Mac | ✅ **Done** — CPU decode path now exercised; CUDA still unexercised |
| A4 | Audio + WPF layers, drafted against the spec | Mac | ➖ **Dissolved.** The audio half is built and measured on the G15; the WPF half is B4 |
| **B0** | Bench + loopback probe ⭐ | G15 | ✅ **Done.** W9, §4.3, W7 and §5.3 all answered — see [`BENCH-RESULTS.md`](BENCH-RESULTS.md) |
| **B1** | WASAPI process + endpoint loopback | G15 | ✅ **Done** — both modes hold the clock within ±100 ms, and a default-endpoint switch mid-capture now recovers |
| **B2** | ASR on CUDA + orchestrator → **first live captions** | G15 | ✅ **Done** — live captions on CUDA, and §5.8's dGPU-loss fallback built |
| **B3** | Session lifecycle, transcript, journal, recovery | G15 | ✅ **Done** — a real session records, saves and recovers end to end |
| **B4** | Caption UI, clipboard, source picker, level meter | G15 | ✅ **Done** — WPF shell, AvalonEdit captions, level meter, both pickers, shortcuts |
| **B5** | Session list, settings | G15 | ✅ **Done** — searchable list, settings window, recovery prompt at launch |
| **B6** | Packaging, soak | G15 | 🟡 **Installer complete** — 623 MB with CUDA, verified running on the GPU. A 3-hour soak is the remaining item |

Verification: `dotnet build` clean, no warnings. **110 tests pass on Windows** (63 `Core`,
31 `Asr`, 16 `Audio`), plus the end-to-end runs the harnesses do against real hardware. The
suite's first runs on the target OS found two real bugs, one in crash recovery and one in the
sample clock (both below). The macOS suite remains green at **85 tests**, six of which are
the shared vectors.

### ✅ The blocker is gone — CUDA works on driver 616.92

Whisper.net's CUDA runtime needs CUDA 13, which needs an r580+ driver; the machine was on
555.97. The owner updated to **616.92** and the GPU path came up on the first try:
`cuda→cuda`, **tiny.en at 98/114 ms** against 597/700 on the CPU.

One piece was guesswork before and is now settled: **`nvcudart_hybrid64.dll` ships with the
display driver**, not in any CUDA redistributable, and it sits in `System32\DriverStore`
where the loader will not find it. `build/package.ps1` copies it out automatically. cuBLAS
and cudart still come from NVIDIA's redistributable archives.

**What the measurements decided** (full detail in [`BENCH-RESULTS.md`](BENCH-RESULTS.md) §2):

- **§5.3 confirmed** — `large-v3-turbo` is the right final default: 416/420 ms against a
  ~2 s budget, 1 765 MiB of VRAM for both models, and visibly better transcripts.
- **W7 answered: keep the hybrid.** Turbo alone clears the interim budget in isolation
  (420 ms) and **fails it under contention (690 ms p90, 38% over)**. §5.4 warned that
  isolated numbers would flatter the design, and they did. The shipping pair — tiny.en +
  turbo — runs 339 ms interim and 426 ms final under contention, inside both budgets.

---

## What is verified now

| Verified by running | Still never executed |
|---|---|
| Every caption algorithm, against vectors both platforms assert | Every **CUDA** decode path |
| Config load/repair/migrate, transcript and journal I/O, SQLite CRUD | DTW word timings against real audio (W2) |
| Pipeline ordering, coalescing, durability-before-publish | A Velopack installer, and an uninstall (§11) |
| Token→word grouping, including malformed spans | A session longer than a minute — nothing has been soaked |
| whisper.cpp decode on CPU: all three models, correct transcripts | Mode A against a **browser** — the process-tree case §4.5 cares about |
| Model download at full size — all three, 2.2 GB total | Clipboard auto-copy (§7.4 — needs the WPF clipboard) |
| WASAPI **endpoint** loopback, both endpoints including the virtual one | WPF — not written yet |
| WASAPI **process** loopback — activation, tree flag, packet path | |
| The capture layer end to end: ±100 ms clock on both modes, no drops | |
| **A whole session**: speech → capture → VAD → decode → journal → `.txt` + `.json` + DB row | |
| **Crash recovery**: `Stop-Process -Force` mid-session, journal survived, transcript recovered | |
| Sleep prevention acquired and released across a session (§4.7.6) | |
| **The application**: launch → Start → live captions on screen → Stop → saved and listed | |
| The launch-time recovery prompt, accepted, writing a `(recovered)` transcript | |
| Keyboard: `Ctrl+N` start and `Ctrl+.` stop, driving a whole session | |
| **A default-endpoint switch mid-capture** (§4.2): followed, clock intact, ±100 ms held | |
| **Pause and resume** through the UI — `duration_seconds` excluded the paused time | |
| A self-contained `win-x64` publish that launches and makes no network calls (§11) | |

B0 was the first time any ASR code executed, and it broke on first run exactly as predicted
— though for a packaging reason rather than a coding one. B1 broke on first run too, and
for a reason no unit test would have found: see the sample clock, below.

---

## Built

```
testdata/                        ← 26 shared vectors, asserted by BOTH platforms (§6.1)
windows/
├── src/
│   ├── LocalCaption.Core/       2011 lines · 18 files — pure logic, no WPF/Whisper/WASAPI
│   ├── LocalCaption.Asr/         571 lines ·  5 files — whisper.cpp via Whisper.net
│   ├── LocalCaption.Audio/                   8 files — WASAPI capture, both modes (§4)
│   ├── LocalCaption.Bench/       424 lines ·  2 files — B0: decode latency (not shipped)
│   ├── LocalCaption.Probe/                  10 files — B0/B1/B3 harness (not shipped)
│   ├── LocalCaption.Session/                 3 files — orchestrator, lifecycle, recovery (§8)
│   └── LocalCaption.App/                     9 files — WPF: the shipping application (§7)
└── tests/
    ├── LocalCaption.Core.Tests/ — 60 tests
    ├── LocalCaption.Asr.Tests/  — 25 tests
    └── LocalCaption.Audio.Tests/ — 11 tests
```

**`LocalCaption.Core`** — `SpeechSegmenter`, `RollingCaption`, `LocalAgreement`,
`CaptionPipeline`, `SerialDispatcher`, `CaptureBuffer`/`Processor`, `Filters`,
`SegmentQuality`, `Sentences`, `Transcript`/`Writer`, `Journal`/`Writer`, `Config`,
`Store`/`SessionRecord`, `TimeFormat`, `AppPaths`. Targets plain `net10.0`, which is what
lets the whole suite run on the Mac.

**`LocalCaption.Asr`** — `WhisperEngine` (two resident models, two factories, two
processors), `ModelCatalog`, `ModelDownloader`, `BackendProbe`, `WordTimings`.

**`LocalCaption.Bench`** — the §5.4 matrix harness: backend × model, p50/p90, RTF, plus a
concurrent-lane mode because the interim budget is specified *under* concurrent final
decoding. Each row now reports `requested→loaded`, because a silent fall back to the CPU
library is how the first "cuda" run passed itself off as a GPU run.

**`LocalCaption.Probe`** — the §4.7.3 harness: endpoint enumeration, mix format, loopback
init, packet arrival, `u64DevicePosition` monotonicity and the §4.3 silence gap. Its
`--play --gap` mode is §4.3's acceptance test made self-contained (tone, silence, tone),
`--keepalive` measures mitigation 1 directly, and `--capture` runs the same acceptance check
through `LocalCaption.Audio` so a B1 regression shows up against the hardware B0
characterised. It grew two more modes with B3: `--session` records a whole session against a
throwaway folder and database, and `--recover` turns leftover journals into transcripts —
which is how §9.4's "kill it with `Stop-Process -Force`" test is run.

**`LocalCaption.App`** — the WPF shell (§7): session list, active session, settings, and the
launch-time recovery prompt. `CaptionTextView` is the §7.2 risk answered — AvalonEdit with
suffix-only replacement and a colorised provisional tail. `ClipboardWriter` carries §7.4's
ten-attempt retry, and `WindowPlacement` refuses to restore a frame onto a monitor that is no
longer there.

**`LocalCaption.Session`** — `StreamingOrchestrator` (capture → VAD → interim/final decode,
the port of the macOS one), `SessionController` (the §8 state machine, journal, transcript
and save-on-stop) and `AppEnvironment` (config, session index, crash recovery). Deliberately
**outside** the WPF project that §15 puts it in, so B3 could be built and driven from a
console before B4 exists — which is how it was smoke-tested.

**`LocalCaption.Audio`** — `WasapiCapture` (the shared capture thread, clock and packet
loop), `EndpointLoopbackCapture` (mode B), `ProcessLoopbackCapture` + `ProcessLoopback` (mode
A and its `ActivateAudioInterfaceAsync` interop, which NAudio does not wrap),
`AudioNormalizer` (§4.4), `SilentRenderKeepalive`, `DeviceWatcher` (§4.2), `AudioSessions`
(the §4.5 source picker) and `SleepPrevention` (§4.7.6). `net10.0-windows` — with
`LocalCaption.Probe`, the only projects that cannot run on the Mac.

---

## Decisions and findings from stage A

- **The vectors are the parity mechanism.** 26 files in `testdata/` are asserted by the
  macOS *and* Windows suites. They were written first and validated against the Swift
  implementation, so they describe the reference rather than the port. Both harnesses were
  checked by deliberately corrupting a vector and confirming they fail.
- **The pipeline takes no locks.** Swift's `@MainActor` maps to a single-threaded
  `SynchronizationContext`, not a mutex — reentrancy at `await` is what lets an interim
  result publish while a slow final decode is in flight. Verified by sabotage: breaking
  coalescing or final ordering makes the suite hang and fail.
- **DTW word timings are available** (W2, previously an open risk).
  `WhisperFactoryOptions.UseDtwTimeStamps` + `HeadsPreset`, with presets for exactly the
  models we ship. It is a **factory-level** option, so it is decided before weights load.
  There is no word-level API, so `WordTimings` groups tokens on leading-space boundaries
  per §5.7 option 2.
- **Only the interim lane gets DTW.** The final lane does not need midpoints and does not
  pay for them.
- **The two lanes never share a factory or processor**, including when both roles pick the
  same model — the `e5ec3bd` rule (§5.1).
- **Culture and encoding are pinned.** `InvariantCulture` everywhere and UTF-8 without BOM
  with `\n` endings, because §17 verifies macOS/Windows parity by diffing transcripts.

### Deliberate divergences from macOS

| | macOS | Windows | Why |
|---|---|---|---|
| `asr.final_model` | `small.en` | `large-v3-turbo` | The RTX 3070 affords it (§0.5) |
| `summary.enabled` | `true` | `false` | Live AI Summary out of scope (§1.3) |
| `audio.*`, `asr.backend`/`threads` | — | added | Capture mode, device, backend (§9.2) |
| Always-on-top / opacity | shipped | in schema, cut from UI | Pointless over remote desktop (§7.3) |

`config.json` stays interchangeable in both directions regardless.

---

## Known gaps and risks

**1. Whisper.net's native runtimes abort on a second ggml load — on both platforms.**
On macOS, `Whisper.net.Runtime` pulls in `Whisper.net.Runtime.Metal`, both ship a ggml
library, and the second `abort()`s inside `dlopen`; the bench detects macOS and refuses
rather than crashing. **B0 found the same assert on Windows**, from
`Whisper.net.Runtime.Cuda.Windows` 1.7.4–1.8.1 whenever the CUDA backend genuinely loads —
and the 1.9.x CUDA runtime needs a newer driver than this machine has, so it does not load
at all and falls back to the CPU in silence. This is now the port's one blocker; see
[`BENCH-RESULTS.md`](BENCH-RESULTS.md) §1. "Windows is unaffected" was wrong.

**2. The `ICaptionMerger` seam was not built — and no longer needs to be.** §18.1 asked for both W2 merge strategies
behind one interface in A1, so the fallback would be a config flag. Both `RollingCaption`
and `LocalAgreement` exist and are tested, but the interim track will call `RollingCaption`
directly — switching is a small code change, not a config one. With W2's API now confirmed
the hedge matters less, but it is cheap now and annoying in B2. **Open decision.**

**3. W2 is closed.** The DTW API is wired, and the timings have now been measured against
the thing that depends on them: overlapping 6-second windows, 500 ms apart, comparing each
word's midpoint in session coordinates. Drift p50 **20 ms**, p90 290 ms, and **every** one of
ten merges offered a word inside `RollingCaption`'s 350 ms anchor. Keep the word-timed merge;
§5.7.1's fallback stays written and unused. Detail in
[`BENCH-RESULTS.md`](BENCH-RESULTS.md) §2.

**4. No audio fixtures yet.** `testdata/audio/*.wav` (§6.1) is still deferred. B0 used
whisper.cpp's public-domain `jfk.wav`, uncommitted; a proper fixture needs a working engine
on both sides, which now means after the CUDA blocker is resolved.

**6. §4.3's mitigation 2 does not cover the case B0 found — and does not exist at all in
mode A.** Device-position padding only corrects a gap once a packet arrives carrying a
position. On an endpoint with no render stream no packet ever arrives, and **process
loopback never reports a position in the first place** — it answers `0` on every packet,
measured (see [`BENCH-RESULTS.md`](BENCH-RESULTS.md) §3a). B1 therefore added the wall-clock
rule in `SampleClock`, which is now the *only* clock protection mode A has. §4.5's claim that
"the §4.3 device-position padding stays in place regardless" needs correcting in the spec.

**7. The keepalive gate in §4.3 is backwards for the Jump Desktop endpoint.** The spec says
to skip mitigation 1 on remote-desktop virtual devices. Measured: that endpoint produces
nothing without it. Also, it reports its form factor as `Speakers`, not
`RemoteNetworkDevice`, so the gate as specified could not identify it anyway.
`SilentRenderKeepalive` therefore runs on every endpoint in mode B, and not at all in mode A,
where there is no endpoint to keep alive and none is needed.

**8. §4.2's device-change recovery is done, and it cost two fixes.** Moving the default
playback device out from under a live capture is now an automated test
(`--switch-device`, which puts the device back afterwards). It failed twice before it passed:

- The watching started in `StreamingOrchestrator`, so a capture constructed anywhere else
  silently stayed on the dead endpoint. "Follow the system default" is a promise
  `EndpointLoopbackCapture` makes, so it now keeps that promise itself.
- The switchover lost **331 ms**. Rebuilding the client adopted the new device's origin and
  reset the clock without paying for the gap between the old stream's last packet and the new
  one's first. That time is part of the interview.

Now: follows the endpoint, keeps recording, and holds ±30 ms across the switch.

**9. B3 is proven on `tiny.en`, not on the shipping models.** The end-to-end run used
`tiny.en` for both lanes because CUDA is blocked; the transcript it produced is correct in
structure and timing and poor in wording ("So am I fellow Americans"). Nothing about the
lifecycle depends on which model decodes, but nobody should read that transcript as a quality
result.

**11. Packaging is complete.** `build/package.ps1` publishes self-contained ReadyToRun
`win-x64`, bundles the CUDA payload and packs a Velopack installer — per-user, no admin,
delta updates. Verified by extracting the package and running it: it loaded both models onto
the GPU (**+1 765 MiB VRAM**) and made **no outbound connections** (§11's offline
requirement). The publish prunes the Linux, macOS and ARM natives Whisper.net copies in
regardless of RID, and the script takes `nvcudart_hybrid64.dll` out of the driver store
because it ships nowhere else.

**The installer is 623 MB**, against §5.2's estimate of "a few hundred MB". `cublasLt64_13.dll`
alone is 493 MB — it carries kernels for every architecture. `nvprune` could cut it to this
GPU's compute capability, but it needs the CUDA toolkit, and 623 MB for a per-user install on
a machine with 193 GB free is not worth that dependency today.

**16. The soak harness outlasts its own audio.** Both long runs stopped producing captions
long before they ended — 12 minutes into the one-hour run, 5 into the twenty-minute one —
while the clock, memory and lifecycle stayed healthy to the end. The looping playback in
`SessionRun` degrades, not the app: drift over the full hour was −84 ms and the session saved
cleanly. It does mean **the long runs measure stability, not sustained recognition**, and a
real interview is still the only test of the latter.

**15. W2 is closed and the `ICaptionMerger` hedge is moot.** Known gap 2 worried that the
two merge strategies were not behind one seam, so switching would be a code change rather
than a config flag. W2 now says there is nothing to switch to: DTW word timings are stable
enough for `RollingCaption`'s anchor (10/10 merges, p50 drift 20 ms), so §5.7.1's fallback
stays written and unused. The seam is no longer worth building.

**14. whisper.cpp's own markup reached the screen.** The live caption read
`for your country. [_BEG_] [_BEG_] [_BEG_] And so my fellow … mirror[_TT_100]`. `WordTimings`
already dropped `<|…|>` special tokens, but whisper.cpp renders its *internal* ones in square
brackets, and asking for token timestamps — which the interim lane does, for W2 — is exactly
what makes it emit them. WhisperKit never produced either shape, so the ported filter had no
reason to know about it. Fixed and covered. It only became visible once the GPU made the
interim lane fast enough to watch.

**13. A rate error hid behind every short test.** The 20-minute soak came back 2323 ms fast
— 0.193%, steady, not a gap. Every earlier run had the same rate and passed, because §4.3's
acceptance is a *duration* (±100 ms) and 0.19% of thirty seconds is 52 ms. Ten seconds across
a ninety-minute interview.

Not the hardware: the raw WASAPI layer measured 179.99 s of audio in 180.01 s of wall clock
over the same three minutes. It was `AudioNormalizer` — this endpoint delivers **512-frame
packets**, 512 ÷ 3 is 170.667, and rounding up once per packet is 0.195%. The unit tests used
480-frame packets, which 3 divides exactly, so they could never have caught it. The normaliser
now accounts input frames and output samples as integers; re-measured, **+358 ms became
+5 ms**, and the tests run at 441, 480, 512 and 1024 frames.

**12. A second copy of the app offered to recover a live session.** Found by launching the
packaged build during the soak: the running session's journal looked exactly like an orphan,
and accepting the prompt would have deleted the only durable record of a recording in
progress. `Journal.Pending` now skips files another handle holds open. **Still open:** nothing
stops two copies running at once, and two sessions recording the same audio into two journals
is its own mess. A single-instance guard is the obvious fix and is not a decision to make
without the owner.

**10. The `(recovered)` path had a timezone bug, now fixed.** Segments store `created_at` as
ISO **UTC** (§9.1) and `TimeFormat.ParseIso` returns a UTC offset, so a recovered session was
headed *and filenamed* hours away from when it actually ran, while a normally-saved one used
local time. The Swift original cannot hit this — `Date` carries no offset. Worth remembering
as a class of porting bug: anywhere a `DateTimeOffset` comes back from `ParseIso`, it needs
`ToLocalTime()` before it is formatted.

**5. `.slnx`, not `.sln`.** .NET 10 defaults to the newer solution format. Cosmetic
deviation from §15.

---

## §17 acceptance criteria

| # | Criterion | State |
|---|---|---|
| 1 | Create, open, rename, delete, search sessions | ✅ built — sort is fixed at newest-first |
| 2 | Capture with no prompt or driver; device change recovers | ✅ **measured** (`--switch-device`) |
| 3 | 20 s silence gap → timestamps within ±100 ms | ✅ **measured** — 44 s run, 6 ms |
| 4 | Two models on-device; zero network once present | ✅ **measured** — no outbound connections |
| 5 | Interim p90 budget; finals ≤ ~2 s; text never rewritten | ✅ **measured** — 339 ms interim / 426 ms final under contention |
| 6 | 5-minute silent soak produces no captions | ✅ **measured** — five minutes, zero captions |
| 7 | Window frame restored, validated against live monitors | ✅ **measured** — an off-screen frame re-centres |
| 8 | Clipboard as configured, off by default, never read, retries | ✅ **measured** under contention |
| 9 | Settings persist; corrupt config repairs with a backup | ✅ **measured** — and once by accident |
| 10 | On Stop: `.txt` + `.json` + DB row, journal deleted | ✅ **measured** |
| 11 | `Stop-Process -Force` leaves a recoverable journal | ✅ **measured** |
| 12 | ≥ 3 h session, flat memory and handles | 🟡 **one hour on GPU: −84 ms drift**, 737 MB peak → 267 MB, handles 642 → 560. Three hours untested |
| 13 | Nothing transmitted, ever | ✅ by construction; verified idle |
| 14 | Same WAV → matching transcripts on macOS and Windows | ⚠ needs the Mac; the Windows side is ready |
| 15 | Installer needs no admin; runs on a clean machine | 🟡 **packaged and run here with CUDA**; no clean machine to try it on |
| 16 | dGPU toggled off mid-session does not lose the transcript | 🟡 **built** — auto-pause, CPU rebuild on resume; not yet tested against a real Eco-mode toggle |
| 17 | Endpoint switch mid-session, including Jump Desktop | 🟡 endpoint switch ✅; connect/disconnect untested |
| 18 | Meeting app pinned to a non-default device, mode A still works | ✅ **measured** — default moved to the virtual speaker, Edge still captured at peak 0.774 |
| 19 | 90-minute unattended remote session, no sleep | 🟡 sleep prevention held across an unattended hour; 90 minutes untested |
| 20 | Header shows live source name and a responsive meter | ✅ built and seen |
| 21 | Clipboard survives sustained remote-sync contention | ✅ **measured** — clipboard seized 83× during copies; app survived, text landed |

**Fifteen measured, none blocked.** The rest wait on a longer run, the Mac, or a person.

The harnesses that produce these are in `LocalCaption.Probe`: `--acceptance`, `--switch-device`,
`--session`, `--session --fail-save`, `--recover`, `--capture [--process <exe>]`.

---

## What's remaining

| Stage | Work | Where | Est. |
|---|---|---|---|
| B6′ | A **3-hour** soak — the one-hour run is flat, so this is time rather than work | G15 | 0.5 d |
| — | Toggle Eco mode mid-session and watch §5.8 recover for real (2 minutes in Armoury Crate) | G15 | — |
| — | Connect and disconnect Jump Desktop mid-recording (§17.17) | G15 | — |
| — | A real interview over Jump Desktop, end to end (§21.4) | G15 | — |
| | **Total** | | **~2–3 days of work, plus one real interview** |

Critical path: **`B2′ → B6′`**. Nothing is blocked on anyone.

### Recommendation: use it

**Every stage is built and the blocker is gone.** The application records on the GPU with the
shipping models, saves, lists and recovers; the installer packages the CUDA payload and runs;
and fourteen of §17's twenty-one criteria have been measured on this machine.

**The honest gap is use.** Everything here was exercised by a script against a looping
eleven-second clip. What is left is mostly not code:

- **A real interview over Jump Desktop** (§21.4). Every bug that mattered today came from
  running the thing — the rate error, the live-journal recovery prompt, the markup on screen
  — and none of them came from a test that was written first.
- **A 3-hour soak** (§17.12). A one-hour run is in hand and flat; three hours is what the
  spec asks.
- **A real Eco-mode toggle** (§17.16). §5.8's fallback is built and its decision is
  unit-tested, but nothing has taken the GPU away mid-sentence for real. It is a two-minute
  check in Armoury Crate.
- **The Mac, for §17.14.** The Windows half of the transcript-parity check is ready.

### The trap in B1, now measured

§4.3 predicted that **WASAPI loopback delivers no packets during digital silence**, stopping
the sample clock. Confirmed on the Jump Desktop Virtual Speaker: **zero packets in twelve
seconds**, and the specified fix — device-position padding — corrected exactly nothing,
because a packet has to arrive before it can carry a position. What did work was the
keepalive the spec says to skip on that very endpoint: with a silent render stream open,
the same endpoint held −18 ms over 12 s.

So B1 needed all three: the keepalive **on every endpoint including the virtual one**,
device-position padding for gaps that end, and a wall-clock rule for gaps that do not. All
three are built, and the wall-clock rule turned out to carry mode A single-handedly — process
loopback reports no device position at all.

**The clock's own bug is worth remembering**, because no unit test would have found it: the
first implementation started the clock on the first packet, which on a silent endpoint never
arrives. Fifteen seconds of session vanished while every indicator read healthy. It was found
by deliberately disabling the keepalive on real hardware — which is why
`--capture --no-keepalive` exists and why `EndpointLoopbackCapture` takes a `keepalive: false`
it never uses in production.

---

## Running it

Needs the **.NET 10 SDK**. If it is not installed:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"
```

```bash
cd windows && PATH="$HOME/.dotnet:$PATH" dotnet test
```

On the G15 the runtime is already present and only the SDK is missing — install it with
`dotnet-install.ps1 -Channel 10.0`. The B0 harnesses and how to re-run them are in
[`BENCH-RESULTS.md`](BENCH-RESULTS.md).

The macOS suite must stay green too — it asserts the same vectors:

```bash
cd app && swift test
```
