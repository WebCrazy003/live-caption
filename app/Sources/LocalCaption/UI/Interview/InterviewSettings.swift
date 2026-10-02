import SwiftUI
import AppKit
import UniformTypeIdentifiers
import LocalCaptionKit

/// The Interview Assist settings (SPEC-15 §Settings → Interview), one Settings tab per `Page`.
/// Every key is SPEC-11's `interview` group.
struct InterviewSettingsSections: View {
    enum Page { case interview, asking, prompts, codex }

    let page: Page
    @EnvironmentObject var env: AppEnvironment
    @ObservedObject var codex: CodexService
    @State private var skillError: String?
    @State private var skillNotice: String?

    private var cfg: Binding<Config.Interview> { $env.config.interview }

    var body: some View {
        Group {
            switch page {
            case .interview: skillsSection; modelSection; privacySection
            case .asking: hotkeySection; sendingSection; screenshotSection
            case .prompts: promptsSection
            case .codex: codexSection; usageSection
            }
        }
    }

    // MARK: Codex

    private var codexSection: some View {
        Section {
            CodexStatusRow(codex: codex)
            LabeledContent("Codex path") {
                TextField("Auto-detect", text: cfg.codexPath).multilineTextAlignment(.trailing)
            }
            Button("Check again") { Task { await codex.refresh() } }.disabled(codex.checking)
        } header: {
            Text("Codex")
        } footer: {
            Text("Interview mode answers live questions with ChatGPT through the Codex app, with its own "
                 + "sign-in (separate from your Codex terminal). Codex is locked down: no files or commands; "
                 + "it may search the web when a skill asks for research.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    // MARK: Skills (required before an interview)

    private var skillsSection: some View {
        Section {
            ForEach(InterviewController.Step.allCases) { step in
                SkillSlotRow(step: step, library: env.library, error: $skillError, notice: $skillNotice)
            }
            if let message = skillError ?? skillNotice {
                Text(message).font(.caption).foregroundStyle(skillError != nil ? .orange : .secondary)
            }
        } header: {
            Text("Skills")
        } footer: {
            Text("Load all four before an interview: a SKILL.md file, any .md file, or a skill folder. "
                 + "Its text is sent the first time that step runs. CVs are uploaded and JDs pasted in "
                 + "the preparation panel, not here.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    // MARK: Usage

    private var usageSection: some View {
        Section {
            if let usage = codex.usage, !usage.windows.isEmpty {
                ForEach(usage.windows, id: \.label) { w in
                    VStack(alignment: .leading, spacing: 3) {
                        HStack {
                            Text(w.label)
                            Spacer()
                            Text("\(w.remainingPercent)% left").monospacedDigit()
                                .foregroundStyle(w.remainingPercent < 20 ? .orange : .primary)
                        }
                        ProgressView(value: Double(w.remainingPercent), total: 100)
                            .tint(w.remainingPercent < 20 ? .orange : .accentColor)
                        Text(w.line).font(.caption).foregroundStyle(.secondary)
                    }
                }
                HStack {
                    if let plan = usage.planType { Text("Plan: \(plan.capitalized)").font(.caption).foregroundStyle(.secondary) }
                    Spacer()
                    if let at = codex.usageUpdatedAt {
                        Text("Updated \(at.formatted(date: .omitted, time: .shortened))").font(.caption).foregroundStyle(.secondary)
                    }
                    Button("Refresh") { Task { await codex.refreshUsage() } }
                }
            } else {
                HStack {
                    Text(codex.isReady ? "No usage reported yet." : "Sign in to see your ChatGPT usage.")
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("Refresh") { Task { codex.isReady ? await codex.refreshUsage() : await codex.refresh() } }
                }
            }
        } header: {
            Text("Plus usage")
        }
        .task { if codex.isReady { await codex.refreshUsage() } }
    }

    // MARK: Model

    private var modelSection: some View {
        Section {
            Picker("Model", selection: cfg.model) {
                Text("Recommended (\(Config.Interview.recommendedModel))").tag("")
                ForEach(codex.models) { m in
                    Text(m.description.isEmpty ? m.displayName : "\(m.displayName) — \(m.description)").tag(m.id)
                }
                if !env.config.interview.model.isEmpty, !codex.models.contains(where: { $0.id == env.config.interview.model }) {
                    Text(env.config.interview.model).tag(env.config.interview.model)
                }
            }
            Picker("Answer effort", selection: cfg.reasoningEffort) {
                ForEach(efforts(including: env.config.interview.reasoningEffort), id: \.self) { Text($0.capitalized).tag($0) }
            }
            Picker("Skill steps & summary effort", selection: cfg.prepReasoningEffort) {
                ForEach(efforts(including: env.config.interview.prepReasoningEffort), id: \.self) { Text($0.capitalized).tag($0) }
            }
            Picker("Answer length", selection: cfg.answerLength) {
                Text("Short").tag(Config.Interview.AnswerLength.short)
                Text("Medium").tag(Config.Interview.AnswerLength.medium)
                Text("Long").tag(Config.Interview.AnswerLength.long)
            }
            .pickerStyle(.segmented)
        } header: {
            Text("Model & answers")
        } footer: {
            Text("Low effort answers fastest (about 1–2 s to first words with \(Config.Interview.recommendedModel)). "
                 + "Model, answer length and custom instructions apply when the next interview's coach starts.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private func efforts(including current: String) -> [String] {
        var list = codex.efforts(for: env.config.interview.effectiveModel)
        if !list.contains(current) { list.insert(current, at: 0) }
        return list
    }

    // MARK: Hotkey + sending

    private var hotkeySection: some View {
        Section {
            HotkeyRecorder(hotkey: cfg.hotkey)
        } header: {
            Text("Hotkey")
        }
    }

    private var sendingSection: some View {
        Section {
            Picker("Send", selection: cfg.sendMode) {
                Text("Everything since my last ask").tag(Config.Interview.SendMode.sinceLastAsk)
                Text("The last few sentences").tag(Config.Interview.SendMode.lastSentences)
            }
            if env.config.interview.sendMode == .lastSentences {
                Stepper(value: cfg.sendSentences, in: 1...20) {
                    LabeledContent("Sentences", value: "\(env.config.interview.clampedSendSentences)")
                }
            }
            Stepper(value: cfg.maxWords, in: 50...2000, step: 50) {
                LabeledContent("At most", value: "\(env.config.interview.clampedMaxWords) words")
            }
            Picker("While an answer is still coming", selection: cfg.busyPolicy) {
                Text("Interrupt it and answer the new question").tag(Config.Interview.BusyPolicy.interrupt)
                Text("Queue the new question").tag(Config.Interview.BusyPolicy.queue)
            }
        } header: {
            Text("What to send")
        } footer: {
            Text("The live, not-yet-final caption line is always included, so you can press right as the interviewer stops.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private var screenshotSection: some View {
        Section {
            Toggle("Send screenshots from the clipboard", isOn: cfg.includeClipboardImages)
            Toggle("Remove them from the clipboard after sending", isOn: cfg.clearClipboardImagesAfterSend)
                .disabled(!env.config.interview.includeClipboardImages)
        } header: {
            Text("Screenshots")
        } footer: {
            Text("Off by default. When on, each Ask also sends up to \(ClipboardImages.maxImages) images on the clipboard "
                 + "(⌘⌃⇧4 copies a screenshot) to OpenAI. Only images are read — never text.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    // MARK: Prompts

    private var promptsSection: some View {
        Section {
            VStack(alignment: .leading, spacing: 4) {
                Text("Custom instructions").font(.caption).foregroundStyle(.secondary)
                TextEditor(text: cfg.customInstructions)
                    .font(.callout).frame(minHeight: 70).border(.quaternary)
                Text("Prefilled into each new interview's setup.").font(.caption).foregroundStyle(.secondary)
            }
            ForEach(env.config.interview.quickPrompts.indices, id: \.self) { i in
                HStack(alignment: .top) {
                    VStack(spacing: 4) {
                        TextField("Label", text: quickPrompt(i).label)
                        TextField("Prompt", text: quickPrompt(i).text, axis: .vertical).lineLimit(1...3)
                    }
                    VStack(spacing: 2) {
                        Button { move(i, by: -1) } label: { Image(systemName: "chevron.up") }.disabled(i == 0)
                        Button { move(i, by: 1) } label: { Image(systemName: "chevron.down") }
                            .disabled(i == env.config.interview.quickPrompts.count - 1)
                    }
                    .buttonStyle(.borderless)
                    Button(role: .destructive) { env.config.interview.quickPrompts.remove(at: i) } label: { Image(systemName: "minus.circle") }
                        .buttonStyle(.borderless)
                }
            }
            HStack {
                Button { env.config.interview.quickPrompts.append(.init(label: "New", text: "")) } label: {
                    Label("Add quick prompt", systemImage: "plus")
                }
                Spacer()
                Button("Restore defaults") { env.config.interview.quickPrompts = Config.Interview.defaultQuickPrompts }
            }
            .buttonStyle(.borderless)
        } header: {
            Text("Prompts")
        }
    }

    private func quickPrompt(_ i: Int) -> Binding<Config.Interview.QuickPrompt> {
        Binding(get: { env.config.interview.quickPrompts.indices.contains(i) ? env.config.interview.quickPrompts[i] : .init(label: "", text: "") },
                set: { if env.config.interview.quickPrompts.indices.contains(i) { env.config.interview.quickPrompts[i] = $0 } })
    }

    private func move(_ i: Int, by delta: Int) {
        var list = env.config.interview.quickPrompts
        let j = i + delta
        guard list.indices.contains(i), list.indices.contains(j) else { return }
        list.swapAt(i, j)
        env.config.interview.quickPrompts = list
    }

    // MARK: After + privacy

    private var privacySection: some View {
        Section {
            Text("Interview mode sends your CV, the job description and your skills (when you run a skill step), "
                 + "the interviewer's recent words (on each Ask) and, if enabled, clipboard screenshots to OpenAI "
                 + "through Codex. Caption only mode sends nothing.")
                .font(.callout).foregroundStyle(.secondary)
            Button("Show the notice again next time") { env.config.interview.privacyAcknowledged = false }
                .disabled(!env.config.interview.privacyAcknowledged)
        } header: {
            Text("Privacy")
        }
    }
}

/// One Settings → Skills slot: what's loaded, and Load / Replace / Remove.
private struct SkillSlotRow: View {
    let step: InterviewController.Step
    @ObservedObject var library: InterviewLibrary
    @Binding var error: String?
    @Binding var notice: String?

    var body: some View {
        let skill = library.skill(slug: step.rawValue)
        HStack(spacing: 8) {
            Image(systemName: skill == nil ? "circle.dashed" : "checkmark.circle.fill")
                .foregroundStyle(skill == nil ? Color.orange : Color.green)
            VStack(alignment: .leading, spacing: 1) {
                Text(step.rawValue).font(.system(.body, design: .monospaced))
                Text(skill.map { "\($0.title) · \($0.chars.formatted()) characters" } ?? (step == .liveCoding ? "Not loaded (used only if you tick it)" : "Not loaded"))
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button(skill == nil ? "Load…" : "Replace…") { load() }
            if skill != nil {
                Button(role: .destructive) { library.removeSkill(slot: step.rawValue) } label: { Image(systemName: "trash") }
                    .buttonStyle(.borderless).help("Remove")
            }
        }
    }

    private func load() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = true
        panel.allowedContentTypes = [UTType(filenameExtension: "md") ?? .plainText, .folder]
        panel.message = "Choose the \(step.rawValue) skill — a SKILL.md / .md file or its folder"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let result = try library.loadSkill(slot: step.rawValue, from: url)
            error = nil
            notice = result.ignored.isEmpty ? "Loaded \(step.rawValue)."
                : "Loaded \(step.rawValue). Left out (Codex can't use them): \(result.ignored.joined(separator: ", "))"
        } catch let e {
            notice = nil
            error = e.localizedDescription
        }
    }
}
