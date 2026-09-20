# Local Caption for Windows

Native Windows port of the macOS app in [`../app`](../app). The specification is
[`../SPEC-WINDOWS.md`](../SPEC-WINDOWS.md); this file only records **how to build it and
where the work currently stands**.

The two apps share no code — what they share is the specification, the file formats, the
config schema and the [conformance vectors](../testdata) (§6.1).

## Status

Following the Mac-first build order in §16.1.

| Stage | Work | Where | Status |
|---|---|---|---|
| **A1** | Core logic port + shared vectors + test suite | Mac | ✅ **done** — 33 tests green |
| A2 | `LocalCaption.Bench` harness | Mac | ⬜ not started |
| A3 | ASR engine wrapper, model/backend plumbing | Mac | ⬜ not started |
| A4 | Audio + WPF layers, drafted against the spec | Mac | ⬜ not started |
| B0 | Run the bench + loopback probe on the G15 | G15 | ⬜ blocked on A2/A3 |
| B1–B6 | WASAPI, CUDA, lifecycle, UI, packaging | G15 | ⬜ not started |

**What A1 settled.** `LocalCaption.Core` targets plain `net10.0` — no WPF, no Whisper.net,
no WASAPI — so it builds and its whole suite runs on macOS, which is what let the most
correctness-critical part of the port be verified before any Windows-specific code exists.
Ported and passing: `SpeechSegmenter`, `RollingCaption`, `LocalAgreement`, `CaptionPipeline`,
`CaptureBuffer`/`CaptureProcessor`, `Filters`, `Sentences`, `Transcript`/`TranscriptWriter`,
`Journal`/`JournalWriter`, `Config`, `Store`/`SessionRecord`, `TimeFormat`, `AppPaths`.

**What A1 could not prove.** Nothing here has seen real audio, a GPU or a window. Stage A
is a correctness exercise; B0–B2 are where the port becomes an application.

## Build and test

Needs the **.NET 10 SDK**. On the Mac, if you do not have it:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"
```

Then, from this directory:

```bash
PATH="$HOME/.dotnet:$PATH" dotnet test
```

The macOS suite must stay green too — it runs the same vectors:

```bash
cd ../app && swift test
```

## Layout

```text
windows/
├── LocalCaption.slnx              ← .NET 10 defaults to the .slnx solution format
├── src/
│   └── LocalCaption.Core/         ← pure logic. NO WPF, NO Whisper.net, NO WASAPI.
│       ├── Audio/                 SpeechStream (segmenter, requests), CaptureBuffer
│       ├── Captions/              RollingCaption, LocalAgreement, CaptionPipeline,
│       │                          SerialDispatcher, Filters, Sentences
│       ├── Data/                  Config, Store, Journal, Files, SessionFiles
│       ├── Transcripts/           Transcript, TranscriptWriter, TimeFormat
│       └── AppPaths.cs
└── tests/
    └── LocalCaption.Core.Tests/   ← reads ../../testdata, shared with macOS
```

`LocalCaption.Core` must not take a dependency on WPF or Whisper.net. That separation is
what keeps this suite fast and a future convergence cheap — the projects in §15 that do not
exist yet (`LocalCaption.Audio`, `.Asr`, `.App`, `.Bench`) are where those belong.

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

## Deliberate divergences from macOS

These are spec decisions, not drift:

| | macOS | Windows | Why |
|---|---|---|---|
| `asr.final_model` | `small.en` | `large-v3-turbo` | The RTX 3070 affords it (§0.5) |
| `summary.enabled` | `true` | `false` | Live AI Summary out of scope (§1.3) |
| `audio.*`, `asr.backend/threads` | — | added | Capture mode, device, backend (§9.2) |
| Always-on-top / opacity | shipped | kept in schema, cut from UI | Pointless over remote desktop (§7.3) |

The `config.json` schema stays interchangeable in both directions regardless.
