import SwiftUI
import LocalCaptionKit

/// Launch-time prompt shown when leftover journals exist (SPEC.md §9.3): recover each
/// unsaved session to a transcript, or discard it.
struct RecoveryView: View {
    @EnvironmentObject var env: AppEnvironment

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Label("Recover unsaved sessions", systemImage: "arrow.uturn.backward.circle")
                .font(.title2)
            Text("These sessions didn't stop cleanly (the app quit or crashed mid-recording). "
                 + "Their captions were journaled to disk and can be saved as transcripts.")
                .font(.callout).foregroundStyle(.secondary)

            List(env.pendingRecoveries, id: \.url) { rec in
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(rec.startedAt.map { TimeFormat.human($0) } ?? "Unknown time")
                            .font(.body)
                        Text("\(rec.segments.count) segment\(rec.segments.count == 1 ? "" : "s")")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("Discard", role: .destructive) { env.discard(rec) }
                    Button("Recover & Save") { env.recover(rec) }
                        .buttonStyle(.borderedProminent)
                }
                .padding(.vertical, 2)
            }
            .frame(minHeight: 160)

            HStack {
                Spacer()
                Button("Discard All", role: .destructive) {
                    env.pendingRecoveries.forEach { env.discard($0) }
                }
            }
        }
        .padding(20)
        .frame(width: 520)
    }
}
