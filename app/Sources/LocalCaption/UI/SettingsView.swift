import SwiftUI
import LocalCaptionKit

/// Settings bound to the config store (SPEC.md §15 / SPEC-07). Every change persists
/// atomically via `AppEnvironment.config`'s `didSet`. Phase 1 wires the settings whose
/// features already exist (General, Caption, Window); the full table (Audio/ASR/Clipboard)
/// is completed in Phase 3 once those features land.
struct SettingsView: View {
    @EnvironmentObject var env: AppEnvironment

    var body: some View {
        Form {
            Section("General") {
                TextField("Session name prefix", text: $env.config.general.sessionNamePrefix)
                LabeledContent("Transcript folder") {
                    Text(env.config.general.transcriptFolder)
                        .font(.callout).foregroundStyle(.secondary)
                        .lineLimit(1).truncationMode(.middle)
                }
            }

            Section("Caption") {
                Stepper(value: $env.config.caption.fontSize, in: 10...48) {
                    LabeledContent("Font size", value: "\(env.config.caption.fontSize) pt")
                }
                Toggle("Auto-scroll", isOn: $env.config.caption.autoScroll)
                Toggle("Show timestamps", isOn: $env.config.caption.showTimestamps)
            }

            Section("Window") {
                Toggle("Always on top", isOn: $env.config.window.alwaysOnTop)
                VStack(alignment: .leading) {
                    LabeledContent("Opacity", value: String(format: "%.2f", env.config.window.opacity))
                    Slider(value: $env.config.window.opacity, in: 0.3...1.0)
                }
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
        .frame(width: 460, height: 420)
    }
}
