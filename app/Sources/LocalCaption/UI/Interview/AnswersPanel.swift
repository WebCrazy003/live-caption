import SwiftUI
import AppKit
import LocalCaptionKit

/// Interview mode, once prepared: one card per turn, newest at the bottom and expanded, with
/// the Ask button, quick prompts and a box to type to the coach (SPEC-14 §Answers panel).
struct AnswersPanel: View {
    @ObservedObject var interview: InterviewController
    @EnvironmentObject var env: AppEnvironment
    @ObservedObject var hotkey = GlobalHotkey.shared
    let fontSize: Double
    var onEditSetup: () -> Void = {}

    @State private var expanded: Set<Int> = []
    @State private var draft = ""
    @State private var showingBriefing = false
    @State private var showingTypeBox = false
    private let clipboardPoll = Timer.publish(every: 0.5, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            header
            if let status = interview.status {
                Text(status).font(.caption).foregroundStyle(.orange).lineLimit(2)
            }
            cards
            Divider()
            bottomBar
        }
        .onReceive(clipboardPoll) { _ in interview.refreshClipboardBadge() }
    }

    // MARK: Header

    private var header: some View {
        HStack(spacing: 8) {
            Text("Answers").font(.headline)
            Spacer()
            if interview.isStreaming {
                Button { Task { await interview.stopStreaming() } } label: { Label("Stop", systemImage: "stop.circle") }
                    .labelStyle(.iconOnly).help("Stop this answer")
            }
            Button { showingBriefing.toggle() } label: { Label("Briefing", systemImage: "doc.text.magnifyingglass") }
                .labelStyle(.iconOnly).help("Show the prep briefing")
                .popover(isPresented: $showingBriefing) {
                    ScrollView { MarkdownText(markdown: interview.briefing, fontSize: 13).padding() }
                        .frame(width: 420, height: 480)
                }
            Button(action: onEditSetup) { Label("Setup", systemImage: "slider.horizontal.3") }
                .labelStyle(.iconOnly).help("Interview setup")
        }
    }

    // MARK: Cards

    private var cards: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 10) {
                    if interview.turns.isEmpty {
                        Text("Press \(hotkeyLabel) (or Ask) when the interviewer finishes a question.")
                            .font(.callout).foregroundStyle(.secondary).padding(.vertical, 20)
                    }
                    ForEach(interview.turns) { turn in
                        AnswerCard(turn: turn, isLatest: turn.n == interview.turns.last?.n,
                                   isExpanded: turn.n == interview.turns.last?.n || expanded.contains(turn.n),
                                   isStreaming: interview.streamingTurn == turn.n,
                                   folder: interview.folder, fontSize: fontSize,
                                   toggle: { if expanded.contains(turn.n) { expanded.remove(turn.n) } else { expanded.insert(turn.n) } },
                                   regenerate: { Task { await interview.regenerate() } })
                            .id(turn.n)
                    }
                    Color.clear.frame(height: 1).id("bottom")
                }
            }
            .onChange(of: interview.turns.count) { _, _ in withAnimation { proxy.scrollTo("bottom") } }
            .onChange(of: interview.turns.last?.answer.count ?? 0) { _, _ in proxy.scrollTo("bottom") }
        }
    }

    // MARK: Bottom bar (shrinks to icons as the panel narrows — SPEC-15 §Buttons shrink to icons)

    private enum Density: Int, CaseIterable { case full, promptsInMenu, iconActions, iconEverything }

    private var bottomBar: some View {
        ViewThatFits(in: .horizontal) {
            ForEach(Density.allCases, id: \.self) { barRow($0) }
        }
    }

    private func barRow(_ d: Density) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                askButton(iconOnly: d.rawValue >= Density.iconActions.rawValue)
                if d == .full {
                    ForEach(env.config.interview.quickPrompts, id: \.self) { qp in
                        Button(qp.label) { Task { await interview.sendQuick(qp) } }
                            .help(qp.text).disabled(!interview.prepState.isReady)
                    }
                } else {
                    Menu {
                        ForEach(env.config.interview.quickPrompts, id: \.self) { qp in
                            Button(qp.label) { Task { await interview.sendQuick(qp) } }
                        }
                    } label: { Image(systemName: "ellipsis.bubble") }
                    .menuStyle(.borderlessButton).fixedSize().help("Quick prompts")
                    .disabled(!interview.prepState.isReady)
                }
                if d == .iconEverything {
                    Button { showingTypeBox.toggle() } label: { Image(systemName: "keyboard") }
                        .help("Type to the coach")
                        .popover(isPresented: $showingTypeBox) { typeField.frame(width: 320).padding() }
                }
            }
            if d != .iconEverything { typeField }
        }
    }

    private func askButton(iconOnly: Bool) -> some View {
        Button { Task { await interview.ask() } } label: {
            HStack(spacing: 4) {
                Image(systemName: "questionmark.bubble.fill")
                if !iconOnly {
                    Text("Ask")
                    Text(hotkeyLabel).font(.caption).foregroundStyle(.secondary)
                }
                if interview.clipboardImageCount > 0 {
                    Label("\(interview.clipboardImageCount)", systemImage: "photo").font(.caption)
                        .labelStyle(.titleAndIcon).foregroundStyle(.blue)
                }
            }
        }
        .buttonStyle(.borderedProminent)
        .disabled(!interview.prepState.isReady)
        .help(interview.prepState.isReady ? "Send the interviewer's latest words (\(hotkeyLabel))" : "Preparing…")
        .accessibilityLabel("Ask")
    }

    private var typeField: some View {
        TextField("Type to the coach…", text: $draft, axis: .vertical)
            .lineLimit(1...4)
            .textFieldStyle(.roundedBorder)
            .disabled(!interview.prepState.isReady)
            .onSubmit {
                let text = draft
                draft = ""
                Task { await interview.sendTyped(text) }
            }
    }

    private var hotkeyLabel: String {
        if case .registered(let hk) = hotkey.state { return hk.description }
        return Hotkey.resolve(env.config.interview.hotkey).description
    }
}

