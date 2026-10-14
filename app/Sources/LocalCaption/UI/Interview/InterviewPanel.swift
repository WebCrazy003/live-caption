import SwiftUI
import AppKit
import LocalCaptionKit

/// Interview mode (SPEC-13 §Interview panel): a header with the apply-instruction profiles, the
/// conversation — one card per turn, newest expanded — and the Ask bar (SPEC-14). The
/// preparation is a stage of its own before this panel appears (owner, 2026-10-02).
struct InterviewPanel: View {
    @ObservedObject var interview: InterviewController
    @EnvironmentObject var env: AppEnvironment
    @ObservedObject var hotkey = GlobalHotkey.shared
    @ObservedObject var screenshotHotkey = GlobalHotkey.screenshot
    let fontSize: Double

    @State private var expanded: Set<Int> = []
    @State private var draft = ""
    @State private var showingTypeBox = false
    private let clipboardPoll = Timer.publish(every: 0.5, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            header
            if let status = interview.status {
                Text(status).font(.caption).foregroundStyle(.orange).lineLimit(2)
            }
            cards
            Divider()
            if !interview.pendingImages.isEmpty { screenshotTray }
            bottomBar
        }
        .onReceive(clipboardPoll) { _ in interview.pollClipboard() }
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
            if !compact { Text("Interview").font(.callout.weight(.semibold)) }
            profileButtons(compact: compact)
            liveCodingButton(compact: compact)
            Spacer(minLength: 4)
            if interview.isStreaming {
                Button { Task { await interview.stopStreaming() } } label: { Label("Stop", systemImage: "stop.circle") }
                    .labelStyle(.iconOnly).help("Stop this answer")
            }
        }
    }

    @ViewBuilder private func profileButtons(compact: Bool) -> some View {
        let blocked = interview.blocker(.applyInstruction)
        if compact {
            Menu {
                ForEach(InterviewController.Profile.allCases) { p in
                    Button(p.label) { Task { interview.draft.profile = p; await interview.run(.applyInstruction, profile: p) } }
                }
            } label: { Text(interview.activeProfile?.label ?? "Profile") }
            .menuStyle(.borderlessButton).fixedSize()
            .disabled(blocked != nil).help(blocked ?? "Apply an answering profile (/apply-instruction)")
        } else {
            HStack(spacing: 2) {
                ForEach(InterviewController.Profile.allCases) { p in
                    let active = interview.activeProfile == p
                    Button(p.label) { Task { interview.draft.profile = p; await interview.run(.applyInstruction, profile: p) } }
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
        return Button { Task { interview.draft.liveCoding = true; await interview.run(.liveCoding) } } label: {
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
                LazyVStack(alignment: .leading, spacing: 6) {
                    if interview.turns.isEmpty {
                        Text("Press \(hotkeyLabel) (or Ask) when the interviewer "
                             + "finishes a question. You can also just start — the coach is ready either way.")
                            .font(.callout).foregroundStyle(.secondary).padding(.vertical, 12)
                    }
                    ForEach(interview.turns) { turn in
                        AnswerCard(turn: turn, isLatest: turn.n == interview.turns.last?.n,
                                   isExpanded: turn.n == interview.turns.last?.n || expanded.contains(turn.n),
                                   isStreaming: interview.streamingTurn == turn.n,
                                   fontSize: fontSize,
                                   toggle: { if expanded.contains(turn.n) { expanded.remove(turn.n) } else { expanded.insert(turn.n) } })
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

    private enum Density: Int, CaseIterable { case full, iconAsk, iconEverything }

    /// Ask and the type-to-coach box (with Send) on one row; narrower, Ask loses its label, then
    /// the box folds into a keyboard button.
    private var bottomBar: some View {
        ViewThatFits(in: .horizontal) {
            ForEach(Density.allCases, id: \.self) { barRow($0) }
        }
    }

    private func barRow(_ d: Density) -> some View {
        HStack(alignment: .bottom, spacing: 8) {
            askButton(iconOnly: d != .full)
            screenshotButton
            if d == .iconEverything {
                Spacer()
                Button { showingTypeBox.toggle() } label: { Image(systemName: "keyboard") }
                    .help("Type to the coach")
                    .popover(isPresented: $showingTypeBox) { typeField.frame(width: 320).padding() }
            } else {
                // A fixed ideal width: otherwise the field's ideal width is its whole text, so a
                // long message made ViewThatFits fall back to the popover layout and never wrap.
                let width: CGFloat = d == .full ? 260 : 200
                typeField.frame(minWidth: width, idealWidth: width, maxWidth: .infinity)
            }
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
                if !interview.pendingImages.isEmpty {
                    Label("\(interview.pendingImages.count)", systemImage: "photo").font(.caption)
                        .labelStyle(.titleAndIcon).foregroundStyle(.white)
                }
            }
        }
        .buttonStyle(.borderedProminent)
        .help(interview.pendingImages.isEmpty ? "Send the interviewer's latest words (\(hotkeyLabel))"
              : "Send the interviewer's latest words and \(interview.pendingImages.count) screenshot(s) (\(hotkeyLabel))")
        .accessibilityLabel("Ask")
    }

    /// Select an area of the screen; it joins this prompt (owner, 2026-10-02).
    private var screenshotButton: some View {
        Button { Task { await interview.takeScreenshot() } } label: {
            Image(systemName: "camera.viewfinder")
        }
        .disabled(interview.capturing)
        .help("Select an area of the screen to add to this prompt (\(screenshotHotkeyLabel)) — Esc cancels")
        .accessibilityLabel("Screenshot")
    }

    private var screenshotHotkeyLabel: String {
        if case .registered(let hk) = screenshotHotkey.state { return hk.description }
        return Hotkey.resolve(env.config.interview.screenshotHotkey, fallback: .defaultScreenshot).description
    }

    /// Screenshots waiting in the current prompt; the next Ask or Send takes them all.
    private var screenshotTray: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Label("\(interview.pendingImages.count) screenshot\(interview.pendingImages.count == 1 ? "" : "s") in this prompt",
                      systemImage: "photo.on.rectangle").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Clear") { interview.clearPending() }.buttonStyle(.link).font(.caption)
            }
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(interview.pendingImages) { item in
                        PendingThumbnail(item: item) { interview.removePending(item.id) }
                    }
                }
            }
        }
    }

    /// One line at first, growing as the text wraps — up to 6 lines, then it scrolls (owner,
    /// 2026-10-02). Return or ⌘Return sends; Option-Return adds a line break.
    private var typeField: some View {
        HStack(alignment: .bottom, spacing: 6) {
            TextField("Type to the coach…", text: $draft, axis: .vertical)
                .lineLimit(1...6)
                // `.roundedBorder` never wraps on macOS; the plain style does, so draw the border.
                .textFieldStyle(.plain)
                .padding(.horizontal, 7).padding(.vertical, 4)
                .background(RoundedRectangle(cornerRadius: 6).fill(Color(nsColor: .textBackgroundColor)))
                .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.secondary.opacity(0.35)))
                .frame(maxWidth: .infinity)
                .onSubmit(sendDraft)
                .help("Return sends · Option-Return adds a line")
            Button(action: sendDraft) { Image(systemName: "paperplane.fill") }
                .keyboardShortcut(.return, modifiers: .command)
                .help("Send" + (interview.pendingImages.isEmpty ? "" : " with \(interview.pendingImages.count) screenshot(s)"))
                .disabled(draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && interview.pendingImages.isEmpty)
        }
    }

    private func sendDraft() {
        guard !draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || !interview.pendingImages.isEmpty else { return }
        let text = draft
        draft = ""
        Task { await interview.sendTyped(text) }
    }

    private var hotkeyLabel: String {
        if case .registered(let hk) = hotkey.state { return hk.description }
        return Hotkey.resolve(env.config.interview.hotkey).description
    }
}

