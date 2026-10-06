import SwiftUI
import AppKit
import LocalCaptionKit

/// The Active Session screen (SPEC-05): session header (name · status pill · elapsed),
/// transport controls (Start / Pause / Resume / Stop), font +/−, and the live caption area.
struct ActiveSessionView: View {
    @EnvironmentObject var env: AppEnvironment
    /// Owned by `AppEnvironment`, so switching to a past session in the sidebar and back keeps
    /// the live session and its interview (SPEC-13 §Skill steps).
    @ObservedObject private var controller: SessionController
    @ObservedObject private var interview: InterviewController
    @State private var showingIssues = false
    @State private var showingPrivacyNotice = false
    @State private var showingEndPrompt = false
    @State private var showingAccentNotice = false
    /// Interview mode layout (owner, 2026-10-02): captions can be hidden, and the border between
    /// captions and answers is draggable. Both are remembered.
    @AppStorage("interview.captionsHidden") private var captionsHidden = false
    @AppStorage("interview.captionShareWide") private var captionShareWide = Self.defaultCaptionShareWide
    @AppStorage("interview.answerShareStacked") private var answerShareStacked = Self.defaultAnswerShareStacked
    private static let defaultCaptionShareWide = 0.58
    private static let defaultAnswerShareStacked = 0.6

    init(env: AppEnvironment) {
        controller = env.session
        interview = env.interview
    }

    var body: some View {
        Group {
            if !env.modeChosen {
                ModeChooserView(lastMode: env.config.interview.mode, choose: chooseMode,
                                accent: env.config.accent.enabled, setAccent: setAccent)
            } else {
                VStack(alignment: .leading, spacing: 10) {
                    header
                    modelStatusArea
                    Divider()
                    captionArea
                    // Interview mode keeps the transport inside the captions panel, and the
                    // preparation stage has none (owner, 2026-10-02).
                    if !isInterviewMode || showsReplay { transportBar }
                }
            }
        }
        .padding()
        .navigationTitle(controller.displayName)
        .task { if controller.phase == .preparing { await controller.prepare() } }
        .sheet(isPresented: $showingPrivacyNotice) {
            InterviewPrivacyNotice(
                accept: {
                    env.config.interview.privacyAcknowledged = true
                    env.config.interview.mode = .interview
                    env.modeChosen = true
                    showingPrivacyNotice = false
                },
                cancel: { showingPrivacyNotice = false })
        }
        .sheet(isPresented: $showingAccentNotice) {
            AccentPrivacyNotice(
                accept: {
                    env.config.accent.noticeAccepted = true
                    showingAccentNotice = false
                    controller.switchSpeech(accent: true)
                },
                cancel: { showingAccentNotice = false })
        }
        .sheet(isPresented: $showingEndPrompt) {
            EndInterviewSheet(interview: interview, transcript: controller.committedText,
                              dismiss: { showingEndPrompt = false })
        }
        .onAppear {
            updateHotkey()
        }
        .onChange(of: env.config.interview.mode) { _, _ in updateHotkey() }
        .onChange(of: env.config.interview.hotkey) { _, _ in updateHotkey() }
        .onChange(of: env.config.interview.screenshotHotkey) { _, _ in updateHotkey() }
        .onChange(of: controller.phase) { _, _ in updateHotkey() }
        .onChange(of: env.modeChosen) { _, _ in updateHotkey() }
        .onChange(of: interview.showingPreparation) { _, _ in updateHotkey() }

    }

    /// Stop — "End interview" in Interview mode — saves the transcript exactly as before; then the
    /// interview links itself to the saved session and asks: summarize, or a follow-up prompt?
    /// (SPEC-15 §Ending the interview).
    private func stopSession() async {
        // Details may have been corrected during the interview: name the session as they are now.
        if isInterviewMode, let name = interview.sessionName(on: controller.startDate) { controller.sessionName = name }
        await controller.stop()
        if isInterviewMode, controller.phase == .saved, interview.record != nil {
            await interview.sessionSaved(sessionId: controller.savedSessionId, transcript: controller.committedText)
            showingEndPrompt = true
        }
    }

