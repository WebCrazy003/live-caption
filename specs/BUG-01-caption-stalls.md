# BUG-01 — Live captions pause, then appear in a burst

**Status:** Implemented — automated regressions pass; extended on-device acceptance pending\
**Reported:** 2026-09-09\
**Priority:** High — interrupts the app's primary live-captioning function\
**Related:** [ASR streaming](SPEC-03-asr-engine.md), [audio capture](SPEC-02-audio-capture.md), [caption UI](SPEC-05-caption-ui.md), [session persistence](SPEC-04-session-transcript.md)

## Problem

While the interviewer continues speaking, captions sometimes stop updating for about
five seconds, then display a large amount of text at once. The user cannot reliably
follow the conversation during the gap.

Expected behavior: provisional captions continue advancing during speech, including
while an earlier utterance is undergoing final transcription. Final text replaces the
matching provisional text without erasing newer speech.

The investigation identified reproducible text-stabilization failure and a serial
decode bottleneck. Neither has yet been correlated with the reported incident using
runtime timing data; five seconds is the observed symptom, not a configured timeout.

## Configuration inspected

The saved configuration at investigation time was:

| Setting | Value |
|---------|-------|
| Interim / final model | `tiny.en` / `small.en` |
| Interim interval | 500 ms |
| Endpoint silence | 600 ms |
| Maximum utterance | 20 s |
| VAD sensitivity | 2, mapped to RMS threshold 0.015 |
| Live summary | Disabled |
| Automatic clipboard update | Enabled |

The interim audio window is capped at six seconds in code. WhisperKit is pinned to
0.18.0 in `app/Package.resolved`. Saved settings do not prove which settings were in
effect during the reported incident.

## Findings and confidence

This section describes the pre-fix code investigated on 2026-09-09. The implementation
below replaces those paths; source links now point to the updated files.

### 1. Final decoding blocks interim decoding — confirmed code path

[`StreamingOrchestrator.beginLoops()`](../app/Sources/LocalCaption/ASR/StreamingOrchestrator.swift)
feeds `.interim` and `.finalize` work into one default, unbounded `AsyncStream`. One
worker awaits each decode before starting the next item. A final decode therefore
blocks all subsequent interim work, even though the models are separate instances.

Capture continues during the wait. Once an interim is queued, `interimInFlight`
prevents replacing it with a fresher snapshot until that work completes. Finals can
also accumulate, increasing queue delay.

The repository records approximately 456 ms for `tiny.en` and 2.06 s for `small.en`
on a seven-second clean clip ([recorded benchmark](STATUS.md)). These are historical
measurements, not measurements of this incident. Slow decoding, retries, or queued
finals could extend a caption gap to five seconds or longer.

### 2. Rolling audio conflicts with prefix stabilization — reproduced

The orchestrator submits only the newest six seconds of an utterance, while
[`LocalAgreement.update()`](../app/Sources/LocalCaptionKit/LocalAgreement.swift)
compares hypotheses as if they all start at the same word. Its committed prefix never
shrinks, and its provisional tail is obtained by removing that prefix's word count
from the new hypothesis, without aligning the overlapping audio/text.

When the window moves, new words can be discarded. If the new hypothesis is no longer
than the old committed prefix, the display can remain completely unchanged until a
final result arrives. This is a display-assembly failure even when inference is fast.

The following sequence was run against the existing Swift implementation:

| Input hypothesis | Displayed result |
|------------------|------------------|
| `one two three four five six` | `one two three four five six` |
| `one two three four five six seven` | `one two three four five six seven` |
| `one two three four five six seven` | `one two three four five six seven` |
| `two three four five six seven eight` | `one two three four five six seven` |
| `three four five six seven eight nine` | `one two three four five six seven` |
| `four five six seven eight nine ten` | `one two three four five six seven` |

This confirms the mechanism; it is not a replay of the user's audio.

### 3. Decode retries can amplify gaps — configuration confirmed, occurrence unverified