/// One turn: the answer (streaming) and, if it failed or was cut off, why. Shared with the
/// read-only history view.
struct AnswerCard: View {
    let turn: InterviewRecord.Turn
    let isLatest: Bool
    let isExpanded: Bool
    let isStreaming: Bool
    let fontSize: Double
    let toggle: () -> Void

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
        .padding(8)
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
        // No footer (answer time, copy, regenerate) — owner, 2026-10-14. Only a problem shows.
        statusBadge
    }

    @ViewBuilder private var statusBadge: some View {
        switch turn.status {
        case .interrupted: Label("Interrupted", systemImage: "pause.circle").font(.caption).foregroundStyle(.orange)
        case .failed: Label(turn.error ?? "Failed", systemImage: "exclamationmark.triangle").font(.caption)
                        .foregroundStyle(.red).lineLimit(2)
        case .streaming, .completed: EmptyView()
        }
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
}

/// Minimal view in Interview mode (owner, 2026-10-13/14): questions and answers only. The last
/// question is the live caption of what the interviewer is saying; Ask keeps it as the question,
/// with its answer under it. At the bottom: the coach box, the hotkey hints and any screenshots.
struct MinimalConversation: View {
    @ObservedObject var interview: InterviewController
    @EnvironmentObject var env: AppEnvironment
    @ObservedObject var hotkey = GlobalHotkey.shared
    @ObservedObject var screenshotHotkey = GlobalHotkey.screenshot
    /// What the interviewer has said since the last Ask.
    let liveQuestion: String
    let isLive: Bool
    let fontSize: Double
    @State private var draft = ""
    private let clipboardPoll = Timer.publish(every: 0.5, on: .main, in: .common).autoconnect()

