import Foundation
import LocalCaptionKit

/// The answer engine interface (SPEC-12 §The interface). Codex today; an OpenAI-API engine can
/// conform later without touching the interview flow or UI (SPEC-11 D2).
protocol AnswerEngine: AnyObject, Sendable {
    func status() async -> EngineStatus
    func models() async throws -> [CodexRPC.Model]
    func usage() async throws -> CodexRPC.Usage
    func startThread(_ cfg: ThreadConfig) async throws -> String
    func resumeThread(id: String, _ cfg: ThreadConfig) async throws
    /// One turn. Effort is per turn: Codex only accepts it on `turn/start`, and it persists.
    func send(threadId: String, input: [CodexRPC.Input], effort: String) -> AsyncThrowingStream<AnswerEvent, Error>
    func interrupt(threadId: String) async
    func archiveThread(id: String) async
    func startLogin() async throws -> LoginTicket
    func cancelLogin(_ ticket: LoginTicket) async
    /// Usage updates and login completion, while the engine runs.
    var notices: AsyncStream<EngineNotice> { get }
    func shutdown() async
}

struct ThreadConfig: Equatable, Sendable {
    var model: String
    var baseInstructions: String
}

struct LoginTicket: Equatable, Sendable {
    let loginId: String
    let authURL: URL
}

enum EngineStatus: Equatable, Sendable {
    case notInstalled
    case tooOld(version: String)
    case signedOut
    case ready(email: String?, plan: String?)
    case failed(String)

    var isReady: Bool { if case .ready = self { return true }; return false }

    /// One line for Settings and the Prepare panel.
    var summary: String {
        switch self {
        case .notInstalled: return "Codex isn't installed. Install it with Homebrew: brew install codex"
        case .tooOld(let v): return "Codex \(v) is too old — LocalCaption needs \(CodexRPC.minimumVersion) or newer (brew upgrade codex)."
        case .signedOut: return "Not signed in to ChatGPT. Sign in from Settings → Interview."
        case .ready(let email, let plan):
            let who = email ?? "ChatGPT"
            return plan.map { "Signed in as \(who) (\($0.capitalized))" } ?? "Signed in as \(who)"
        case .failed(let message): return "Codex failed to start: \(message)"
        }
    }
}

enum AnswerEvent: Equatable, Sendable {
    case started(turnId: String)
    /// A reasoning item began — show "Thinking…" until text arrives.
    case thinking
    case delta(String)
    /// No text 30 s after the request — still waiting.
    case slow
    case completed(String)
    case interrupted(partial: String)
    case failed(EngineError, partial: String)
}

enum EngineNotice: Equatable, Sendable {
    case usage(CodexRPC.Usage)
    case loginCompleted(success: Bool, error: String?)
    case accountChanged
}

enum EngineError: Error, Equatable, Sendable, LocalizedError {
    case notInstalled
    case tooOld(String)
    case signedOut
    case busy
    case timeout
    case crashed
    case usageLimit(resetsAt: Date?)
    case network(String)
    case blockedTool(String)
    case rpc(String)
    case other(String)

    var errorDescription: String? {
        switch self {
        case .notInstalled: return "Codex isn't installed."
        case .tooOld(let v): return "Codex \(v) is too old."
        case .signedOut: return "Not signed in to ChatGPT."
        case .busy: return "Still answering the previous question."
        case .timeout: return "No answer after 2 minutes."
        case .crashed: return "Codex restarted — press Ask again."
        case .usageLimit(let at):
            guard let at else { return "Plus usage limit reached." }
            return "Plus limit reached — resets at \(at.formatted(date: .omitted, time: .shortened))."
        case .network(let m): return "Network problem: \(m)"
        case .blockedTool(let t): return "Blocked: the model tried to use a tool (\(t))."
        case .rpc(let m): return m
        case .other(let m): return m
        }
    }
}
