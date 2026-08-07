# Running LocalCaption

Native macOS app — build, sign, and launch it locally. (Architecture &  status:
[`STATUS.md`](STATUS.md).)

## Requirements

- **Apple Silicon Mac**, **macOS 14 (Sonoma)** or later.
- **Xcode 16+** installed and selected (`xcode-select -p` → an Xcode path, not just Command
  Line Tools). Needed because WhisperKit is a Swift package.
- Network **once** for the first-run model download (~0.5 GB: tiny.en + small.en).

## Quick start

```bash
cd app
./scripts/setup-signing.sh     # once — creates a stable local signing identity
./run.sh                       # build → bundle → sign → launch
```

Then, on first launch:
1. Press **Start** in the app. macOS prompts for **Screen Recording** — click *Open System
   Settings* and enable **LocalCaption**.
2. **Quit and relaunch** (`./run.sh` again, or the Desktop shortcut) — macOS only applies a
   Screen Recording grant to a freshly launched process.
3. The model downloads once (progress shown), then live captions appear when audio plays.

After that it just works — the stable signing identity means macOS keeps the permission
across rebuilds.

## Desktop shortcut

A **“Run LocalCaption”** shortcut is on your Desktop. Double-click it to launch the app
(it builds first if needed). It runs the same `run.sh`.

## Why the signing step matters

`setup-signing.sh` creates a stable self-signed code-signing identity (kept in the gitignored
`.signing/`). Without it, each rebuild is ad-hoc-signed with a new identity and macOS
**forgets the Screen Recording permission every time**. With it, the grant sticks. Run it
once per machine.

## Capturing call audio

The app captions whatever your Mac is **playing** (via ScreenCaptureKit). Just have your
call/meeting audio come out of the system output — no routing or virtual devices needed.
Your microphone is never captured.

## Everyday use

- **Start / Pause / Resume / Stop** in the session controls. On **Stop**, a transcript is
  saved and appears in the sidebar.
- Saved transcripts: `~/Library/Application Support/LocalCaption/transcripts/` (`.txt` +
  `.json`). Use **Reveal in Finder** from the app.
- **Settings (⌘,)**: transcript folder, fonts, VAD/endpointing, interim/final model,
  always-on-top, opacity, clipboard.
- **Copy last N** copies the last N sentences to the clipboard (write-only).

## Tests

```bash
cd app
swift test        # 35 unit tests (config, store, transcript, journal, filters, …)
```

## Dev: re-run the ASR benchmark

```bash
cd app
say -v Samantha -o /tmp/clip.aiff "your test sentence here."
afconvert /tmp/clip.aiff /tmp/clip.wav -d LEF32@16000 -c 1 -f WAVE
swift run Benchmark /tmp/clip.wav      # prints load/decode latency + RTF per model
```

## Troubleshooting

- **It keeps asking for Screen Recording every launch** → you skipped `setup-signing.sh`, or
  the signing keychain is missing. Run `./scripts/setup-signing.sh`, then in System Settings ▸
  Privacy & Security ▸ Screen Recording remove old "LocalCaption" entries, then `./run.sh` and
  grant once.
- **Permission granted but the app still says it's needed** → quit and relaunch; macOS applies
  the grant only to a newly started process.
- **Model downloads again** → it shouldn't; models cache in `…/LocalCaption/models/`. A partial
  first download can re-fetch — let one finish while connected.
- **“xcode-select” points at Command Line Tools** → `sudo xcode-select -s /Applications/Xcode.app`
  (needs your password), then retry.
- **No captions** → confirm audio is actually playing out of the system output and the status
  pill shows ● Recording.
