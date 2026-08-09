import SwiftUI
import LocalCaptionKit

/// The right-hand "Key points" panel (SPEC-10): a scrolling list of summary cards, one per
/// ~100-word block, newest at the bottom (autoscrolled). Each card is short, easy-English
/// "what the other person wants". Display-only — never saved to the transcript.
struct SummaryView: View {
    let cards: [SummaryCard]
    let summarizing: Bool
    let unavailable: Bool
    let fontSize: Double
    let isLive: Bool

    private var bulletFont: Font { .system(size: max(11, fontSize * 0.9)) }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Label("Key points", systemImage: "sparkles").font(.headline)
                Spacer()
                if summarizing { ProgressView().controlSize(.small) }
            }
            Divider()
            content
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }

    @ViewBuilder private var content: some View {
        if cards.isEmpty && unavailable {
            unavailableNote
        } else if cards.isEmpty {
            Text(isLive ? "Listening… a summary appears every so often." : "No key points yet.")
                .font(.system(size: fontSize)).foregroundStyle(.tertiary)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .center)
                .multilineTextAlignment(.center)
        } else {
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 10) {
                        ForEach(cards) { card($0) }
                        if unavailable { unavailableInline }
                        Color.clear.frame(height: 1).id("SUMMARY_BOTTOM")
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .onChange(of: cards.count) {
                    withAnimation(.easeOut(duration: 0.2)) { proxy.scrollTo("SUMMARY_BOTTOM", anchor: .bottom) }
                }
            }
        }
    }

    private func card(_ c: SummaryCard) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(c.main.isEmpty ? "(nothing important)" : c.main)
                .font(.system(size: fontSize, weight: .semibold))
                .frame(maxWidth: .infinity, alignment: .leading)
            ForEach(Array(c.bullets.enumerated()), id: \.offset) { _, b in
                HStack(alignment: .top, spacing: 6) {
                    Text("•").font(bulletFont).foregroundStyle(.secondary)
                    Text(b).font(bulletFont)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            if !c.want.isEmpty {
                (Text("They want you to: ").foregroundStyle(.secondary) + Text(c.want))
                    .font(bulletFont)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.secondary.opacity(0.09)))
        .textSelection(.enabled)
    }

    private var unavailableInline: some View {
        Label("summary paused — model server not reachable", systemImage: "bolt.slash")
            .font(.caption).foregroundStyle(.secondary)
    }

    private var unavailableNote: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("Summary model not running", systemImage: "bolt.slash").font(.callout)
            Text("Start the on-device model server, then it fills in live:")
                .font(.caption).foregroundStyle(.secondary)
            Text("app/scripts/summary-server.sh")
                .font(.system(.caption, design: .monospaced))
                .textSelection(.enabled)
            Text("Captions keep working without it.").font(.caption).foregroundStyle(.tertiary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}
