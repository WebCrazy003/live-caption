import SwiftUI
import AppKit
import UniformTypeIdentifiers
import LocalCaptionKit

/// The interview setup (SPEC-13 §Interview panel): Codex status, the CV (pick or upload), the
/// pasted JD, and the two discovery steps. Each step is run by hand and can be run again.
struct InterviewSetupSection: View {
    @ObservedObject var interview: InterviewController
    @ObservedObject var codex: CodexService
    @ObservedObject var library: InterviewLibrary
    @State private var showingLibrary = false
    @State private var uploadError: String?
    @State private var confirmingStartOver = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            CodexStatusRow(codex: codex)

            VStack(alignment: .leading, spacing: 6) {
                Text("CV").font(.subheadline.weight(.semibold))
                HStack {
                    Picker("CV", selection: $interview.draft.cvId) {
                        Text("Choose a CV…").tag(String?.none)
                        ForEach(library.documents(of: .cv)) { Text($0.title).tag(Optional($0.id)) }
                    }
                    .labelsHidden()
                    Button("Upload…") { uploadCV() }
                }
                if let uploadError {
                    Text(uploadError).font(.caption).foregroundStyle(.orange)
                }
                stepRow(.discoveryCV)
            }

            VStack(alignment: .leading, spacing: 6) {
                Text("Job description").font(.subheadline.weight(.semibold))
                TextEditor(text: $interview.draft.jobDescription)
                    .font(.callout).frame(minHeight: 90, maxHeight: 180)
                    .overlay(alignment: .topLeading) {
                        if interview.draft.jobDescription.isEmpty {
                            Text("Paste the job description here").font(.callout).foregroundStyle(.tertiary)
                                .padding(.top, 1).padding(.leading, 5).allowsHitTesting(false)
                        }
                    }
                    .border(.quaternary)
                stepRow(.discoveryJD)
            }

            Text("Then pick an answering profile — Intro, Tech or Behavioral — in the header. "
                 + "Live coding & design is optional, after Tech.")
                .font(.caption).foregroundStyle(.secondary)

            HStack {
                Button("Manage library…") { showingLibrary = true }.buttonStyle(.link)
                Spacer()
                if interview.record != nil, interview.record?.startedAt == nil {
                    Button("Start over") { confirmingStartOver = true }.buttonStyle(.link)
                        .help("Discard this interview's conversation and begin a new one")
                }
            }
        }
        .sheet(isPresented: $showingLibrary) { LibraryView(library: library) }
        .confirmationDialog("Start over?", isPresented: $confirmingStartOver) {
            Button("Discard the conversation", role: .destructive) {
                Task { await interview.discardUnstarted(); interview.resetForNewInterview() }
            }
        } message: {
            Text("The skill steps you've run so far are deleted. Your CV and library stay.")
        }
    }

    private func stepRow(_ step: InterviewController.Step) -> some View {
        let done = interview.isDone(step)
        let running = interview.streamingTurn != nil
            && interview.turns.last?.question.hasPrefix("/\(step.rawValue)") == true
        let blocker = interview.blocker(step)
        return HStack(spacing: 8) {
            if running {
                ProgressView().controlSize(.small)
            } else {
                Image(systemName: done ? "checkmark.circle.fill" : "circle")
                    .foregroundStyle(done ? .green : .secondary)
            }
            Text(step.title)
            Spacer()
            if let blocker {
                Text(blocker).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Button(done ? "Run again" : "Run") { Task { await interview.run(step) } }
                .disabled(blocker != nil || running)
        }
    }

    private func uploadCV() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.pdf, .plainText, UTType(filenameExtension: "md") ?? .plainText]
        panel.allowsMultipleSelection = false
        panel.message = "Choose your CV (PDF, Markdown or text)"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do { try interview.uploadCV(from: url); uploadError = nil }
        catch { uploadError = error.localizedDescription }
    }
}
