import SwiftUI
import AppKit
import LocalCaptionKit

/// Settings bound to the config store (SPEC.md §15 / SPEC-07), in tabs. Every change persists
/// atomically via `AppEnvironment.config`'s `didSet`. Live-applied settings take effect
/// immediately; others apply to the next session (noted inline).
struct SettingsView: View {
    @EnvironmentObject var env: AppEnvironment
    @State private var folderError: String?
    @State private var micDenied = false

    private let interimModels = ["tiny.en", "base.en", "small.en"]
    private let finalModels = ["small.en", "large-v3-turbo", "large-v3", "distil-large-v3"]

    /// Settings tabs; the last one used is reopened.
    private enum Tab: String { case general, captions, accent, interview, asking, prompts, codex }
    @AppStorage("settings.tab") private var tab: Tab = .general

    var body: some View {
        TabView(selection: $tab) {
            page { generalSections }
                .tabItem { Label("General", systemImage: "gearshape") }.tag(Tab.general)
            page { captionSections }
                .tabItem { Label("Captions", systemImage: "captions.bubble") }.tag(Tab.captions)
            page { AccentSettingsSections(codex: env.codex) }
                .tabItem { Label("Accent mode", systemImage: "waveform.badge.magnifyingglass") }.tag(Tab.accent)
            page { InterviewSettingsSections(page: .interview, codex: env.codex) }
                .tabItem { Label("Interview", systemImage: "person.2.wave.2") }.tag(Tab.interview)
            page { InterviewSettingsSections(page: .asking, codex: env.codex) }
                .tabItem { Label("Asking", systemImage: "questionmark.bubble") }.tag(Tab.asking)
            page { InterviewSettingsSections(page: .prompts, codex: env.codex) }
                .tabItem { Label("Prompts", systemImage: "text.bubble") }.tag(Tab.prompts)
            page { InterviewSettingsSections(page: .codex, codex: env.codex) }
                .tabItem { Label("Codex", systemImage: "person.badge.key") }.tag(Tab.codex)
        }
        .frame(width: 600, height: 640)
    }

    /// One tab: a scrolling grouped form.
    private func page<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        Form { content() }.formStyle(.grouped)
    }

    // MARK: General tab

    @ViewBuilder private var generalSections: some View {
        if env.configWasRepaired {
            Section {
                Label("Your config file was unreadable and has been reset to defaults (a backup was saved).",
                      systemImage: "exclamationmark.triangle")
                    .font(.callout).foregroundStyle(.secondary)
            }
        }

        Section("Sessions") {
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

        Section {
            Picker("Record audio", selection: $env.config.audio.recordSource) {
                Text("Off").tag(Config.RecordSource.off)
                Text("Call audio").tag(Config.RecordSource.call)
                Text("My microphone").tag(Config.RecordSource.microphone)
            }
            .onChange(of: env.config.audio.recordSource) { _, source in
                micDenied = false
                guard source == .microphone else { return }
                Task { micDenied = !(await MicrophoneRecorder.requestAccess()) }
            }
            if micDenied {
                Label("LocalCaption doesn't have the Microphone permission. Turn it on in System Settings ▸ "
                      + "Privacy & Security ▸ Microphone.", systemImage: "exclamationmark.triangle")
                    .font(.caption).foregroundStyle(.orange)
            }
        } header: {
            Text("Recording")
        } footer: {
            Text("Saves each session's audio as an .m4a beside its transcript, from the next Start. "
                 + "Call audio is what the captions are made from; My microphone is your own voice. "
                 + "Captions always come from the call. Check that everyone on the call agrees to be recorded.")
                .font(.caption).foregroundStyle(.secondary)
        }

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
    }

    // MARK: Captions tab

    @ViewBuilder private var captionSections: some View {
        Section("Display") {
            Stepper(value: $env.config.caption.fontSize, in: 10...48) {
                LabeledContent("Font size", value: "\(env.config.caption.fontSize) pt")
            }
            Toggle("Auto-scroll", isOn: $env.config.caption.autoScroll)
            Toggle("Show timestamps (view + saved file)", isOn: $env.config.caption.showTimestamps)
        }

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
