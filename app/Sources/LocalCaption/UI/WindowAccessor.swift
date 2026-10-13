import SwiftUI
import AppKit

/// Bridges SwiftUI to the hosting `NSWindow` to apply overlay behavior (SPEC.md §14):
/// always-on-top level and window opacity, live from config, and the bare window of Minimal
/// view (no title, traffic lights or toolbar; drag anywhere to move). Size/position are remembered
/// via AppKit's frame autosave, which also validates the saved frame against connected
/// displays (recentering if it would be off-screen).
struct WindowAccessor: NSViewRepresentable {
    var alwaysOnTop: Bool
    var opacity: Double
    var chromeless = false
    var autosaveName = "LocalCaptionMainWindow"

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async {
            guard let window = view.window else { return }
            if !context.coordinator.didConfigure {
                window.setFrameAutosaveName(autosaveName)
                context.coordinator.didConfigure = true
                context.coordinator.styleMask = window.styleMask
            }
            apply(to: window, context.coordinator)
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        DispatchQueue.main.async { if let w = nsView.window { apply(to: w, context.coordinator) } }
    }

    private func apply(to window: NSWindow, _ coordinator: Coordinator) {
        window.level = alwaysOnTop ? .floating : .normal
        window.alphaValue = CGFloat(min(1.0, max(0.3, opacity)))
        guard coordinator.chromeless != chromeless, let original = coordinator.styleMask else { return }
        coordinator.chromeless = chromeless
        window.titleVisibility = chromeless ? .hidden : .visible
        window.titlebarAppearsTransparent = chromeless
        window.styleMask = chromeless ? original.union(.fullSizeContentView) : original
        for button in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton] {
            window.standardWindowButton(button)?.isHidden = chromeless
        }
        window.isMovableByWindowBackground = chromeless
    }

    func makeCoordinator() -> Coordinator { Coordinator() }
    final class Coordinator {
        var didConfigure = false
        var chromeless = false
        /// The window's own style, restored when Minimal view ends.
        var styleMask: NSWindow.StyleMask?
    }
}
