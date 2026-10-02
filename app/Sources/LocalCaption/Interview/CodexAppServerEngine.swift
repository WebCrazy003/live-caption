import Foundation
import os
import LocalCaptionKit

/// `AnswerEngine` over a long-lived, locked-down `codex app-server` child process (SPEC-12).
///
/// One process per app run, started lazily on first use. Requests are JSON-RPC over stdio;
/// one turn at a time. If the process dies, the in-flight turn fails with `.crashed`, the next
/// call restarts it once and re-applies the lockdown to known threads with `thread/resume`; a
/// second crash leaves the engine failed (captions are never affected).
actor CodexAppServerEngine: AnswerEngine {
    typealias TransportFactory = @Sendable (_ codex: URL, _ arguments: [String], _ cwd: URL,
                                            _ environment: [String: String]) throws -> LineTransport
    typealias Locator = @Sendable (_ configuredPath: String) -> Result<CodexLocator.Found, CodexLocator.Problem>

    struct Timing: Sendable {
        var request: TimeInterval = 15
        var slowAfter: TimeInterval = 30
        var giveUpAfter: TimeInterval = 120
        var interruptGrace: TimeInterval = 2
    }

    nonisolated let notices: AsyncStream<EngineNotice>
    private let noticeSink: AsyncStream<EngineNotice>.Continuation

    private let codexPath: @Sendable () -> String
    private let workspace: URL
    private let codexHome: URL
    private let locate: Locator
    private let makeTransport: TransportFactory
    private let timing: Timing
    private let log = Logger(subsystem: "com.livecaption.app", category: "codex")

    private var transport: LineTransport?
    private var launching: Task<Void, Error>?
    private var nextId = 0
    private var pending: [Int: CheckedContinuation<JSONValue, Error>] = [:]
    private var unexpectedExits = 0
    private var shuttingDown = false
    private var threads: [String: ThreadConfig] = [:]
    private var lastUsage: CodexRPC.Usage?
    private var active: ActiveTurn?

    private struct ActiveTurn {
        let token = UUID()
        let threadId: String
        var turnId: String?
        var text = ""
        var gotText = false
        var thinkingSent = false
        var blockedItem: String?
        var failure: CodexRPC.TurnFailure?
        let continuation: AsyncThrowingStream<AnswerEvent, Error>.Continuation
    }

    init(codexPath: @escaping @Sendable () -> String,
         workspace: URL = AppPaths.interviewWorkspace,
         codexHome: URL = AppPaths.codexHome,
         stderrLog: URL? = AppPaths.interview.appendingPathComponent("codex.log"),
         timing: Timing = Timing(),
         locate: @escaping Locator = { CodexLocator.locate(configured: $0) },
         makeTransport: TransportFactory? = nil) {
        (notices, noticeSink) = AsyncStream.makeStream(of: EngineNotice.self, bufferingPolicy: .bufferingNewest(16))
        self.codexPath = codexPath
        self.workspace = workspace
        self.codexHome = codexHome
        self.timing = timing
        self.locate = locate
        self.makeTransport = makeTransport ?? { codex, args, cwd, env in
            try ProcessLineTransport(executable: codex, arguments: args, cwd: cwd, environment: env, stderrLog: stderrLog)
        }
    }

    // MARK: AnswerEngine

    func status() async -> EngineStatus {
        switch locate(codexPath()) {
        case .failure(.notInstalled): return .notInstalled
        case .failure(.tooOld(let v)): return .tooOld(version: v)
        case .success: break
        }
        do {
            try await ensureStarted()
            switch CodexRPC.account(from: try await request("account/read", .object([:]))) {
            case .signedOut: return .signedOut
            case .chatgpt(let email, let plan): return .ready(email: email, plan: plan)
            case .other(let type): return .ready(email: nil, plan: type)
            }
        } catch let e as EngineError {
            switch e {
            case .notInstalled: return .notInstalled
            case .tooOld(let v): return .tooOld(version: v)
            default: return .failed(e.localizedDescription)
            }
        } catch {
            return .failed(error.localizedDescription)
        }
    }

    func models() async throws -> [CodexRPC.Model] {
        try await ensureStarted()
        return CodexRPC.models(from: try await request("model/list", CodexRPC.modelListParams))
    }

    func usage() async throws -> CodexRPC.Usage {
        try await ensureStarted()
        let u = CodexRPC.usage(fromRead: try await request("account/rateLimits/read", .object([:])))
        lastUsage = u
        return u
    }

    func startThread(_ cfg: ThreadConfig) async throws -> String {
        try await ensureStarted()
        try prepareWorkspace()
        let result = try await request("thread/start", CodexRPC.threadStartParams(
            model: cfg.model, cwd: workspace.path, baseInstructions: cfg.baseInstructions))
        guard let id = result["thread"]?["id"]?.stringValue else { throw EngineError.rpc("thread/start returned no id") }
        threads[id] = cfg
        return id
    }

    func resumeThread(id: String, _ cfg: ThreadConfig) async throws {
        try await ensureStarted()
        try prepareWorkspace()
        _ = try await request("thread/resume", CodexRPC.threadResumeParams(
            threadId: id, model: cfg.model, cwd: workspace.path, baseInstructions: cfg.baseInstructions))
        threads[id] = cfg
    }

    nonisolated func send(threadId: String, input: [CodexRPC.Input], effort: String,
                          model: String? = nil) -> AsyncThrowingStream<AnswerEvent, Error> {
        AsyncThrowingStream { continuation in
            Task { await self.beginTurn(threadId: threadId, input: input, effort: effort, model: model,
                                        continuation: continuation) }
        }
    }

    func interrupt(threadId: String) async {
        guard var a = active, a.threadId == threadId else { return }
        // turn/start may still be in flight; give it a moment to report the turn id.
        let deadline = Date().addingTimeInterval(timing.interruptGrace)
        while a.turnId == nil, Date() < deadline {
            try? await Task.sleep(nanoseconds: 50_000_000)
            guard let now = active, now.token == a.token else { return }
            a = now
        }
        guard let turnId = a.turnId else { return }
        _ = try? await request("turn/interrupt", CodexRPC.turnInterruptParams(threadId: threadId, turnId: turnId))
    }

    func archiveThread(id: String) async {
        threads[id] = nil
        guard (try? await ensureStarted()) != nil else { return }
        _ = try? await request("thread/archive", CodexRPC.threadArchiveParams(threadId: id))
    }

    func startLogin() async throws -> LoginTicket {
        try await ensureStarted()
        let r = try await request("account/login/start", CodexRPC.loginStartParams)
        guard let id = r["loginId"]?.stringValue, let s = r["authUrl"]?.stringValue, let url = URL(string: s) else {
            throw EngineError.rpc("Sign-in is not available from this Codex version.")
        }
        return LoginTicket(loginId: id, authURL: url)
    }

    /// Signs out LocalCaption's own Codex home (`interview/codex-home`) only — the user's Codex CLI
    /// and editor sign-ins live elsewhere and are untouched.
    func logout() async throws {
        try await ensureStarted()
        _ = try await request("account/logout", CodexRPC.logoutParams)
    }

    func cancelLogin(_ ticket: LoginTicket) async {
        _ = try? await request("account/login/cancel", CodexRPC.loginCancelParams(loginId: ticket.loginId))
    }

    func shutdown() async {
        shuttingDown = true
        transport?.terminate()
        transport = nil
        failAll(.crashed)
    }

    // MARK: Process lifecycle

    private func ensureStarted() async throws {
        if transport != nil { return }
        if let launching { return try await launching.value }
        let task = Task { try await self.launch() }
        launching = task
        defer { launching = nil }
        try await task.value
    }

    private func launch() async throws {
        if unexpectedExits >= 2 { throw EngineError.crashed }
        let found: CodexLocator.Found
        switch locate(codexPath()) {
        case .success(let f): found = f
        case .failure(.notInstalled): throw EngineError.notInstalled
        case .failure(.tooOld(let v)): throw EngineError.tooOld(v)
        }
        try FileManager.default.createDirectory(at: codexHome, withIntermediateDirectories: true)
        try prepareWorkspace()
        let t = try makeTransport(found.url, CodexRPC.launchArguments, workspace,
                                  CodexLocator.environment(codexURL: found.url, codexHome: codexHome))
        transport = t
        shuttingDown = false
        Task { [weak self] in
            for await line in t.lines { await self?.handle(line: line) }
            await self?.transportEnded(t)
        }
        do {
            _ = try await request("initialize", CodexRPC.initializeParams(
                name: "localcaption", title: "LocalCaption",
                version: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"))
            try t.send(try CodexRPC.notification(method: "initialized").line())
        } catch {
            t.terminate(); transport = nil
            throw error
        }
        // After a restart, re-apply the lockdown to threads this run already knows.
        for (id, cfg) in threads {
            _ = try? await request("thread/resume", CodexRPC.threadResumeParams(
                threadId: id, model: cfg.model, cwd: workspace.path, baseInstructions: cfg.baseInstructions))
        }
    }

    private func transportEnded(_ t: LineTransport) {
        guard t === transport else { return }
        transport = nil
        if !shuttingDown {
            unexpectedExits += 1
            log.error("codex app-server exited unexpectedly (\(self.unexpectedExits))")
        }
        failAll(.crashed)
    }

    private func failAll(_ error: EngineError) {
        let waiting = pending
        pending = [:]
        for (_, c) in waiting { c.resume(throwing: error) }
        if active != nil { finishActive(.failed(error, partial: active?.text ?? "")) }
    }

    /// The workspace is Codex's `cwd` and must stay empty (SPEC-12 §Lockdown). App-owned, so
    /// anything found in it is removed.
    private func prepareWorkspace() throws {
        let fm = FileManager.default
        try fm.createDirectory(at: workspace, withIntermediateDirectories: true)
        for item in (try? fm.contentsOfDirectory(at: workspace, includingPropertiesForKeys: nil)) ?? [] {
            log.error("workspace was not empty: removing \(item.lastPathComponent, privacy: .public)")
            try fm.removeItem(at: item)
        }
    }

    // MARK: JSON-RPC

    private func request(_ method: String, _ params: JSONValue) async throws -> JSONValue {
        guard let transport else { throw EngineError.crashed }
        nextId += 1
        let id = nextId
        let line = try CodexRPC.request(id: id, method: method, params: params).line()
        let timeout = timing.request
        return try await withCheckedThrowingContinuation { (c: CheckedContinuation<JSONValue, Error>) in
            Task {
                await self.register(id: id, continuation: c, line: line, transport: transport)
                try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                await self.timeOut(id: id, method: method)
            }
        }
    }

    private func register(id: Int, continuation: CheckedContinuation<JSONValue, Error>, line: String,
                          transport: LineTransport) {
        pending[id] = continuation
        do { try transport.send(line) } catch {
            pending[id] = nil
            continuation.resume(throwing: EngineError.crashed)
        }
    }

    private func timeOut(id: Int, method: String) {
        pending.removeValue(forKey: id)?.resume(throwing: EngineError.rpc("\(method) timed out"))
    }

    private func handle(line: String) {
        switch CodexRPC.decode(line: line) {
        case .response(let id, let result)?:
            pending.removeValue(forKey: id)?.resume(returning: result)
        case .error(let id, _, let message)?:
            pending.removeValue(forKey: id)?.resume(throwing: EngineError.rpc(message))
        case .serverRequest(let id, let method, _)?:
            // Approvals are `never`, so none should arrive. Decline and record the breach.
            log.error("lockdown breach: server request \(method, privacy: .public)")
            if let t = transport, let reply = try? CodexRPC.declineResponse(id: id, method: method).line() {
                try? t.send(reply)
            }
        case .notification(let method, let params)?:
            handle(event: CodexRPC.event(method: method, params: params))
        case nil:
            break
        }
    }

    // MARK: Turns

    private func beginTurn(threadId: String, input: [CodexRPC.Input], effort: String, model: String?,
                           continuation: AsyncThrowingStream<AnswerEvent, Error>.Continuation) async {
        do { try await ensureStarted() } catch {
            continuation.yield(.failed(engineError(error), partial: "")); continuation.finish(); return
        }
        guard active == nil else {
            continuation.yield(.failed(.busy, partial: "")); continuation.finish(); return
        }
        let turn = ActiveTurn(threadId: threadId, continuation: continuation)
        active = turn
        startWatchdog(token: turn.token)
        do {
            let r = try await request("turn/start", CodexRPC.turnStartParams(threadId: threadId, input: input, effort: effort, model: model))
            guard var a = active, a.token == turn.token else { return }
            if let id = r["turn"]?["id"]?.stringValue {
                if a.turnId == nil { a.turnId = id; active = a }
                continuation.yield(.started(turnId: id))
            }
        } catch {
            guard active?.token == turn.token else { return }
            finishActive(.failed(engineError(error), partial: ""))
        }
    }

    private func startWatchdog(token: UUID) {
        let t = timing
        Task {
            try? await Task.sleep(nanoseconds: UInt64(t.slowAfter * 1_000_000_000))
            await self.watchdogSlow(token: token)
            try? await Task.sleep(nanoseconds: UInt64(max(0, t.giveUpAfter - t.slowAfter) * 1_000_000_000))
            await self.watchdogGiveUp(token: token)
        }
    }

    private func watchdogSlow(token: UUID) {
        guard let a = active, a.token == token, !a.gotText else { return }
        a.continuation.yield(.slow)
    }

    private func watchdogGiveUp(token: UUID) async {
        guard let a = active, a.token == token else { return }
        await interrupt(threadId: a.threadId)
        guard let still = active, still.token == token else { return }
        finishActive(.failed(.timeout, partial: still.text))
    }

    private func isActive(_ threadId: String, _ turnId: String) -> Bool {
        guard let a = active, a.threadId == threadId else { return false }
        return a.turnId == nil || turnId.isEmpty || a.turnId == turnId
    }

    private func handle(event: CodexRPC.Event) {
        switch event {
        case .rateLimitsUpdated(let u):
            lastUsage = u
            noticeSink.yield(.usage(u))
        case .loginCompleted(_, let success, let error):
            noticeSink.yield(.loginCompleted(success: success, error: error))
        case .accountUpdated:
            noticeSink.yield(.accountChanged)
        case .turnStarted(let t, let u):
            if var a = active, a.threadId == t, a.turnId == nil { a.turnId = u; active = a }
        case .itemStarted(let t, let u, let type):
            guard isActive(t, u), var a = active else { return }
            if !CodexRPC.allowedItemTypes.contains(type) {
                // The tool-call guard (SPEC-12 §Lockdown): stop the turn the moment a tool appears.
                log.error("lockdown breach: item \(type, privacy: .public) — interrupting")
                if a.blockedItem == nil {
                    a.blockedItem = type; active = a
                    Task { await self.interrupt(threadId: t) }
                }
            } else if type == "reasoning", !a.thinkingSent, !a.gotText {
                a.thinkingSent = true; active = a
                a.continuation.yield(.thinking)
            }
        case .agentDelta(let t, let u, let delta):
            guard isActive(t, u), var a = active, a.blockedItem == nil else { return }
            a.text += delta; a.gotText = true; active = a
            a.continuation.yield(.delta(delta))
        case .agentMessageCompleted(let t, let u, let text):
            guard isActive(t, u), var a = active, !text.isEmpty else { return }
            a.text = text; active = a
        case .error(let t, let u, let failure, let willRetry):
            guard !willRetry, isActive(t, u), var a = active else { return }
            a.failure = failure; active = a
        case .turnCompleted(let t, let u, let status, let failure):
            guard isActive(t, u), let a = active else { return }
            if let blocked = a.blockedItem {
                finishActive(.failed(.blockedTool(blocked), partial: ""))
            } else if status == "completed" {
                finishActive(.completed(a.text))
            } else if status == "interrupted" {
                finishActive(.interrupted(partial: a.text))
            } else {
                finishActive(.failed(engineError(failure ?? a.failure), partial: a.text))
            }
        case .other:
            break
        }
    }

    private func finishActive(_ event: AnswerEvent) {
        guard let a = active else { return }
        active = nil
        a.continuation.yield(event)
        a.continuation.finish()
    }

    private func engineError(_ failure: CodexRPC.TurnFailure?) -> EngineError {
        guard let failure else { return .other("The turn failed.") }
        switch failure.kind {
        case .usageLimit:
            return .usageLimit(resetsAt: lastUsage?.lowest?.resetsAt.map { Date(timeIntervalSince1970: TimeInterval($0)) })
        case .signedOut: return .signedOut
        case .network: return .network(failure.message)
        case .contextFull, .other: return .other(failure.message)
        }
    }

    private func engineError(_ error: Error) -> EngineError {
        (error as? EngineError) ?? .other(error.localizedDescription)
    }
}
