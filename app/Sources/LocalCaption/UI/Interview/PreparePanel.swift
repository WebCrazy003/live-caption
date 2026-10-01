import SwiftUI
import LocalCaptionKit

/// Interview mode, before the interview: setup form, Prepare, and the streamed briefing
/// (SPEC-13 §Prepare panel).
struct PreparePanel: View {
    @ObservedObject var interview: InterviewController
    @ObservedObject var codex: CodexService
    @ObservedObject var library: InterviewLibrary
    let fontSize: Double
    @State private var showingLibrary = false
    @State private var setupExpanded = true

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                CodexStatusRow(codex: codex)
                if interview.setupChangedSinceReady {
                    Label("Setup changed — Prepare again to apply it.", systemImage: "arrow.triangle.2.circlepath")
                        .font(.callout).foregroundStyle(.orange)
                }
                DisclosureGroup(isExpanded: $setupExpanded) { form } label: {
                    Text("Interview setup").font(.headline)
                }
                prepareRow
                briefingArea
            }
            .padding(.trailing, 4)
        }
        .sheet(isPresented: $showingLibrary) { LibraryView(library: library) }
        .onChange(of: interview.prepState) { _, state in
            if state == .preparing { setupExpanded = false }
        }
    }

    // MARK: Form

    private var form: some View {
        VStack(alignment: .leading, spacing: 10) {
            TextField("Interview name (optional)", text: $interview.draft.name)
            HStack {
                TextField("Company", text: $interview.draft.company)
                TextField("Role", text: $interview.draft.role)
            }
            Picker("CV", selection: $interview.draft.cvId) {
                Text("None").tag(String?.none)
                ForEach(library.documents(of: .cv)) { Text($0.title).tag(Optional($0.id)) }
            }
            Picker("Job description", selection: jdSelection) {
                Text("None").tag("none")
                ForEach(library.documents(of: .jd)) { Text($0.title).tag("doc:" + $0.id) }
                Text("Paste below…").tag("paste")
            }
            if interview.draft.usePastedJD {
                TextEditor(text: $interview.draft.jdPaste)
                    .font(.callout).frame(minHeight: 90).border(.quaternary)
            }
            if !library.documents(of: .notes).isEmpty {
                multiSelect("Notes", items: library.documents(of: .notes).map { ($0.id, $0.title) },
                            selection: $interview.draft.noteIds)
            }
            if library.skills.isEmpty {
                Text("No interview skills yet — import one from the library.").font(.caption).foregroundStyle(.secondary)
            } else {
                multiSelect("Skills", items: library.skills.map { ($0.id, $0.title) }, selection: $interview.draft.skillIds)
            }
            VStack(alignment: .leading, spacing: 4) {
                Text("Instructions").font(.caption).foregroundStyle(.secondary)
                TextEditor(text: $interview.draft.instructions)
                    .font(.callout).frame(minHeight: 60).border(.quaternary)
            }
            Picker("Answer length", selection: $interview.draft.answerLength) {
                Text("Short").tag(Config.Interview.AnswerLength.short)
                Text("Medium").tag(Config.Interview.AnswerLength.medium)
                Text("Long").tag(Config.Interview.AnswerLength.long)
            }
            .pickerStyle(.segmented)
            HStack {
                Picker("Model", selection: $interview.draft.model) {
                    ForEach(modelChoices, id: \.self) { id in
                        Text(codex.models.first { $0.id == id }?.displayName ?? id).tag(id)
                    }
                }
                Picker("Effort", selection: $interview.draft.effort) {
                    ForEach(codex.efforts(for: interview.draft.model), id: \.self) { Text($0.capitalized).tag($0) }
                }
                .frame(maxWidth: 150)
            }
            HStack {
                Text(sizeLine).font(.caption)
                    .foregroundStyle(interview.prepChars > InterviewController.maxPrepChars ? .red : .secondary)
                Spacer()
                Button("Manage library…") { showingLibrary = true }.buttonStyle(.link)
            }
            if !interview.oversizedSkills.isEmpty {
                Label("Large skill: \(interview.oversizedSkills.joined(separator: ", ")) — it slows every answer.",
                      systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.orange)
            }
        }
        .padding(.top, 6)
    }

    private var jdSelection: Binding<String> {
        Binding(
            get: { interview.draft.usePastedJD ? "paste" : (interview.draft.jdId.map { "doc:" + $0 } ?? "none") },
            set: { tag in
                if tag == "paste" { interview.draft.usePastedJD = true; interview.draft.jdId = nil }
                else if tag.hasPrefix("doc:") { interview.draft.usePastedJD = false; interview.draft.jdId = String(tag.dropFirst(4)) }
                else { interview.draft.usePastedJD = false; interview.draft.jdId = nil }
            })
    }

    private var modelChoices: [String] {
        var ids = codex.models.map(\.id)
        if !ids.contains(interview.draft.model) { ids.insert(interview.draft.model, at: 0) }
        return ids
    }

    private var sizeLine: String {
        let chars = interview.prepChars
        let tokens = chars / 4
        if chars > InterviewController.maxPrepChars {
            return "Too much context: \(chars.formatted()) characters (limit \(InterviewController.maxPrepChars.formatted()))"
        }
        return "≈ \(tokens.formatted()) tokens of context"
    }

    private func multiSelect(_ title: String, items: [(String, String)], selection: Binding<Set<String>>) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            ForEach(items, id: \.0) { id, label in
                Toggle(label, isOn: Binding(
                    get: { selection.wrappedValue.contains(id) },
                    set: { on in if on { selection.wrappedValue.insert(id) } else { selection.wrappedValue.remove(id) } }))
                .toggleStyle(.checkbox)
            }
        }
    }

    // MARK: Prepare

    @ViewBuilder private var prepareRow: some View {
        HStack(spacing: 10) {
            switch interview.prepState {
            case .notPrepared:
                Button { Task { await interview.prepare() } } label: { Label("Prepare", systemImage: "sparkles") }
                    .buttonStyle(.borderedProminent).disabled(!interview.canPrepare)
            case .preparing:
                ProgressView().controlSize(.small)
                Text("Preparing…").foregroundStyle(.secondary)
            case .ready:
                Label("Ready", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                Spacer()
                Button("Prepare again") { Task { await interview.prepare() } }
                    .disabled(!interview.canPrepare)
            case .failed(let message):
                Label(message, systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
                    .font(.callout).lineLimit(3)
                Spacer()
                Button("Retry") { Task { await interview.retryPrepare() } }.disabled(!codex.isReady)
            }
        }
    }

    @ViewBuilder private var briefingArea: some View {
        if !interview.briefing.isEmpty {
            GroupBox {
                MarkdownText(markdown: interview.briefing, fontSize: fontSize * 0.85)
                    .padding(4)
            } label: {
                Text("Briefing").font(.headline)
            }
        }
    }
}

/// Codex status + sign-in, shared by the Prepare panel and Settings (SPEC-12 §Sign-in).
struct CodexStatusRow: View {
    @ObservedObject var codex: CodexService

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                if codex.checking || codex.status == nil {
                    ProgressView().controlSize(.small)
                    Text("Checking Codex…").foregroundStyle(.secondary)
                } else if let status = codex.status {
                    Image(systemName: status.isReady ? "checkmark.seal.fill" : "exclamationmark.triangle.fill")
                        .foregroundStyle(status.isReady ? .green : .orange)
                    Text(status.summary).font(.callout).lineLimit(2)
                    Spacer()
                    if case .signedOut = status {
                        if codex.signIn == nil {
                            Button("Sign in…") { Task { await codex.startSignIn() } }
                        } else {
                            ProgressView().controlSize(.small)
                            Button("Cancel") { Task { await codex.cancelSignIn() } }
                        }
                    } else if !status.isReady {
                        Button("Check again") { Task { await codex.refresh() } }
                    }
                }
            }
            if codex.signIn != nil {
                Text("Finish signing in to ChatGPT in your browser.").font(.caption).foregroundStyle(.secondary)
            }
            if let err = codex.signInError {
                Text(err).font(.caption).foregroundStyle(.orange)
            }
            if codex.isReady, let w = codex.usage?.lowest {
                Label(w.line, systemImage: "gauge.with.dots.needle.33percent")
                    .font(.caption).foregroundStyle(codex.usageIsLow ? .orange : .secondary)
            }
        }
        .task { if codex.status == nil { await codex.refresh() } }
    }
}

/// One-time notice before Interview mode is first used (SPEC-11 §Privacy).
struct InterviewPrivacyNotice: View {
    let accept: () -> Void
    let cancel: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Label("Interview mode sends data to OpenAI", systemImage: "network").font(.title3.weight(.semibold))
            Text("Caption only mode keeps everything on this Mac. Interview mode is different:")
            VStack(alignment: .leading, spacing: 6) {
                Label("Your CV, the job description, your skills and instructions are sent when you press Prepare.",
                      systemImage: "doc.text")
                Label("The interviewer's recent words are sent each time you press Ask.", systemImage: "text.bubble")
                Label("Screenshots on your clipboard are sent only if you turn that on in Settings.",
                      systemImage: "photo")
            }
            .font(.callout)
            Text("They go to OpenAI through the Codex app, signed in with your ChatGPT account. "
                 + "Codex is locked down: it can't read your files, run commands or browse.")
                .font(.callout).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Cancel", action: cancel)
                Button("Use Interview mode", action: accept).keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 460)
    }
}
