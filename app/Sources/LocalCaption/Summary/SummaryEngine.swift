import Foundation
import LocalCaptionKit

/// Produces a `SummaryCard` from a ~100-word transcript block (SPEC-10).
protocol SummaryEngine: Sendable {
    /// Availability check — cheap, short timeout. Used to show the "summary unavailable" note
    /// without blocking captions.
    func probe() async -> Bool
    /// Summarize one block. `background` is earlier transcript passed as context only.
    /// Throws on any failure; the caller degrades gracefully.
    func summarize(chunk: String, background: String, id: Int, maxBullets: Int) async throws -> SummaryCard
}

enum SummaryEngineError: Error { case badStatus(Int), empty }

/// Talks to a local, on-device `mlx_lm.server` (OpenAI-compatible) over **localhost only** —
/// nothing leaves the machine. The native MLX-Swift path can't build its Metal library from a
/// bare `swift build` on this toolchain (see spike/RESULTS.md), so the LLM runs in a sibling
/// process and this client posts to it. Swappable: conform another `SummaryEngine` (native MLX,
/// Apple Foundation Models on macOS 26) without touching the rest of the app.
actor MLXServerEngine: SummaryEngine {
    private let chatURL: URL
    private let modelsURL: URL
    private let model: String
    private let session: URLSession

    init(serverURL: String, model: String) {
        let base = URL(string: serverURL) ?? URL(string: "http://127.0.0.1:8765")!
        self.chatURL = base.appendingPathComponent("v1/chat/completions")
        self.modelsURL = base.appendingPathComponent("v1/models")
        self.model = model
        let cfg = URLSessionConfiguration.ephemeral
        cfg.waitsForConnectivity = false
        self.session = URLSession(configuration: cfg)
    }

    func probe() async -> Bool {
        var req = URLRequest(url: modelsURL)
        req.timeoutInterval = 2
        do {
            let (_, resp) = try await session.data(for: req)
            return (resp as? HTTPURLResponse)?.statusCode == 200
        } catch { return false }
    }

    func summarize(chunk: String, background: String, id: Int, maxBullets: Int) async throws -> SummaryCard {
        let body = ChatRequest(
            model: model,
            messages: [
                .init(role: "system", content: SummaryPrompt.system),
                .init(role: "user", content: SummaryPrompt.user(chunk, background: background)),
            ],
            max_tokens: 120,
            temperature: 0,
            stream: false)

        var req = URLRequest(url: chatURL)
        req.httpMethod = "POST"
        req.timeoutInterval = 30
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try JSONEncoder().encode(body)

        let (data, resp) = try await session.data(for: req)
        guard let http = resp as? HTTPURLResponse else { throw SummaryEngineError.empty }
        guard http.statusCode == 200 else { throw SummaryEngineError.badStatus(http.statusCode) }

        let decoded = try JSONDecoder().decode(ChatResponse.self, from: data)
        let content = decoded.choices.first?.message.content ?? ""
        guard !content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw SummaryEngineError.empty
        }
        // Gate the ToDo on the *new* block only (not background), so a task must come from
        // what was just said — not carried over or invented.
        return SummaryCard.parse(content, id: id, maxBullets: maxBullets, requestContext: chunk)
    }

    // MARK: OpenAI-compatible wire types
    private struct ChatRequest: Encodable {
        let model: String
        let messages: [Message]
        let max_tokens: Int
        let temperature: Double
        let stream: Bool
    }
    private struct Message: Encodable { let role: String; let content: String }
    private struct ChatResponse: Decodable {
        struct Choice: Decodable { struct Msg: Decodable { let content: String }; let message: Msg }
        let choices: [Choice]
    }
}
