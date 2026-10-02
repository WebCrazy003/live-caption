import XCTest
@testable import LocalCaption
import LocalCaptionKit

/// `CodexAppServerEngine` against a scripted server (SPEC-12 §Acceptance): no real process.
final class CodexEngineTests: XCTestCase {

    /// A fake `codex app-server`: records what the engine sends and answers via a script.
    final class FakeServer: LineTransport, @unchecked Sendable {
        let lines: AsyncStream<String>
        private let sink: AsyncStream<String>.Continuation
        private let lock = NSLock()
        private var _sent: [JSONValue] = []
        /// (method, params) → lines to emit. Responses must echo the request id: use `reply`.
        var script: (_ method: String, _ id: JSONValue?, _ params: JSONValue) -> [String] = { _, _, _ in [] }

        init() { (lines, sink) = AsyncStream.makeStream(of: String.self) }

        var sent: [JSONValue] { lock.lock(); defer { lock.unlock() }; return _sent }
        func sent(_ method: String) -> [JSONValue] { sent.filter { $0["method"]?.stringValue == method } }

        func send(_ line: String) throws {
            let msg = try JSONDecoder().decode(JSONValue.self, from: Data(line.utf8))
            lock.lock(); _sent.append(msg); lock.unlock()
            guard let method = msg["method"]?.stringValue else { return }
            for out in script(method, msg["id"], msg["params"] ?? .null) { push(out) }
        }

        func push(_ line: String) { sink.yield(line) }
        func push(_ value: JSONValue) { push(try! value.line()) }
        func crash() { sink.finish() }
        func terminate() { sink.finish() }

        static func reply(_ id: JSONValue?, _ result: JSONValue) -> String {
            try! JSONValue.object(["id": id ?? .null, "result": result]).line()
        }
        static func note(_ method: String, _ params: JSONValue) -> String {
            try! JSONValue.object(["method": .string(method), "params": params]).line()
        }
    }

    private var servers: [FakeServer] = []
    private var tmp: URL!

