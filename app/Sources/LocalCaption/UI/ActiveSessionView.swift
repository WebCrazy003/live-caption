import SwiftUI
import AppKit
import LocalCaptionKit

/// The Active Session screen (SPEC-05): session header (name · status pill · elapsed),
/// transport controls (Start / Pause / Resume / Stop), font +/−, and the live caption area.
struct ActiveSessionView: View {
    @EnvironmentObject var env: AppEnvironment
    @StateObject private var controller: SessionController
    @StateObject private var interview: InterviewController
    @State private var showingIssues = false
    @State private var showingPrivacyNotice = false
    @State private var showingSetup = false

    init(env: AppEnvironment) {
        _controller = StateObject(wrappedValue: SessionController(env: env))
        _interview = StateObject(wrappedValue: InterviewController(env: env))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            header
            modelStatusArea
            if canChangeMode { modePicker }
            Divider()
            captionArea
            transportBar
        }
        .padding()
        .navigationTitle(controller.displayName)
        .task { if controller.phase == .preparing { await controller.prepare() } }
        .sheet(isPresented: $showingPrivacyNotice) {
            InterviewPrivacyNotice(
                accept: {
                    env.config.interview.privacyAcknowledged = true
                    env.config.interview.mode = .interview
                    showingPrivacyNotice = false
                },
                cancel: { showingPrivacyNotice = false })
        }
        .onAppear {
            interview.transcriptSource = { [weak controller] in
                controller?.askSnapshot ?? (segments: [], interim: "", audioMs: 0)
            }
            updateHotkey()
        }
        .onChange(of: env.config.interview.mode) { _, _ in updateHotkey() }
        .onChange(of: env.config.interview.hotkey) { _, _ in updateHotkey() }
        .onChange(of: controller.phase) { _, _ in updateHotkey() }
        .onDisappear {
            GlobalHotkey.shared.unregister()
            Task { await interview.discardUnstarted() }
        }
    }

    /// The Ask hotkey is live only on this screen, in Interview mode, until the session is
    /// saved (SPEC-14 §Global hotkey).
    private func updateHotkey() {
        let hk = GlobalHotkey.shared
        guard isInterviewMode, controller.phase != .saved else { hk.unregister(); return }
        hk.onPress = { [weak interview] in Task { await interview?.ask() } }
        let wanted = Hotkey.resolve(env.config.interview.hotkey)
        if case .registered(let current) = hk.state, current == wanted { return }
        hk.register(wanted)
    }

    // MARK: Mode (SPEC-13 §Mode picker)

    private var isInterviewMode: Bool { env.config.interview.mode == .interview }

    /// The mode is fixed once a session is live or has unsaved data.
    private var canChangeMode: Bool { !isLive && !controller.hasUnsavedSession && controller.phase != .saving }

    private var modePicker: some View {
        Picker("Mode", selection: Binding(
            get: { env.config.interview.mode },
            set: { mode in
                if mode == .interview && !env.config.interview.privacyAcknowledged { showingPrivacyNotice = true }
                else { env.config.interview.mode = mode }
            })) {
            Label("Caption only", systemImage: "captions.bubble").tag(Config.Interview.Mode.caption)
            Label("Interview", systemImage: "person.2.wave.2").tag(Config.Interview.Mode.interview)
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .frame(maxWidth: 320)
    }

    private func startSession() async {
        await controller.start()
        if isInterviewMode, controller.phase == .recording {
            interview.recordingStarted(uuid: controller.sessionId, at: controller.startDate)
        }
    }

    // MARK: Header (name · status pill · elapsed)

    private var header: some View {
        HStack(spacing: 10) {
            statusPill
            if controller.orchestrator.errorText != nil || controller.saveError != nil {
                Button { showingIssues.toggle() } label: {
                    Label("Session issues", systemImage: "info.circle")
                        .font(.caption).foregroundStyle(.secondary)
                }
                .popover(isPresented: $showingIssues) {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("Session issues").font(.headline)
                        if let issue = controller.orchestrator.errorText { Text(issue) }
                        if let issue = controller.saveError { Text(issue) }
                    }
                    .textSelection(.enabled).padding().frame(width: 360)
                }
            }
            Spacer()
            if isLive {
                Text(controller.elapsed).font(.headline).monospacedDigit()
            }
            Label(controller.orchestrator.modelLabel, systemImage: "waveform")
                .font(.caption).foregroundStyle(.secondary)
                .help("Speech model in use (on-device)")
        }
    }

    private var isLive: Bool { controller.phase == .recording || controller.phase == .paused }

    // MARK: Caption area (captions left, live AI summary right — SPEC-10)

    private var captionView: some View {
        CaptionView(
            paragraphs: controller.paragraphs,
            current: controller.current,
            hypothesis: controller.orchestrator.hypothesis,
            isReady: isLive,
            fontSize: Double(env.config.caption.fontSize),
            autoScroll: env.config.caption.autoScroll
        )
    }

    @ViewBuilder private var captionArea: some View {
        if isInterviewMode {
            HStack(alignment: .top, spacing: 12) {
                captionView.frame(maxWidth: .infinity)
                Divider()
                interviewPanel.frame(minWidth: 300, idealWidth: 400, maxWidth: 520)
            }
        } else if env.config.summary.enabled {
            HStack(alignment: .top, spacing: 12) {
                captionView.frame(maxWidth: .infinity)
                Divider()
                SummaryView(
                    cards: controller.summaries,
                    summarizing: controller.summarizing,
                    unavailable: controller.summaryUnavailable,
                    fontSize: Double(env.config.caption.fontSize),
                    isLive: isLive
                )
                .frame(minWidth: 240, idealWidth: 320, maxWidth: 420)
            }
        } else {
            captionView
        }
    }

    /// Prepare until ready; then Answers (with Key points below when enabled).
    @ViewBuilder private var interviewPanel: some View {
        if interview.prepState.isReady && !showingSetup {
            VStack(spacing: 8) {
                AnswersPanel(interview: interview, fontSize: Double(env.config.caption.fontSize),
                             onEditSetup: { showingSetup = true })
                    .layoutPriority(2)
                if env.config.interview.showKeyPoints && env.config.summary.enabled {
                    Divider()
                    SummaryView(cards: controller.summaries, summarizing: controller.summarizing,
                                unavailable: controller.summaryUnavailable,
                                fontSize: Double(env.config.caption.fontSize), isLive: isLive)
                        .frame(maxHeight: 220)
                }
            }
        } else {
            VStack(alignment: .leading, spacing: 6) {
                if interview.prepState.isReady {
                    Button { showingSetup = false } label: { Label("Back to answers", systemImage: "chevron.left") }
                        .buttonStyle(.link)
                }
                PreparePanel(interview: interview, codex: env.codex, library: env.library,
                             fontSize: Double(env.config.caption.fontSize))
            }
        }
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
        if !controller.hasUnsavedSession, let saveErr = controller.saveError {
            Label(saveErr, systemImage: "exclamationmark.triangle").foregroundStyle(.red).font(.callout)
        }
        if controller.phase == .saved, let url = controller.savedTxtURL {
            HStack(spacing: 8) {
                Label("Saved", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                Button("Reveal in Finder") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
                    .buttonStyle(.link)
            }.font(.callout)
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
            Group {
                switch controller.phase {
                case .failed where controller.hasUnsavedSession:
                    Button { Task { await controller.resume() } } label: {
                        Label("Retry capture", systemImage: "play.fill")
                    }
                    .help("Retry capture")
                    Button { Task { await controller.stop() } } label: {
                        Label("Retry save", systemImage: "square.and.arrow.down")
                    }
                    .help("Retry save")
                case .ready, .saved, .failed:
                    Button { Task { await startSession() } } label: {
                        Label("Start", systemImage: "record.circle")
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(!controller.orchestrator.modelReady)
                    .help("Start")
                case .recording:
                    Button { Task { await controller.pause() } } label: { Label("Pause", systemImage: "pause.fill") }
                        .help("Pause")
                    Button(role: .destructive) { Task { await controller.stop() } } label: { Label("Stop", systemImage: "stop.fill") }
                        .keyboardShortcut(".", modifiers: .command)
                        .help("Stop")
                case .paused:
                    Button { Task { await controller.resume() } } label: { Label("Resume", systemImage: "play.fill") }
                        .buttonStyle(.borderedProminent)
                        .help("Resume")
                    Button(role: .destructive) { Task { await controller.stop() } } label: { Label("Stop", systemImage: "stop.fill") }
                        .help("Stop")
                case .preparing, .pausing, .saving:
                    EmptyView()
                }
            }
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

            Divider().frame(height: 16)

            // Live AI summary panel toggle (SPEC-10). Off = captions get full width, no LLM runs.
            Button { env.config.summary.enabled.toggle() } label: {
                Label("Key points", systemImage: "sparkles")
                    .foregroundStyle(env.config.summary.enabled ? Color.accentColor : Color.secondary)
            }
            .help("Show the live AI summary of what the other person wants")
            .iconOnly(iconActions)

            Divider().frame(height: 16)

            // Font size (live-applies via config).
            HStack(spacing: 4) {
                Button { adjustFont(-1) } label: { Image(systemName: "textformat.size.smaller") }
                Text("\(env.config.caption.fontSize)").font(.caption).monospacedDigit().frame(width: 22)
                Button { adjustFont(1) } label: { Image(systemName: "textformat.size.larger") }
            }
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
