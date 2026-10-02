import SwiftUI
import LocalCaptionKit

/// The Sessions window (owner, 2026-10-02): every saved session on the left; the selected one's
/// details on the right, with Open in interview panel for interviews.
struct SessionsWindow: View {
    static let id = "sessions"

    @EnvironmentObject var env: AppEnvironment
    @State private var selection: Int64?

    var body: some View {
        NavigationSplitView {
            SessionListView(selection: $selection)
                .navigationSplitViewColumnWidth(min: 240, ideal: 300)
        } detail: {
            if let id = selection {
                SessionDetailView(sessionID: id, session: env.session, interview: env.interview)
            } else {
                VStack(spacing: 8) {
                    Image(systemName: "list.bullet.rectangle").font(.largeTitle).foregroundStyle(.tertiary)
                    Text("Select a session to see its details.").foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .frame(minWidth: 720, minHeight: 420)
        .background(WindowAccessor(alwaysOnTop: env.config.window.alwaysOnTop, opacity: 1,
                                   autosaveName: "LocalCaptionSessionsWindow"))
    }
}
