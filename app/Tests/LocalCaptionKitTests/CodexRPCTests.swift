import XCTest
@testable import LocalCaptionKit

/// `testdata/codex/` — the Codex app-server contract (SPEC-12). Events and responses are mostly
/// real codex-cli 0.159.3 output, so a protocol change in a future Codex shows up here.
final class CodexRPCTests: XCTestCase {
    private static let dir = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent()
        .deletingLastPathComponent().deletingLastPathComponent()
        .appendingPathComponent("testdata/codex", isDirectory: true)

    private func load(_ name: String) throws -> JSONValue {
        try JSONDecoder().decode(JSONValue.self, from: Data(contentsOf: Self.dir.appendingPathComponent(name)))
    }

    private func cases(_ name: String) throws -> [JSONValue] {
        try XCTUnwrap(load(name)["cases"]?.arrayValue, "\(name): cases")
    }

    // MARK: Requests

    func testRequestBuilders() throws {
        for c in try cases("requests.json") {
            let a = c["args"] ?? .object([:])
            let s = { (k: String) in a[k]?.stringValue ?? "" }
            let built: JSONValue
            switch c["build"]?.stringValue {
            case "initialize": built = CodexRPC.initializeParams(name: s("name"), title: s("title"), version: s("version"))
            case "thread_start":
                built = CodexRPC.threadStartParams(model: s("model"), cwd: s("cwd"), baseInstructions: s("base_instructions"))
            case "thread_resume":
                built = CodexRPC.threadResumeParams(threadId: s("thread_id"), model: s("model"), cwd: s("cwd"),
                                                    baseInstructions: s("base_instructions"))
            case "turn_start":
                let input: [CodexRPC.Input] = (a["input"]?.arrayValue ?? []).map {
                    $0["type"]?.stringValue == "localImage" ? .localImage(path: $0["path"]?.stringValue ?? "")
                                                           : .text($0["text"]?.stringValue ?? "")
                }
                built = CodexRPC.turnStartParams(threadId: s("thread_id"), input: input, effort: s("effort"))
            case "turn_interrupt": built = CodexRPC.turnInterruptParams(threadId: s("thread_id"), turnId: s("turn_id"))
            case "thread_archive": built = CodexRPC.threadArchiveParams(threadId: s("thread_id"))
            case "model_list": built = CodexRPC.modelListParams
            case "login_start": built = CodexRPC.loginStartParams
            case "login_cancel": built = CodexRPC.loginCancelParams(loginId: s("login_id"))
            case "decline": built = CodexRPC.declineResponse(id: a["id"] ?? .null, method: s("method"))
            default: XCTFail("unknown builder \(String(describing: c["build"]))"); continue
            }
            XCTAssertEqual(built, c["expect"], "requests.json: \(c["build"]?.stringValue ?? "?")")
        }
        let args = try XCTUnwrap(load("requests.json")["launch_arguments"]?.arrayValue).compactMap(\.stringValue)
        XCTAssertEqual(CodexRPC.launchArguments, args, "launch arguments (S0.8 lockdown)")
    }

    func testRequestLineIsSingleLineJSON() throws {
        let line = try CodexRPC.request(id: 3, method: "turn/start",
                                        params: CodexRPC.turnStartParams(threadId: "t", input: [.text("a\nb")], effort: "low")).line()
        XCTAssertFalse(line.contains("\n"))
        XCTAssertEqual(CodexRPC.decode(line: line).map { if case .serverRequest = $0 { return true }; return false }, true,
                       "an outgoing request has id + method")
    }

    // MARK: Decode

    func testDecodeVectors() throws {
        for c in try cases("decode.json") {
            let line = c["line"]?.stringValue ?? ""
            let decoded = CodexRPC.decode(line: line)
            guard let expect = c["expect"], expect != .null else {
                XCTAssertNil(decoded, "decode.json: \(line)"); continue
            }
            switch (expect["class"]?.stringValue, decoded) {
            case ("response", .response(let id, _)?): XCTAssertEqual(id, expect["id"]?.intValue)
            case ("error", .error(let id, let code, let message)?):
                XCTAssertEqual(id, expect["id"]?.intValue); XCTAssertEqual(code, expect["code"]?.intValue)
                XCTAssertEqual(message, expect["message"]?.stringValue)
            case ("server_request", .serverRequest(_, let method, _)?): XCTAssertEqual(method, expect["method"]?.stringValue)
            case ("notification", .notification(let method, _)?): XCTAssertEqual(method, expect["method"]?.stringValue)
            default: XCTFail("decode.json: \(line) → \(String(describing: decoded))")
            }
        }
    }

    // MARK: Events

    private func json(_ f: CodexRPC.TurnFailure?) -> JSONValue {
        guard let f else { return .null }
        let kind: String
        switch f.kind {
        case .usageLimit: kind = "usage_limit"
        case .signedOut: kind = "signed_out"
        case .network: kind = "network"
        case .contextFull: kind = "context_full"
        case .other: kind = "other"
        }
        return .object(["message": .string(f.message), "kind": .string(kind)])
    }

    private func json(_ u: CodexRPC.Usage) -> JSONValue {
        .object([
            "plan_type": u.planType.map(JSONValue.string) ?? .null,
            "windows": .array(u.windows.map {
                .object(["minutes": $0.minutes.map { .int(Int64($0)) } ?? .null,
                         "used_percent": .int(Int64($0.usedPercent)),
                         "resets_at": $0.resetsAt.map { .int(Int64($0)) } ?? .null])
            }),
        ])
    }

