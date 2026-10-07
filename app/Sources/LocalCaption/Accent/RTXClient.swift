import Foundation
import Network
import LocalCaptionKit

/// HTTP client for the RTX agent (SPEC-18 §RTX agent). Thread-safe: the speech engine calls
/// `transcribe` from its decode lanes, the UI calls the rest.
final class RTXClient: Sendable {
    let baseURL: URL
    private let token: String?
    private let session: URLSession

    init(baseURL: URL, token: String?) {
        self.baseURL = baseURL
        self.token = token
        let cfg = URLSessionConfiguration.ephemeral
        cfg.httpMaximumConnectionsPerHost = 4      // interim, final primary, final secondary, status
        cfg.timeoutIntervalForRequest = 10
        cfg.waitsForConnectivity = false
        session = URLSession(configuration: cfg)
    }

    func hello(timeout: TimeInterval = 3) async throws -> RTXProtocol.Hello {
        try await get("hello", timeout: timeout)
    }

    func pair(code: String, name: String) async throws -> RTXProtocol.PairReply {
        try await send("pair", method: "POST", json: ["code": code, "name": name], timeout: 5)
    }

    func unpair() async throws {
        let _: [String: String] = try await send("pair", method: "DELETE", timeout: 5)
    }

    func models() async throws -> [RTXProtocol.Model] {
        let m: RTXProtocol.Models = try await get("models", timeout: 5)
        return m.models
    }

    func status(timeout: TimeInterval = 3) async throws -> RTXProtocol.Status {
        try await get("status", timeout: timeout)
    }

    func load(primary: String, secondary: String?) async throws -> RTXProtocol.Status {
        try await send("load", method: "POST", json: ["primary": primary, "secondary": secondary], timeout: 10)
    }

    func transcribe(_ samples: [Float], role: RTXProtocol.Role, lane: RTXProtocol.Lane, id: Int,
                    words: Bool, timeout: TimeInterval) async throws -> RTXProtocol.Result? {
        var c = URLComponents(url: baseURL.appendingPathComponent("transcribe"), resolvingAgainstBaseURL: false)!
        c.queryItems = RTXProtocol.transcribeQuery(role: role, lane: lane, id: id, words: words)
        var req = request(c.url!, method: "POST", timeout: timeout)
        req.httpBody = RTXProtocol.pcm16(samples)
        req.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        let reply: RTXProtocol.TranscribeReply = try await perform(req)
        return role == .primary ? reply.primary : reply.secondary
    }

    // MARK: plumbing

    private func request(_ url: URL, method: String, timeout: TimeInterval) -> URLRequest {
        var req = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        req.httpMethod = method
        if let token { req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        return req
    }

    private func get<T: Decodable>(_ path: String, timeout: TimeInterval) async throws -> T {
        try await perform(request(baseURL.appendingPathComponent(path), method: "GET", timeout: timeout))
    }

    private func send<T: Decodable>(_ path: String, method: String, json: [String: String?] = [:],
                                    timeout: TimeInterval) async throws -> T {
        var req = request(baseURL.appendingPathComponent(path), method: method, timeout: timeout)
        req.httpBody = try JSONSerialization.data(withJSONObject: json.mapValues { $0 ?? NSNull() as Any })
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        return try await perform(req)
    }

    private func perform<T: Decodable>(_ req: URLRequest) async throws -> T {
        let data: Data, response: URLResponse
        do { (data, response) = try await session.data(for: req) }
        catch let e as URLError where e.code == .cancelled { throw CancellationError() }
        catch { throw RTXError.unreachable(error.localizedDescription) }
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(code) else {
            let message = (try? JSONDecoder().decode(RTXProtocol.ErrorReply.self, from: data))?.error ?? "HTTP \(code)"
            throw code == 401 ? RTXError.notPaired : RTXError.http(code, message)
        }
        do { return try JSONDecoder().decode(T.self, from: data) }
        catch { throw RTXError.http(code, "Unexpected reply from the RTX agent") }
    }
}

enum RTXError: Error, Equatable, LocalizedError {
    case notConfigured
    case unreachable(String)
    case notPaired
    case http(Int, String)

