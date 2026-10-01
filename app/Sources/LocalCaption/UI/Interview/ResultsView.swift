import SwiftUI
import AppKit
import LocalCaptionKit

/// After an interview, and when reopening one from history (SPEC-15 §Results view):
/// Summary · Q&A · Briefing · Transcript.
struct ResultsView: View {
    @ObservedObject var interview: InterviewController
    let transcript: String
    let fontSize: Double

    enum Tab: String, CaseIterable { case summary = "Summary", qa = "Q&A", briefing = "Briefing", transcript = "Transcript" }
    @State private var tab: Tab = .summary
    @State private var copied: Tab?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Picker("", selection: $tab) {
                    ForEach(Tab.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented).labelsHidden().frame(maxWidth: 420)
                Spacer()
                actions
            }
            content
        }
    }

    @ViewBuilder private var actions: some View {
        HStack(spacing: 10) {
            switch tab {
            case .summary:
                if needsSummary {
                    Button { Task { await interview.generateSummary(transcript: transcript) } } label: {
                        Label("Generate summary", systemImage: "sparkles")
                    }
                    .disabled(interview.summarizing || interview.record?.threadId == nil)
                }
                copyButton(.summary, text: interview.summaryText)
            case .qa:
                copyButton(.qa, text: interview.record?.qaMarkdown() ?? "")
            case .briefing:
                copyButton(.briefing, text: interview.briefing)
            case .transcript:
                copyButton(.transcript, text: transcript)
            }
            if let folder = interview.folder {
                Button { NSWorkspace.shared.activateFileViewerSelecting([folder]) } label: {
                    Label("Reveal", systemImage: "folder")
                }
                .help("Reveal the interview folder in Finder")
            }
        }
        .labelStyle(.titleAndIcon)
    }

    private var needsSummary: Bool {
        guard let s = interview.record?.summary.status else { return false }
        return s == .failed || s == .pending || s == .skipped || (s == .done && interview.summaryText.isEmpty)
    }

    private func copyButton(_ which: Tab, text: String) -> some View {
        Button {
            let pb = NSPasteboard.general
            pb.clearContents(); pb.setString(text, forType: .string)
            copied = which
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { if copied == which { copied = nil } }
        } label: {
            Label(copied == which ? "Copied" : "Copy", systemImage: copied == which ? "checkmark" : "doc.on.doc")
        }
        .disabled(text.isEmpty)
    }

    @ViewBuilder private var content: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                switch tab {
                case .summary: summary
                case .qa: qa
                case .briefing:
                    if interview.briefing.isEmpty { placeholder("No briefing was saved.") }
                    else { MarkdownText(markdown: interview.briefing, fontSize: fontSize * 0.9) }
                case .transcript:
                    if transcript.isEmpty { placeholder("No transcript.") }
                    else {
                        Text(transcript).font(.system(size: fontSize * 0.9)).textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
            }
            .padding(.vertical, 4)
        }
    }

    @ViewBuilder private var summary: some View {
        if interview.summarizing && interview.summaryText.isEmpty {
            HStack(spacing: 8) { ProgressView().controlSize(.small); Text("Writing the summary…").foregroundStyle(.secondary) }
        }
        if let err = interview.summaryError {
            Label(err, systemImage: "exclamationmark.triangle").foregroundStyle(.orange).font(.callout)
        }
        if !interview.summaryText.isEmpty {
            MarkdownText(markdown: interview.summaryText, fontSize: fontSize * 0.9)
        } else if !interview.summarizing && interview.summaryError == nil {
            placeholder(interview.record?.summary.status == .skipped
                        ? "No summary — summarizing is off in Settings. Generate one now?"
                        : "No summary yet.")
        }
    }

    @ViewBuilder private var qa: some View {
        let turns = interview.record?.turns ?? []
        if turns.isEmpty { placeholder("No questions were asked.") }
        ForEach(turns) { t in
            VStack(alignment: .leading, spacing: 6) {
                HStack(alignment: .firstTextBaseline) {
                    if let ms = t.audioToMs {
                        Text(TimeFormat.clock(ms / 1000)).font(.caption).monospacedDigit().foregroundStyle(.secondary)
                    }
                    Text(t.question).font(.callout.weight(.medium)).lineLimit(3)
                    Spacer()
                    if t.status == .interrupted { Text("Interrupted").font(.caption).foregroundStyle(.orange) }
                    if t.status == .failed { Text("Failed").font(.caption).foregroundStyle(.red) }
                }
                if !t.images.isEmpty, let folder = interview.folder {
                    HStack {
                        ForEach(t.images, id: \.self) { rel in
                            if let img = NSImage(contentsOf: folder.appendingPathComponent(rel)) {
                                Image(nsImage: img).resizable().scaledToFit().frame(height: 60)
                            }
                        }
                    }
                }
                MarkdownText(markdown: t.answer.isEmpty ? "_(no answer)_" : t.answer, fontSize: fontSize * 0.85)
            }
            .padding(10)
            .background(Color.secondary.opacity(0.05), in: RoundedRectangle(cornerRadius: 8))
        }
    }

    private func placeholder(_ s: String) -> some View {
        Text(s).foregroundStyle(.secondary).frame(maxWidth: .infinity, alignment: .leading).padding(.vertical, 12)
    }
}
