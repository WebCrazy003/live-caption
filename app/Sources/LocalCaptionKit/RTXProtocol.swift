import Foundation

/// The RTX agent's HTTP API (SPEC-18 §RTX agent, `rtx-agent/agent.py`): request builders and
/// response types. Pure — the app's `RTXClient` does the networking.
public enum RTXProtocol {
    public static let bonjourType = "_localcaption._tcp"

    public struct Hello: Codable, Equatable, Sendable {
        public let name: String
        public let version: String
        public let gpu: String
        public let vramTotalMb: Int
        public let paired: Bool
        /// False when the agent runs with `--no-pairing`: every request is accepted. Absent
        /// (older agents) means required.
        public let pairingRequired: Bool?
        public var needsPairing: Bool { pairingRequired ?? true }
        enum CodingKeys: String, CodingKey {
            case name, version, gpu, paired
            case vramTotalMb = "vram_total_mb"
            case pairingRequired = "pairing_required"
        }
    }

    public struct PairReply: Codable, Equatable, Sendable {
        public let token: String
        public let name: String
    }

    public struct Model: Codable, Equatable, Sendable, Identifiable {
        public let id: String
        public let label: String
        public let family: String
        public let sizeMb: Int
        public let vramMb: Int
        public let tested: Bool
        public let downloaded: Bool
        public let loaded: Bool
        enum CodingKeys: String, CodingKey {
            case id, label, family, tested, downloaded, loaded
            case sizeMb = "size_mb"
            case vramMb = "vram_mb"
        }
    }

    public struct Models: Codable, Sendable { public let models: [Model] }

    public struct Status: Codable, Equatable, Sendable {
        public enum State: String, Codable, Sendable {
            case idle, downloading, loading, warming, ready, error
        }
        public let state: State
        public let model: String?
        public let progress: Double
        public let message: String
        public let loaded: Loaded
        public let vramUsedMb: Int?

        public struct Loaded: Codable, Equatable, Sendable {
            public let primary: String?
            public let secondary: String?
        }
        enum CodingKeys: String, CodingKey {
            case state, model, progress, message, loaded
            case vramUsedMb = "vram_used_mb"
        }

        /// Ready with exactly this pair loaded.
        public func isReady(primary: String, secondary: String?) -> Bool {
            state == .ready && loaded.primary == primary && loaded.secondary == secondary
        }

        /// One line for the session screen: "Downloading whisper-large-v3 · 45%".
        public var line: String {
            let name = model ?? ""
            switch state {
            case .idle: return "Models not loaded"
            case .downloading: return "Downloading \(name) · \(Int(progress * 100))%"
            case .loading: return "Loading \(name)…"
            case .warming: return "Warming \(name)…"
            case .ready: return "Ready"
            case .error: return message.isEmpty ? "Couldn't load the models" : message
            }
        }
    }

    public struct Word: Codable, Equatable, Sendable {
        public let word: String
        public let start: Double
        public let end: Double
    }

    public struct Result: Codable, Equatable, Sendable {
        public let text: String
        public let ms: Int
        public let words: [Word]?
    }

    public struct TranscribeReply: Codable, Equatable, Sendable {
        public let id: String
        public let primary: Result?
        public let secondary: Result?
    }

    public struct ErrorReply: Codable, Sendable { public let error: String }

    public enum Role: String, Sendable { case primary, secondary }
    public enum Lane: String, Sendable { case interim, final }

    public static func transcribeQuery(role: Role, lane: Lane, id: Int, words: Bool) -> [URLQueryItem] {
        [URLQueryItem(name: "roles", value: role.rawValue), URLQueryItem(name: "lane", value: lane.rawValue),
         URLQueryItem(name: "id", value: String(id))] + (words ? [URLQueryItem(name: "words", value: "1")] : [])
    }

    /// 16 kHz mono float samples → the agent's body: 16-bit little-endian PCM.
    public static func pcm16(_ samples: [Float]) -> Data {
        var data = Data(count: samples.count * 2)
        data.withUnsafeMutableBytes { raw in
            let out = raw.bindMemory(to: Int16.self)
            for (i, s) in samples.enumerated() {
                out[i] = Int16(max(-32768, min(32767, (s * 32767).rounded()))).littleEndian
            }
        }
        return data
    }
}
