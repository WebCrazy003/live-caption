import SwiftUI
import AppKit
import LocalCaptionKit

/// Settings → Interview → Hotkey: record a shortcut, validate it with the shared grammar, and
/// show whether macOS accepted it (SPEC-14 §Global hotkey).
struct HotkeyRecorder: View {
    @Binding var hotkey: String
    @ObservedObject var registration = GlobalHotkey.shared
    @State private var recording = false
    @State private var monitor: Any?
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text("Ask hotkey")
                Spacer()
                Text(recording ? "Press a shortcut…" : Hotkey.resolve(hotkey).description)
                    .font(.system(.body, design: .monospaced))
                    .padding(.horizontal, 8).padding(.vertical, 3)
                    .background(recording ? Color.accentColor.opacity(0.15) : Color.secondary.opacity(0.1),
                                in: RoundedRectangle(cornerRadius: 5))
                Button(recording ? "Cancel" : "Record…") { recording ? stop() : start() }
                Button("Reset to F8") { hotkey = Hotkey.defaultString; error = nil }
                    .disabled(hotkey == Hotkey.defaultString)
            }
            if let error {
                Text(error).font(.caption).foregroundStyle(.orange)
            } else if case .unavailable(_, let reason) = registration.state {
                Text("Can't use it: \(reason). The on-screen Ask button still works.").font(.caption).foregroundStyle(.orange)
            }
            if Hotkey.resolve(hotkey).isFunctionKey && Hotkey.resolve(hotkey).modifiers.isEmpty {
                Text("On Apple keyboards hold fn with the F-key, or turn on System Settings → Keyboard → "
                     + "“Use F1, F2, etc. keys as standard function keys”. Otherwise F8 is play/pause.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .onDisappear { stop() }
    }

    private func start() {
        error = nil
        recording = true
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            if event.keyCode == 53 { stop(); return nil }             // Escape cancels
            guard let s = HotkeyKeys.string(for: event) else {
                error = "That key can't be used."; stop(); return nil
            }
            switch Hotkey.parse(s) {
            case .success(let hk): hotkey = hk.description; error = nil
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
