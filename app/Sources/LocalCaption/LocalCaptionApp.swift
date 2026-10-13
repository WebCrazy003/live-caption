import SwiftUI

@main
struct LocalCaptionApp: App {
    @StateObject private var env = AppEnvironment()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(env)
                .frame(minWidth: 360, minHeight: 240)
        }
        .defaultSize(width: CGFloat(env.config.window.width),
                     height: CGFloat(env.config.window.height))
        .commands { SessionsCommands(); MinimalViewCommands(env: env) }

        // Past sessions: the list, each one's details, and Open in interview panel.
        Window("Sessions", id: SessionsWindow.id) {
            SessionsWindow()
                .environmentObject(env)
        }
        .defaultSize(width: 980, height: 640)
        .windowResizability(.contentMinSize)

        // Maximize on a code block or diagram in an answer.
        Window("Code & Diagram", id: CodeViewer.windowID) {
            CodeViewerWindow()
                .environmentObject(env)
        }
        .defaultSize(width: 760, height: 560)

        // ⌘, Settings (SPEC.md §13). Bound to the same config store.
        Settings {
            SettingsView()
                .environmentObject(env)
        }
    }
}

/// File → Sessions… (⌘L) in place of New: the main window is always the live session.
private struct SessionsCommands: Commands {
    @Environment(\.openWindow) private var openWindow

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("Sessions…") { openWindow(id: SessionsWindow.id) }
                .keyboardShortcut("l", modifiers: .command)
        }
    }
}

/// View → Minimal View (⇧⌘M): works with the header hidden, so it is a menu command.
private struct MinimalViewCommands: Commands {
    @ObservedObject var env: AppEnvironment

    var body: some Commands {
        CommandGroup(before: .toolbar) {
            Toggle("Minimal View", isOn: $env.minimal)
                .keyboardShortcut("m", modifiers: [.command, .shift])
                .disabled(!env.modeChosen)
            Divider()
        }
    }
}

extension Notification.Name {
    static let sessionsChanged = Notification.Name("LocalCaption.sessionsChanged")
    /// Ask the session list to confirm deleting a session (object: the session id).
    static let requestDeleteSession = Notification.Name("LocalCaption.requestDeleteSession")
}
