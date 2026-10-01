import Foundation
import AppKit
import LocalCaptionKit

/// App-wide view of the answer engine for the UI: sign-in state, Plus usage, the model list,
/// and in-app ChatGPT sign-in (SPEC-12 §Sign-in, §Usage). The single consumer of the engine's
/// notices, so Settings and the interview screen see the same values.
@MainActor
final class CodexService: ObservableObject {
    let engine: AnswerEngine

    /// `nil` until the first check.
    @Published private(set) var status: EngineStatus?
    @Published private(set) var checking = false
    @Published private(set) var usage: CodexRPC.Usage?
    @Published private(set) var usageUpdatedAt: Date?
    @Published private(set) var models: [CodexRPC.Model] = []
    @Published private(set) var signIn: LoginTicket?
    @Published var signInError: String?

    private var noticeTask: Task<Void, Never>?

    init(engine: AnswerEngine) {
        self.engine = engine
        noticeTask = Task { [weak self, engine] in
            for await notice in engine.notices {
                await self?.handle(notice)
            }
        }
    }

    deinit { noticeTask?.cancel() }

    var isReady: Bool { status?.isReady == true }

    /// Status, then (when signed in) the model list and usage.
    func refresh() async {
        guard !checking else { return }
        checking = true
        defer { checking = false }
        let s = await engine.status()
        status = s
        guard s.isReady else { return }
        if let m = try? await engine.models() { models = m }
        await refreshUsage()
    }

    func refreshUsage() async {
        if let u = try? await engine.usage() { usage = u; usageUpdatedAt = Date() }
    }

    /// Open ChatGPT sign-in in the browser; completion arrives as a notice.
    func startSignIn() async {
        signInError = nil
        do {
            let ticket = try await engine.startLogin()
            signIn = ticket
            NSWorkspace.shared.open(ticket.authURL)
        } catch {
            signInError = error.localizedDescription
        }
    }

    func cancelSignIn() async {
        guard let t = signIn else { return }
        signIn = nil
        await engine.cancelLogin(t)
    }

    /// The configured model if Codex offers it, else the S0 default, else Codex's default.
    func resolvedModel(_ configured: String) -> String {
        let want = configured.isEmpty ? Config.Interview.recommendedModel : configured
        if models.isEmpty || models.contains(where: { $0.id == want }) { return want }
        return models.first(where: { $0.id == Config.Interview.recommendedModel })?.id
            ?? models.first(where: \.isDefault)?.id ?? want
    }

    func efforts(for model: String) -> [String] {
        models.first { $0.id == model }?.efforts ?? ["low", "medium", "high"]
    }

    func acceptsImages(_ model: String) -> Bool {
        models.first { $0.id == model }?.acceptsImages ?? true
    }

    /// Below 20 % in any window → warn (SPEC-13, SPEC-15).
    var usageIsLow: Bool { (usage?.lowest?.remainingPercent ?? 100) < 20 }

    private func handle(_ notice: EngineNotice) async {
        switch notice {
        case .usage(let u):
            usage = u; usageUpdatedAt = Date()
        case .loginCompleted(let success, let error):
            signIn = nil
            if !success { signInError = error ?? "Sign-in did not complete." }
            await refresh()
        case .accountChanged:
            await refresh()
        }
    }
}

extension CodexRPC.Usage.Window {
    /// "5-hour: 82% left · resets 14:20"
    var line: String {
        var s = "\(label): \(remainingPercent)% left"
        if let at = resetsAt {
            let d = Date(timeIntervalSince1970: TimeInterval(at))
            let sameDay = Calendar.current.isDateInToday(d)
            s += " · resets " + (sameDay ? d.formatted(date: .omitted, time: .shortened)
                                         : d.formatted(.dateTime.weekday(.abbreviated).hour().minute()))
        }
        return s
    }
}