    /// In Interview mode the four skills must be loaded before Start (owner, 2026-10-02).
    private var skillsBlockStart: Bool { isInterviewMode && !interview.allSkillsLoaded }

    private var stopLabel: some View {
        isInterviewMode ? Label("End interview", systemImage: "flag.checkered") : Label("Stop", systemImage: "stop.fill")
    }

    /// The Ask and Screenshot hotkeys are live only on this screen, in Interview mode, until the
    /// session is saved (SPEC-14 §Global hotkey).
    private func updateHotkey() {
        let ask = GlobalHotkey.shared, shot = GlobalHotkey.screenshot
        guard env.modeChosen, isInterviewMode, !interview.showingPreparation, controller.phase != .saved else { ask.unregister(); shot.unregister(); return }
        ask.onPress = { [weak interview] in Task { await interview?.ask() } }
        shot.onPress = { [weak interview] in Task { await interview?.takeScreenshot() } }
        register(ask, Hotkey.resolve(env.config.interview.hotkey))
        register(shot, Hotkey.resolve(env.config.interview.screenshotHotkey, fallback: .defaultScreenshot))
    }

    private func register(_ hk: GlobalHotkey, _ wanted: Hotkey) {
        if case .registered(let current) = hk.state, current == wanted { return }
        hk.register(wanted)
    }

    // MARK: Mode (SPEC-13 §Mode picker)

    private var isInterviewMode: Bool { env.config.interview.mode == .interview }

    /// The mode is fixed once a session is live or has unsaved data.
    private var canChangeMode: Bool { !isLive && !controller.hasUnsavedSession && controller.phase != .saving }

    /// The first screen's choice. Interview asks for the privacy notice once.
    private func chooseMode(_ mode: Config.Interview.Mode) {
        if mode == .interview && !env.config.interview.privacyAcknowledged { showingPrivacyNotice = true; return }
        env.config.interview.mode = mode
        env.modeChosen = true
    }

    /// The Standard ↔ Accent switch. Accent shows the privacy notice once.
    private func setAccent(_ on: Bool) {
        guard on != env.config.accent.enabled, controller.canSwitchSpeech else { return }
        if on && !env.config.accent.noticeAccepted { showingAccentNotice = true; return }
        controller.switchSpeech(accent: on)
    }

    /// Back to the first screen; only while nothing is recording or unsaved.
    private var changeModeButton: some View {
        Button { env.modeChosen = false } label: {
            Label(isInterviewMode ? "Interview" : "Caption only", systemImage: "arrow.left.arrow.right")
        }
        .buttonStyle(.bordered).controlSize(.small)
        .help("Change mode — back to Caption only / Interview")
    }

    private func startSession() async {
        if interview.isFinished { interview.resetForNewInterview() }
        await controller.start(name: isInterviewMode ? interview.sessionName(on: Date()) : nil)
        if isInterviewMode, controller.phase == .recording {
            interview.recordingStarted(uuid: controller.sessionId, at: controller.startDate)
        }
    }

    // MARK: Header (name · status pill · elapsed)

