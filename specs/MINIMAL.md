# MINIMAL — "just show caption text" MVP

The smallest buildable slice: **mic audio → live caption text in a window.** No sessions,
no database, no saving, no settings, no packaging. Use this to prove the core on-device
captioning works before building the full app (specs 00–09).

## Scope

**In:** one SwiftUI window, WhisperKit streaming from the **microphone**, a scrolling text
view showing confirmed + in-progress captions.

**Out (deliberately):** system-audio/BlackHole, dual-model hybrid, session list, config,
SQLite, transcript files, journal/recovery, settings, always-on-top/opacity, clipboard,
notarization.

**Simplifications vs. full spec:** mic instead of system audio (no routing setup); single
model instead of `tiny.en`+`large-v3-turbo`; WhisperKit's built-in `AudioStreamTranscriber`
does capture + streaming + token stabilization for us.

## Build path

1. **Install full Xcode** (blocker B1 — this Mac's Command Line Tools SwiftPM is broken; no Swift package builds without Xcode).
2. New Xcode project → **macOS → App → SwiftUI**, minimum deployment **macOS 14**.
3. **Add Package Dependency:** `https://github.com/argmaxinc/WhisperKit` (up to next major from 0.9.0).
4. **Info.plist:** add `NSMicrophoneUsageDescription` = "Used to caption speech."
5. Replace the generated `App` + `ContentView` with the two files below.
6. **Run.** First launch downloads the model once (needs network), then grant the mic prompt → captions appear.

> ⚠️ WhisperKit's `AudioStreamTranscriber` initializer signature changes between versions.
> If it doesn't compile, open WhisperKit's bundled example app and match the current
> parameter list — the *shape* (construct WhisperKit → build a streamer → start it → read
> `confirmedSegments` / `unconfirmedSegments` in the state callback) stays the same.

---

## `MinimalCaptionApp.swift`

```swift
import SwiftUI

@main
struct MinimalCaptionApp: App {
    var body: some Scene {
        WindowGroup {
            ContentView()
                .frame(minWidth: 420, minHeight: 300)
        }
    }
}
```

## `ContentView.swift`

```swift
import SwiftUI
import WhisperKit

@MainActor
final class CaptionModel: ObservableObject {
    @Published var confirmed: String = ""      // committed text
    @Published var hypothesis: String = ""     // in-progress (provisional) tail
    @Published var status: String = "Loading model…"

    private var whisperKit: WhisperKit?
    private var streamer: AudioStreamTranscriber?

    func start() async {
        do {
            // Single model. "base.en" = fast/light; "large-v3-turbo" = most accurate.
            let wk = try await WhisperKit(WhisperKitConfig(model: "base.en"))
            self.whisperKit = wk
            self.status = "Listening…"

            let audioProcessor = AudioProcessor()
            let streamer = AudioStreamTranscriber(
                audioEncoder: wk.audioEncoder,
                featureExtractor: wk.featureExtractor,
                segmentSeeker: wk.segmentSeeker,
                textDecoder: wk.textDecoder,
                tokenizer: wk.tokenizer!,
                audioProcessor: audioProcessor,
                decodingOptions: DecodingOptions(task: .transcribe, language: "en")
            ) { [weak self] _, newState in
                Task { @MainActor in
                    self?.confirmed = newState.confirmedSegments.map { $0.text }.joined(separator: " ")
                    self?.hypothesis = newState.unconfirmedSegments.map { $0.text }.joined(separator: " ")
                }
            }
            self.streamer = streamer
            try await streamer.startStreamTranscription()   // requests mic permission
        } catch {
            self.status = "Error: \(error.localizedDescription)"
        }
    }
}

struct ContentView: View {
    @StateObject private var model = CaptionModel()

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(model.status).font(.caption).foregroundStyle(.secondary)
            ScrollView {
                VStack(alignment: .leading, spacing: 4) {
                    Text(model.confirmed).font(.title3)
                    // provisional tail, dimmed
                    if !model.hypothesis.isEmpty {
                        Text(model.hypothesis).font(.title3).foregroundStyle(.secondary)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .padding()
        .task { await model.start() }   // starts on appear
    }
}
```

---

## Acceptance (you'll know it works when)

- App opens, shows "Listening…", and mic-permission prompt appears (grant it).
- Speaking into the mic makes words appear within a fraction of a second (dimmed
  provisional), then firm up into confirmed text.
- No network after the model has downloaded.

## Next steps from here (grow into the full app)

- Swap mic → **system audio** (SPEC-02: BlackHole or ScreenCaptureKit).
- Add the **dual-model hybrid** + your own endpointing/filters (SPEC-03) if you want lower
  interim latency and hallucination suppression.
- Add **saving + sessions + list + settings** (SPEC-04, 06, 07) and **packaging** (SPEC-09).
```
