import SwiftUI
import LocalCaptionKit

/// Settings → Accent mode (SPEC-18 §Settings): the RTX desktop (find, pair, test), its speech
/// models, audio cleanup, Codex correction and the vocabulary.
struct AccentSettingsSections: View {
    @EnvironmentObject var env: AppEnvironment
    @ObservedObject var codex: CodexService
    @StateObject private var browser = RTXBrowser()
    @State private var hello: RTXProtocol.Hello?
    @State private var helloError: String?
    @State private var checking = false
    @State private var code = ""
    @State private var pairing = false
    @State private var pairError: String?
    @State private var paired = RTXToken.load() != nil
    @State private var models: [RTXProtocol.Model] = []
    @State private var status: RTXProtocol.Status?

    private var cfg: Binding<Config.Accent> { $env.config.accent }

    var body: some View {
        rtxSection
        modelsSection
        audioSection
        correctionSection
        vocabularySection
    }

    // MARK: RTX desktop

    private var rtxSection: some View {
        Section {
            if !browser.found.isEmpty {
                LabeledContent("Found on your network") {
                    VStack(alignment: .trailing, spacing: 4) {
                        ForEach(browser.found) { f in
                            Button(f.name) { Task { await choose(f) } }.buttonStyle(.link)
                        }
                    }
                }
            }
            TextField("Address", text: cfg.rtxAddress, prompt: Text("e.g. 192.168.1.20"))
                .onSubmit { Task { await check() } }
            connectionLine
            if hello != nil {
                if paired {
                    HStack {
                        Button("Test connection") { Task { await check() } }
                        Button("Unpair", role: .destructive) { Task { await unpair() } }
                    }
                } else {
                    HStack {
                        TextField("Pairing code", text: $code, prompt: Text("6 digits shown on the RTX desktop"))
                            .onSubmit { Task { await pair() } }
                        Button("Pair") { Task { await pair() } }
                            .disabled(code.filter(\.isNumber).count != 6 || pairing)
                    }
                    if let pairError { Text(pairError).font(.caption).foregroundStyle(.red) }
                }
            }
        } header: {
            Text("RTX desktop")
        } footer: {
            Text("Accent mode runs its speech models on your RTX desktop (the LocalCaption RTX agent, see "
                 + "rtx-agent/README.md). Audio goes to it over your local network only.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .task { browser.start(); await check() }
        .onDisappear { browser.stop() }
    }

    @ViewBuilder private var connectionLine: some View {
        if checking {
            HStack(spacing: 6) { ProgressView().controlSize(.small); Text("Checking…").foregroundStyle(.secondary) }
        } else if let hello {
            Label(paired ? "Paired with \(hello.name) — \(hello.gpu), \(hello.vramTotalMb / 1024) GB"
                         : "Found \(hello.name) — \(hello.gpu). Enter the pairing code it shows.",
                  systemImage: paired ? "checkmark.circle.fill" : "link")
                .foregroundStyle(paired ? .green : .primary)
            if paired, let status {
                Text("Models: \(status.state == .ready ? "\(status.loaded.primary ?? "–") + \(status.loaded.secondary ?? "none") loaded" : status.line)"
                     + (status.vramUsedMb.map { " · GPU memory in use \($0 / 1024) GB" } ?? ""))
                    .font(.caption).foregroundStyle(.secondary)
            }
        } else if let helloError {
            Label(helloError, systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
        }
    }

    private func client() -> RTXClient? {
        env.config.accent.agentURL.map { RTXClient(baseURL: $0, token: RTXToken.load()) }
    }

    private func choose(_ found: RTXBrowser.Found) async {
        guard let address = await RTXBrowser.resolve(found.endpoint) else {
            helloError = "Couldn't reach \(found.name)."
            return
        }
        env.config.accent.rtxAddress = address
        await check()
    }

    private func check() async {
        guard let client = client() else { hello = nil; helloError = nil; return }
        checking = true
        defer { checking = false }
        do {
            hello = try await client.hello()
            helloError = nil
            paired = RTXToken.load() != nil && hello?.paired == true
            if paired {
                do {
                    status = try await client.status()
                    models = try await client.models()
                } catch RTXError.notPaired {
                    paired = false          // the agent forgot this Mac (tokens.json edited)
                }
            }
        } catch {
            hello = nil
            helloError = error.localizedDescription
        }
    }

    private func pair() async {
        guard let client = client() else { return }
        pairing = true
        defer { pairing = false }
        do {
            let reply = try await client.pair(code: code.filter(\.isNumber), name: Host.current().localizedName ?? "Mac")
            RTXToken.save(reply.token)
            env.config.accent.rtxName = reply.name
            code = ""; pairError = nil
            await check()
            if env.config.accent.enabled { env.session.reloadEngine() }
        } catch RTXError.http(403, _) {
            pairError = "Wrong code. Check the code shown on the RTX desktop."
        } catch {
            pairError = error.localizedDescription
        }
    }

    private func unpair() async {
        try? await client()?.unpair()
        RTXToken.delete()
        paired = false; status = nil; models = []
        env.config.accent.rtxName = ""
    }

    // MARK: Models

    private var modelsSection: some View {
        Section {
            Picker("Primary", selection: cfg.primaryModel) { modelOptions(including: env.config.accent.primaryModel) }
            Picker("Secondary", selection: cfg.secondaryModel) {
                Text("None").tag("")
                modelOptions(including: env.config.accent.secondaryModel)
            }
            if env.config.accent.primaryModel != Config.Accent.defaultPrimaryModel
                || env.config.accent.secondaryModel != Config.Accent.defaultSecondaryModel {
                Button("Reset to defaults") {
                    env.config.accent.primaryModel = Config.Accent.defaultPrimaryModel
                    env.config.accent.secondaryModel = Config.Accent.defaultSecondaryModel
                }
            }
        } header: {
            Text("Speech models on the RTX")
        } footer: {
            Text("The primary model's text is the caption; the secondary's helps the correction. "
                 + "Defaults are the measured pair. A model not downloaded yet downloads when Accent mode starts.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .onChange(of: env.config.accent.primaryModel) { _, _ in reloadIfActive() }
        .onChange(of: env.config.accent.secondaryModel) { _, _ in reloadIfActive() }
    }

    @ViewBuilder private func modelOptions(including current: String) -> some View {
        ForEach(models) { m in
            Text("\(m.label)\(m.tested ? "" : " — not tested")\(m.downloaded ? "" : " — \(m.sizeMb / 1000) GB download")")
                .tag(m.id)
        }
        if !current.isEmpty, !models.contains(where: { $0.id == current }) {
            Text(current).tag(current)
        }
    }

    private func reloadIfActive() {
        if env.config.accent.enabled { env.session.reloadEngine() }
    }

    // MARK: Audio

    private var audioSection: some View {
        Section {
            Toggle("Clean up audio (cut rumble and hiss)", isOn: cfg.audioBandpass)
            Toggle("Even out loudness", isOn: cfg.audioLevel)
            LabeledContent("Pause before a caption ends") {
                HStack {
                    Slider(value: Binding(get: { Double(env.config.accent.endpointSilenceMs) },
                                          set: { env.config.accent.endpointSilenceMs = Int($0) }),
                           in: 400...1500, step: 100)
                    Text("\(env.config.accent.endpointSilenceMs) ms").monospacedDigit().frame(width: 64, alignment: .trailing)
                }
            }
        } header: {
            Text("Audio")
        } footer: {
            Text("A longer pause gives the models longer pieces of speech (more accurate), a shorter one "
                 + "shows captions sooner. Applies to the next session.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .onChange(of: env.config.accent.audioBandpass) { _, _ in reloadIfActive() }
        .onChange(of: env.config.accent.audioLevel) { _, _ in reloadIfActive() }
    }

    // MARK: Correction

    private var correctionSection: some View {
        Section {
            Toggle("Correct captions live", isOn: cfg.liveCorrection)
            if env.config.accent.liveCorrection {
                codexModelPicker("Live model", selection: cfg.liveModel, recommended: Config.Accent.recommendedLiveModel)
                effortPicker("Live reasoning effort", selection: cfg.liveEffort, model: env.config.accent.effectiveLiveModel)
            }
            Toggle("Improve the whole transcript after Stop", isOn: cfg.finalPass)
            if env.config.accent.finalPass {
                codexModelPicker("Final model", selection: cfg.finalModel, recommended: Config.Accent.recommendedFinalModel)
                effortPicker("Final reasoning effort", selection: cfg.finalEffort, model: env.config.accent.effectiveFinalModel)
            }
            if let status = codex.status, !status.isReady {
                Label(status.summary, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.orange)
            }
        } header: {
            Text("Correction (Codex)")
        } footer: {
            Text("Transcript text (never audio) goes to OpenAI through your Codex sign-in. Live: low effort keeps "
                 + "corrections about 2 s behind the captions. Final: high effort took about 2 minutes for a "
                 + "13-minute session.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .task { if codex.models.isEmpty { await codex.refresh() } }
    }

    private func codexModelPicker(_ title: String, selection: Binding<String>, recommended: String) -> some View {
        Picker(title, selection: selection) {
            Text("Recommended (\(recommended))").tag("")
            ForEach(codex.models) { m in Text(m.displayName).tag(m.id) }
            if !selection.wrappedValue.isEmpty, !codex.models.contains(where: { $0.id == selection.wrappedValue }) {
                Text(selection.wrappedValue).tag(selection.wrappedValue)
            }
        }
    }

    private func effortPicker(_ title: String, selection: Binding<String>, model: String) -> some View {
        var list = codex.efforts(for: model)
        if !list.contains(selection.wrappedValue) { list.insert(selection.wrappedValue, at: 0) }
        return Picker(title, selection: selection) {
            ForEach(list, id: \.self) { Text($0.capitalized).tag($0) }
        }
    }

    // MARK: Vocabulary

    private var vocabularySection: some View {
        Section {
            TextEditor(text: cfg.vocabulary)
                .font(.body).frame(minHeight: 70)
        } header: {
            Text("Vocabulary")
        } footer: {
            Text("Names of people, products and your stack, separated by commas — e.g. Victor, Claude, "
                 + "Cowork, Paystack, TypeScript. The correction uses them to fix misheard words.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}
