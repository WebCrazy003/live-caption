import SwiftUI
import LocalCaptionKit

/// Codex status + sign-in, shared by the interview setup and Settings (SPEC-12 §Sign-in).
struct CodexStatusRow: View {
    @ObservedObject var codex: CodexService

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                if codex.checking || codex.status == nil {
                    ProgressView().controlSize(.small)
                    Text("Checking Codex…").foregroundStyle(.secondary)
                } else if let status = codex.status {
                    Image(systemName: status.isReady ? "checkmark.seal.fill" : "exclamationmark.triangle.fill")
                        .foregroundStyle(status.isReady ? .green : .orange)
                    Text(status.summary).font(.callout).lineLimit(2)
                    Spacer()
                    if case .signedOut = status {
                        if codex.signIn == nil {
                            Button("Sign in…") { Task { await codex.startSignIn() } }
                        } else {
                            ProgressView().controlSize(.small)
                            Button("Cancel") { Task { await codex.cancelSignIn() } }
                        }
                    } else if !status.isReady {
                        Button("Check again") { Task { await codex.refresh() } }
                    }
                }
            }
            if codex.signIn != nil {
                Text("Finish signing in to ChatGPT in your browser.").font(.caption).foregroundStyle(.secondary)
            }
            if let err = codex.signInError {
                Text(err).font(.caption).foregroundStyle(.orange)
            }
            if codex.isReady, let w = codex.usage?.lowest {
                Label(w.line, systemImage: "gauge.with.dots.needle.33percent")
                    .font(.caption).foregroundStyle(codex.usageIsLow ? .orange : .secondary)
            }
        }
        .task { if codex.status == nil { await codex.refresh() } }
    }
}

/// One-time notice before Interview mode is first used (SPEC-11 §Privacy).
struct InterviewPrivacyNotice: View {
    let accept: () -> Void
    let cancel: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Label("Interview mode sends data to OpenAI", systemImage: "network").font(.title3.weight(.semibold))
            Text("Caption only mode keeps everything on this Mac. Interview mode is different:")
            VStack(alignment: .leading, spacing: 6) {
                Label("Your CV, the job description and your skills are sent when you run a skill step.",
                      systemImage: "doc.text")
                Label("The interviewer's recent words are sent each time you press Ask.", systemImage: "text.bubble")
                Label("Screenshots on your clipboard are sent only if you turn that on in Settings.",
                      systemImage: "photo")
            }
            .font(.callout)
            Text("They go to OpenAI through the Codex app, signed in with your ChatGPT account. "
                 + "Codex is locked down: it can't read your files or run commands. It may search the "
                 + "web when a skill asks it to research the company.")
                .font(.callout).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Cancel", action: cancel)
                Button("Use Interview mode", action: accept).keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 460)
    }
}
