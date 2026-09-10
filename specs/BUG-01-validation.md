# BUG-01 — Implementation validation

**Date:** 2026-09-10\
**Related:** [Bug specification](BUG-01-caption-stalls.md)\
**Result:** Automated regressions and local-model checks pass. Extended real-call acceptance remains open.

## Environment and fixture

- Mac mini (`Macmini9,1`), Apple M1, 16 GB RAM; macOS 14.7.6.
- WhisperKit 0.18.0; cached `tiny.en` interim and `small.en` final models; CPU+GPU.
- 26.401125 seconds of synthetic English interview speech: Samantha at 170 words/minute,
  converted to 16 kHz mono Float32 WAV. No live system audio or microphone was captured.
- WAV SHA-256: `97df60f81a50209b5670824f57065acace66c439b3138c7eb601ebb2c5c8a0c2`.
- Summary generation was not running as part of this test. This was a debug test build.

Fixture text (78 words after punctuation normalization):

> Please explain how you would design a reliable notification service. Imagine that
> several customers update their preferences at the same time. We need to preserve
> the order of their changes and avoid sending the same message twice. Describe how
> you would handle a slow database, retry a failed delivery, and measure the time
> between receiving a request and displaying the result. We can retry, we can retry,
> but we must keep track of which request has already succeeded.

## Automated tests

`swift test` compiles the app and runs **74 non-replay tests**, all passing. Two
additional opt-in model tests passed separately with `LOCALCAPTION_REPLAY_WAV` set.

Coverage includes a five-second delayed final, pending-interim coalescing, advancing
in-flight results, stale/previous-generation result rejection, final ordering,
overload notification, independent clipboard refresh, filtered/error outcomes, slow
interim cancellation without overlapping model calls, durable final acknowledgment,
rolling windows and repeated phrases, 300 seconds of silent input without decode
requests, the utterance cap, the last partial audio frame, latched buffer overflow,
journal recovery, save-failure retry without clearing unsaved text, repeated automatic
pause requests, and caption observation independent of the session clock.

## On-device replay

The comparison feeds the same audio in 100 ms frames at real-time speed through:

1. The previous serial interim/final queue and prefix-only `LocalAgreement` logic.
2. The new independent lanes and word-timed `RollingCaption` logic.

Both use the **current** engine options and VAD, including the short-window cutoff fix.
This isolates scheduling and assembly; it is not a comparison against an unmodified
historical app binary. Measurements are test-host caption-publication events, not
SwiftUI frame presentation or annotated word-to-display latency.

| Measurement | Serial/prefix baseline | Independent/timed windows |
|-------------|------------------------|---------------------------|
| First nonempty caption | 657 ms | 716 ms |
| Changed, nonempty caption publications | 32 | 45 |
| p95 interval between publications | 1,661 ms | 1,033 ms |
| Maximum interval between publications | 8,619 ms | 1,633 ms |
| Final word errors against fixture | 0 / 78 | 0 / 78 |

The final transcripts were identical between the two streaming passes, including
punctuation. Word-error comparison with the fixture ignored punctuation and case.
The fixed pass continued producing interims while a final decode took **4,466 ms**.
The separate six-second-window check decoded an interim in **302 ms** alone and
**568 ms** while final inference ran. These are individual observations, not model
latency percentiles or guarantees for other audio/hardware.

Both real-model tests passed, including concurrent transcription of different audio
with `tiny.en` selected for both roles. The models returned word alignments, a
one-second final utterance was decoded, and silent interim audio produced no caption.

## Additional findings during implementation

- A cold interim prediction initially exceeded the two-second deadline. Loading
  model weights alone did not establish readiness for live inference.
- WhisperKit's default one-second `windowClipTime` skipped 500 ms and 1,000 ms input
  windows altogether. It also skipped a one-second warm-up. Both lanes now use a zero
  cutoff, and preparation runs actual inference over two seconds of silence.
- Disabling the first-token confidence threshold did not fix the short-window issue;
  that experiment was reverted. The original confidence gates remain enabled.
- Earlier captions remain provisional: the first 500 ms snapshot decoded “Please
  exit.”, corrected to “Please explain how” at one second. Final text contained no word
  errors. Showing text sooner does not guarantee each short interim is accurate.

## Reproduction

Generate the fixture with `say -v Samantha -r 170 -o /tmp/localcaption-replay.aiff`
and the text above. Convert it with:

```sh
afconvert /tmp/localcaption-replay.aiff /tmp/localcaption-replay.wav -d LEF32@16000 -c 1 -f WAVE
```

From `app/`, with models already downloaded:

```sh
swift test
LOCALCAPTION_REPLAY_WAV=/tmp/localcaption-replay.wav LOCALCAPTION_REPLAY_STREAM=1 swift test --filter EngineReplayTests
```

In the restricted development environment, compilation needed writable module caches
(`CLANG_MODULE_CACHE_PATH` and `SWIFTPM_MODULECACHE_OVERRIDE` under `/tmp`) and
`--disable-sandbox --disable-automatic-resolution`. macOS speech synthesis and CoreML
replay required approved execution outside that environment's sandbox. The final
reported model results were collected with CoreML access enabled.

## Acceptance still pending

The measured p95 publication gap of 1,033 ms is slightly above the proposed 1,000 ms
target, and this fixture includes natural pauses. Do not relabel that measurement as
a pass or use frequent publications as a proxy for word freshness.

Before closing BUG-01, collect a five-minute annotated speech replay, quiet/noisy
real-audio accuracy comparisons, summary-enabled contention measurements, actual UI
presentation timing, and long-session capture/pause/recovery checks. The synthetic
test supports the scheduling/assembly fix but does not establish those broader claims.