[`WhisperEngine`](../app/Sources/LocalCaption/ASR/WhisperEngine.swift) uses the same
decoding options for interim and final work and leaves fallback settings at defaults.
The pinned WhisperKit source permits five temperature fallback retries after the
initial attempt. Its decoder can retry uncertain or repetitive results before returning.
The app supplies no progress callback and publishes only after transcription returns.

Source: [WhisperKit decoding options at the pinned revision](https://github.com/argmaxinc/WhisperKit/blob/e2adabbe7d98dc4d0ab9a5b75424ecc42a9cdbef/Sources/WhisperKit/Core/Configurations.swift#L192).

### 4. Short-window cutoff and cold inference — confirmed during implementation

The pinned WhisperKit `TranscribeTask` defaults `windowClipTime` to one second and
executes its decoder loop only while `seek < seekClipEnd - windowPadding`. A 500 ms
or 1,000 ms live window therefore returns empty without inference. This was reproduced
with audible synthetic speech and explained the approximately 1.7-second first caption
in the initial replay. It also means a one-second warm-up does not exercise the full
model. Both lanes now use `windowClipTime: 0`, and preparation warms both instances
with two seconds of generated silence before reporting Ready. Confidence/hallucination
filters remain enabled. A first prediction before proper warm-up exceeded the interim
budget; a later warmed concurrent prediction took approximately 429 ms on this Mac.

### 5. Other possible contributors — require measurement

- **Speech detection and filters:** interim scheduling requires a frame above a fixed
  RMS threshold. Quiet speech can be missed or split into extra endpoints. Metadata
  and text filters can reject decoded results; thrown decode errors become empty
  strings. These outcomes currently lack distinct diagnostics.
- **UI observation:** `ActiveSessionView` observes `SessionController`, which owns an
  unobserved nested orchestrator. Orchestrator publications alone do not directly
  invalidate that view. The controller's elapsed-time updates can mask this wiring
  issue, so it does not independently establish a five-second delay.
- **Main-thread work:** the capture-drain/VAD loop runs on the main actor, and final
  ingestion synchronously writes and synchronizes the journal before updating the
  displayed transcript. Slow disk operations or UI work could delay processing.
  Awaiting inference on the main actor does not itself prove synchronous inference
  blocks the main thread.
- **Capture backlog:** `SampleBuffer` is unbounded and drained in batches, with no
  audio-arrival or buffer-age measurements. Capture delivery gaps and delayed draining
  cannot currently be distinguished from slow decoding.
- **Cold inference or hardware load:** model prewarming is disabled. First-use latency
  and CPU/GPU contention need separate measurements. The summary feature was disabled
  in the inspected configuration.

## Implementation decisions (2026-09-10)

These decisions resolve the review's open questions and take precedence over the
original exploratory implementation tasks below.

- **Model ownership:** two independently loaded WhisperKit instances, even for equal
  model selections. Exactly one active call per instance; interim and final lanes
  may run simultaneously. Final decoding accuracy options and quality filters are
  retained; only the short-window cutoff changes (`windowClipTime: 0` in both lanes).
- **Alignment:** interim inference requests word timestamps. `RollingCaption` converts
  them to session coordinates. A same-origin result replaces that window's provisional
  suffix, allowing shorter corrections. A moving window first looks for a normalized
  lexical match within 350 ms; its earliest time-compatible anchor preserves the old
  prefix. If no anchor exists, new words replace the covered time range using word
  start/midpoint times. Punctuation does not affect lexical matching. Repetition at
  different times is retained. Missing/invalid timings preserve the last valid text
  and report the problem; the code does not invent word timestamps from word counts.
  Recognition or alignment errors can still revise provisional text; zero omissions
  and duplicates apply to deterministic assembly fixtures, not arbitrary ASR output.
- **Freshness:** keep one active and one replaceable pending interim. A newer request
  does not invalidate a useful in-flight result. Reject results from completed
  utterances, previous capture generations, non-advancing windows, and windows ending
  more than three seconds behind the newest submitted audio. This three-second guard
  is a stale-result ceiling, not the one-second performance target.
- **Interim execution:** zero temperature fallback retries, 128-token ceiling, and a
  two-second cooperative deadline. Both models receive an actual two-second audio
  warm-up before Ready. The watchdog requests cancellation and shows
  “Live captions are catching up…”. The model callback also requests early stopping.
  An outstanding CoreML operation may exceed the deadline; its result is discarded
  and its replacement waits for it to return. We do not claim hard preemption or run
  overlapping calls on one model instance.
- **Overload:** request automatic pause at 40 seconds of outstanding final audio
  (including active work awaiting journal acknowledgment), or 16 queued final jobs.
  Stop delivery, retain/drain accepted audio, and finish finals. The maximum accepted
  utterance is clamped to the existing Settings range of 5–60 seconds. The handoff
  buffer holds five seconds; overflow is latched, counted, and visibly reported before
  automatic pause. A threshold-crossing utterance and shutdown tail can exceed the
  warning threshold; it is not a hard 40-second memory limit. The capture handoff is
  bounded independently so a stalled main actor cannot cause unbounded growth.
- **Utterance lifecycle:** active → pending final → retired. Final success is published
  after the ordered journal acknowledgment, then only its own provisional text is
  retired. Newer utterances remain visible. Failed finals report their time interval
  as missing and retire their provisional text without saving it as accurate text.
  Empty/filtered finals that replace visible provisional words also report rejection.
  Endpoint clipboard text includes pending provisional utterances through that endpoint.
- **Persistence:** journal fsync runs on an actor. The final lane waits for its result;
  the interim lane and UI remain independent. Failed saves offer Retry save; Start
  cannot replace a session with unsaved text. Capture-error pause requests received
  during startup/resume are latched until the transition finishes. On journal failure, retain final text in
  memory for Stop/save and display a persistent save error. Pause/Stop are serialized;
  Pause shows “Finishing speech…” until accepted audio and final writes are drained.
- **Capture lifecycle:** frame/VAD work runs on `CaptureProcessor`, off the main actor.
  Silence retains only 200 ms of pre-roll and never requests transcription. Stop first
  places a fence on the serial audio callback queue (draining earlier callbacks and
  rejecting later arrivals), stops the OS stream, then processes the residual
  sub-100 ms frame, then finishes both decode lanes and journal work. Resume uses a
  fresh buffer/generation and continues the session sample clock.
- **Diagnostics:** retain the latest 256 decode metrics and emit metadata to the
  `com.livecaption.app` / `caption-latency` OS log category: queue/decode times, audio
  lag, outcome, fallbacks, delayed capture callbacks/buffer age, and dropped samples.
  Production diagnostics contain neither raw audio nor caption text. Audio lag is a
  sample-window metric; it does not substitute for annotated word-to-display latency.

## Implementation plan

### 1. Add diagnostics and a deterministic replay harness

- [x] Introduce an injectable transcription interface and controllable clock/audio
  source so scheduling can be tested without loading models or capturing a call.
- [x] Attach capture-generation ID, utterance ID, and window sample range to
  work and results; the window end sample supplies the monotonic interim sequence. Use a monotonic clock for elapsed-time measurements.
- [x] Measure capture callback gaps, oldest buffered audio age, VAD transitions,
  queue depth/wait, decode duration/fallbacks, filtering outcomes, result publication,
  and UI observation. Keep diagnostics bounded and omit raw audio/transcript text.
- [x] Return distinct success, empty/filtered, cancellation, and failure outcomes
  instead of converting all outcomes into an empty string.

### 2. Keep live transcription moving while finals run

- [x] Use separate controlled interim and final workers, with at most one active
  decode per model instance. Handle equal model selections explicitly: the engine
  previously shared one instance; it now loads independent instances for both lanes.
- [x] Keep at most one pending interim snapshot, replacing it with the newest eligible
  window. Discard stale results by session/utterance/window identity.
- [x] Preserve final work and chronological commit order. Define a queue/memory budget
  and explicit overload behavior; never silently drop final audio or transcript text.
- [x] Benchmark simultaneous inference on the target Mac. If hardware contention
  defeats the live-caption budget, revise scheduling/compute settings before claiming
  that separate workers resolve latency.
- [x] Track pending provisional utterances separately from the active utterance.
  A completed final replaces only its own provisional text and cannot clear a newer
  hypothesis. Clipboard endpoint callbacks use the matching utterance's text.

### 3. Correct text stabilization across moving windows

- [x] Track audio offsets and reconcile overlapping recognized words before extending
  the provisional transcript. Choose and document the alignment method using replay
  results; do not remove words solely by an unrelated committed-prefix count.
- [x] Scope agreement state to the appropriate utterance and aligned audio window.
  Preserve earlier stabilized text as the six-second window advances.
- [x] Handle revised punctuation, repeated phrases, shorter/empty hypotheses, and
  ambiguous overlap without duplicating or silently deleting recognized words.
- [x] Keep final transcript commitment distinct from provisional stabilization:
  the accurate final may correct its matching provisional text.

### 4. Bound interim retry cost and make failures observable

- [x] Split interim and final decoding options. Start evaluation with
  `temperatureFallbackCount: 0` for interim work; retain accuracy-oriented final
  retries and compare latency and recognition quality against the baseline.
- [x] Preserve valid provisional text on a transient empty/error result, scoped to
  its utterance. Do not carry stale text into later speech or persist it as a final
  merely because final decoding failed.
- [x] Measure cold versus warm inference and add preparation-time warm-up if needed.
- [ ] Tune speech detection only if quiet-speech replay demonstrates missed speech;
  evaluate hysteresis/pre-roll or adaptive thresholds against silence hallucinations.

### 5. Remove incidental UI and persistence delays

- [x] Observe orchestrator caption/status changes directly in the relevant SwiftUI
  view, or explicitly forward them through the controller.
- [x] Move capture draining/VAD processing off the main actor; publish UI state on
  the main actor. Keep buffer ownership and overflow handling explicit.
- [x] Move journal writes to an ordered background writer while preserving the
  existing durability contract. Pause/stop must await pending writes; failures must
  remain visible and a failed save must retain recovery data.
- [x] Define shutdown ordering for capture, residual buffered samples, in-flight
  decodes, and journal writes. Resume/start must not replay stale samples or accept
  results from a previous session.

## Validation and acceptance

### Deterministic regression tests

- [x] A final decode delayed by five seconds does not prevent a ready interim worker
  from publishing newer speech. Pending interim snapshots coalesce to the newest one.
- [x] The rolling-window sequence above advances the displayed transcript to include
  `eight nine ten`; it does not freeze at `seven` or lose the overlapping words.
- [ ] Continuous speech spanning multiple six-second windows and the 20-second
  utterance cap preserves recognized words and order without duplication.
- [x] Rapid endpoints and delayed results preserve final order and cannot erase a
  newer provisional caption. Equal interim/final model selections are covered.
- [x] Caption publications reach the view without relying on the session clock or a
  final-caption event to trigger a refresh.
- [ ] Empty/filtered/error results, delayed journal writes, pause/resume, and stop/start
  preserve caption ownership, transcript integrity, and recovery behavior.
- [ ] Sustained overload exercises the documented buffer/queue limit behavior without
  unbounded growth or silent loss of final work.

### On-device replay and manual checks

Run baseline and fixed builds on the same Mac with the inspected configuration.
Use a five-minute clean continuous-speech fixture plus quiet/noisy speech, short
pauses, and long utterances. Record hardware, build, model versions, cold/warm state,
and system load. Repeat with summaries enabled to measure shared-resource contention.

- [ ] On warmed clean speech at normal load, p95 first-visible-caption latency from
  annotated speech onset is at most 1 s, and p95 gaps between advancing captions
  during continuous speech are at most 1 s. Report maximum gaps as well; a five-second
  speech-to-caption gap fails acceptance even if percentiles pass.
- [ ] Measure annotated word-end → first-visible-word latency as well as update
  cadence. On warmed clean fixtures require p95 ≤ 1.5 s and maximum < 3 s; record final
  endpoint → durable-display latency separately (target p95 ≤ 3 s). Do not infer word
  freshness from frequent UI updates or the sample-window lag metric alone.
- [ ] Slow final decoding does not introduce a corresponding live-caption pause.
- [ ] Separate audio arrival, inference, publication, and UI delays in the report so
  silent input or rejected speech is not counted as successful responsiveness.
- [ ] Final transcript accuracy is compared with ground truth and the baseline.
  Deterministic fixtures have zero assembly-induced omissions or duplications;
  require final WER to worsen by no more than one absolute percentage point on the
  same ground-truth corpus before accepting decode/VAD tuning.
- [ ] Five minutes of silence produce no junk captions; a long-session run shows no
  unbounded backlog and no main-thread stall over 100 ms attributable to caption work.
- [ ] Pause/stop complete pending finalization and persistence correctly; saved
  transcript and clipboard behavior remain consistent with utterance order.

The one-second live targets are proposed acceptance budgets, not current measurements
or a guarantee for arbitrary hardware/noise. Record any failure and its cause before
revising the budget. Do not close this bug based solely on passing data-layer tests.

## Scope and completion

Primary changes belong in `StreamingOrchestrator`, `WhisperEngine`, `LocalAgreement`,
`ActiveSessionView`, and their test seams. Audio buffering, `SessionController`, and
`Journal` changes support scheduling, lifecycle, and responsiveness guarantees.

This bug does not require a speech-model migration or a summary-feature redesign.
It is complete when the fixes, deterministic regressions, and before/after on-device
results satisfy the acceptance criteria and are linked here. Investigation and this
specification alone do not constitute a fix.

## Implementation evidence

- [On-device results and reproduction](BUG-01-validation.md): maximum publication gap
  fell from 8,619 ms to 1,633 ms in the controlled scheduling/assembly comparison.
  Both streaming passes had zero final word errors on the 78-word synthetic fixture.
- Regression code: [`SpeechStreamTests`](../app/Tests/LocalCaptionKitTests/SpeechStreamTests.swift),
  [`CaptionPipelineTests`](../app/Tests/LocalCaptionKitTests/CaptionPipelineTests.swift),
  [`CaptionObservationTests`](../app/Tests/LocalCaptionTests/CaptionObservationTests.swift),
  and [`JournalTests`](../app/Tests/LocalCaptionKitTests/JournalTests.swift).
- The current implementation build passes **74 non-replay tests**, with two optional
  real-model checks skipped (76 tests discovered). Both model checks passed in the
  separate opt-in run. The normal run also compiles the app executable.
- [`EngineReplayTests`](../app/Tests/LocalCaptionTests/EngineReplayTests.swift) accepts
  `LOCALCAPTION_REPLAY_WAV` for local synthetic/consented fixture audio and uses only
  already-downloaded models. It does not open ScreenCaptureKit or a microphone.
- Run `swift test` from `app/`. To opt into the model check:
  `LOCALCAPTION_REPLAY_WAV=/absolute/path/fixture.wav swift test --filter EngineReplayTests`.
  Add `LOCALCAPTION_REPLAY_STREAM=1` for a real-time comparison of the old serial queue /
  prefix stabilization against the new pipeline, using the same models/options/VAD.
  Caption-publication gaps in that comparison are not annotated word-latency measurements.
- Extended noisy/quiet real-audio WER, a five-minute annotated latency replay, summary
  contention, and long-session UI profiling remain required before closing the bug.
  Automated scheduling and assembly tests alone do not establish those results.