    private var header: some View {
        HStack(spacing: 10) {
            if canChangeMode { changeModeButton }
            if controller.canSwitchSpeech {
                SpeechSwitch(accent: env.config.accent.enabled, set: setAccent, compact: true).frame(width: 170)
            } else if controller.isAccentSession {
                Label("Accent", systemImage: "waveform.badge.magnifyingglass").font(.caption).foregroundStyle(.secondary)
            }
            statusPill
            if isLive && controller.recordingSource != .off { recordingBadge }
            if controller.orchestrator.errorText != nil || controller.saveError != nil
                || controller.recordingIssue != nil {
                Button { showingIssues.toggle() } label: {
                    Label("Session issues", systemImage: "info.circle")
                        .font(.caption).foregroundStyle(.secondary)
                }
                .popover(isPresented: $showingIssues) {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("Session issues").font(.headline)
                        if let issue = controller.orchestrator.errorText { Text(issue) }
                        if let issue = controller.saveError { Text(issue) }
                        if let issue = controller.recordingIssue { Text(issue) }
                    }
                    .textSelection(.enabled).padding().frame(width: 360)
                }
            }
            if isInterviewMode { InterviewHeaderChips(interview: interview, codex: env.codex) }
            Spacer()
            if isLive {
                Text(controller.elapsed).font(.headline).monospacedDigit()
            }
            if showsInterviewLayout && !isLive && controller.phase != .saving {
                Button { withAnimation { interview.showingPreparation = true } } label: {
                    Label("Preparation", systemImage: "slider.horizontal.3")
                }
                .buttonStyle(.bordered).controlSize(.small)
                .help("Back to the preparation (interview details, CV, JD, mode)")
            }
            // With captions hidden there's no captions panel to hold Start / Stop: keep them here.
            if showsInterviewLayout && captionsHidden {
                HStack(spacing: 6) { transportControls }.labelStyle(.iconOnly).controlSize(.small)
            }
            if showsInterviewLayout {
                Button { withAnimation { captionsHidden.toggle() } } label: {
                    Label("Captions", systemImage: captionsHidden ? "eye.slash" : "eye")
                }
                .buttonStyle(.bordered).controlSize(.small)
                .help(captionsHidden ? "Show the caption panel" : "Hide the caption panel — answers get the whole width")
                .accessibilityLabel(captionsHidden ? "Show captions" : "Hide captions")
            }
            if isInterviewMode { fontControls }
            Label(controller.orchestrator.modelLabel, systemImage: "waveform")
                .font(.caption).foregroundStyle(.secondary)
                .help(env.config.accent.enabled ? "Speech models on your RTX desktop" : "Speech model in use (on-device)")
        }
    }

    private var isLive: Bool { controller.phase == .recording || controller.phase == .paused }

    // MARK: Caption area

    private var captionView: some View {
        CaptionView(
            paragraphs: controller.paragraphs,
            current: controller.current,
            // Accent mode: raw captions waiting for their correction show muted, like interim text.
            hypothesis: [controller.pendingRaw, controller.orchestrator.hypothesis]
                .filter { !$0.isEmpty }.joined(separator: " "),
            isReady: isLive,
            fontSize: Double(env.config.caption.fontSize),
            autoScroll: env.config.caption.autoScroll
        )
    }

    /// The live interview layout (captions + answers), as opposed to the finished-interview replay.
    private var showsInterviewLayout: Bool {
        isInterviewMode && !showsReplay && !interview.showingPreparation
    }

    private var showsReplay: Bool { isInterviewMode && controller.phase == .saved && interview.isFinished }

    /// Interview mode's first stage: the preparation alone (owner, 2026-10-02).
    private var showsPreparation: Bool { isInterviewMode && !showsReplay && interview.showingPreparation }

    @ViewBuilder private var captionArea: some View {
        if showsReplay {
            InterviewReplayView(interview: interview, interactive: true, transcript: controller.committedText,
                                fontSize: Double(env.config.caption.fontSize))
        } else if showsPreparation {
            preparationStage
        } else if isInterviewMode {
            interviewLayout
        } else {
            captionView
        }
    }

    /// The interview window often sits in a narrow strip beside the call (SPEC-15 §Responsive
    /// layout): side by side ≥ 820 pt, stacked below. Never tabs: the divider is draggable in both,
    /// and with captions hidden the answers fill the space.
    private var interviewLayout: some View {
        GeometryReader { geo in
            let w = geo.size.width
            Group {
                if captionsHidden {
                    interviewPanel
                } else if sideBySide(width: w) {
                    ResizableSplit(axis: .horizontal, fraction: $captionShareWide,
                                   defaultFraction: Self.defaultCaptionShareWide, minFirst: 220, minSecond: 300) {
                        captionColumn.padding(.trailing, 6)
                    } second: {
                        interviewPanel.padding(.leading, 6)
                    }
                } else {
                    // Narrow: stacked, still two panels with a draggable border — never tabs.
                    ResizableSplit(axis: .vertical, fraction: $answerShareStacked,
                                   defaultFraction: Self.defaultAnswerShareStacked, minFirst: 160, minSecond: 120) {
                        interviewPanel.padding(.bottom, 4)
                    } second: {
                        captionColumn.padding(.top, 4)
                    }
                }
            }
            .frame(width: geo.size.width, height: geo.size.height)
        }
    }

    /// Settings → Interview → Layout; Automatic goes side by side from 820 pt.
    private func sideBySide(width: CGFloat) -> Bool {
        switch env.config.interview.panelLayout {
        case .automatic: return width >= 820
        case .sideBySide: return true
        case .stacked: return false
        }
    }

    /// The preparation on its own; completing it opens the captions and answers.
    private var preparationStage: some View {
        VStack(spacing: 8) {
            ScrollView {
                InterviewSetupSection(interview: interview, codex: env.codex, library: env.library)
                    .frame(maxWidth: 760)
                    .padding(.horizontal, 4)
                    .frame(maxWidth: .infinity)
            }
            HStack {
                Spacer()
                Button(interview.isPrepared ? "Back to the interview" : "Skip preparation") {
                    withAnimation { interview.showingPreparation = false }
                }
                .buttonStyle(.link)
                .disabled(interview.preparing)
                .help(interview.isPrepared ? "Show the captions and answers"
                      : "Go to the captions and answers without preparing — the coach still works")
            }
        }
    }

    /// Interview mode: the captions with Start / Pause / Stop, Auto-copy and Copy last N under them.
    private var captionColumn: some View {
        VStack(spacing: 8) {
            captionView
            transportBar
        }
    }

    private var interviewPanel: some View {
        InterviewPanel(interview: interview, fontSize: Double(env.config.caption.fontSize))
    }

    @ViewBuilder private var statusPill: some View {
        switch controller.phase {
        case .recording:
            pill(color: .red, text: "Recording", filled: true)
        case .paused:
            pill(color: .orange, text: "Paused", filled: false)
        case .pausing:
            pill(color: .orange, text: "Finishing speech…", filled: false)
        case .saved:
            pill(color: .green, text: "Saved", filled: false)
        case .saving:
            pill(color: .secondary, text: "Saving…", filled: false)
        case .ready:
            pill(color: .secondary, text: "Ready", filled: false)
        case .preparing:
            HStack(spacing: 6) { ProgressView().controlSize(.small); Text(controller.orchestrator.status).font(.headline) }
        case .failed:
            pill(color: .red, text: "Error", filled: false)
        }
    }

    /// Shown beside the status pill while a session saves its audio to a file.
    private var recordingBadge: some View {
        let mic = controller.recordingSource == .microphone
        return Label(mic ? "Mic" : "Call audio", systemImage: mic ? "mic.fill" : "speaker.wave.2.fill")
            .font(.caption).foregroundStyle(.secondary)
            .help(mic ? "Saving your microphone as an audio file" : "Saving the call audio as an audio file")
            .accessibilityLabel(mic ? "Recording microphone audio" : "Recording call audio")
    }

    private func pill(color: Color, text: String, filled: Bool) -> some View {
        HStack(spacing: 6) {
            Circle().fill(filled ? color : .clear)
                .overlay(Circle().stroke(color, lineWidth: filled ? 0 : 1.5))
                .frame(width: 9, height: 9)
            Text(text).font(.headline)
        }
    }

    // MARK: Model download / error area

    @ViewBuilder private var modelStatusArea: some View {
        if controller.orchestrator.isDownloading {
            ProgressView(value: controller.orchestrator.downloadFraction).progressViewStyle(.linear)
        }
        if !controller.orchestrator.detail.isEmpty {
            Text(controller.orchestrator.detail).font(.caption).foregroundStyle(.secondary).monospacedDigit()
        }
        if !controller.hasUnsavedSession, let err = controller.orchestrator.errorText {
            ScrollView {
                Text(err).font(.callout).foregroundStyle(.red)
                    .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 120)
            if !controller.hasUnsavedSession && (controller.phase == .failed || controller.phase == .ready) {
                Button("Retry") { controller.retryPrepare() }
            }
        }
        if let issue = controller.correctionIssue {
            Label(issue, systemImage: "exclamationmark.triangle").foregroundStyle(.orange).font(.callout)
        }
        finalPassLine
        if !controller.hasUnsavedSession, let saveErr = controller.saveError {
            Label(saveErr, systemImage: "exclamationmark.triangle").foregroundStyle(.red).font(.callout)
        }
        if controller.phase == .saved, controller.savedTxtURL != nil || controller.savedAudioURL != nil {
            HStack(spacing: 8) {
                Label("Saved", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                if let url = controller.savedTxtURL {
                    Button("Reveal in Finder") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
                        .buttonStyle(.link)
                }
                if let audio = controller.savedAudioURL {
                    Button("Play Audio") { NSWorkspace.shared.open(audio) }
                        .buttonStyle(.link)
                        .help(audio.path)
                }
            }.font(.callout)
        }
    }

    /// Accent mode's final pass on the session just saved (SPEC-18 §Final pass).
    @ViewBuilder private var finalPassLine: some View {
        switch controller.finalPass {
        case .running?:
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                Text("Improving the transcript with \(env.config.accent.effectiveFinalModel)… you can start a new session meanwhile.")
            }.font(.callout).foregroundStyle(.secondary)
        case .done?:
            Label("Transcript improved — the saved files have the corrected text.", systemImage: "sparkles")
                .font(.callout).foregroundStyle(.green)
        case .failed(let message)?:
            HStack(spacing: 8) {
                Label("Couldn't improve the transcript: \(message)", systemImage: "exclamationmark.triangle")
                    .foregroundStyle(.orange)
                if let id = controller.savedSessionId {
                    Button("Retry") { controller.retryFinalPass(sessionId: id) }.buttonStyle(.link)
                }
            }.font(.callout)
        case nil:
            EmptyView()
        }
    }

    // MARK: Transport controls

    /// How much of the transport bar's text survives at the current window width.
    /// Each step gives up a little more, in order of how much the text is missed.
    private enum BarDensity: Int, CaseIterable {
        case full, autoCopyUnlabelled, iconActions, iconTransport
    }

    /// Picks the roomiest bar that fits. Controls that lose their text keep their
    /// tooltip and accessibility label.
    private var transportBar: some View {
        ViewThatFits(in: .horizontal) {
            ForEach(BarDensity.allCases, id: \.self) { transportRow($0) }
        }
    }

    private func transportRow(_ density: BarDensity) -> some View {
        HStack(spacing: 12) {
            Group { transportControls }
                .iconOnly(density.rawValue >= BarDensity.iconTransport.rawValue)

            Spacer()

            let iconActions = density.rawValue >= BarDensity.iconActions.rawValue
            let autoCopy = Toggle("Auto-copy", isOn: $env.config.clipboard.autoUpdate)
                .toggleStyle(.switch).controlSize(.small)
                .help("Automatically copy recent captions at speech endpoints and final updates")
            if density == .full { autoCopy } else { autoCopy.labelsHidden() }
            Button { controller.copyLastN() } label: {
                Label(controller.justCopied ? "Copied" : "Copy last \(env.config.clipboard.recentSentences)",
                      systemImage: controller.justCopied ? "checkmark" : "doc.on.doc")
            }
            .disabled(!controller.hasTranscript)
            .help("Copy the last \(env.config.clipboard.recentSentences) sentences")
            .iconOnly(iconActions)

            // Interview mode has the font size in the top bar instead.
            if !isInterviewMode {
                Divider().frame(height: 16)
                fontControls
            }
        }
    }

    /// Start / Pause / Resume / Stop (and the retry pair after a failed save).
    @ViewBuilder private var transportControls: some View {
        switch controller.phase {
        case .failed where controller.hasUnsavedSession:
            Button { Task { await controller.resume() } } label: {
                Label("Retry capture", systemImage: "play.fill")
            }
            .help("Retry capture")
            Button { Task { await stopSession() } } label: {
                Label("Retry save", systemImage: "square.and.arrow.down")
            }
            .help("Retry save")
        case .ready, .saved, .failed:
            Button { Task { await startSession() } } label: {
                Label("Start", systemImage: "record.circle")
            }
            .buttonStyle(.borderedProminent)
            .disabled(!controller.orchestrator.modelReady || skillsBlockStart)
            .help(skillsBlockStart ? "Load your 4 skills in Settings → Interview → Skills first" : "Start")
        case .recording:
            Button { Task { await controller.pause() } } label: { Label("Pause", systemImage: "pause.fill") }
                .help("Pause")
            Button(role: .destructive) { Task { await stopSession() } } label: { stopLabel }
                .keyboardShortcut(".", modifiers: .command)
                .help("Stop")
        case .paused:
            Button { Task { await controller.resume() } } label: { Label("Resume", systemImage: "play.fill") }
                .buttonStyle(.borderedProminent)
                .help("Resume")
            Button(role: .destructive) { Task { await stopSession() } } label: { stopLabel }
                .help("Stop")
        case .preparing, .pausing, .saving:
            EmptyView()
        }
    }

    /// Font size (live-applies via config).
    private var fontControls: some View {
        HStack(spacing: 4) {
            Button { adjustFont(-1) } label: { Image(systemName: "textformat.size.smaller") }
                .help("Smaller text")
            Text("\(env.config.caption.fontSize)").font(.caption).monospacedDigit().frame(width: 22)
            Button { adjustFont(1) } label: { Image(systemName: "textformat.size.larger") }
                .help("Larger text")
        }
    }

    private func adjustFont(_ delta: Int) {
        env.config.caption.fontSize = min(48, max(10, env.config.caption.fontSize + delta))
    }
}

