import SwiftUI
import LocalCaptionKit

/// The default screen: browse past sessions (SPEC.md §10 / SPEC-06). Phase 1 wires it to
/// the SQLite store with an empty state + New Session; create/rename/delete/search/sort
/// UI is fleshed out in Phase 3. Rows appear once sessions are saved on Stop (Phase 2).
struct SessionListView: View {
    @EnvironmentObject var env: AppEnvironment
    @Binding var selection: Int64?
    @State private var sessions: [SessionRecord] = []

    var body: some View {
        List(selection: $selection) {
            if sessions.isEmpty {
                Text("No sessions yet")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .center)
                    .listRowSeparator(.hidden)
            } else {
                ForEach(sessions) { rec in
                    row(rec).tag(rec.id)
                }
            }
        }
        .navigationTitle("Sessions")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button {
                    NotificationCenter.default.post(name: .newSession, object: nil)
                } label: {
                    Label("New Session", systemImage: "plus")
                }
            }
        }
        .onAppear(perform: reload)
        .onReceive(NotificationCenter.default.publisher(for: .newSession)) { _ in reload() }
        .onReceive(NotificationCenter.default.publisher(for: .sessionsChanged)) { _ in reload() }
    }

    private func row(_ rec: SessionRecord) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(rec.sessionName).font(.body)
            HStack(spacing: 6) {
                Text(rec.createdAt.prefix(19).replacingOccurrences(of: "T", with: " "))
                Text("·")
                Text(Self.duration(rec.durationSeconds))
            }
            .font(.caption).foregroundStyle(.secondary).monospacedDigit()
        }
        .padding(.vertical, 2)
    }

    private func reload() {
        sessions = (try? env.store.all(sort: .createdDesc)) ?? []
    }

    static func duration(_ seconds: Int) -> String {
        let h = seconds / 3600, m = (seconds % 3600) / 60, s = seconds % 60
        return h > 0 ? String(format: "%d:%02d:%02d", h, m, s)
                     : String(format: "%02d:%02d", m, s)
    }
}
