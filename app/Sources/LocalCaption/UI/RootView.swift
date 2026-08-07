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
                ReadOnlyPlaceholder(sessionID: id)
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

/// Placeholder for opening a past session read-only. Loading the saved `.txt` into the
/// caption area is Phase 3/6 (SPEC-06); for now it confirms selection + DB lookup work.
struct ReadOnlyPlaceholder: View {
    @EnvironmentObject var env: AppEnvironment
    let sessionID: Int64

    var body: some View {
        VStack(spacing: 8) {
            Image(systemName: "doc.text").font(.largeTitle).foregroundStyle(.tertiary)
            if let rec = try? env.store.fetch(id: sessionID) {
                Text(rec.sessionName).font(.title3)
                Text("Read-only transcript viewer arrives in a later phase.")
                    .font(.callout).foregroundStyle(.secondary)
            } else {
                Text("Session not found.").foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
    }
}
