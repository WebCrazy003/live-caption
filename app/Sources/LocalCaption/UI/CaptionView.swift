import SwiftUI

/// Incremental live-caption renderer (SPEC.md §9.2): completed paragraphs, then the
/// building paragraph with a dimmed provisional tail, with auto-scroll to bottom.
/// Extracted from the minimal app's `ContentView`. Font size is driven by config for
/// live-apply. "Jump to latest" on manual scroll-up is Phase 2 (SPEC-05).
struct CaptionView: View {
    let paragraphs: [String]
    let current: String
    let hypothesis: String
    let isReady: Bool
    let fontSize: Double
    let autoScroll: Bool

    private var font: Font { .system(size: fontSize) }

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    ForEach(Array(paragraphs.enumerated()), id: \.offset) { _, p in
                        Text(p).font(font).lineSpacing(3).textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    if !current.isEmpty || !hypothesis.isEmpty {
                        (Text(current)
                         + Text(current.isEmpty ? hypothesis : " " + hypothesis)
                            .foregroundColor(.secondary))
                            .font(font).lineSpacing(3)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    if paragraphs.isEmpty && current.isEmpty && hypothesis.isEmpty && isReady {
                        Text("Play some audio…").font(font).foregroundStyle(.tertiary)
                    }
                    Color.clear.frame(height: 1).id("BOTTOM")   // scroll anchor
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .onChange(of: paragraphs.count) { scrollToBottom(proxy) }
            .onChange(of: current) { scrollToBottom(proxy) }
            .onChange(of: hypothesis) { scrollToBottom(proxy) }
        }
    }

    private func scrollToBottom(_ proxy: ScrollViewProxy) {
        guard autoScroll else { return }
        withAnimation(.easeOut(duration: 0.2)) { proxy.scrollTo("BOTTOM", anchor: .bottom) }
    }
}
