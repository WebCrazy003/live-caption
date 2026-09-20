# Local Caption for Windows

Native Windows port of the macOS app in [`../app`](../app). The specification is
[`../SPEC-WINDOWS.md`](../SPEC-WINDOWS.md); this file only records **how to build it and
where the work currently stands**.

The two apps share no code — what they share is the specification, the file formats, the
config schema and the [conformance vectors](../testdata) (§6.1).

## Status

**See [`STATUS.md`](STATUS.md)** for current progress, what is verified versus merely
compiled, known gaps and the remaining work.

In short: stages **A1–A3 are done** (core logic, ASR wrapper, bench harness — 66 tests
green on the Mac); **B0 on the G15 is next**. Nothing built so far has seen real audio, a
GPU or a window.

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
│   ├── LocalCaption.Core/         ← pure logic. NO WPF, NO Whisper.net, NO WASAPI.
│   │   ├── Audio/                 SpeechStream (segmenter, requests), CaptureBuffer
│   │   ├── Captions/              RollingCaption, LocalAgreement, CaptionPipeline,
│   │   │                          SerialDispatcher, Filters, SegmentQuality, Sentences
│   │   ├── Data/                  Config, Store, Journal, Files, SessionFiles
│   │   ├── Transcripts/           Transcript, TranscriptWriter, TimeFormat
│   │   └── AppPaths.cs
│   ├── LocalCaption.Asr/          ← whisper.cpp via Whisper.net; models, backend, word timings
│   └── LocalCaption.Bench/        ← the §5.4 B0 harness (not shipped)
└── tests/
    ├── LocalCaption.Core.Tests/   ← reads ../../testdata, shared with macOS
    └── LocalCaption.Asr.Tests/    ← runs without the native library
```

`LocalCaption.Core` must not take a dependency on WPF, Whisper.net or WASAPI. That
separation is what keeps this suite fast and a future convergence cheap. The §15 projects
still to come — `LocalCaption.Audio` and `LocalCaption.App` — are where that code belongs.

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

There are four, and they are spec decisions rather than drift —
[`STATUS.md`](STATUS.md#deliberate-divergences-from-macos) lists them with the reason for
each. `config.json` stays interchangeable in both directions regardless.