    override func setUpWithError() throws {
        tmp = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("lc-engine-\(UUID().uuidString)")
        servers = []
    }

    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: tmp) }

    /// Default script: a healthy server that answers every turn with "Hello".
    private func healthy(_ s: FakeServer, turnEvents: ((JSONValue?) -> [String])? = nil) {
        s.script = { method, id, params in
            switch method {
            case "initialize": return [FakeServer.reply(id, .object(["userAgent": "fake/0.159.3"]))]
            case "thread/start": return [FakeServer.reply(id, .object(["thread": .object(["id": "thr"])]))]
            case "thread/resume": return [FakeServer.reply(id, .object(["thread": .object(["id": params["threadId"] ?? .null])]))]
            case "account/read":
                return [FakeServer.reply(id, .object(["account": .object(["type": "chatgpt", "email": "me@example.com", "planType": "plus"])]))]
            case "account/rateLimits/read":
                return [FakeServer.reply(id, .object(["rateLimits": .object([
                    "primary": .object(["usedPercent": .int(97), "windowDurationMins": .int(300), "resetsAt": .int(1_800_000_000)]),
                ])]))]
            case "turn/interrupt":
                return [FakeServer.reply(id, .object([:])),
                        FakeServer.note("turn/completed", .object(["threadId": "thr", "turn": .object(["id": "u1", "status": "interrupted"])]))]
            case "turn/start":
                return [FakeServer.reply(id, .object(["turn": .object(["id": "u1", "status": "inProgress"])]))]
                    + (turnEvents?(id) ?? [
                        FakeServer.note("turn/started", .object(["threadId": "thr", "turn": .object(["id": "u1"])])),
                        FakeServer.note("item/started", .object(["threadId": "thr", "turnId": "u1", "item": .object(["type": "userMessage"])])),
                        FakeServer.note("item/started", .object(["threadId": "thr", "turnId": "u1", "item": .object(["type": "agentMessage"])])),
                        FakeServer.note("item/agentMessage/delta", .object(["threadId": "thr", "turnId": "u1", "delta": "Hel"])),
                        FakeServer.note("item/agentMessage/delta", .object(["threadId": "thr", "turnId": "u1", "delta": "lo"])),
                        FakeServer.note("item/completed", .object(["threadId": "thr", "turnId": "u1",
                                                                   "item": .object(["type": "agentMessage", "text": "Hello"])])),
                        FakeServer.note("turn/completed", .object(["threadId": "thr", "turn": .object(["id": "u1", "status": "completed"])])),
                    ])
            default: return [FakeServer.reply(id, .object([:]))]
            }
        }
    }

    private func engine(timing: CodexAppServerEngine.Timing = .init(),
                        locate: CodexAppServerEngine.Locator? = nil,
                        configure: @escaping (FakeServer) -> Void) -> CodexAppServerEngine {
        let found = CodexLocator.Found(url: URL(fileURLWithPath: "/fake/codex"), version: [0, 159, 3])
        return CodexAppServerEngine(
            codexPath: { "" },
            workspace: tmp.appendingPathComponent("workspace"),
            codexHome: tmp.appendingPathComponent("codex-home"),
            stderrLog: nil,
            timing: timing,
            locate: locate ?? { _ in .success(found) },
            makeTransport: { [unowned self] _, args, _, env in
                XCTAssertEqual(args, CodexRPC.launchArguments)
                XCTAssertTrue(env["CODEX_HOME"]?.hasSuffix("codex-home") == true)
                let s = FakeServer()
                configure(s)
                self.servers.append(s)
                return s
            })
    }

    private func collect(_ stream: AsyncThrowingStream<AnswerEvent, Error>) async throws -> [AnswerEvent] {
        var out: [AnswerEvent] = []
        for try await e in stream { out.append(e) }
        return out
    }

    private let cfg = ThreadConfig(model: "gpt-6-luna", baseInstructions: "Coach.")

    // MARK: Tests

    func testThreadStartIsLockedDownAndTurnStreams() async throws {
        let e = engine { self.healthy($0) }
        let thread = try await e.startThread(cfg)
        XCTAssertEqual(thread, "thr")

        let events = try await collect(e.send(threadId: thread, input: [.text("Q?")], effort: "low"))
        XCTAssertEqual(events, [.started(turnId: "u1"), .delta("Hel"), .delta("lo"), .completed("Hello")])

        let s = try XCTUnwrap(servers.first)
        XCTAssertEqual(s.sent.first?["method"], "initialize")
        XCTAssertTrue(s.sent.contains { $0["method"] == "initialized" && $0["id"] == nil })
        let start = try XCTUnwrap(s.sent("thread/start").first?["params"])
        XCTAssertEqual(start["sandbox"], "read-only")
        XCTAssertEqual(start["approvalPolicy"], "never")
        XCTAssertEqual(start["baseInstructions"], "Coach.")
        XCTAssertEqual(start["cwd"]?.stringValue, tmp.appendingPathComponent("workspace").path)
        XCTAssertEqual(s.sent("turn/start").first?["params"]?["effort"], "low", "effort is sent on every turn")
    }

    func testToolItemTripsTheGuard() async throws {
        let e = engine { s in
            self.healthy(s) { _ in [
                FakeServer.note("item/started", .object(["threadId": "thr", "turnId": "u1",
                                                         "item": .object(["type": "commandExecution"])])),
            ] }
        }
        let thread = try await e.startThread(cfg)
        let events = try await collect(e.send(threadId: thread, input: [.text("run ls")], effort: "low"))
        XCTAssertEqual(events.last, .failed(.blockedTool("commandExecution"), partial: ""))
        XCTAssertEqual(servers.first?.sent("turn/interrupt").count, 1, "the guard interrupts the turn")
    }

    func testUsageLimitFailureCarriesResetTime() async throws {
        let e = engine { s in
            self.healthy(s) { _ in [
                FakeServer.note("turn/completed", .object(["threadId": "thr", "turn": .object([
                    "id": "u1", "status": "failed",
                    "error": .object(["message": "limit", "codexErrorInfo": "usageLimitExceeded"]),
                ])])),
            ] }
        }
        let usage = try await e.usage()
        XCTAssertEqual(usage.lowest?.remainingPercent, 3)
        let thread = try await e.startThread(cfg)
        let events = try await collect(e.send(threadId: thread, input: [.text("Q")], effort: "low"))
        XCTAssertEqual(events.last, .failed(.usageLimit(resetsAt: Date(timeIntervalSince1970: 1_800_000_000)), partial: ""))
    }

    func testCrashFailsTheTurnThenRestartsAndResumes() async throws {
        let e = engine { s in self.healthy(s) { _ in [] } }   // turns never complete
        let thread = try await e.startThread(cfg)
        let stream = e.send(threadId: thread, input: [.text("Q")], effort: "low")
        let task = Task { try await self.collect(stream) }
        await waitUntil { self.servers.first?.sent("turn/start").isEmpty == false }
        servers[0].crash()
        let events = try await task.value
        XCTAssertEqual(events.last, .failed(.crashed, partial: ""))

        // Next use restarts once and re-applies the lockdown to the known thread.
        let status = await e.status()
        XCTAssertEqual(status, .ready(email: "me@example.com", plan: "plus"))
        XCTAssertEqual(servers.count, 2)
        XCTAssertEqual(servers[1].sent("thread/resume").first?["params"]?["threadId"], "thr")
        XCTAssertEqual(servers[1].sent("thread/resume").first?["params"]?["sandbox"], "read-only")
    }

    func testServerRequestsAreDeclined() async throws {
        let e = engine { self.healthy($0) }
        _ = try await e.startThread(cfg)
        servers[0].push(#"{"id":"srv-1","method":"item/commandExecution/requestApproval","params":{}}"#)
        await waitUntil { self.servers[0].sent.contains { $0["id"] == "srv-1" } }
        let reply = servers[0].sent.first { $0["id"] == "srv-1" }
        XCTAssertEqual(reply?["result"]?["decision"], "decline")
    }

    func testSecondTurnWhileBusyIsRejected() async throws {
        let e = engine { s in self.healthy(s) { _ in [] } }
        let thread = try await e.startThread(cfg)
        let first = e.send(threadId: thread, input: [.text("A")], effort: "low")
        let firstTask = Task { try await self.collect(first) }
        await waitUntil { self.servers.first?.sent("turn/start").isEmpty == false }
        let second = try await collect(e.send(threadId: thread, input: [.text("B")], effort: "low"))
        XCTAssertEqual(second, [.failed(.busy, partial: "")])
        await e.interrupt(threadId: thread)
        let firstEvents = try await firstTask.value
        XCTAssertEqual(firstEvents.last, .interrupted(partial: ""))
    }

    func testSilentTurnGoesSlowThenTimesOut() async throws {
        var timing = CodexAppServerEngine.Timing()
        timing.slowAfter = 0.1; timing.giveUpAfter = 0.3; timing.interruptGrace = 0.1
        let e = engine(timing: timing) { s in
            self.healthy(s) { _ in [] }
            let base = s.script
            s.script = { m, id, p in m == "turn/interrupt" ? [FakeServer.reply(id, .object([:]))] : base(m, id, p) }
        }
        let thread = try await e.startThread(cfg)
        let events = try await collect(e.send(threadId: thread, input: [.text("Q")], effort: "low"))
        XCTAssertEqual(events, [.started(turnId: "u1"), .slow, .failed(.timeout, partial: "")])
    }

    func testSignOutSendsLogoutAndReportsSignedOut() async throws {
        let signedIn = LockedValue(true)
        let e = engine { s in
            self.healthy(s)
            let base = s.script
            s.script = { m, id, p in
                switch m {
                case "account/logout":
                    signedIn.set(false)
                    return [FakeServer.reply(id, .object([:]))]
                case "account/read" where !signedIn.get():
                    return [FakeServer.reply(id, .object(["account": .null, "requiresOpenaiAuth": true]))]
                default:
                    return base(m, id, p)
                }
            }
        }
        let before = await e.status()
        XCTAssertEqual(before, .ready(email: "me@example.com", plan: "plus"))
        try await e.logout()
        let after = await e.status()
        XCTAssertEqual(after, .signedOut)
        let logout = try XCTUnwrap(servers.first?.sent("account/logout").first)
        XCTAssertEqual(logout["params"], .null, "account/logout takes params: null")
    }

    func testMissingCodexIsReportedNotHung() async throws {
        let e = engine(locate: { _ in .failure(.notInstalled) }) { _ in XCTFail("must not spawn") }
        let status = await e.status()
        XCTAssertEqual(status, .notInstalled)
        let events = try await collect(e.send(threadId: "x", input: [.text("Q")], effort: "low"))
        XCTAssertEqual(events, [.failed(.notInstalled, partial: "")])
    }

    func testWorkspaceIsEmptiedBeforeAThreadStarts() async throws {
        let ws = tmp.appendingPathComponent("workspace")
        try FileManager.default.createDirectory(at: ws, withIntermediateDirectories: true)
        try Data("x".utf8).write(to: ws.appendingPathComponent("stray.txt"))
        let e = engine { self.healthy($0) }
        _ = try await e.startThread(cfg)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: ws.path), [])
    }
}

private func == (lhs: JSONValue?, rhs: String) -> Bool { lhs == .string(rhs) }
