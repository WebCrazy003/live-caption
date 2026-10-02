import SwiftUI
import AppKit
import UniformTypeIdentifiers
import LocalCaptionKit

/// The preparation panel (SPEC-13 §Preparation): four parts, each run by hand —
/// ① Discovery CV (with the CV upload), ② Discovery JD (pasted), ③ Apply instruction (one of three
/// profiles), ④ Live coding & design (optional, a checkbox). The four skills themselves are
/// loaded in Settings → Skills.
struct InterviewSetupSection: View {
    @ObservedObject var interview: InterviewController
    @ObservedObject var codex: CodexService
    @ObservedObject var library: InterviewLibrary
    @State private var uploadError: String?
    @State private var confirmingStartOver = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            CodexStatusRow(codex: codex)
            if !interview.allSkillsLoaded { missingSkillsBanner }

            part(1, "Discovery CV", step: .discoveryCV) {
                HStack {
                    Picker("CV", selection: $interview.draft.cvId) {
                        Text("Choose a CV…").tag(String?.none)
                        ForEach(library.documents(of: .cv)) { Text($0.title).tag(Optional($0.id)) }
                    }
                    .labelsHidden()
                    Button("Upload CV…") { uploadCV() }
                }
                if let uploadError { Text(uploadError).font(.caption).foregroundStyle(.orange) }
                runRow(.discoveryCV)
            }

            part(2, "Discovery JD", step: .discoveryJD) {
                TextEditor(text: $interview.draft.jobDescription)
                    .font(.callout).frame(minHeight: 90, maxHeight: 180)
                    .overlay(alignment: .topLeading) {
                        if interview.draft.jobDescription.isEmpty {
                            Text("Paste the job description here").font(.callout).foregroundStyle(.tertiary)
                                .padding(.top, 1).padding(.leading, 5).allowsHitTesting(false)
                        }
                    }
                    .border(.quaternary)
                runRow(.discoveryJD)
            }

            part(3, "Apply instruction", step: .applyInstruction) {
                HStack(spacing: 6) {
                    ForEach(InterviewController.Profile.allCases) { p in
                        let active = interview.activeProfile == p
                        Button { Task { await interview.run(.applyInstruction, profile: p) } } label: {
                            Label(p.label, systemImage: active ? "largecircle.fill.circle" : "circle")
                        }
                        .buttonStyle(.bordered).tint(active ? .accentColor : nil)
                        .disabled(interview.blocker(.applyInstruction) != nil || busy)
                        .help("/apply-instruction \(p.rawValue)")
                    }
                    Spacer()
                    if running("/apply-instruction") { ProgressView().controlSize(.small) }
                }
                Text("Choosing a mode applies it. You can switch modes during the interview from the header.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            part(4, "Live coding & design (optional)", step: .liveCoding) {
                HStack {
                    Toggle("Use live coding & system design answers", isOn: Binding(
                        get: { interview.liveCodingActive },
                        set: { on in Task { await interview.setLiveCoding(on) } }))
                        .toggleStyle(.checkbox)
                        .disabled(interview.blocker(.liveCoding) != nil || busy)
                    Spacer()
                    if running("/live-coding-design") { ProgressView().controlSize(.small) }
                }
                if let b = interview.blocker(.liveCoding), interview.skill(for: .liveCoding) != nil {
                    Text(b).font(.caption).foregroundStyle(.secondary)
                }
            }

            if interview.record != nil, interview.record?.startedAt == nil {
                HStack {
                    Spacer()
                    Button("Start over") { confirmingStartOver = true }.buttonStyle(.link)
                        .help("Discard this preparation and begin again")
                }
            }
        }
        .confirmationDialog("Start over?", isPresented: $confirmingStartOver) {
            Button("Discard the preparation", role: .destructive) {
                Task { await interview.discardUnstarted(); interview.resetForNewInterview() }
            }
        } message: {
            Text("The steps you've run so far are deleted. Your uploaded CVs and skills stay.")
        }
    }

    private var busy: Bool { interview.isStreaming }

    private func running(_ prefix: String) -> Bool {
        busy && interview.turns.last?.question.hasPrefix(prefix) == true
    }

    private var missingSkillsBanner: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
            VStack(alignment: .leading, spacing: 4) {
                Text("Load your 4 skills before the interview").font(.callout.weight(.semibold))
                Text("Missing: " + interview.missingSkills.map(\.rawValue).joined(separator: ", "))
                    .font(.caption).foregroundStyle(.secondary)
                SettingsLink { Text("Open Settings → Interview → Skills") }.buttonStyle(.link).font(.caption)
            }
        }
        .padding(8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.orange.opacity(0.08), in: RoundedRectangle(cornerRadius: 6))
    }

    private func part<Content: View>(_ n: Int, _ title: String, step: InterviewController.Step,
                                     @ViewBuilder _ content: () -> Content) -> some View {
        let done = step == .applyInstruction ? interview.activeProfile != nil
                 : step == .liveCoding ? interview.liveCodingActive : interview.isDone(step)
        return GroupBox {
            VStack(alignment: .leading, spacing: 6) { content() }.padding(4)
        } label: {
            HStack(spacing: 6) {
                Text("\(n)").font(.caption.weight(.bold)).monospacedDigit()
                    .frame(width: 18, height: 18)
                    .background(done ? Color.green.opacity(0.25) : Color.secondary.opacity(0.15), in: Circle())
                Text(title).font(.subheadline.weight(.semibold))
                if done { Image(systemName: "checkmark").font(.caption).foregroundStyle(.green) }
            }
        }
    }

    private func runRow(_ step: InterviewController.Step) -> some View {
        let blocker = interview.blocker(step)
        let done = interview.isDone(step)
        return HStack(spacing: 8) {
            if let blocker { Text(blocker).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
            Spacer()
            if running("/\(step.rawValue)") { ProgressView().controlSize(.small) }
            Button(done ? "Run again" : "Run \(step.title)") { Task { await interview.run(step) } }
                .disabled(blocker != nil || busy)
        }
    }

    private func uploadCV() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.pdf, .plainText, UTType(filenameExtension: "md") ?? .plainText]
        panel.allowsMultipleSelection = false
        panel.message = "Choose your CV (PDF, Markdown or text)"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do { try interview.uploadCV(from: url); uploadError = nil }
        catch { uploadError = error.localizedDescription }
    }
}
