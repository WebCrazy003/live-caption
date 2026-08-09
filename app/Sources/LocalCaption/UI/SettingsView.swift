import SwiftUI
import AppKit
import LocalCaptionKit

/// Settings bound to the config store (SPEC.md §15 / SPEC-07). Every change persists
/// atomically via `AppEnvironment.config`'s `didSet`. Live-applied settings take effect
/// immediately; others apply to the next session (noted inline).
struct SettingsView: View {
    @EnvironmentObject var env: AppEnvironment
    @State private var folderError: String?

    private let interimModels = ["tiny.en", "base.en", "small.en"]
    private let finalModels = ["small.en", "large-v3-turbo", "large-v3", "distil-large-v3"]

    var body: some View {
        Form {
            // MARK: General
            Section("General") {
                TextField("Session name prefix", text: $env.config.general.sessionNamePrefix)
                LabeledContent("Transcript folder") {
                    HStack(spacing: 8) {
                        Text(env.config.general.transcriptFolder)
                            .font(.callout).foregroundStyle(.secondary)
                            .lineLimit(1).truncationMode(.middle)
                        Button("Change…") { pickFolder() }
                    }
                }
                if let folderError {
                    Label(folderError, systemImage: "exclamationmark.triangle")
                        .font(.caption).foregroundStyle(.orange)
                }
            }

            // MARK: Caption (live)
            Section("Caption") {
                Stepper(value: $env.config.caption.fontSize, in: 10...48) {
                    LabeledContent("Font size", value: "\(env.config.caption.fontSize) pt")
                }
                Toggle("Auto-scroll", isOn: $env.config.caption.autoScroll)
                Toggle("Show timestamps (view + saved file)", isOn: $env.config.caption.showTimestamps)
            }

            // MARK: Audio / ASR (applied on next Start)
            Section {
                Picker("VAD sensitivity", selection: $env.config.audio.vadSensitivity) {
                    ForEach(0...3, id: \.self) { Text("\($0)").tag($0) }
                }
                .pickerStyle(.segmented)
                Stepper(value: $env.config.asr.endpointSilenceMs, in: 200...2000, step: 50) {
                    LabeledContent("Endpoint silence", value: "\(env.config.asr.endpointSilenceMs) ms")
                }
                Stepper(value: $env.config.asr.maxUtteranceS, in: 5...60) {
                    LabeledContent("Max utterance", value: "\(env.config.asr.maxUtteranceS) s")
                }
            } header: {
                Text("Audio & endpointing")
            } footer: {
                Text("0 = least sensitive, 3 = most. Applied when you next press Start.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            // MARK: Models (Phase 4)
            Section {
                Picker("Interim model", selection: $env.config.asr.interimModel) {
                    ForEach(interimModels, id: \.self) { Text($0).tag($0) }
                }
                Picker("Final model", selection: $env.config.asr.finalModel) {
                    ForEach(finalModels, id: \.self) { Text($0).tag($0) }
                }
            } header: {
                Text("Speech models")
            } footer: {
                Text("Interim drives fast partials; final produces committed captions (applied "
                     + "on next Start). Measured on-device: tiny.en ≈0.45s, small.en ≈2s, "
                     + "large-v3-turbo ≈3.5s + a 1.5 GB download. small.en matches turbo on clear "
                     + "audio; pick turbo for hard/noisy audio at higher latency.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            // MARK: Live AI summary (SPEC-10)
            Section {
                Toggle("Show live AI summary panel", isOn: $env.config.summary.enabled)
                Stepper(value: $env.config.summary.wordsPerSummary, in: 20...300, step: 10) {
                    LabeledContent("New summary every", value: "\(env.config.summary.wordsPerSummary) words")
                }
                Stepper(value: $env.config.summary.maxBullets, in: 1...6) {
                    LabeledContent("Max points per card", value: "\(env.config.summary.maxBullets)")
                }
            } header: {
                Text("Live AI summary")
            } footer: {
                Text("On-device summary of what the other person wants, in short easy English — a "
                     + "new card every N words of speech. Needs the local model server running "
                     + "(app/scripts/summary-server.sh); model \(env.config.summary.model). "
                     + "Runs over localhost only — nothing leaves your Mac.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            // MARK: Window (Phase 5)
            Section {
                Toggle("Always on top", isOn: $env.config.window.alwaysOnTop)
                VStack(alignment: .leading) {
                    LabeledContent("Opacity", value: String(format: "%.2f", env.config.window.opacity))
                    Slider(value: $env.config.window.opacity, in: 0.3...1.0)
                }
            } header: {
                Text("Window")
            } footer: {
                Text("Applied live. Note: an always-on-top window can be captured if you screen-share.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            // MARK: Clipboard (Phase 5)
            Section {
                Toggle("Auto-update clipboard", isOn: $env.config.clipboard.autoUpdate)
                Stepper(value: $env.config.clipboard.recentSentences, in: 1...50) {
                    LabeledContent("Recent sentences (N)", value: "\(env.config.clipboard.recentSentences)")
                }
                Toggle("Auto-copy selection", isOn: $env.config.clipboard.autoCopySelection)
            } header: {
                Text("Clipboard")
            } footer: {
                Text("Off by default; only ever writes, never reads. “Copy last N” in the session "
                     + "controls always works. (Auto-copy selection arrives in a later update.)")
                    .font(.caption).foregroundStyle(.secondary)
            }

            if env.configWasRepaired {
                Section {
                    Label("Your config file was unreadable and has been reset to defaults (a backup was saved).",
                          systemImage: "exclamationmark.triangle")
                        .font(.callout).foregroundStyle(.secondary)
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 480, height: 560)
    }

    private func pickFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "Choose"
        panel.directoryURL = URL(fileURLWithPath:
            (env.config.general.transcriptFolder as NSString).expandingTildeInPath)
        guard panel.runModal() == .OK, let url = panel.url else { return }

        let fm = FileManager.default
        let writable = fm.isWritableFile(atPath: url.path)
            || (try? fm.createDirectory(at: url, withIntermediateDirectories: true)) != nil
        if writable {
            env.config.general.transcriptFolder = url.path
            folderError = nil
        } else {
            folderError = "That folder isn't writable — pick another."
        }
    }
}
