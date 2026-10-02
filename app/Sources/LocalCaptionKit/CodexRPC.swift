import Foundation

/// The pure half of the Codex engine (SPEC-12 §Kit vs app split): JSON-RPC message builders,
/// incoming-message decoding, event mapping, usage/model/account parsing and error
/// classification for `codex app-server` (pinned: 0.159.3). No process, no I/O — the app's
/// `CodexAppServerEngine` owns those. Shared with Windows through `testdata/codex/`.
public enum CodexRPC {
    public static let minimumVersion = "0.159.3"

    // MARK: Lockdown (SPEC-12 §Lockdown, verified in S0.8)

    public static let disabledFeatures = [
        "shell_tool", "unified_exec", "apps", "browser_use", "browser_use_external", "computer_use",
        "image_generation", "multi_agent", "plugins", "tool_suggest", "skill_search", "sleep_tool",
        "in_app_browser", "goals", "hooks",
    ]
    /// Web search stays on (owner decision 2026-10-02): the discovery-jd skill researches the company
    /// with source URLs. Files, commands, edits, MCP and AGENTS.md stay off.
    public static let configOverrides = [#"web_search="live""#, "mcp_servers={}", "project_doc_max_bytes=0"]

    /// Arguments after the `codex` executable.
    public static var launchArguments: [String] {
        ["app-server"] + disabledFeatures.flatMap { ["--disable", $0] } + configOverrides.flatMap { ["-c", $0] }
    }

    /// The only item types a locked-down turn may produce. Anything else trips the tool-call guard.
    public static let allowedItemTypes: Set<String> = ["userMessage", "agentMessage", "reasoning", "webSearch"]

    // MARK: Outgoing

    public enum Input: Equatable, Sendable {
        case text(String)
        case localImage(path: String)
    }

    public static func request(id: Int, method: String, params: JSONValue) -> JSONValue {
        .object(["jsonrpc": "2.0", "id": .int(Int64(id)), "method": .string(method), "params": params])
    }

    public static func notification(method: String, params: JSONValue = .object([:])) -> JSONValue {
        .object(["jsonrpc": "2.0", "method": .string(method), "params": params])
    }

    public static func initializeParams(name: String, title: String, version: String) -> JSONValue {
        .object([
            "clientInfo": .object(["name": .string(name), "title": .string(title), "version": .string(version)]),
            "capabilities": .object(["experimentalApi": true]),
        ])
    }

    /// `thread/start`: the locked-down chat thread (SPEC-12 §Threads & turns).
    public static func threadStartParams(model: String, cwd: String, baseInstructions: String,
                                         ephemeral: Bool = false) -> JSONValue {
        .object([
            "model": .string(model),
            "cwd": .string(cwd),
            "approvalPolicy": "never",
            "sandbox": "read-only",
            "baseInstructions": .string(baseInstructions),
            "ephemeral": .bool(ephemeral),
            "serviceName": "localcaption",
        ])
    }

    /// `thread/resume` re-applies the same lockdown to a stored thread.
    public static func threadResumeParams(threadId: String, model: String, cwd: String,
                                          baseInstructions: String) -> JSONValue {
        .object([
            "threadId": .string(threadId),
            "model": .string(model),
            "cwd": .string(cwd),
            "approvalPolicy": "never",
            "sandbox": "read-only",
            "baseInstructions": .string(baseInstructions),
        ])
    }

    /// `turn/start`. `effort` goes on every turn: Codex persists a turn's effort to later turns.
    /// `model`, when given, switches the thread's model from this turn on (the picker changed).
    public static func turnStartParams(threadId: String, input: [Input], effort: String, model: String? = nil) -> JSONValue {
        var params: [String: JSONValue] = [
            "threadId": .string(threadId),
            "input": .array(input.map {
                switch $0 {
                case .text(let t): return .object(["type": "text", "text": .string(t)])
                case .localImage(let p): return .object(["type": "localImage", "path": .string(p)])
                }
            }),
            "effort": .string(effort),
        ]
        if let model { params["model"] = .string(model) }
        return .object(params)
    }

    public static func turnInterruptParams(threadId: String, turnId: String) -> JSONValue {
        .object(["threadId": .string(threadId), "turnId": .string(turnId)])
    }

    public static func threadArchiveParams(threadId: String) -> JSONValue {
        .object(["threadId": .string(threadId)])
    }

    public static let modelListParams: JSONValue = .object(["includeHidden": false])
    public static let loginStartParams: JSONValue = .object(["type": "chatgpt"])
    public static func loginCancelParams(loginId: String) -> JSONValue { .object(["loginId": .string(loginId)]) }
    /// `account/logout` takes `params: null` (0.159.3 schema).
    public static let logoutParams: JSONValue = .null

    /// The answer to a server→client request: always "no", in the shape each method expects
    /// (0.159.3 schema). With approvals `never` none should arrive; one that does is a lockdown
    /// breach the engine logs. Unknown methods get a JSON-RPC error.
    public static func declineResponse(id: JSONValue, method: String) -> JSONValue {
        let result: JSONValue
        switch method {
        case "item/commandExecution/requestApproval", "item/fileChange/requestApproval":
            result = .object(["decision": "decline"])
        case "applyPatchApproval", "execCommandApproval":
            result = .object(["decision": "abort"])
        case "item/permissions/requestApproval":
            result = .object(["permissions": .object([:])])
        case "item/tool/requestUserInput":
            result = .object(["answers": .object([:])])
        case "item/tool/call":
            result = .object(["contentItems": .array([]), "success": false])
        case "mcpServer/elicitation/request":
            result = .object(["action": "decline"])
        default:
            return .object(["jsonrpc": "2.0", "id": id,
                            "error": .object(["code": .int(-32601), "message": "Not supported by LocalCaption"])])
        }
        return .object(["jsonrpc": "2.0", "id": id, "result": result])
    }

    // MARK: Incoming

    public enum Incoming: Equatable, Sendable {
        case response(id: Int, result: JSONValue)
        case error(id: Int, code: Int, message: String)
        case notification(method: String, params: JSONValue)
        case serverRequest(id: JSONValue, method: String, params: JSONValue)
    }

    /// Decode one line of stdout. `nil` for blank or non-JSON lines.
    public static func decode(line: String) -> Incoming? {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty,
              let msg = try? JSONDecoder().decode(JSONValue.self, from: Data(trimmed.utf8)),
              case .object(let o) = msg else { return nil }
        let method = o["method"]?.stringValue
        let params = o["params"] ?? .object([:])
        if let id = o["id"], id != .null {
            if let method { return .serverRequest(id: id, method: method, params: params) }
            guard let n = id.intValue else { return nil }
            if let err = o["error"], err != .null {
                return .error(id: n, code: err["code"]?.intValue ?? 0, message: err["message"]?.stringValue ?? "")
            }
            return .response(id: n, result: o["result"] ?? .null)
        }
        if let method { return .notification(method: method, params: params) }
        return nil
    }

    // MARK: Events

    public struct TurnFailure: Equatable, Sendable {
        public let message: String
        public let kind: FailureKind
        public init(message: String, kind: FailureKind) { self.message = message; self.kind = kind }
    }

    public enum FailureKind: Equatable, Sendable {
        case usageLimit, signedOut, network, contextFull, other
    }

    public enum Event: Equatable, Sendable {
        case turnStarted(threadId: String, turnId: String)
        case itemStarted(threadId: String, turnId: String, type: String)
        case agentDelta(threadId: String, turnId: String, delta: String)
        case agentMessageCompleted(threadId: String, turnId: String, text: String)
        case turnCompleted(threadId: String, turnId: String, status: String, failure: TurnFailure?)
        case error(threadId: String, turnId: String, failure: TurnFailure, willRetry: Bool)
        case rateLimitsUpdated(Usage)
        case loginCompleted(loginId: String?, success: Bool, error: String?)
        case accountUpdated
        case other(method: String)
    }

    public static func event(method: String, params p: JSONValue) -> Event {
        let thread = p["threadId"]?.stringValue ?? ""
        switch method {
        case "turn/started":
            return .turnStarted(threadId: thread, turnId: p["turn"]?["id"]?.stringValue ?? "")
        case "item/started":
            return .itemStarted(threadId: thread, turnId: p["turnId"]?.stringValue ?? "",
                                type: p["item"]?["type"]?.stringValue ?? "")
        case "item/agentMessage/delta":
            return .agentDelta(threadId: thread, turnId: p["turnId"]?.stringValue ?? "",
                               delta: p["delta"]?.stringValue ?? "")
        case "item/completed":
            guard p["item"]?["type"]?.stringValue == "agentMessage" else { return .other(method: method) }
            return .agentMessageCompleted(threadId: thread, turnId: p["turnId"]?.stringValue ?? "",
                                          text: p["item"]?["text"]?.stringValue ?? "")
        case "turn/completed":
            let turn = p["turn"] ?? .null
            return .turnCompleted(threadId: thread, turnId: turn["id"]?.stringValue ?? "",
                                  status: turn["status"]?.stringValue ?? "failed",
                                  failure: turn["error"].flatMap(failure(from:)))
        case "error":
            return .error(threadId: thread, turnId: p["turnId"]?.stringValue ?? "",
                          failure: p["error"].flatMap(failure(from:)) ?? TurnFailure(message: "Unknown error", kind: .other),
                          willRetry: p["willRetry"]?.boolValue ?? false)
        case "account/rateLimits/updated":
            return .rateLimitsUpdated(usage(fromSnapshot: p["rateLimits"] ?? .null, plan: nil))
        case "account/login/completed":
            return .loginCompleted(loginId: p["loginId"]?.stringValue, success: p["success"]?.boolValue ?? false,
                                   error: p["error"]?.stringValue)
        case "account/updated":
            return .accountUpdated
        default:
            return .other(method: method)
        }
    }

    /// `TurnError` → failure kind, from `codexErrorInfo` (string or single-key object).
    public static func failure(from error: JSONValue) -> TurnFailure? {
        guard case .object = error else { return nil }
        let message = error["message"]?.stringValue ?? "Unknown error"
        let info = error["codexErrorInfo"] ?? .null
        let code: String? = info.stringValue ?? info.objectValue?.keys.first
        let kind: FailureKind
        switch code {
        case "usageLimitExceeded", "rateLimitExceeded", "sessionBudgetExceeded": kind = .usageLimit
        case "unauthorized": kind = .signedOut
        case "httpConnectionFailed", "responseStreamConnectionFailed", "responseStreamDisconnected",
             "serverOverloaded": kind = .network
        case "contextWindowExceeded": kind = .contextFull
        default: kind = .other
        }
        return TurnFailure(message: message, kind: kind)
    }

    // MARK: Usage (Plus limits)

    public struct Usage: Equatable, Sendable {
        public struct Window: Equatable, Sendable {
            public let minutes: Int?
            public let usedPercent: Int
            /// Unix seconds, UTC.
            public let resetsAt: Int?
            public var remainingPercent: Int { max(0, 100 - usedPercent) }
            /// Classify by duration, never by the `primary`/`secondary` slot (SPEC-12 §Usage).
            public var label: String {
                switch minutes {
                case 300: return "5-hour"
                case 10080: return "Weekly"
                case let m?: return m % 1440 == 0 ? "\(m / 1440)-day" : "\(m / 60)-hour"
                case nil: return "Usage"
                }
            }
            public init(minutes: Int?, usedPercent: Int, resetsAt: Int?) {
                self.minutes = minutes; self.usedPercent = usedPercent; self.resetsAt = resetsAt
            }
        }
        public let planType: String?
        /// Shortest window first (5-hour before weekly).
        public let windows: [Window]
        public init(planType: String?, windows: [Window]) { self.planType = planType; self.windows = windows }

        /// The tightest window — what a "running low" warning should look at.
        public var lowest: Window? { windows.min { $0.remainingPercent < $1.remainingPercent } }
    }

    /// `account/rateLimits/read` result → usage. Prefers the `codex` bucket (S0), then the
    /// single-bucket `rateLimits` view.
    public static func usage(fromRead result: JSONValue) -> Usage {
        let snapshot = result["rateLimitsByLimitId"]?["codex"] ?? result["rateLimits"] ?? .null
        return usage(fromSnapshot: snapshot, plan: nil)
    }

    public static func usage(fromSnapshot s: JSONValue, plan: String?) -> Usage {
        let windows = ["primary", "secondary"].compactMap { slot -> Usage.Window? in
            guard let w = s[slot], case .object = w, let used = w["usedPercent"]?.intValue else { return nil }
            return Usage.Window(minutes: w["windowDurationMins"]?.intValue, usedPercent: used,
                                resetsAt: w["resetsAt"]?.intValue)
        }.sorted { ($0.minutes ?? .max) < ($1.minutes ?? .max) }
        return Usage(planType: plan ?? s["planType"]?.stringValue, windows: windows)
    }

    // MARK: Account

    public enum Account: Equatable, Sendable {
        case signedOut
        case chatgpt(email: String?, plan: String?)
        case other(type: String)
    }

    /// `account/read` result → account. `account: null` means signed out.
    public static func account(from result: JSONValue) -> Account {
        guard let a = result["account"], case .object = a else { return .signedOut }
        let type = a["type"]?.stringValue ?? ""
        if type == "chatgpt" { return .chatgpt(email: a["email"]?.stringValue, plan: a["planType"]?.stringValue) }
        return .other(type: type)
    }

    // MARK: Models

    public struct Model: Equatable, Sendable, Identifiable, Hashable {
        public let id: String
        public let displayName: String
        public let description: String
        public let isDefault: Bool
        public let defaultEffort: String?
        public let efforts: [String]
        public let acceptsImages: Bool
        public init(id: String, displayName: String, description: String, isDefault: Bool,
                    defaultEffort: String?, efforts: [String], acceptsImages: Bool) {
            self.id = id; self.displayName = displayName; self.description = description
            self.isDefault = isDefault; self.defaultEffort = defaultEffort
            self.efforts = efforts; self.acceptsImages = acceptsImages
        }
    }

    /// `model/list` result → visible models. `inputModalities` defaults to text+image per schema.
    public static func models(from result: JSONValue) -> [Model] {
        (result["data"]?.arrayValue ?? []).compactMap { m in
            guard let id = m["id"]?.stringValue, m["hidden"]?.boolValue != true else { return nil }
            let modalities = m["inputModalities"]?.arrayValue?.compactMap(\.stringValue) ?? ["text", "image"]
            return Model(id: id,
                         displayName: m["displayName"]?.stringValue ?? id,
                         description: m["description"]?.stringValue ?? "",
                         isDefault: m["isDefault"]?.boolValue ?? false,
                         defaultEffort: m["defaultReasoningEffort"]?.stringValue,
                         efforts: m["supportedReasoningEfforts"]?.arrayValue?
                            .compactMap { $0["reasoningEffort"]?.stringValue } ?? [],
                         acceptsImages: modalities.contains("image"))
        }
    }

    // MARK: Version

    /// `codex-cli 0.159.3` → `[0, 159, 3]`.
    public static func version(fromOutput output: String) -> [Int]? {
        guard let token = output.split(whereSeparator: \.isWhitespace).last(where: { $0.first?.isNumber == true })
        else { return nil }
        let core = token.split(whereSeparator: { $0 == "-" || $0 == "+" }).first ?? token[...]   // drop -beta.1 / +build
        let parts = core.split(separator: ".").map { Int($0.prefix { $0.isNumber }) }
        guard !parts.isEmpty, parts.allSatisfy({ $0 != nil }) else { return nil }
        return parts.compactMap { $0 }
    }

    public static func isSupported(version: [Int]) -> Bool {
        let minimum = minimumVersion.split(separator: ".").compactMap { Int($0) }
        for i in 0..<max(version.count, minimum.count) {
            let a = i < version.count ? version[i] : 0, b = i < minimum.count ? minimum[i] : 0
            if a != b { return a > b }
        }
        return true
    }
}

// MARK: - JSON value

/// A small JSON tree for building and reading JSON-RPC messages without a type per method.
public indirect enum JSONValue: Codable, Equatable, Hashable, Sendable,
                                 ExpressibleByStringLiteral, ExpressibleByBooleanLiteral {
    case null
    case bool(Bool)
    case int(Int64)
    case double(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    public init(stringLiteral value: String) { self = .string(value) }
    public init(booleanLiteral value: Bool) { self = .bool(value) }

    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { self = .null }
        else if let b = try? c.decode(Bool.self) { self = .bool(b) }
        else if let i = try? c.decode(Int64.self) { self = .int(i) }
        else if let d = try? c.decode(Double.self) { self = .double(d) }
        else if let s = try? c.decode(String.self) { self = .string(s) }
        else if let a = try? c.decode([JSONValue].self) { self = .array(a) }
        else { self = .object(try c.decode([String: JSONValue].self)) }
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .null: try c.encodeNil()
        case .bool(let b): try c.encode(b)
        case .int(let i): try c.encode(i)
        case .double(let d): try c.encode(d)
        case .string(let s): try c.encode(s)
        case .array(let a): try c.encode(a)
        case .object(let o): try c.encode(o)
        }
    }

    public subscript(key: String) -> JSONValue? {
        if case .object(let o) = self { return o[key] }
        return nil
    }

    public var stringValue: String? { if case .string(let s) = self { return s }; return nil }
    public var boolValue: Bool? { if case .bool(let b) = self { return b }; return nil }
    public var arrayValue: [JSONValue]? { if case .array(let a) = self { return a }; return nil }
    public var objectValue: [String: JSONValue]? { if case .object(let o) = self { return o }; return nil }
    public var intValue: Int? {
        switch self {
        case .int(let i): return Int(i)
        case .double(let d) where d == d.rounded(): return Int(d)
        default: return nil
        }
    }

    /// One line of newline-delimited JSON (no pretty printing, no trailing newline).
    public func line() throws -> String {
        let enc = JSONEncoder()
        enc.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return String(decoding: try enc.encode(self), as: UTF8.self)
    }
}
