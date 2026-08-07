import SwiftUI
import AppKit

/// Bridges SwiftUI to the hosting `NSWindow` to apply overlay behavior (SPEC.md §14):
/// always-on-top level and window opacity, live from config. Size/position are remembered
/// via AppKit's frame autosave, which also validates the saved frame against connected
/// displays (recentering if it would be off-screen).
struct WindowAccessor: NSViewRepresentable {
    var alwaysOnTop: Bool
    var opacity: Double

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async {
            guard let window = view.window else { return }
            if !context.coordinator.didConfigure {
                window.setFrameAutosaveName("LocalCaptionMainWindow")
                context.coordinator.didConfigure = true
            }
            apply(to: window)
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        DispatchQueue.main.async { if let w = nsView.window { apply(to: w) } }
    }

    private func apply(to window: NSWindow) {
        window.level = alwaysOnTop ? .floating : .normal
        window.alphaValue = CGFloat(min(1.0, max(0.3, opacity)))
    }

    func makeCoordinator() -> Coordinator { Coordinator() }
    final class Coordinator { var didConfigure = false }
}
