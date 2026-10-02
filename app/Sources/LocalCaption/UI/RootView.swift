import SwiftUI
import LocalCaptionKit

/// The main window: the live session — captions, and the interview panel in Interview mode
/// (SPEC-00, SPEC.md §13). Past sessions open in their own Sessions window (owner, 2026-10-02).
struct RootView: View {
    @EnvironmentObject var env: AppEnvironment
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        ActiveSessionView(env: env)
            .id("active")
            .toolbar {
                ToolbarItem(placement: .primaryAction) {
                    Button { openWindow(id: SessionsWindow.id) } label: {
                        Label("Sessions", systemImage: "list.bullet.rectangle")
                    }
                    .help("Past sessions and interviews (⌘L)")
                }
                ToolbarItem(placement: .primaryAction) {
                    SettingsLink { Label("Settings", systemImage: "gearshape") }
                }
            }
            .sheet(isPresented: .constant(!env.pendingRecoveries.isEmpty)) {
                RecoveryView()
            }
            .background(WindowAccessor(alwaysOnTop: env.config.window.alwaysOnTop,
                                       opacity: env.config.window.opacity))
    }
}
