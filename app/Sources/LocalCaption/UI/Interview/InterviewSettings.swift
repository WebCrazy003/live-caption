import SwiftUI
import LocalCaptionKit

/// Settings → Interview (SPEC-15 §Settings → Interview). Every key is SPEC-11's `interview` group.
struct InterviewSettingsSections: View {
    @EnvironmentObject var env: AppEnvironment
    @ObservedObject var codex: CodexService
    @State private var showingLibrary = false

    private var cfg: Binding<Config.Interview> { $env.config.interview }

    var body: some View {
        Group {
            codexSection
            usageSection
            modelSection
            hotkeySection
            sendingSection
            screenshotSection
            promptsSection
            afterSection
            privacySection
        }
        .sheet(isPresented: $showingLibrary) { LibraryView(library: env.library) }
    }

    // MARK: Codex

    private var codexSection: some View {
        Section {
            CodexStatusRow(codex: codex)
            LabeledContent("Codex path") {
                TextField("Auto-detect", text: cfg.codexPath).multilineTextAlignment(.trailing)
            }
            HStack {
                Button("Check again") { Task { await codex.refresh() } }.disabled(codex.checking)
                Button("Open interview library…") { showingLibrary = true }
            }
        } header: {
            Text("Interview — Codex")
        } footer: {
            Text("Interview mode answers live questions with ChatGPT through the Codex app, with its own "
                 + "sign-in (separate from your Codex terminal). Codex is locked down: no files, commands or web.")
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
            Text("Interview — Plus usage")
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
            Picker("Prep & summary effort", selection: cfg.prepReasoningEffort) {
                ForEach(efforts(including: env.config.interview.prepReasoningEffort), id: \.self) { Text($0.capitalized).tag($0) }
            }
            Picker("Answer length", selection: cfg.answerLength) {
                Text("Short").tag(Config.Interview.AnswerLength.short)
                Text("Medium").tag(Config.Interview.AnswerLength.medium)
                Text("Long").tag(Config.Interview.AnswerLength.long)
            }
            .pickerStyle(.segmented)
        } header: {
            Text("Interview — Model")
        } footer: {
            Text("Low effort answers fastest (about 1–2 s to first words with \(Config.Interview.recommendedModel)). "
                 + "Changes apply to the next interview you prepare.")
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
            Text("Interview — Hotkey")
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
            Text("Interview — Sending")
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
            Text("Interview — Screenshots")
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
            Text("Interview — Prompts")
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

    private var afterSection: some View {
        Section {
            Toggle("Summarize when the interview ends", isOn: cfg.summarizeOnEnd)
        } header: {
            Text("Interview — After the interview")
        }
    }

    private var privacySection: some View {
        Section {
            Text("Interview mode sends your CV, the job description, your skills and instructions (on Prepare), "
                 + "the interviewer's recent words (on each Ask) and, if enabled, clipboard screenshots to OpenAI "
                 + "through Codex. Caption only mode sends nothing.")
                .font(.callout).foregroundStyle(.secondary)
            Button("Show the notice again next time") { env.config.interview.privacyAcknowledged = false }
                .disabled(!env.config.interview.privacyAcknowledged)
        } header: {
            Text("Interview — Privacy")
        }
    }
}
