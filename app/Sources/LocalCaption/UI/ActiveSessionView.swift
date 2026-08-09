import SwiftUI
import AppKit
import LocalCaptionKit

/// The Active Session screen (SPEC-05): session header (name · status pill · elapsed),
/// transport controls (Start / Pause / Resume / Stop), font +/−, and the live caption area.
struct ActiveSessionView: View {
    @EnvironmentObject var env: AppEnvironment
    @StateObject private var controller: SessionController

    init(env: AppEnvironment) {
        _controller = StateObject(wrappedValue: SessionController(env: env))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            header
            modelStatusArea
            Divider()
            captionArea
            transportBar
        }
        .padding()
        .navigationTitle(controller.displayName)
        .task { if controller.phase == .preparing { await controller.prepare() } }
    }

    // MARK: Header (name · status pill · elapsed)

    private var header: some View {
        HStack(spacing: 10) {
            statusPill
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
        if env.config.summary.enabled {
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

    @ViewBuilder private var statusPill: some View {
        switch controller.phase {
        case .recording:
            pill(color: .red, text: "Recording", filled: true)
        case .paused:
            pill(color: .orange, text: "Paused", filled: false)
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
        if let err = controller.orchestrator.errorText {
            ScrollView {
                Text(err).font(.callout).foregroundStyle(.red)
                    .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 120)
            Button("Retry") { controller.retryPrepare() }
        }
        if let saveErr = controller.saveError {
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

    private var transportBar: some View {
        HStack(spacing: 12) {
            switch controller.phase {
            case .ready, .saved, .failed:
                Button { Task { await controller.start() } } label: {
                    Label("Start", systemImage: "record.circle")
                }
                .buttonStyle(.borderedProminent)
                .disabled(!controller.orchestrator.modelReady)
            case .recording:
                Button { Task { await controller.pause() } } label: { Label("Pause", systemImage: "pause.fill") }
                Button(role: .destructive) { Task { await controller.stop() } } label: { Label("Stop", systemImage: "stop.fill") }
                    .keyboardShortcut(".", modifiers: .command)
            case .paused:
                Button { Task { await controller.resume() } } label: { Label("Resume", systemImage: "play.fill") }
                    .buttonStyle(.borderedProminent)
                Button(role: .destructive) { Task { await controller.stop() } } label: { Label("Stop", systemImage: "stop.fill") }
            case .preparing, .saving:
                EmptyView()
            }

            Spacer()

            if env.config.clipboard.autoUpdate {
                Label("Auto-copy", systemImage: "doc.on.clipboard")
                    .font(.caption).foregroundStyle(.secondary)
                    .help("Each final sentence copies the last \(env.config.clipboard.recentSentences) to the clipboard")
            }
            Button { controller.copyLastN() } label: {
                Label(controller.justCopied ? "Copied" : "Copy last \(env.config.clipboard.recentSentences)",
                      systemImage: controller.justCopied ? "checkmark" : "doc.on.doc")
            }
            .disabled(!controller.hasTranscript)

            Divider().frame(height: 16)

            // Live AI summary panel toggle (SPEC-10). Off = captions get full width, no LLM runs.
            Button { env.config.summary.enabled.toggle() } label: {
                Label("Key points", systemImage: "sparkles")
                    .foregroundStyle(env.config.summary.enabled ? Color.accentColor : Color.secondary)
            }
            .help("Show the live AI summary of what the other person wants")

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
