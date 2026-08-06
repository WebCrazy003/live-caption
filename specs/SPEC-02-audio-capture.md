# SPEC-02 — Audio Capture (system audio)

**Status:** Design + spike learnings · **Depends on:** SPEC-00 · **Full detail:** SPEC.md §6, §17.2

## Goal
Capture **system (speaker) audio** as a single 16 kHz mono Float32 stream and hand frames
to the ASR engine. No microphone / local-voice capture.

## What should be done
- [ ] **Decide capture method (B3):** BlackHole (baseline) vs ScreenCaptureKit (native, no install).
- [ ] Capture pipeline:
  - **BlackHole path:** `AVAudioEngine` tap on the BlackHole input device.
  - **ScreenCaptureKit path:** `SCStream` audio-only capture.
- [ ] **Format normalization:** downmix to mono + resample 48 kHz → 16 kHz Float32 via `AVAudioConverter`, off the render thread (SPEC.md §6.3).
- [ ] **Device detection & guidance (BlackHole):** detect device; if absent, show install + Multi-Output setup instructions and disable Start; "Re-check" button (SPEC.md §6.2).
- [ ] **Permission:** request audio-input (microphone) TCC permission; handle denied gracefully (banner + link to System Settings).
- [ ] **Disconnect recovery:** on device error, banner + retry every 2 s (bounded); gap audio lost, logged.
- [ ] Bounded ring buffer (drop-oldest) with a dropped-frames counter.

## What is done
- Nothing built in Swift. Design fully specified in SPEC.md §6.
- **Spike learnings (important):**
  - Confirmed **any input device (incl. virtual) requires the microphone TCC permission** — dropping physical mic does **not** drop the permission.
  - Confirmed a virtual device is **only a loopback if system OUTPUT is routed into it** (the "CaptionEd Device" on this Mac received silence → not a loopback). This is exactly the Multi-Output requirement.
  - Confirmed 48 kHz→16 kHz mono is needed; and that acoustic/room capture badly degrades accuracy while clean digital capture is flawless — reinforces capturing digital system audio, not a mic.

## Blockers
- **B1** — Xcode.
- **B3** — capture-method decision (BlackHole vs ScreenCaptureKit) gates permission model and UX.

## Acceptance
- With the chosen method configured, a playing call produces non-zero 16 kHz mono frames to the ASR queue.
- BlackHole-absent path (if chosen) shows guidance and blocks Start without crashing.
- Denied permission is handled gracefully.
- Device disconnect mid-capture recovers or prompts, no crash.
