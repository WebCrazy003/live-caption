import SwiftUI
import LocalCaptionKit

/// Shown right after End interview (owner, 2026-10-02): summarize the interview, or send a
/// follow-up prompt on the same thread. Both stay available afterwards in the wrap-up view.
struct EndInterviewSheet: View {
    @ObservedObject var interview: InterviewController
    let transcript: String
    let dismiss: () -> Void
    @State private var followUp = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Label("Interview ended", systemImage: "flag.checkered").font(.title3.weight(.semibold))
            Text("The transcript is saved. What would you like the coach to do?")
                .foregroundStyle(.secondary)

            Button {
                dismiss()
                Task { await interview.generateSummary(transcript: transcript) }
            } label: {
                Label("Summarize the interview", systemImage: "sparkles").frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent).controlSize(.large)
            .disabled(interview.record?.threadId == nil)

            VStack(alignment: .leading, spacing: 6) {
                Text("Or send a follow-up prompt").font(.callout.weight(.medium))
                TextField("e.g. Draft a thank-you email to the interviewer", text: $followUp, axis: .vertical)
                    .lineLimit(2...5).textFieldStyle(.roundedBorder)
                    .onSubmit(send)
                HStack {
                    Spacer()
                    Button("Send", action: send)
                        .disabled(followUp.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                                  || interview.record?.threadId == nil)
                }
            }

            HStack {
                if interview.record?.threadId == nil {
                    Text("The coach never started in this interview, so there's nothing to ask.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Not now", action: dismiss).keyboardShortcut(.cancelAction)
            }
        }
        .padding(20)
        .frame(width: 440)
    }

    private func send() {
        let text = followUp.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        dismiss()
        Task { await interview.sendFollowUp(text) }
    }
}
