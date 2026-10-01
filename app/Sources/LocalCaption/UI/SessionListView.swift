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
    @State private var filter: ModeFilter = .all
    /// Interview folder → "name company role", for search (SPEC-15 §History).
    @State private var interviewText: [String: String] = [:]

    enum ModeFilter: String, CaseIterable { case all = "All", captions = "Captions", interviews = "Interviews" }

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
                    Picker("Show", selection: $filter) {
                        ForEach(ModeFilter.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                    }
                    Picker("Sort by", selection: $sort) {
                        ForEach(SessionSort.allCases, id: \.self) { Text($0.label).tag($0) }
                    }
                } label: { Label("Sort & filter", systemImage: filter == .all ? "arrow.up.arrow.down" : "line.3.horizontal.decrease.circle.fill") }
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
        .onChange(of: filter) { reload() }
        .onReceive(NotificationCenter.default.publisher(for: .newSession)) { _ in reload() }
        .onReceive(NotificationCenter.default.publisher(for: .sessionsChanged)) { _ in reload() }
        .sheet(item: $renameTarget) { rec in renameSheet(rec) }
        .confirmationDialog("Delete “\(deleteTarget?.sessionName ?? "")”?",
                            isPresented: Binding(get: { deleteTarget != nil },
                                                 set: { if !$0 { deleteTarget = nil } }),
                            presenting: deleteTarget) { rec in
            if rec.isInterview {
                // Interview data (CV text, Q&A, screenshots) goes by default (SPEC-15 §History).
                Button("Delete Session and Interview Data") { delete(rec, alsoFile: false, alsoInterview: true) }
                if rec.transcriptFile != nil {
                    Button("Delete Session, Interview Data and Transcript File", role: .destructive) {
                        delete(rec, alsoFile: true, alsoInterview: true)
                    }
                }
                Button("Delete Session Only (keep interview data)") { delete(rec, alsoFile: false, alsoInterview: false) }
            } else {
                Button("Delete Session Only") { delete(rec, alsoFile: false) }
                if rec.transcriptFile != nil {
                    Button("Delete Session and Transcript File", role: .destructive) { delete(rec, alsoFile: true) }
                }
            }
            Button("Cancel", role: .cancel) {}
        } message: { rec in
            Text(rec.isInterview
                 ? "Interview data is the CV text, questions, answers and screenshots. The transcript file is kept unless you choose to delete it."
                 : "The transcript file is kept unless you choose to delete it.")
        }
    }

    // MARK: Rows

    private func row(_ rec: SessionRecord) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 5) {
                if rec.isInterview {
                    Image(systemName: "briefcase.fill").font(.caption).foregroundStyle(.secondary)
                        .help("Interview")
                }
                Text(rec.sessionName).font(.body)
            }
            if rec.isInterview, let dir = rec.interviewDir, let name = interviewText[dir]?.components(separatedBy: "\n").first,
               !name.isEmpty {
                Text(name).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
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

    private func delete(_ rec: SessionRecord, alsoFile: Bool, alsoInterview: Bool = false) {
        guard let id = rec.id else { return }
        try? env.store.delete(id: id)
        if alsoFile, let path = rec.transcriptFile { SessionFiles.deleteTranscript(atTxtPath: path) }
        if alsoInterview, let dir = rec.interviewDir {
            let folder = URL(fileURLWithPath: dir)
            let thread = (try? InterviewFiles.read(from: folder))?.threadId
            try? FileManager.default.removeItem(at: folder)
            // Don't leave the CV in Codex's session store either (SPEC-12 §Threads & turns).
            if let thread { Task { await env.codex.engine.archiveThread(id: thread) } }
        }
        if selection == id { selection = nil }
        deleteTarget = nil
        reload()
    }

    private func reload() {
        let mode: String? = filter == .all ? nil : (filter == .interviews ? SessionRecord.interviewMode : SessionRecord.captionMode)
        let rows = (try? env.store.all(sort: sort, mode: mode)) ?? []
        var texts = interviewText
        for rec in rows where rec.isInterview {
            guard let dir = rec.interviewDir, texts[dir] == nil else { continue }
            if let r = try? InterviewFiles.read(from: URL(fileURLWithPath: dir)) {
                texts[dir] = [r.name, r.setup.company, r.setup.role].joined(separator: "\n")
            } else {
                texts[dir] = ""
            }
        }
        interviewText = texts
        let q = search.trimmingCharacters(in: .whitespacesAndNewlines)
        sessions = q.isEmpty ? rows : rows.filter { rec in
            rec.sessionName.localizedCaseInsensitiveContains(q)
                || (rec.interviewDir.flatMap { texts[$0] }?.localizedCaseInsensitiveContains(q) ?? false)
        }
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
