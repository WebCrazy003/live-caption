import SwiftUI
import AppKit
import LocalCaptionKit

/// A finished interview (SPEC-15 §History): the transcript, CV and JD beside the AI conversation
/// (skill steps, questions and answers), with the summary on top. Read-only from the sessions
/// list; right after End interview it is `interactive` — Summarize and follow-up prompts.
struct InterviewReplayView: View {
    @ObservedObject var interview: InterviewController
    var interactive = false
    let transcript: String
    let fontSize: Double

    private enum Pane: String, CaseIterable { case conversation = "Conversation", transcript = "Transcript" }
    private enum Source: String, CaseIterable { case transcript = "Transcript", cv = "CV", jd = "JD" }
    @State private var pane: Pane = .conversation
    @State private var source: Source = .transcript
    @State private var followUp = ""
    @State private var expanded: Set<Int>?
    @State private var summaryExpanded = true
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            toolbar
            summary
            GeometryReader { geo in
                if geo.size.width >= 700 {
                    HSplitView {
                        transcriptPane.frame(minWidth: 220, idealWidth: geo.size.width * 0.45)
                        conversationPane.frame(minWidth: 300)
                    }
                } else {
                    VStack(spacing: 8) {
                        Picker("", selection: $pane) {
                            ForEach(Pane.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                        }
                        .pickerStyle(.segmented).labelsHidden()
                        if pane == .conversation { conversationPane } else { transcriptPane }
                    }
                }
            }
        }
    }

    // MARK: Toolbar

    private var toolbar: some View {
        HStack(spacing: 10) {
            if interactive {
                Label("Interview ended", systemImage: "flag.checkered").font(.caption).foregroundStyle(.secondary)
            } else {
                Label("Read-only", systemImage: "lock").font(.caption).foregroundStyle(.secondary)
            }
            if let p = interview.activeProfile {
                Text("Profile: \(p.label)").font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button {
                let pb = NSPasteboard.general
                pb.clearContents(); pb.setString(interview.record?.qaMarkdown() ?? "", forType: .string)
                copied = true
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { copied = false }
            } label: { Label(copied ? "Copied" : "Copy Q&A", systemImage: copied ? "checkmark" : "doc.on.doc") }
            .disabled(interview.turns.isEmpty)
            if let folder = interview.folder {
                Button { NSWorkspace.shared.activateFileViewerSelecting([folder]) } label: {
                    Label("Reveal", systemImage: "folder")
                }
                .help("Reveal the interview folder in Finder")
            }
        }
    }

    // MARK: Summary

    @ViewBuilder private var summary: some View {
        let status = interview.record?.summary.status
        let hasThread = interview.record?.threadId != nil
        if !interview.summaryText.isEmpty || interview.summarizing || interview.summaryError != nil || hasThread {
            DisclosureGroup(isExpanded: $summaryExpanded) {
                VStack(alignment: .leading, spacing: 6) {
                    if interview.summarizing && interview.summaryText.isEmpty {
                        HStack(spacing: 8) { ProgressView().controlSize(.small); Text("Writing the summary…").foregroundStyle(.secondary) }
                    }
                    if let err = interview.summaryError {
                        Label(err, systemImage: "exclamationmark.triangle").foregroundStyle(.orange).font(.callout)
                    }
                    if !interview.summaryText.isEmpty {
                        ScrollView { MarkdownText(markdown: interview.summaryText, fontSize: fontSize * 0.85) }
                            .frame(maxHeight: 260)
                    } else if !interview.summarizing && interview.summaryError == nil {
                        Text(status == .skipped ? "No summary — summarizing is off in Settings." : "No summary yet.")
                            .foregroundStyle(.secondary)
                    }
                }
                .padding(.top, 4)
            } label: {
                HStack {
                    Text("Summary").font(.headline)
                    Spacer()
                    if hasThread && (status != .done || interview.summaryText.isEmpty) {
                        Button { Task { await interview.generateSummary(transcript: transcript) } } label: {
                            Label("Summarize interview", systemImage: "sparkles")
                        }
                        .disabled(interview.summarizing)
                    }
                }
            }
        }
    }

    // MARK: Panes

    /// Transcript, CV or JD — what the interviewer said and what the coach worked from.
    private var transcriptPane: some View {
        VStack(alignment: .leading, spacing: 6) {
            Picker("", selection: $source) {
                ForEach(Source.allCases, id: \.self) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.segmented).labelsHidden()
            if source == .cv, let title = interview.cvTitle {
                Text(title).font(.caption).foregroundStyle(.secondary)
            }
            ScrollView {
                Text(sourceText.isEmpty ? emptyText : sourceText)
                    .font(.system(size: fontSize * 0.9))
                    .foregroundStyle(sourceText.isEmpty ? .secondary : .primary)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.trailing, 6)
            }
        }
    }

    private var sourceText: String {
        switch source {
        case .transcript: return transcript
        case .cv: return interview.cvText
        case .jd: return interview.jdText
        }
    }

    private var emptyText: String {
        switch source {
        case .transcript: return "No transcript."
        case .cv: return "No CV was used in this interview."
        case .jd: return "No job description was pasted."
        }
    }

    private var conversationPane: some View {
        VStack(spacing: 8) {
            conversationList
            if interactive { wrapUpBar }
        }
    }

    /// After End interview: summarize, or keep asking the coach (same thread).
    private var wrapUpBar: some View {
        VStack(alignment: .leading, spacing: 6) {
            Divider()
            HStack {
                TextField("Follow-up prompt — e.g. what should I improve?", text: $followUp, axis: .vertical)
                    .lineLimit(1...4).textFieldStyle(.roundedBorder)
                    .onSubmit(sendFollowUp)
                Button("Send", action: sendFollowUp)
                    .disabled(followUp.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || interview.isStreaming)
            }
            .disabled(interview.record?.threadId == nil)
        }
    }

    private func sendFollowUp() {
        let text = followUp.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        followUp = ""
        Task { await interview.sendFollowUp(text) }
    }

    private var conversationList: some View {
        let open = expanded ?? Set(interview.turns.filter { $0.kind != .skill }.map(\.n))
        return ScrollViewReader { proxy in ScrollView {
            LazyVStack(alignment: .leading, spacing: 10) {
                if !interview.legacyBriefing.isEmpty {
                    GroupBox("Briefing") { MarkdownText(markdown: interview.legacyBriefing, fontSize: fontSize * 0.8) }
                }
                if interview.turns.isEmpty {
                    Text("No questions were asked.").foregroundStyle(.secondary).padding(.vertical, 12)
                }
                ForEach(interview.turns) { turn in
                    AnswerCard(turn: turn, isLatest: false,
                               isExpanded: open.contains(turn.n) || interview.streamingTurn == turn.n,
                               isStreaming: interview.streamingTurn == turn.n,
                               folder: interview.folder, fontSize: fontSize,
                               toggle: {
                                   var set = open
                                   if set.contains(turn.n) { set.remove(turn.n) } else { set.insert(turn.n) }
                                   expanded = set
                               },
                               regenerate: nil)
                }
                Color.clear.frame(height: 1).id("end")
            }
            .padding(.leading, 6)
        }
        .onChange(of: interview.turns.count) { _, _ in if interactive { withAnimation { proxy.scrollTo("end") } } }
        .onChange(of: interview.turns.last?.answer.count ?? 0) { _, _ in if interactive { proxy.scrollTo("end") } }
        }
    }
}