    private var turns: [InterviewRecord.Turn] { interview.turns.filter { $0.kind != .skill } }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            conversation
            Divider()
            composer
        }
        .onReceive(clipboardPoll) { _ in interview.pollClipboard() }
    }

    private var conversation: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 12) {
                    ForEach(turns) { turn in
                        turnView(turn).id(turn.n)
                        Divider()
                    }
                    live.id("live")
                }
                .padding(.top, 22)   // clear of the hidden title bar, where the window is dragged
            }
            .onChange(of: interview.turns.count) { _, _ in withAnimation { proxy.scrollTo("live", anchor: .bottom) } }
            .onChange(of: interview.turns.last?.answer.count ?? 0) { _, _ in proxy.scrollTo("live", anchor: .bottom) }
            .onChange(of: liveQuestion) { _, _ in proxy.scrollTo("live", anchor: .bottom) }
        }
    }

    /// The question being asked right now, live from the captions.
    @ViewBuilder private var live: some View {
        if !liveQuestion.isEmpty {
            questionText(liveQuestion, live: true)
        } else if turns.isEmpty {
            Text(isLive ? "Listening… the interviewer's question appears here." : "Press Start to begin.")
                .font(.callout).foregroundStyle(.secondary)
        }
    }

    private func turnView(_ turn: InterviewRecord.Turn) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            questionText(Self.question(turn), live: false)
            let answer = Self.answer(turn)
            if answer.isEmpty && interview.streamingTurn == turn.n {
                ProgressView().controlSize(.small)
            } else if turn.status == .failed {
                Label(turn.error ?? "Failed", systemImage: "exclamationmark.triangle").foregroundStyle(.red)
            } else {
                MarkdownText(markdown: answer, fontSize: fontSize)
            }
        }
    }

    private func questionText(_ text: String, live: Bool) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Image(systemName: live ? "waveform" : "questionmark.circle.fill")
                .foregroundStyle(live ? Color.red : Color.accentColor)
                .symbolEffect(.variableColor.iterative, isActive: live && isLive)
            Text(text)
                .font(.system(size: fontSize * 0.9, weight: .semibold))
                .foregroundStyle(live ? .primary : .secondary)
                .textSelection(.enabled)
        }
    }

    /// What was asked: the interviewer's words, or what was typed.
    static func question(_ turn: InterviewRecord.Turn) -> String { turn.question }

    /// The answer without the model's `**Q:**` line — the question is already shown above it.
    static func answer(_ turn: InterviewRecord.Turn) -> String {
        let lines = turn.answer.split(separator: "\n", maxSplits: 1, omittingEmptySubsequences: false)
        guard let first = lines.first, first.hasPrefix("**Q:**") else { return turn.answer }
        return lines.count > 1 ? String(lines[1]).trimmingCharacters(in: .whitespacesAndNewlines) : ""
    }

    // MARK: Bottom: screenshots, the coach box, hints

    private var composer: some View {
        VStack(alignment: .leading, spacing: 4) {
            if !interview.pendingImages.isEmpty {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 6) {
                        ForEach(interview.pendingImages) { item in
                            PendingThumbnail(item: item) { interview.removePending(item.id) }
                        }
                    }
                }
            }
            HStack(alignment: .bottom, spacing: 6) {
                TextField("Type to the coach…", text: $draft, axis: .vertical)
                    .lineLimit(1...4)
                    .textFieldStyle(.plain)
                    .padding(.horizontal, 7).padding(.vertical, 4)
                    .background(RoundedRectangle(cornerRadius: 6).fill(Color(nsColor: .textBackgroundColor)))
                    .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.secondary.opacity(0.35)))
                    .onSubmit(send)
                Button { Task { await interview.takeScreenshot() } } label: { Image(systemName: "camera.viewfinder") }
                    .disabled(interview.capturing)
                    .help("Add a screenshot (\(label(screenshotHotkey, fallback: .defaultScreenshot)))")
                Button(action: send) { Image(systemName: "paperplane.fill") }
                    .disabled(draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && interview.pendingImages.isEmpty)
                    .help("Send")
            }
            Text("\(label(hotkey, fallback: nil)) answer the question · \(label(screenshotHotkey, fallback: .defaultScreenshot)) add a screenshot"
                 + (interview.pendingImages.isEmpty ? "" : " · \(interview.pendingImages.count) screenshot(s) go with the next answer"))
                .font(.caption2).foregroundStyle(.secondary)
        }
    }

    private func label(_ registration: GlobalHotkey, fallback: Hotkey?) -> String {
        if case .registered(let hk) = registration.state { return hk.description }
        if let fallback { return Hotkey.resolve(env.config.interview.screenshotHotkey, fallback: fallback).description }
        return Hotkey.resolve(env.config.interview.hotkey).description
    }

    private func send() {
        guard !draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || !interview.pendingImages.isEmpty else { return }
        let text = draft
        draft = ""
        Task { await interview.sendTyped(text) }
    }
}

/// One screenshot in the tray, with a remove button.
private struct PendingThumbnail: View {
    let item: InterviewController.PendingImage
    let remove: () -> Void
    @State private var image: NSImage?

    var body: some View {
        ZStack(alignment: .topTrailing) {
            Group {
                if let image { Image(nsImage: image).resizable().scaledToFill() } else { Color.secondary.opacity(0.2) }
            }
            .frame(width: 72, height: 48).clipShape(RoundedRectangle(cornerRadius: 4))
            .overlay(RoundedRectangle(cornerRadius: 4).stroke(.quaternary))
            Button(action: remove) {
                Image(systemName: "xmark.circle.fill").symbolRenderingMode(.palette)
                    .foregroundStyle(.white, .black.opacity(0.6))
            }
            .buttonStyle(.plain).padding(2).help("Remove this screenshot")
        }
        .onAppear { if image == nil { image = NSImage(data: item.png) } }
    }
}