    var errorDescription: String? {
        switch self {
        case .notConfigured: return "No RTX desktop set up. Add one in Settings → Accent mode."
        case .unreachable: return "Can't reach the RTX desktop. Check it's on and on this network."
        case .notPaired: return "This Mac isn't paired with the RTX desktop. Pair it in Settings → Accent mode."
        case .http(409, let m), .http(503, let m): return m.prefix(1).capitalized + m.dropFirst()
        case .http(_, let m): return "The RTX agent said: \(m)"
        }
    }
}

/// The pairing token: a private file (0600) in the app's Application Support folder, beside
/// `config.json` and Codex's own sign-in. Not the Keychain: a development build is self-signed
/// and changes on every rebuild, so the Keychain would ask for the login password each time.
enum RTXToken {
    static var url: URL { AppPaths.root.appendingPathComponent("rtx-token") }

    static func load() -> String? {
        guard let s = try? String(contentsOf: url, encoding: .utf8) else { return nil }
        let token = s.trimmingCharacters(in: .whitespacesAndNewlines)
        return token.isEmpty ? nil : token
    }

    static func save(_ token: String) {
        let fm = FileManager.default
        try? fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        fm.createFile(atPath: url.path, contents: Data(token.utf8), attributes: [.posixPermissions: 0o600])
    }

    static func delete() {
        try? FileManager.default.removeItem(at: url)
    }
}

/// Bonjour discovery of RTX agents on the LAN (`_localcaption._tcp`).
@MainActor
final class RTXBrowser: ObservableObject {
    struct Found: Identifiable, Hashable {
        let name: String
        let endpoint: NWEndpoint
        var id: String { name }
    }

    @Published private(set) var found: [Found] = []
    private var browser: NWBrowser?

    func start() {
        guard browser == nil else { return }
        let b = NWBrowser(for: .bonjour(type: RTXProtocol.bonjourType, domain: nil), using: .tcp)
        b.browseResultsChangedHandler = { [weak self] results, _ in
            let items = results.compactMap { r -> Found? in
                guard case .service(let name, _, _, _) = r.endpoint else { return nil }
                return Found(name: name, endpoint: r.endpoint)
            }.sorted { $0.name < $1.name }
            Task { @MainActor in self?.found = items }
        }
        b.start(queue: .main)
        browser = b
    }

    func stop() {
        browser?.cancel(); browser = nil
    }

    /// Resolve a found agent to `ip:port` by connecting to it once.
    nonisolated static func resolve(_ endpoint: NWEndpoint) async -> String? {
        let conn = NWConnection(to: endpoint, using: .tcp)
        // One serial queue for the state handler and the timeout, so `finish` runs once.
        let queue = DispatchQueue(label: "rtx.resolve")
        final class Once: @unchecked Sendable { var done = false }   // touched only on `queue`
        let once = Once()
        return await withCheckedContinuation { cont in
            let finish: @Sendable (String?) -> Void = { value in
                guard !once.done else { return }
                once.done = true
                conn.cancel()
                cont.resume(returning: value)
            }
            conn.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    if case .hostPort(let host, let port)? = conn.currentPath?.remoteEndpoint {
                        var h = "\(host)"
                        if let pct = h.firstIndex(of: "%") { h = String(h[..<pct]) }   // drop IPv6 zone
                        finish(h.contains(":") ? "[\(h)]:\(port)" : "\(h):\(port)")
                    } else { finish(nil) }
                case .failed, .cancelled: finish(nil)
                default: break
                }
            }
            conn.start(queue: queue)
            queue.asyncAfter(deadline: .now() + 5) { finish(nil) }
        }
    }
}
