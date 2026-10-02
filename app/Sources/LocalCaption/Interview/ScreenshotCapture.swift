import Foundation
import AppKit

/// Screenshot hotkey (owner, 2026-10-02): drag to select an area — macOS's own selector
/// (`screencapture -i`: crosshair, Space toggles window mode, Esc cancels). LocalCaption's windows
/// are hidden meanwhile so they don't cover what the interviewer is sharing. Uses the app's
/// Screen Recording permission (already granted for capturing call audio).
enum ScreenshotCapture {
    /// PNG of the selected area, or nil when the selection was cancelled or failed.
    @MainActor
    static func selectArea() async -> Data? {
        let file = FileManager.default.temporaryDirectory
            .appendingPathComponent("localcaption-shot-\(UUID().uuidString).png")
        defer { try? FileManager.default.removeItem(at: file) }

        let wasHidden = NSApp.isHidden
        if !wasHidden { NSApp.hide(nil) }
        defer { if !wasHidden { NSApp.unhideWithoutActivation() } }

        let ok = await run("/usr/sbin/screencapture", ["-i", "-x", "-o", "-t", "png", file.path])
        guard ok, let data = try? Data(contentsOf: file), !data.isEmpty else { return nil }
        return data
    }

    /// Runs a tool to completion without blocking the main thread.
    private static func run(_ path: String, _ args: [String]) async -> Bool {
        await withCheckedContinuation { continuation in
            let p = Process()
            p.executableURL = URL(fileURLWithPath: path)
            p.arguments = args
            p.terminationHandler = { continuation.resume(returning: $0.terminationStatus == 0) }
            do { try p.run() } catch { continuation.resume(returning: false) }
        }
    }
}
