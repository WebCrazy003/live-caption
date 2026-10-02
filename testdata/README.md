# Cross-platform conformance vectors

Platform-neutral test vectors for the pure-logic layer, shared by **both** implementations:

| Suite | Reads these |
|---|---|
| macOS — `app/Tests/LocalCaptionKitTests/ConformanceTests.swift` | captions, filters, sentences, config |
| macOS — `app/Tests/LocalCaptionKitTests/InterviewConformanceTests.swift` | `hotkey/`, `ask/`, `interview-prompt/` |
| macOS — `app/Tests/LocalCaptionKitTests/CodexRPCTests.swift` | `codex/` |
| Windows — `windows/tests/LocalCaption.Core.Tests/ConformanceTests.cs`, `ConfigConformanceTests.cs` | captions, filters, sentences, config |
| Windows — `windows/tests/LocalCaption.Core.Tests/InterviewConformanceTests.cs` | `hotkey/`, `ask/`, `interview-prompt/` |
| Windows — `windows/tests/LocalCaption.Core.Tests/CodexRpcTests.cs` | `codex/` |

Specified by [`SPEC-WINDOWS.md`](../SPEC-WINDOWS.md) §6.1. The point is to make "parity" a
checkable claim rather than an aspiration: a port that is subtly different fails here
rather than three months later.

**These files are the contract. Do not edit a vector to make a failing port pass** — a
disagreement means either the port is wrong, or the vector was wrong for both platforms
and the macOS suite must be re-run to prove the new value.

## Layout

```
testdata/
├── segmenter/       frames in → expected requests + transitions out  (SpeechSegmenter)
├── rolling/         hypothesis sequences → expected merged word list (RollingCaption)
├── localagreement/  hypothesis sequences → expected committed/provisional (LocalAgreement)
├── filters/         text + metadata → expected verdict                (Filters)
├── sentences/       transcript + N → expected clipboard string        (Sentences)
├── config/          malformed configs → expected repaired config      (Config)
├── hotkey/          hotkey strings → canonical form or error kind      (Hotkey — SPEC-11)
├── ask/             finals + interim + mark → text sent on an Ask      (AskSelection — SPEC-14)
├── interview-prompt/ skill step / question / transcript → exact prompt text (InterviewPrompt — SPEC-13–15)
└── codex/           Codex app-server requests, decoding, events, responses (CodexRPC — SPEC-12)
```

The `hotkey/`, `ask/`, `interview-prompt/` and `codex/` suites belong to Interview Assist
([`specs/SPEC-11`](../specs/SPEC-11-interview-assist.md)); the Windows port of it is
[`specs/SPEC-16`](../specs/SPEC-16-windows-parity.md). Every file here is read byte for byte,
so the repository's `.gitattributes` keeps them LF on every platform.

`audio/*.wav` (16 kHz mono fixtures + expected transcripts) is deferred to stage B0 —
it needs a working ASR engine on both sides to be meaningful.

## Conventions

- **Times** are seconds (`double`); **samples** are 16 kHz mono frame counts (`int`).
- Audio is expressed as **runs** — `{"amplitude": 0.1, "count": 16000}` means 16000
  consecutive samples of that constant value — so vectors stay readable and small.
- `"$default"` in a config expectation means "equal to the freshly-constructed default
  config's value at this key path". It exists because a few defaults are legitimately
  platform-specific (`general.transcript_folder`, `summary.enabled`), and the vector
  asserts *"the default was applied"*, not a literal value.
- Key paths are dotted and use the **on-disk snake_case JSON names**, not the field names
  of either language.
