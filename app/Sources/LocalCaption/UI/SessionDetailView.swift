import SwiftUI
import AppKit
import LocalCaptionKit

/// Sessions window → the selected session (SPEC.md §10 / SPEC-06): its details and, read-only,
/// its interview (summary, conversation, transcript, CV, JD) or its captions — all from the
/// database. An interview can be opened in the main window's interview panel.
struct SessionDetailView: View {
    @EnvironmentObject var env: AppEnvironment
    @Environment(\.dismissWindow) private var dismissWindow
    let sessionID: Int64
    /// Observed so Open in interview panel enables as soon as the main window allows it.
    @ObservedObject var session: SessionController
    @ObservedObject var interview: InterviewController

    @State private var record: SessionRecord?
    @State private var text = ""
    @State private var replay: InterviewController?
    @State private var confirmingOpen = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if let rec = record {
                header(rec)
                if let saved = replay?.record { details(saved) }
                Divider()
                if let replay {
                    InterviewReplayView(interview: replay, transcript: text,
                                        fontSize: Double(env.config.caption.fontSize))
                } else if text.isEmpty {
                    placeholder("No captions were saved for this session.")
                } else {
                    ScrollView {
                        Text(text)
                            .font(.system(size: CGFloat(env.config.caption.fontSize)))
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
            } else {
                placeholder("Session not found.")
            }
        }
        .padding()
        .navigationTitle(record?.sessionName ?? "Session")
        .onAppear(perform: load)
        .onChange(of: sessionID) { load() }
        .onReceive(NotificationCenter.default.publisher(for: .sessionsChanged)) { _ in load() }
        .confirmationDialog("Discard the current preparation?", isPresented: $confirmingOpen) {
            Button("Discard and Open", role: .destructive) { open() }
        } message: {
            Text("The main window has a preparation that hasn't started recording. Opening this interview deletes it.")
        }
    }

    // MARK: Header

    private func header(_ rec: SessionRecord) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                Text(rec.sessionName).font(.headline).textSelection(.enabled)
                HStack(spacing: 6) {
                    Text(rec.createdAt.prefix(19).replacingOccurrences(of: "T", with: " "))
                    Text("·")
                    Text(TimeFormat.clock(rec.durationSeconds))
                }
                .font(.caption).foregroundStyle(.secondary).monospacedDigit()
            }
            HStack(spacing: 10) {
                if replay != nil {
                    let blocker = env.openInPanelBlocker
                    Button {
                        if interview.hasUnstartedPreparation { confirmingOpen = true } else { open() }
                    } label: { Label("Open in Interview Panel", systemImage: "arrow.up.forward.app") }
                    .buttonStyle(.borderedProminent)
                    .disabled(blocker != nil)
                    .help(blocker ?? "Show this interview in the main window — summary and follow-up prompts")
                    if let blocker { Text(blocker).font(.caption).foregroundStyle(.secondary) }
                }
                Spacer()
                if let path = rec.audioFile, FileManager.default.fileExists(atPath: path) {
                    Button {
                        NSWorkspace.shared.open(URL(fileURLWithPath: path))
                    } label: { Label("Play Audio", systemImage: "play.circle") }
                    .buttonStyle(.link)
                    .help(path)
                }
                if let path = rec.transcriptFile, FileManager.default.fileExists(atPath: path) {
                    Button {
                        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
                    } label: { Label("Reveal in Finder", systemImage: "folder") }
                    .buttonStyle(.link)
                }
                Button(role: .destructive) {
                    NotificationCenter.default.post(name: .requestDeleteSession, object: rec.id)
                } label: { Label("Delete…", systemImage: "trash") }
                .help("Delete this session")
            }
        }
    }

    /// Interviewee, company, step and the rest of the interview's setup.
    private func details(_ rec: InterviewRecord) -> some View {
        let questions: Int = rec.turns.filter { $0.kind != .skill }.count
        let mode: String = rec.activeProfile.flatMap(InterviewController.Profile.init(rawValue:))?.label ?? ""
        let step: String = rec.setup.step.map { String($0) } ?? ""
        let role: String = rec.name == "Interview" ? "" : rec.name
        var all: [(String, String)] = []
        all.append(("Interviewee", rec.setup.candidate ?? ""))
        all.append(("Company", rec.setup.company))
        all.append(("Step", step))
        all.append(("Role", role))
        all.append(("Mode", mode))
        all.append(("CV", rec.setup.cvTitle ?? ""))
        all.append(("Model", rec.model + " · " + rec.reasoningEffort))
        all.append(("Questions", String(questions)))
        let items = all.filter { !$0.1.isEmpty }
        return Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 3) {
            ForEach(Array(stride(from: 0, to: items.count, by: 2)), id: \.self) { i in
                GridRow {
                    cell(items[i])
                    if i + 1 < items.count { cell(items[i + 1]) }
                }
            }
        }
        .font(.callout)
    }

    @ViewBuilder private func cell(_ item: (String, String)) -> some View {
        Text(item.0).foregroundStyle(.secondary)
        Text(item.1).lineLimit(1).truncationMode(.tail).textSelection(.enabled)
    }

    private func placeholder(_ message: String) -> some View {
        VStack(spacing: 8) {
            Image(systemName: "doc.text.magnifyingglass").font(.largeTitle).foregroundStyle(.tertiary)
            Text(message).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    // MARK: Actions

    private func open() {
        Task {
            await env.openInPanel(sessionId: sessionID)
            dismissWindow(id: SessionsWindow.id)
        }
    }

    private func load() {
        record = try? env.store.fetch(id: sessionID)
        replay = nil
        guard let rec = record, let id = rec.id else { text = ""; return }
        if rec.isInterview, let saved = try? env.store.interview(sessionId: id) {
            replay = InterviewController(env: env, existing: saved)
        }
        let segments = (try? env.store.segments(sessionId: id)) ?? []
        text = Transcript(segments: segments).body(showTimestamps: env.config.caption.showTimestamps)
    }
}
