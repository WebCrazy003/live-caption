import SwiftUI

@main
struct LocalCaptionApp: App {
    @StateObject private var env = AppEnvironment()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(env)
                .frame(minWidth: 640, minHeight: 420)
        }
        .defaultSize(width: CGFloat(env.config.window.width),
                     height: CGFloat(env.config.window.height))
        .commands {
            // ⌘N — New Session (handled inside RootView via notification).
            CommandGroup(replacing: .newItem) {
                Button("New Session") {
                    NotificationCenter.default.post(name: .newSession, object: nil)
                }
                .keyboardShortcut("n", modifiers: .command)
            }
        }

        // ⌘, Settings (SPEC.md §13). Bound to the same config store.
        Settings {
            SettingsView()
                .environmentObject(env)
        }
    }
}

extension Notification.Name {
    static let newSession = Notification.Name("LocalCaption.newSession")
    static let sessionsChanged = Notification.Name("LocalCaption.sessionsChanged")
}
