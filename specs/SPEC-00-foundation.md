# SPEC-00 — Foundation & App Shell

**Status:** 🟡 Partial (minimal app) · **Depends on:** — · **Full detail:** SPEC.md §6, §20

> **Progress:** `minimal/` has a launchable SwiftUI app, SwiftPM building, WhisperKit dep,
> and off-main threading. **Remaining:** multi-screen navigation shell, GRDB dependency,
> App Support directory bootstrap.

## Goal
A buildable, signed-locally Swift/SwiftUI macOS app skeleton with screen navigation and
dependency wiring, ready for feature modules to plug into.

## What should be done
- [ ] Create Xcode project: SwiftUI app, macOS 14+, Apple Silicon.
- [ ] Add SwiftPM dependencies: **WhisperKit**, **GRDB** (SQLite).
- [ ] App entry (`LocalCaptionApp`) + top-level navigation between three screens: Session List, Active Session, Settings (empty placeholders).
- [ ] Threading skeleton: main/UI actor + a background actor/queue for audio+ASR (no work on the UI thread) — see SPEC.md §6.1.
- [ ] `Info.plist` with `NSMicrophoneUsageDescription` ("capture call/system audio for captioning") — required even without a physical mic (SPEC.md §17.2).
- [ ] App Support directory bootstrap: `~/Library/Application Support/LocalCaption/{transcripts,journal,models}`.
- [ ] Splash/loading state shown while models load (wired in SPEC-03).

## What is done
- Nothing built. Project structure, thread model, and directory layout are specified in SPEC.md §6, §20.

## Blockers
- **B1** — Full Xcode not installed; this Mac's Command Line Tools SwiftPM is broken (even a trivial package fails to link the manifest). Cannot create/build any Swift package until Xcode is installed.

## Acceptance
- App launches to an empty Session List; can navigate to Settings and back.
- Builds and runs on Apple Silicon, macOS 14+.
- WhisperKit + GRDB resolve and link.
- App Support subfolders are created on first launch.
