import SwiftUI
import LocalCaptionKit

/// The Active Session screen: hosts the streaming orchestrator and renders live captions
/// (SPEC-05). Phase 1 preserves the minimal app's captioning end-to-end; the session
/// header (elapsed clock, status pill) and Pause/Resume/Stop controls are Phase 2.
struct ActiveSessionView: View {
    @EnvironmentObject var env: AppEnvironment
    @StateObject private var orchestrator = StreamingOrchestrator()

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            statusHeader

            if orchestrator.isDownloading {
                ProgressView(value: orchestrator.downloadFraction).progressViewStyle(.linear)
            }
            if !orchestrator.detail.isEmpty {
                Text(orchestrator.detail).font(.caption).foregroundStyle(.secondary).monospacedDigit()
            }
            if let err = orchestrator.errorText {
                ScrollView {
                    Text(err).font(.callout).foregroundStyle(.red)
                        .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 140)
                Button("Retry") { orchestrator.retry() }
            }

            Divider()

            CaptionView(
                paragraphs: orchestrator.paragraphs,
                current: orchestrator.current,
                hypothesis: orchestrator.hypothesis,
                isReady: orchestrator.isReady,
                fontSize: Double(env.config.caption.fontSize),
                autoScroll: env.config.caption.autoScroll
            )
        }
        .padding()
        .navigationTitle("New Session")
        .task { await orchestrator.start() }
    }

    private var statusHeader: some View {
        HStack(spacing: 8) {
            if !orchestrator.isReady && orchestrator.errorText == nil {
                ProgressView().controlSize(.small)
            } else if orchestrator.isReady {
                Circle().fill(.red).frame(width: 9, height: 9)
            }
            Text(orchestrator.status)
                .font(.headline)
                .foregroundStyle(orchestrator.errorText == nil ? Color.primary : Color.red)
            Spacer()
            Label(orchestrator.modelLabel, systemImage: "waveform")
                .font(.caption).foregroundStyle(.secondary)
                .help("Speech model in use (on-device)")
        }
    }
}
