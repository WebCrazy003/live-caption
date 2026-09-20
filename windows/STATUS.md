# Local Caption for Windows — Implementation Status

Native Windows port of the macOS app in [`../app`](../app). This document reflects what is
actually built in `windows/`. The port plan is [`../SPEC-WINDOWS.md`](../SPEC-WINDOWS.md);
the product behaviour it targets is [`../SPEC.md`](../SPEC.md) and
[`../specs/STATUS.md`](../specs/STATUS.md).

**Last updated:** 2026-09-20

---

## Status at a glance

Following the Mac-first build order in §16.1. Stage A is written and tested on this Mac;
stage B needs the ASUS G15.

| Stage | Area | Where | Status |
|---|---|---|---|
| **A1** | Core logic port + shared vectors + test suite | Mac | ✅ **Done** |
| **A2** | `LocalCaption.Bench` harness | Mac | ✅ **Done** — compiles; cannot execute here (see below) |
| **A3** | ASR engine wrapper, model/backend plumbing | Mac | ✅ **Done** — compiles; pure logic tested, decode path unexercised |
| A4 | Audio + WPF layers, drafted against the spec | Mac | ⬜ Not started — *see the recommendation below* |
| **B0** | Run bench + loopback probe ⭐ | G15 | ⬜ **Next.** Answers W2, W7, W9 |
| B1 | WASAPI process + endpoint loopback | G15 | ⬜ Not started |
| B2 | ASR on CUDA + orchestrator → **first live captions** | G15 | ⬜ Not started |
| B3 | Session lifecycle, transcript, journal, recovery | G15 | ⬜ Not started |
| B4 | Caption UI, clipboard, source picker, level meter | G15 | ⬜ Not started |
| B5 | Session list, settings | G15 | ⬜ Not started |
| B6 | Packaging, soak | G15 | ⬜ Not started |

**Roughly a third of the port is done** — the third the spec identified as most
correctness-critical and most expensive to get wrong late.

Verification: `dotnet build` clean, no warnings. **66 tests pass** (41 `Core`, 25 `Asr`).
The macOS suite remains green at **85 tests**, six of which are the shared vectors.

---

## What "done" means for stage A, honestly

Stage A is a correctness exercise, not a working application. Nothing built so far has seen
real audio, a GPU, or a window.

| Verified by running | Written but never executed |
|---|---|
| Every caption algorithm, against vectors the macOS build also asserts | Every whisper.cpp decode path |
| Config load/repair/migrate, transcript and journal I/O, SQLite CRUD | CUDA and the backend probe's Windows branch |
| Pipeline ordering, coalescing, durability-before-publish | Model download at full size (only `tiny.en`, 75 MB, has been fetched) |
| Token→word grouping, including malformed spans | WASAPI, WPF — not written yet |

**B0 is the first time any ASR code executes.** That is a change from the original plan,
which assumed the bench could be smoke-tested on macOS first — see the runtime limitation
below. Budget for first-run breakage there.

---

## Built

```
testdata/                        ← 26 shared vectors, asserted by BOTH platforms (§6.1)
windows/
├── src/
│   ├── LocalCaption.Core/       2011 lines · 18 files — pure logic, no WPF/Whisper/WASAPI
│   ├── LocalCaption.Asr/         571 lines ·  5 files — whisper.cpp via Whisper.net
│   └── LocalCaption.Bench/       424 lines ·  2 files — the B0 harness (not shipped)
└── tests/
    ├── LocalCaption.Core.Tests/ 1172 lines — 41 tests
    └── LocalCaption.Asr.Tests/   281 lines — 25 tests
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
decoding.

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

**1. `LocalCaption.Bench` cannot run on macOS.** Whisper.net 1.9.1's
`Whisper.net.Runtime` depends on `Whisper.net.Runtime.Metal`; both ship a ggml library,
both run ggml's static initialiser, and the second `abort()`s inside `dlopen`. It is a
native abort, not a catchable exception, and it is upstream — 1.8.1 and 1.7.4 do not load
on arm64 at all, and Metal-only omits the base library. Windows is unaffected. The bench
detects macOS and refuses with an explanation rather than crashing.

**2. The `ICaptionMerger` seam was not built.** §18.1 asked for both W2 merge strategies
behind one interface in A1, so the fallback would be a config flag. Both `RollingCaption`
and `LocalAgreement` exist and are tested, but the interim track will call `RollingCaption`
directly — switching is a small code change, not a config one. With W2's API now confirmed
the hedge matters less, but it is cheap now and annoying in B2. **Open decision.**

**3. W2 is narrowed, not closed.** The DTW *API* is confirmed and wired. Whether the
timings are accurate enough for `RollingCaption`'s 350 ms anchor needs real audio (B0).
The §5.7.1 fallback is unchanged.

**4. No audio fixtures yet.** `testdata/audio/*.wav` (§6.1) is deferred — it needs a
working ASR engine on both sides to mean anything, which is B0 at the earliest.

**5. `.slnx`, not `.sln`.** .NET 10 defaults to the newer solution format. Cosmetic
deviation from §15.

---

## What's remaining

| Stage | Work | Where | Est. |
|---|---|---|---|
| A4 | Audio + WPF layers drafted | Mac | 3–4 d |
| B0 | Bench + loopback probe; fill `BENCH-RESULTS.md` | G15 | 0.5 d |
| B1 | WASAPI process + endpoint loopback, resampling, device changes, silence padding, sleep prevention | G15 | 4–6 d |
| B2 | ASR on CUDA, dGPU-loss fallback (§5.8), orchestrator | G15 | 4–6 d |
| B3 | Lifecycle, transcript, journal, recovery | G15 | 2–3 d |
| B4 | Caption view, clipboard, source picker + level meter | G15 | 4–5 d |
| B5 | Session list, settings | G15 | 3–4 d |
| B6 | Packaging, soak | G15 | 3–4 d |
| | **Total** | | **~5–7 weeks** |

Critical path: **`B0 → B2 → B3 → B4 → B6`**. A4 is off it.

### Recommendation: skip A4, go to B0

A4 produces WPF and WASAPI code that **compiles but cannot run** on this Mac — WPF needs
`EnableWindowsTargeting=true` and has no macOS runtime; WASAPI is Windows COM. Writing it
here means debugging blind, and B0 may invalidate some of it.

B0 is half a day and it is worth doing before anything else on the G15, because it can
*delete* work:

> **W7 — is turbo-only streaming viable?** If `large-v3-turbo` decodes a 6 s window in well
> under 500 ms on the 3070, the dual-model architecture collapses to one resident model,
> taking the interim/final split and the two-lane isolation rule with it. The Mac said no on
> its hardware; this GPU may answer differently.

### The one trap waiting in B1

§4.3: **WASAPI loopback delivers no callbacks at all during digital silence**, so the
sample clock silently stops. That breaks session timing and endpointing, and it is
invisible until a transcript's timestamps are wrong. The spec flags it as the one real trap
in the audio layer.

---

## Running it

Needs the **.NET 10 SDK**. If it is not installed:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"
```

```bash
cd windows && PATH="$HOME/.dotnet:$PATH" dotnet test
```

The macOS suite must stay green too — it asserts the same vectors:

```bash
cd app && swift test
```
