import SwiftUI
import AppKit
import LocalCaptionKit

/// Interview mode (SPEC-13 §Interview panel): a header with the apply-instruction profiles, the
/// setup (CV, pasted JD, skill steps), the conversation — one card per turn, newest expanded —
/// and the Ask bar (SPEC-14).
struct InterviewPanel: View {
    @ObservedObject var interview: InterviewController
    @EnvironmentObject var env: AppEnvironment
    @ObservedObject var hotkey = GlobalHotkey.shared
    let fontSize: Double
    /// Recording or paused: the setup folds away once the interview is live.
    let isLive: Bool

    @State private var expanded: Set<Int> = []
    @State private var draft = ""
    @State private var setupExpanded = true
    @State private var showingTypeBox = false
    private let clipboardPoll = Timer.publish(every: 0.5, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            header
            if let status = interview.status {
                Text(status).font(.caption).foregroundStyle(.orange).lineLimit(2)
            }
            if setupExpanded {
                ScrollView {
                    InterviewSetupSection(interview: interview, codex: env.codex, library: env.library)
                        .padding(.trailing, 4)
                }
                .frame(maxHeight: interview.turns.isEmpty ? .infinity : 340)
                Divider()
            }
            cards
            Divider()
            bottomBar
        }
        .onReceive(clipboardPoll) { _ in interview.refreshClipboardBadge() }
        .onAppear { setupExpanded = !isLive }
        .onChange(of: isLive) { _, live in if live { setupExpanded = false } }
    }

    // MARK: Header (profiles stay reachable during the interview)

    private var header: some View {
        ViewThatFits(in: .horizontal) {
            headerRow(compact: false)
            headerRow(compact: true)
        }
    }

    private func headerRow(compact: Bool) -> some View {
        HStack(spacing: 6) {
            if !compact { Text("Interview").font(.headline) }
            profileButtons(compact: compact)
            liveCodingButton(compact: compact)
            Spacer(minLength: 4)
            if interview.isStreaming {
                Button { Task { await interview.stopStreaming() } } label: { Label("Stop", systemImage: "stop.circle") }
                    .labelStyle(.iconOnly).help("Stop this answer")
            }
            Button { withAnimation { setupExpanded.toggle() } } label: {
                Label("Setup", systemImage: setupExpanded ? "chevron.up.circle" : "slider.horizontal.3")
            }
            .labelStyle(.iconOnly).help(setupExpanded ? "Hide the setup" : "Show the setup (CV, JD, skill steps)")
        }
    }

    @ViewBuilder private func profileButtons(compact: Bool) -> some View {
        let blocked = interview.blocker(.applyInstruction)
        if compact {
            Menu {
                ForEach(InterviewController.Profile.allCases) { p in
                    Button(p.label) { Task { await interview.run(.applyInstruction, profile: p) } }
                }
            } label: { Text(interview.activeProfile?.label ?? "Profile") }
            .menuStyle(.borderlessButton).fixedSize()
            .disabled(blocked != nil).help(blocked ?? "Apply an answering profile (/apply-instruction)")
        } else {
            HStack(spacing: 2) {
                ForEach(InterviewController.Profile.allCases) { p in
                    let active = interview.activeProfile == p
                    Button(p.label) { Task { await interview.run(.applyInstruction, profile: p) } }
                        .buttonStyle(.bordered)
                        .tint(active ? .accentColor : nil)
                        .fontWeight(active ? .semibold : .regular)
                        .help(blocked ?? "/apply-instruction \(p.rawValue)" + (active ? " (active)" : ""))
                }
            }
            .controlSize(.small)
            .disabled(blocked != nil)
        }
    }

    private func liveCodingButton(compact: Bool) -> some View {
        let blocked = interview.blocker(.liveCoding)
        let active = interview.liveCodingActive
        let icon = active ? "chevron.left.forwardslash.chevron.right" : "curlybraces"
        return Button { Task { await interview.run(.liveCoding) } } label: {
            if compact { Image(systemName: icon) } else { Label("Live coding", systemImage: icon) }
        }
        .buttonStyle(.bordered).controlSize(.small)
        .tint(active ? .accentColor : nil)
        .disabled(blocked != nil)
        .help(blocked ?? (active ? "Live coding & design is active" : "Apply /live-coding-design (optional)"))
    }

    // MARK: Cards

    private var cards: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 10) {
                    if interview.turns.isEmpty {
                        Text("Run the steps in the setup, then press \(hotkeyLabel) (or Ask) when the interviewer "
                             + "finishes a question. You can also just start — the coach is ready either way.")
                            .font(.callout).foregroundStyle(.secondary).padding(.vertical, 12)
                    }
                    ForEach(interview.turns) { turn in
                        AnswerCard(turn: turn, isLatest: turn.n == interview.turns.last?.n,
                                   isExpanded: turn.n == interview.turns.last?.n || expanded.contains(turn.n),
                                   isStreaming: interview.streamingTurn == turn.n,
                                   folder: interview.folder, fontSize: fontSize,
                                   toggle: { if expanded.contains(turn.n) { expanded.remove(turn.n) } else { expanded.insert(turn.n) } },
                                   regenerate: turn.kind == .skill ? nil : { Task { await interview.regenerate() } })
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
                            .help(qp.text)
                    }
                } else {
                    Menu {
                        ForEach(env.config.interview.quickPrompts, id: \.self) { qp in
                            Button(qp.label) { Task { await interview.sendQuick(qp) } }
                        }
                    } label: { Image(systemName: "ellipsis.bubble") }
                    .menuStyle(.borderlessButton).fixedSize().help("Quick prompts")
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
        .help("Send the interviewer's latest words (\(hotkeyLabel))")
        .accessibilityLabel("Ask")
    }

    private var typeField: some View {
        TextField("Type to the coach…", text: $draft, axis: .vertical)
            .lineLimit(1...4)
            .textFieldStyle(.roundedBorder)
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

/// One turn: the answer (streaming), its state, and Copy / Regenerate / Sent text. Shared with
/// the read-only history view, where `regenerate` is nil.
struct AnswerCard: View {
    let turn: InterviewRecord.Turn
    let isLatest: Bool
    let isExpanded: Bool
    let isStreaming: Bool
    let folder: URL?
    let fontSize: Double
    let toggle: () -> Void
    let regenerate: (() -> Void)?
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
            if isLatest && !isStreaming, let regenerate {
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
        case .skill: return "Skill: "
        }
    }

    /// The model's `**Q:**` line, else what was sent.
    private var summaryLine: String {
        if turn.kind == .skill { return "Skill: \(turn.question)" }
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