    private func json(_ e: CodexRPC.Event) -> JSONValue {
        switch e {
        case .turnStarted(let t, let u): return ["event": "turn_started", "thread_id": .string(t), "turn_id": .string(u)]
        case .itemStarted(let t, let u, let type):
            return ["event": "item_started", "thread_id": .string(t), "turn_id": .string(u), "type": .string(type)]
        case .agentDelta(let t, let u, let d):
            return ["event": "agent_delta", "thread_id": .string(t), "turn_id": .string(u), "delta": .string(d)]
        case .agentMessageCompleted(let t, let u, let text):
            return ["event": "agent_message_completed", "thread_id": .string(t), "turn_id": .string(u), "text": .string(text)]
        case .turnCompleted(let t, let u, let status, let failure):
            return ["event": "turn_completed", "thread_id": .string(t), "turn_id": .string(u),
                    "status": .string(status), "failure": json(failure)]
        case .error(let t, let u, let failure, let retry):
            return ["event": "error", "thread_id": .string(t), "turn_id": .string(u),
                    "will_retry": .bool(retry), "failure": json(failure)]
        case .rateLimitsUpdated(let usage): return ["event": "rate_limits_updated", "usage": json(usage)]
        case .loginCompleted(let id, let ok, let err):
            return ["event": "login_completed", "login_id": id.map(JSONValue.string) ?? .null,
                    "success": .bool(ok), "error": err.map(JSONValue.string) ?? .null]
        case .accountUpdated: return ["event": "account_updated"]
        case .other(let m): return ["event": "other", "method": .string(m)]
        }
    }

    func testEventVectors() throws {
        for c in try cases("events.json") {
            let name = c["name"]?.stringValue ?? "?"
            let message = try XCTUnwrap(c["message"])
            // Through the same path the engine uses: serialize to a line, decode, map.
            guard case .notification(let method, let params)? = CodexRPC.decode(line: try message.line()) else {
                XCTFail("\(name): not decoded as a notification"); continue
            }
            XCTAssertEqual(json(CodexRPC.event(method: method, params: params)), c["expect"], name)
        }
    }

    func testToolGuardAllowsChatItemsAndWebSearchOnly() {
        // Web search is allowed since 2026-10-02 (discovery-jd); commands, edits, MCP stay blocked.
        XCTAssertEqual(CodexRPC.allowedItemTypes, ["userMessage", "agentMessage", "reasoning", "webSearch"])
        for blocked in ["commandExecution", "fileChange", "mcpToolCall", "dynamicToolCall", "imageGeneration"] {
            XCTAssertFalse(CodexRPC.allowedItemTypes.contains(blocked), blocked)
        }
    }

    // MARK: Responses

    func testResponseVectors() throws {
        for c in try cases("responses.json") {
            let result = c["result"] ?? .null
            switch c["kind"]?.stringValue {
            case "account":
                let expect = c["expect"] ?? .null
                switch CodexRPC.account(from: result) {
                case .signedOut: XCTAssertEqual(expect["account"], "signed_out")
                case .chatgpt(let email, let plan):
                    XCTAssertEqual(expect["account"], "chatgpt")
                    XCTAssertEqual(email, expect["email"]?.stringValue); XCTAssertEqual(plan, expect["plan"]?.stringValue)
                case .other(let type): XCTFail("unexpected account type \(type)")
                }
            case "usage":
                XCTAssertEqual(json(CodexRPC.usage(fromRead: result)), c["expect"], "usage")
            case "models":
                let got: JSONValue = .array(CodexRPC.models(from: result).map { m in
                    .object(["id": .string(m.id), "display_name": .string(m.displayName), "is_default": .bool(m.isDefault),
                             "default_effort": m.defaultEffort.map(JSONValue.string) ?? .null,
                             "efforts": .array(m.efforts.map(JSONValue.string)), "accepts_images": .bool(m.acceptsImages)])
                })
                XCTAssertEqual(got, c["expect"], "models")
            default: XCTFail("unknown kind")
            }
        }
    }

    // MARK: Version

    func testVersionGate() {
        XCTAssertEqual(CodexRPC.version(fromOutput: "codex-cli 0.159.3\n"), [0, 159, 3])
        XCTAssertEqual(CodexRPC.version(fromOutput: "codex-cli 0.160.0-beta.1"), [0, 160, 0])
        XCTAssertNil(CodexRPC.version(fromOutput: "codex-cli"))
        XCTAssertTrue(CodexRPC.isSupported(version: [0, 159, 3]))
        XCTAssertTrue(CodexRPC.isSupported(version: [1, 0]))
        XCTAssertFalse(CodexRPC.isSupported(version: [0, 158, 9]))
    }

    func testUsageLowestWindow() {
        let u = CodexRPC.Usage(planType: "plus", windows: [.init(minutes: 300, usedPercent: 10, resetsAt: nil),
                                                         .init(minutes: 10080, usedPercent: 85, resetsAt: nil)])
        XCTAssertEqual(u.lowest?.label, "Weekly")
        XCTAssertEqual(u.lowest?.remainingPercent, 15)
    }
}

extension JSONValue: ExpressibleByDictionaryLiteral {
    public init(dictionaryLiteral elements: (String, JSONValue)...) {
        self = .object(Dictionary(uniqueKeysWithValues: elements))
    }
}