/// One turn: the answer (streaming), its state, and Copy / Regenerate / Sent text.
private struct AnswerCard: View {
    let turn: InterviewRecord.Turn
    let isLatest: Bool
    let isExpanded: Bool
    let isStreaming: Bool
    let folder: URL?
    let fontSize: Double
    let toggle: () -> Void
    let regenerate: () -> Void
    @State private var showingSent = false
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if isExpanded {
                expandedBody
            } else {
                Button(action: toggle) {
                    HStack {
                        Image(systemName: "chevron.right").font(.caption)
                        Text(summaryLine).lineLimit(1).foregroundStyle(.secondary)
                        Spacer()
                        statusBadge
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(10)
        .background(isLatest ? Color.accentColor.opacity(0.06) : Color.secondary.opacity(0.05),
                    in: RoundedRectangle(cornerRadius: 8))
    }

    @ViewBuilder private var expandedBody: some View {
        if !isLatest {
            Button(action: toggle) {
                HStack { Image(systemName: "chevron.down").font(.caption); Spacer() }.contentShape(Rectangle())
            }
            .buttonStyle(.plain)
        }
        if turn.kind != .ask {
            Text(kindLabel + turn.question).font(.caption).foregroundStyle(.secondary).lineLimit(2)
        }
        if turn.answer.isEmpty && isStreaming {
            HStack(spacing: 6) { ProgressView().controlSize(.small); Text("Thinking…").foregroundStyle(.secondary) }
        } else {
            MarkdownText(markdown: turn.answer, fontSize: fontSize)
        }
        HStack(spacing: 10) {
            statusBadge
            Spacer()
            if let ttft = turn.ttftMs {
                Text(String(format: "%.1f s", Double(ttft) / 1000)).font(.caption2).foregroundStyle(.tertiary)
                    .help("Time to first words")
            }
            Button { showingSent.toggle() } label: { Image(systemName: "text.quote") }
                .buttonStyle(.borderless).help("What was sent")
                .popover(isPresented: $showingSent) { sentPopover }
            if isLatest && !isStreaming {
                Button(action: regenerate) { Image(systemName: "arrow.clockwise") }
                    .buttonStyle(.borderless).help("Give me a different answer")
            }
            Button { copy() } label: { Image(systemName: copied ? "checkmark" : "doc.on.doc") }
                .buttonStyle(.borderless).help("Copy answer").disabled(turn.answer.isEmpty)
        }
    }

    @ViewBuilder private var statusBadge: some View {
        switch turn.status {
        case .interrupted: Label("Interrupted", systemImage: "pause.circle").font(.caption).foregroundStyle(.orange)
        case .failed: Label(turn.error ?? "Failed", systemImage: "exclamationmark.triangle").font(.caption)
                        .foregroundStyle(.red).lineLimit(2)
        case .streaming, .completed: EmptyView()
        }
    }

    private var sentPopover: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 8) {
                Text("Sent").font(.headline)
                Text(turn.question).textSelection(.enabled).font(.callout)
                ForEach(turn.images, id: \.self) { rel in
                    if let folder, let img = NSImage(contentsOf: folder.appendingPathComponent(rel)) {
                        Image(nsImage: img).resizable().scaledToFit().frame(maxHeight: 160)
                    }
                }
            }
            .padding()
        }
        .frame(width: 360, height: 300)
    }

    private var kindLabel: String {
        switch turn.kind {
        case .ask: return ""
        case .typed: return "You: "
        case .quick: return "Quick: "
        case .regenerate: return ""
        }
    }

    /// The model's `**Q:**` line, else what was sent.
    private var summaryLine: String {
        if let first = turn.answer.split(separator: "\n").first, first.hasPrefix("**Q:**") {
            return first.replacingOccurrences(of: "**Q:**", with: "Q:").trimmingCharacters(in: .whitespaces)
        }
        return turn.question
    }

    private func copy() {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(turn.answer, forType: .string)
        copied = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { copied = false }
    }
}
