import SwiftUI
import AppKit
import LocalCaptionKit

/// A finished interview, read-only (SPEC-15 §History): the transcript beside the AI conversation
/// (skill steps, questions and answers), with the summary on top. Shown after Stop and when the
/// interview is opened from the sessions list.
struct InterviewReplayView: View {
    @ObservedObject var interview: InterviewController
    let transcript: String
    let fontSize: Double

    private enum Pane: String, CaseIterable { case conversation = "Conversation", transcript = "Transcript" }
    @State private var pane: Pane = .conversation
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
            Label("Read-only", systemImage: "lock").font(.caption).foregroundStyle(.secondary)
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
                            Label("Generate summary", systemImage: "sparkles")
                        }
                        .disabled(interview.summarizing)
                    }
                }
            }
        }
    }

    // MARK: Panes

    private var transcriptPane: some View {
        ScrollView {
            Text(transcript.isEmpty ? "No transcript." : transcript)
                .font(.system(size: fontSize * 0.9))
                .foregroundStyle(transcript.isEmpty ? .secondary : .primary)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.trailing, 6)
        }
    }

    private var conversationPane: some View {
        let open = expanded ?? Set(interview.turns.filter { $0.kind != .skill }.map(\.n))
        return ScrollView {
            LazyVStack(alignment: .leading, spacing: 10) {
                if !interview.legacyBriefing.isEmpty {
                    GroupBox("Briefing") { MarkdownText(markdown: interview.legacyBriefing, fontSize: fontSize * 0.8) }
                }
                if interview.turns.isEmpty {
                    Text("No questions were asked.").foregroundStyle(.secondary).padding(.vertical, 12)
                }
                ForEach(interview.turns) { turn in
                    AnswerCard(turn: turn, isLatest: false, isExpanded: open.contains(turn.n), isStreaming: false,
                               folder: interview.folder, fontSize: fontSize,
                               toggle: {
                                   var set = open
                                   if set.contains(turn.n) { set.remove(turn.n) } else { set.insert(turn.n) }
                                   expanded = set
                               },
                               regenerate: nil)
                }
            }
            .padding(.leading, 6)
        }
    }
}
