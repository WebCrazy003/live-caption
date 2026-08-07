import SwiftUI
import LocalCaptionKit

/// Top-level navigation shell: Session List (sidebar) ↔ Active Session / read-only viewer
/// (detail), plus Settings via the ⌘, scene (SPEC-00, SPEC.md §13).
struct RootView: View {
    @EnvironmentObject var env: AppEnvironment
    @State private var selection: Int64?

    var body: some View {
        NavigationSplitView {
            SessionListView(selection: $selection)
                .navigationSplitViewColumnWidth(min: 220, ideal: 280)
        } detail: {
            if let id = selection {
                TranscriptViewer(sessionID: id)
            } else {
                ActiveSessionView(env: env)
                    .id("active")   // fresh controller per New Session
            }
        }
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                SettingsLink { Label("Settings", systemImage: "gearshape") }
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .newSession)) { _ in
            selection = nil
        }
        .sheet(isPresented: .constant(!env.pendingRecoveries.isEmpty)) {
            RecoveryView()
        }
    }
}