private extension View {
    /// Drop the title of any `Label` inside, keeping the icon.
    @ViewBuilder func iconOnly(_ on: Bool) -> some View {
        if on { labelStyle(.iconOnly) } else { self }
    }
}

/// Interview header chips (SPEC-15 §Header): name + prep state always; usage and hotkey only
/// when there is something to warn about. Text drops away as the window narrows.
private struct InterviewHeaderChips: View {
    @ObservedObject var interview: InterviewController
    @ObservedObject var codex: CodexService
    @ObservedObject var hotkey = GlobalHotkey.shared
    @ObservedObject var screenshotHotkey = GlobalHotkey.screenshot

    var body: some View {
        ViewThatFits(in: .horizontal) {
            chips(compact: false)
            chips(compact: true)
        }
    }

    private func chips(compact: Bool) -> some View {
        HStack(spacing: 8) {
            if !compact, let name = interview.record?.name {
                Text(name).font(.callout.weight(.medium)).lineLimit(1)
            }
            prepChip(compact: compact)
            if codex.usageIsLow, let w = codex.usage?.lowest {
                chip(compact ? "\(w.remainingPercent)%" : "\(w.label): \(w.remainingPercent)% left",
                     icon: "gauge.with.dots.needle.67percent", color: .orange)
                    .help((codex.usage?.windows ?? []).map(\.line).joined(separator: "\n"))
            }
            if case .unavailable(let hk, let reason) = hotkey.state {
                chip(compact ? "" : "\(hk) unavailable", icon: "keyboard.badge.exclamationmark", color: .orange)
                    .help("Hotkey \(hk) unavailable: \(reason) — change it in Settings. The Ask button still works.")
            }
            if case .unavailable(let hk, let reason) = screenshotHotkey.state {
                chip(compact ? "" : "\(hk) unavailable", icon: "camera.badge.ellipsis", color: .orange)
                    .help("Screenshot hotkey \(hk) unavailable: \(reason) — change it in Settings. The screenshot button still works.")
            }
        }
    }

    @ViewBuilder private func prepChip(compact: Bool) -> some View {
        switch interview.threadState {
        case .none: chip(compact ? "" : "Coach not started", icon: "circle.dashed", color: .secondary)
        case .opening: chip(compact ? "" : "Starting coach…", icon: "hourglass", color: .secondary)
        case .open:
            if let p = interview.activeProfile {
                chip(compact ? p.label : "Coach ready · \(p.label)", icon: "checkmark.circle.fill", color: .green)
            } else {
                chip(compact ? "" : "Coach ready", icon: "checkmark.circle.fill", color: .green)
            }
        case .failed: chip(compact ? "" : "Coach unavailable", icon: "exclamationmark.triangle.fill", color: .orange)
        }
    }

    private func chip(_ text: String, icon: String, color: Color) -> some View {
        HStack(spacing: 4) {
            Image(systemName: icon)
            if !text.isEmpty { Text(text) }
        }
        .font(.caption).foregroundStyle(color)
        .padding(.horizontal, 6).padding(.vertical, 2)
        .background(color.opacity(0.1), in: Capsule())
    }
}
