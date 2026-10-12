import AppKit

/// Keeps every app window out of screen capture (screen sharing, recordings, screenshots)
/// while leaving it visible on this Mac, via `NSWindow.sharingType = .none`.
///
/// Applied to all windows, including Settings, sheets and alerts: any window that appears
/// later is caught on the next run-loop update.
@MainActor
final class ScreenShareShield {
    static let shared = ScreenShareShield()

    var isEnabled = false {
        didSet { if isEnabled != oldValue { applyToAllWindows() } }
    }

    private init() {
        NotificationCenter.default.addObserver(
            forName: NSApplication.didUpdateNotification, object: nil, queue: .main
        ) { _ in MainActor.assumeIsolated { ScreenShareShield.shared.applyToAllWindows() } }
    }

    private func applyToAllWindows() {
        guard let app = NSApp else { return }
        let type: NSWindow.SharingType = isEnabled ? .none : .readOnly
        for window in app.windows where window.sharingType != type {
            window.sharingType = type
        }
    }
}
