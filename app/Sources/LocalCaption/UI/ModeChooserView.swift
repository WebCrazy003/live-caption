import SwiftUI
import LocalCaptionKit

/// The first screen (owner, 2026-10-02): choose Caption only or Interview before starting. The
/// session screen's top bar has **Change mode** to come back here while nothing is recording.
struct ModeChooserView: View {
    /// The mode used last time, highlighted.
    let lastMode: Config.Interview.Mode
    let choose: (Config.Interview.Mode) -> Void

    var body: some View {
        VStack(spacing: 20) {
            Text("What are you doing?").font(.title2.weight(.semibold))
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 16) { cards }
                VStack(spacing: 12) { cards }
            }
            .frame(maxWidth: 640)
        }
        .padding()
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    @ViewBuilder private var cards: some View {
        card(.caption, title: "Caption only", icon: "captions.bubble",
             detail: "Live captions of the call audio, on this Mac. Saved as a transcript. Nothing leaves the device.")
        card(.interview, title: "Interview", icon: "person.2.wave.2",
             detail: "Captions plus an AI coach: prepare with your CV and the job description, then get answers to the interviewer's questions.")
    }

    private func card(_ mode: Config.Interview.Mode, title: String, icon: String, detail: String) -> some View {
        let last = mode == lastMode
        return Button { choose(mode) } label: {
            VStack(alignment: .leading, spacing: 10) {
                Image(systemName: icon).font(.system(size: 28)).foregroundStyle(Color.accentColor)
                Text(title).font(.title3.weight(.semibold))
                Text(detail).font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
                if last { Text("Last used").font(.caption).foregroundStyle(.secondary) }
            }
            .padding(16)
            .frame(maxWidth: .infinity, minHeight: 170, alignment: .topLeading)
            .contentShape(RoundedRectangle(cornerRadius: 12))
            .background(RoundedRectangle(cornerRadius: 12).fill(Color.secondary.opacity(0.08)))
            .overlay(RoundedRectangle(cornerRadius: 12)
                .stroke(last ? Color.accentColor.opacity(0.6) : Color.secondary.opacity(0.25), lineWidth: last ? 2 : 1))
        }
        .buttonStyle(.plain)
        .keyboardShortcut(last ? .defaultAction : nil)
        .accessibilityLabel("\(title) mode")
    }
}
