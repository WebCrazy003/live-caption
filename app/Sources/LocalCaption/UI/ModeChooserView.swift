import SwiftUI
import LocalCaptionKit

/// The first screen (owner, 2026-10-02): choose Caption only or Interview before starting. The
/// session screen's top bar has **Change mode** to come back here while nothing is recording.
struct ModeChooserView: View {
    /// The mode used last time, highlighted.
    let lastMode: Config.Interview.Mode
    let choose: (Config.Interview.Mode) -> Void
    /// The Standard ↔ Accent speech switch (SPEC-18), independent of the mode.
    let accent: Bool
    let setAccent: (Bool) -> Void

    var body: some View {
        VStack(spacing: 20) {
            Text("What are you doing?").font(.title2.weight(.semibold))
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 16) { cards }
                VStack(spacing: 12) { cards }
            }
            .frame(maxWidth: 640)
            SpeechSwitch(accent: accent, set: setAccent)
                .frame(maxWidth: 640)
        }
        .padding()
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    @ViewBuilder private var cards: some View {
        card(.caption, title: "Caption only", icon: "captions.bubble",
             detail: accent ? "Live captions of the call audio, from your RTX desktop. Saved as a transcript, corrected after you stop."
                            : "Live captions of the call audio, on this Mac. Saved as a transcript. Nothing leaves the device.")
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

/// Standard ↔ Accent (SPEC-18 D1): which speech pipeline the next session uses.
struct SpeechSwitch: View {
    let accent: Bool
    let set: (Bool) -> Void
    var compact = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Picker("Speech", selection: Binding(get: { accent }, set: set)) {
                Text("Standard").tag(false)
                Text(compact ? "Accent" : "Accent / noisy").tag(true)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .help("Standard: on this Mac. Accent: for accented or noisy speech — models on your RTX desktop, corrected by Codex.")
            if !compact {
                Text(accent ? "For accented or noisy speech. Audio goes to your RTX desktop; after you stop, transcript text goes to OpenAI for correction."
                            : "Standard English. Speech recognition runs on this Mac.")
                    .font(.callout).foregroundStyle(.secondary)
            }
        }
    }
}

/// Shown the first time Accent mode is chosen (SPEC-18 §Privacy).
struct AccentPrivacyNotice: View {
    let accept: () -> Void
    let cancel: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Label("Accent mode", systemImage: "waveform.badge.magnifyingglass").font(.title2.weight(.semibold))
            Text("Accent mode is for accented or noisy speech. Unlike Standard mode, it doesn't stay on this Mac:")
            VStack(alignment: .leading, spacing: 8) {
                Label("The call audio goes to your RTX desktop on your local network, which runs the speech models.",
                      systemImage: "desktopcomputer")
                Label("After you stop, the transcript text goes to OpenAI through your Codex sign-in, to correct "
                      + "misheard words using the whole conversation.", systemImage: "text.badge.checkmark")
            }
            Text("You can turn the correction off in Settings → Accent mode. Standard mode is unchanged.")
                .font(.callout).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Cancel", action: cancel).keyboardShortcut(.cancelAction)
                Button("Use Accent mode", action: accept).keyboardShortcut(.defaultAction)
            }
        }
        .padding(20).frame(width: 460)
    }
}
