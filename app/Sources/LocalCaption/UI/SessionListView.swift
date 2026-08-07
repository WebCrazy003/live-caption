import SwiftUI
import AppKit
import LocalCaptionKit

/// Session List (SPEC.md §10 / SPEC-06): browse, open (read-only), rename, delete, search,
/// and sort. Backed by the SQLite store.
struct SessionListView: View {
    @EnvironmentObject var env: AppEnvironment
    @Binding var selection: Int64?

    @State private var sessions: [SessionRecord] = []
    @State private var search = ""
    @State private var sort: SessionSort = .createdDesc
    @State private var renameTarget: SessionRecord?
    @State private var renameText = ""
    @State private var deleteTarget: SessionRecord?

    var body: some View {
        List(selection: $selection) {
            if sessions.isEmpty {
                Text(search.isEmpty ? "No sessions yet" : "No matches")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .center)
                    .listRowSeparator(.hidden)
            } else {
                ForEach(sessions) { rec in
                    row(rec)
                        .tag(rec.id)
                        .contextMenu { rowMenu(rec) }
                }
            }
        }
        .searchable(text: $search, placement: .sidebar, prompt: "Search sessions")
        .navigationTitle("Sessions")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Picker("Sort by", selection: $sort) {
                        ForEach(SessionSort.allCases, id: \.self) { Text($0.label).tag($0) }
                    }
                } label: { Label("Sort", systemImage: "arrow.up.arrow.down") }
            }
            ToolbarItem(placement: .primaryAction) {
                Button { NotificationCenter.default.post(name: .newSession, object: nil) } label: {
                    Label("New Session", systemImage: "plus")
                }
            }
        }
        .onAppear(perform: reload)
        .onChange(of: search) { reload() }
        .onChange(of: sort) { reload() }
        .onReceive(NotificationCenter.default.publisher(for: .newSession)) { _ in reload() }
        .onReceive(NotificationCenter.default.publisher(for: .sessionsChanged)) { _ in reload() }
        .sheet(item: $renameTarget) { rec in renameSheet(rec) }
        .confirmationDialog("Delete “\(deleteTarget?.sessionName ?? "")”?",
                            isPresented: Binding(get: { deleteTarget != nil },
                                                 set: { if !$0 { deleteTarget = nil } }),
                            presenting: deleteTarget) { rec in
            Button("Delete Session Only") { delete(rec, alsoFile: false) }
            if rec.transcriptFile != nil {
                Button("Delete Session and Transcript File", role: .destructive) { delete(rec, alsoFile: true) }
            }
            Button("Cancel", role: .cancel) {}
        } message: { _ in
            Text("The transcript file is kept unless you choose to delete it.")
        }
    }

    // MARK: Rows

    private func row(_ rec: SessionRecord) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(rec.sessionName).font(.body)
            HStack(spacing: 6) {
                Text(rec.createdAt.prefix(19).replacingOccurrences(of: "T", with: " "))
                Text("·")
                Text(TimeFormat.clock(rec.durationSeconds))
            }
            .font(.caption).foregroundStyle(.secondary).monospacedDigit()
        }
        .padding(.vertical, 2)
    }

    @ViewBuilder private func rowMenu(_ rec: SessionRecord) -> some View {
        Button("Rename…") { renameText = rec.sessionName; renameTarget = rec }
        if let path = rec.transcriptFile {
            Button("Reveal in Finder") {
                NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
            }
        }
        Divider()
        Button("Delete…", role: .destructive) { deleteTarget = rec }
    }

    private func renameSheet(_ rec: SessionRecord) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Rename Session").font(.headline)
            TextField("Session name", text: $renameText).frame(width: 320)
            HStack {
                Spacer()
                Button("Cancel") { renameTarget = nil }
                Button("Save") {
                    let name = renameText.trimmingCharacters(in: .whitespacesAndNewlines)
                    if let id = rec.id, !name.isEmpty { try? env.store.rename(id: id, to: name) }
                    renameTarget = nil
                    reload()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(renameText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(20)
    }

    // MARK: Actions

    private func delete(_ rec: SessionRecord, alsoFile: Bool) {
        guard let id = rec.id else { return }
        try? env.store.delete(id: id)
        if alsoFile, let path = rec.transcriptFile { SessionFiles.deleteTranscript(atTxtPath: path) }
        if selection == id { selection = nil }
        deleteTarget = nil
        reload()
    }

    private func reload() {
        sessions = (try? env.store.all(sort: sort, search: search.isEmpty ? nil : search)) ?? []
        if let sel = selection, !sessions.contains(where: { $0.id == sel }) { selection = nil }
    }
}

extension SessionSort {
    var label: String {
        switch self {
        case .createdDesc: return "Newest first"
        case .createdAsc: return "Oldest first"
        case .nameAsc: return "Name (A–Z)"
        case .nameDesc: return "Name (Z–A)"
        case .durationDesc: return "Longest first"
        case .durationAsc: return "Shortest first"
        }
    }
}
