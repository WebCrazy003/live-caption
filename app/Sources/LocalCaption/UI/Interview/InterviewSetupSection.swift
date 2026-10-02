import SwiftUI
import AppKit
import UniformTypeIdentifiers
import LocalCaptionKit

/// The preparation panel (SPEC-13 §Preparation): who and where (interviewee, company, step — they
/// name the session), then the four parts — ① CV, ② JD, ③ mode, ④ optional live coding — pick the
/// model and effort, then **Start preparation** runs the skills in that order. The four skills themselves are loaded in Settings → Interview → Skills.
struct InterviewSetupSection: View {
    @ObservedObject var interview: InterviewController
    @ObservedObject var codex: CodexService
    @ObservedObject var library: InterviewLibrary
    @EnvironmentObject var env: AppEnvironment
    @State private var uploadError: String?
    @State private var confirmingStartOver = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            // Account details live in Settings → Codex; here only what blocks the interview.
            if !codex.isReady || codex.signIn != nil || codex.signInError != nil { CodexStatusRow(codex: codex) }
            if !interview.allSkillsLoaded { missingSkillsBanner }

            detailsBox

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
            }

            part(3, "Apply instruction", step: .applyInstruction) {
                Picker("Mode", selection: $interview.draft.profile) {
                    ForEach(InterviewController.Profile.allCases) { Text($0.label).tag(Optional($0)) }
                }
                .pickerStyle(.segmented).labelsHidden()
                Text(interview.draft.profile == nil ? "Choose the mode for this interview."
                     : "Applied by Start preparation. During the interview you can switch modes from the header.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            part(4, "Live coding & design (optional)", step: .liveCoding) {
                Toggle("Use live coding & system design answers (needs the Tech mode)", isOn: $interview.draft.liveCoding)
                    .toggleStyle(.checkbox)
            }

            modelRow
            startRow

            if interview.record != nil, interview.record?.startedAt == nil, !interview.preparing {
                HStack {
                    Spacer()
                    Button("Start over") { confirmingStartOver = true }.buttonStyle(.link)
                        .help("Discard this preparation and begin again")
                }
            }
        }
        .onChange(of: interview.draft) { _, _ in interview.detailsChanged() }
        .confirmationDialog("Start over?", isPresented: $confirmingStartOver) {
            Button("Discard the preparation", role: .destructive) {
                Task { await interview.discardUnstarted(); interview.resetForNewInterview() }
            }
        } message: {
            Text("The steps run so far are deleted. Your uploaded CVs and skills stay.")
        }
    }

    // MARK: Interview details (owner, 2026-10-02)

    private var detailsBox: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 6) {
                Grid(alignment: .leading, horizontalSpacing: 8, verticalSpacing: 6) {
                    GridRow {
                        Text("Interviewee").gridColumnAlignment(.trailing)
                        TextField("Name", text: $interview.draft.candidate).textFieldStyle(.roundedBorder)
                    }
                    GridRow {
                        Text("Company")
                        TextField("Company", text: $interview.draft.company).textFieldStyle(.roundedBorder)
                    }
                    GridRow {
                        Text("Step")
                        Stepper(value: $interview.draft.step, in: 1...20) {
                            Text("\(interview.draft.step)").monospacedDigit()
                        }
                    }
                }
                Text(interview.sessionName(on: Date()).map { "Session name: \($0)" }
                     ?? "The session is named <interviewee>-<company>-<step>-<date>.")
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            .padding(4)
        } label: {
            Text("Interview").font(.subheadline.weight(.semibold))
        }
        .disabled(interview.preparing)
    }

    // MARK: Model + Start preparation

    private var modelRow: some View {
        let cfg = $env.config.interview
        let model = env.config.interview.effectiveModel
        return VStack(alignment: .leading, spacing: 6) {
            Picker("Model", selection: cfg.model) {
                Text("Recommended (\(Config.Interview.recommendedModel))").tag("")
                ForEach(codex.models) { Text($0.displayName).tag($0.id) }
                if !env.config.interview.model.isEmpty, !codex.models.contains(where: { $0.id == env.config.interview.model }) {
                    Text(env.config.interview.model).tag(env.config.interview.model)
                }
            }
            HStack {
                Picker("Preparation effort", selection: cfg.prepReasoningEffort) {
                    ForEach(efforts(model, including: env.config.interview.prepReasoningEffort), id: \.self) { Text($0.capitalized).tag($0) }
                }
                Picker("Answer effort", selection: cfg.reasoningEffort) {
                    ForEach(efforts(model, including: env.config.interview.reasoningEffort), id: \.self) { Text($0.capitalized).tag($0) }
                }
            }
            Text("Low answer effort answers fastest. These are the same settings as Settings → Interview.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .padding(.top, 2)
    }

    private func efforts(_ model: String, including current: String) -> [String] {
        var list = codex.efforts(for: model)
        if !list.contains(current) { list.insert(current, at: 0) }
        return list
    }

    @ViewBuilder private var startRow: some View {
        let blocker = interview.preparationBlocker
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 10) {
                if interview.preparing {
                    ProgressView().controlSize(.small)
                    Text("Preparing — \(interview.runningStep?.title ?? "starting")…").foregroundStyle(.secondary)
                    Spacer()
                } else if let failed = interview.preparationFailedAt {
                    Label("Stopped at \(failed.title)", systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                    Spacer()
                    Button("Continue") { Task { await interview.startPreparation(resume: true) } }
                        .buttonStyle(.borderedProminent).disabled(blocker != nil)
                    Button("Start over") { Task { await interview.startPreparation() } }.disabled(blocker != nil)
                } else {
                    if interview.isPrepared {
                        Label("Prepared", systemImage: "checkmark.seal.fill").foregroundStyle(.green)
                    }
                    Spacer()
                    Button { Task { await interview.startPreparation() } } label: {
                        Label(interview.isPrepared ? "Prepare again" : "Start preparation", systemImage: "sparkles")
                            .frame(minWidth: 150)
                    }
                    .buttonStyle(.borderedProminent).controlSize(.large)
                    .disabled(blocker != nil)
                }
            }
            if let blocker, !interview.preparing {
                Text(blocker).font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    // MARK: Parts

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
        let running = interview.runningStep == step
        let failed = interview.preparationFailedAt == step && !interview.preparing
        let done: Bool = {
            switch step {
            case .applyInstruction: return interview.activeProfile != nil && interview.activeProfile == interview.draft.profile
            case .liveCoding: return interview.liveCodingActive
            default: return interview.isDone(step)
            }
        }()
        return GroupBox {
            VStack(alignment: .leading, spacing: 6) { content() }.padding(4)
        } label: {
            HStack(spacing: 6) {
                Text("\(n)").font(.caption.weight(.bold)).monospacedDigit()
                    .frame(width: 18, height: 18)
                    .background(done ? Color.green.opacity(0.25) : Color.secondary.opacity(0.15), in: Circle())
                Text(title).font(.subheadline.weight(.semibold))
                if running { ProgressView().controlSize(.mini) }
                else if failed { Image(systemName: "exclamationmark.triangle.fill").font(.caption).foregroundStyle(.orange) }
                else if done { Image(systemName: "checkmark").font(.caption).foregroundStyle(.green) }
            }
        }
        .disabled(interview.preparing)
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
