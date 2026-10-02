import SwiftUI
import AppKit
import LocalCaptionKit

/// Settings → Asking → Hotkeys: record a shortcut, validate it with the shared grammar, and
/// show whether macOS accepted it (SPEC-14 §Global hotkey). One per action (Ask, Screenshot).
struct HotkeyRecorder: View {
    let title: String
    @Binding var hotkey: String
    let defaultHotkey: Hotkey
    @ObservedObject var registration: GlobalHotkey
    /// The other action's hotkey — the same combination can't do both.
    var other: (name: String, hotkey: Hotkey)?
    @State private var recording = false
    @State private var monitor: Any?
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(title)
                Spacer()
                Text(recording ? "Press a shortcut…" : resolved.description)
                    .font(.system(.body, design: .monospaced))
                    .padding(.horizontal, 8).padding(.vertical, 3)
                    .background(recording ? Color.accentColor.opacity(0.15) : Color.secondary.opacity(0.1),
                                in: RoundedRectangle(cornerRadius: 5))
                Button(recording ? "Cancel" : "Record…") { recording ? stop() : start() }
                Button("Reset to \(defaultHotkey)") { hotkey = defaultHotkey.description; error = nil }
                    .disabled(resolved == defaultHotkey)
            }
            if let error {
                Text(error).font(.caption).foregroundStyle(.orange)
            } else if case .unavailable(_, let reason) = registration.state {
                Text("Can't use it: \(reason). The on-screen Ask button still works.").font(.caption).foregroundStyle(.orange)
            }
            if resolved.isFunctionKey && resolved.modifiers.isEmpty {
                Text("On Apple keyboards hold fn with the F-key, or turn on System Settings → Keyboard → "
                     + "“Use F1, F2, etc. keys as standard function keys”. Otherwise it's a media key.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .onDisappear { stop() }
    }

    private var resolved: Hotkey { Hotkey.resolve(hotkey, fallback: defaultHotkey) }

    private func start() {
        error = nil
        recording = true
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            if event.keyCode == 53 { stop(); return nil }             // Escape cancels
            guard let s = HotkeyKeys.string(for: event) else {
                error = "That key can't be used."; stop(); return nil
            }
            switch Hotkey.parse(s) {
            case .success(let hk):
                if let other, other.hotkey == hk {
                    error = "\(hk) is already the \(other.name) hotkey."
                } else {
                    hotkey = hk.description; error = nil
                }
            case .failure(.needsModifier(let key)): error = "\(key) needs Ctrl, Alt or Cmd so it doesn't block typing."
            case .failure: error = "“\(s)” isn't a valid shortcut."
            }
            stop()
            return nil
        }
    }

    private func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        recording = false
    }
}
