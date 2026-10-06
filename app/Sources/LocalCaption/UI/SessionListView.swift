import SwiftUI
import AppKit
import LocalCaptionKit

/// Session List (SPEC.md §10 / SPEC-06), the Sessions window's sidebar: browse, select (details
/// on the right), rename, delete, search, and sort. Backed by the SQLite store.
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
    /// Session id → the interview's row subtitle (interviewee · company · step, else its name).
    @State private var interviewSubtitle: [Int64: String] = [:]
    /// Session id → the interview's searchable text (details, name, JD, CV title).
    @State private var interviewText: [Int64: String] = [:]

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
                        .tag(rec.id ?? -1)
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
        }
        .onAppear(perform: reload)
        .onChange(of: search) { reload() }
        .onChange(of: sort) { reload() }
        .onChange(of: filter) { reload() }
        .onReceive(NotificationCenter.default.publisher(for: .sessionsChanged)) { _ in reload() }
        // ⌫ on the selected session, and the viewer's Delete… button, open the same confirmation.
        .onDeleteCommand { if let id = selection { deleteTarget = sessions.first { $0.id == id } } }
        .onReceive(NotificationCenter.default.publisher(for: .requestDeleteSession)) { note in
            if let id = note.object as? Int64 { deleteTarget = sessions.first { $0.id == id } }
        }
        .sheet(item: $renameTarget) { rec in renameSheet(rec) }
        .confirmationDialog("Delete “\(deleteTarget?.sessionName ?? "")”?",
                            isPresented: Binding(get: { deleteTarget != nil },
                                                 set: { if !$0 { deleteTarget = nil } }),
                            presenting: deleteTarget) { rec in
            if rec.isInterview {
                // Interview data (CV text, Q&A, screenshots) goes by default (SPEC-15 §History).
                Button("Delete Session and Interview Data") { delete(rec, alsoFile: false, alsoInterview: true) }
                if hasFiles(rec) {
                    Button("Delete Session, Interview Data and \(filesLabel(rec))", role: .destructive) {
                        delete(rec, alsoFile: true, alsoInterview: true)
                    }
                }
                Button("Delete Session Only (keep interview data)") { delete(rec, alsoFile: false, alsoInterview: false) }
            } else {
                Button("Delete Session Only") { delete(rec, alsoFile: false) }
                if hasFiles(rec) {
                    Button("Delete Session and \(filesLabel(rec))", role: .destructive) { delete(rec, alsoFile: true) }
                }
            }
            Button("Cancel", role: .cancel) {}
        } message: { rec in
            let kept = rec.audioFile == nil ? "The transcript file is" : "The transcript and audio files are"
            Text(rec.isInterview
                 ? "Interview data is the CV text, questions, answers and screenshots. \(kept) kept unless you choose to delete them."
                 : "\(kept) kept unless you choose to delete them.")
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
                if rec.audioFile != nil {
                    Image(systemName: "waveform").font(.caption).foregroundStyle(.secondary)
                        .help("Has an audio recording")
                }
            }
            if rec.isInterview, let id = rec.id, let subtitle = interviewSubtitle[id], !subtitle.isEmpty {
                Text(subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1)
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
        if let path = rec.audioFile {
            Button("Play Audio") { NSWorkspace.shared.open(URL(fileURLWithPath: path)) }
            Button("Reveal Audio in Finder") {
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

    private func hasFiles(_ rec: SessionRecord) -> Bool { rec.transcriptFile != nil || rec.audioFile != nil }

    private func filesLabel(_ rec: SessionRecord) -> String {
        switch (rec.transcriptFile != nil, rec.audioFile != nil) {
        case (true, true): return "Transcript and Audio Files"
        case (false, true): return "Audio File"
        default: return "Transcript File"
        }
    }

    // MARK: Actions

    private func delete(_ rec: SessionRecord, alsoFile: Bool, alsoInterview: Bool = false) {
        guard let id = rec.id else { return }
        // Read the interviews first: deleting the session sets their session_id to NULL, after
        // which they can no longer be found and would be left behind.
        let linked = alsoInterview ? ((try? env.store.interviews(sessionId: id)) ?? []) : []
        try? env.store.delete(id: id)
        if alsoFile, let path = rec.transcriptFile { SessionFiles.deleteTranscript(atTxtPath: path) }
        if alsoFile, let path = rec.audioFile { SessionFiles.deleteAudio(atPath: path) }
        for saved in linked {
            // Rows, turns and screenshots go together; the Codex thread is archived too, so the CV
            // doesn't linger in its session store (SPEC-12 §Threads & turns).
            try? env.store.deleteInterview(id: saved.id)
            if let thread = saved.threadId { Task { await env.codex.engine.archiveThread(id: thread) } }
        }
        if selection == id { selection = nil }
        deleteTarget = nil
        reload()
    }

    private func reload() {
        let mode: String? = filter == .all ? nil : (filter == .interviews ? SessionRecord.interviewMode : SessionRecord.captionMode)
        let rows = (try? env.store.all(sort: sort, mode: mode)) ?? []
        var texts: [Int64: String] = [:]
        var subtitles: [Int64: String] = [:]
        for r in (try? env.store.allInterviews()) ?? [] {
            guard let sid = r.sessionId, texts[sid] == nil else { continue }
            let who = [r.setup.candidate ?? "", r.setup.company, r.setup.step.map { "step \($0)" } ?? ""]
                .filter { !$0.isEmpty }
            subtitles[sid] = who.count > 1 ? who.joined(separator: " · ") : r.name
            texts[sid] = [r.setup.candidate ?? "", r.setup.company, r.name, r.setup.cvTitle ?? "",
                          r.setup.jdTextInline ?? ""].joined(separator: "\n")
        }
        interviewText = texts
        interviewSubtitle = subtitles
        let q = search.trimmingCharacters(in: .whitespacesAndNewlines)
        sessions = q.isEmpty ? rows : rows.filter { rec in
            rec.sessionName.localizedCaseInsensitiveContains(q)
                || (rec.id.flatMap { texts[$0] }?.localizedCaseInsensitiveContains(q) ?? false)
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
