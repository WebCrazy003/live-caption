# SPEC-09 — Packaging & Distribution

**Status:** Not started · **Depends on:** all (00–08) · **Full detail:** SPEC.md §17.3, §19

## Goal
Ship a signed, notarized macOS app that launches without Gatekeeper warnings and never
touches the network at runtime.

## What should be done
- [ ] **Hardened Runtime** with entitlement `com.apple.security.device.audio-input` (+ Screen Recording usage string if ScreenCaptureKit chosen — see B3).
- [ ] **Codesign** with Developer ID; **notarize** + staple.
- [ ] Package as a signed **DMG**.
- [ ] Verify no dylib loads from outside the bundle; bundle WhisperKit/CoreML assets correctly.
- [ ] **Offline enforcement:** no telemetry; runtime makes zero network calls after model present (verify with a network monitor). Optional bundled-model build for zero-network installs.
- [ ] Privacy invariants audit (SPEC.md §19.1): no audio/transcript leaves device; raw audio discarded after inference.

## What is done
- Nothing built. Packaging/entitlement/notarization requirements specified in SPEC.md §17.3/§19. (Native Swift avoids the Python-bundling pain of the old v1 plan.)

## Blockers
- **B1** — Xcode.
- **B2** — Apple Developer account + Developer ID certificate (required for notarization).
- **B3** — capture method affects which entitlements/usage strings are needed.

## Acceptance
- Signed, notarized app opens on a clean Mac with no Gatekeeper warning.
- Network monitor shows zero runtime traffic after the model is present.
- All privacy invariants hold (no external transmission; audio not persisted).
