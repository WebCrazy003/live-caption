import Foundation

extension Config {
    /// Accent mode (SPEC-18): speech models on the user's RTX desktop, Codex correction live and
    /// at Stop. Merge-default like `interview`: a config without the group gets these defaults.
    /// The pairing token is not here — it is in `rtx-token` beside config.json (0600).
    public struct Accent: Codable, Equatable {
        public static let defaultPort = 8765
        public static let defaultPrimaryModel = "parakeet-tdt-0.6b-v2"
        public static let defaultSecondaryModel = "whisper-large-v3"
        /// Used when `final_model` is empty (SPEC-18 spike, 2026-10-07).
        public static let recommendedFinalModel = "gpt-6.1-sol"

        /// The Standard ↔ Accent switch, remembered.
        public var enabled: Bool
        /// `host[:port]` of the RTX agent.
        public var rtxAddress: String
        /// The paired agent's PC name (display only).
        public var rtxName: String
        public var primaryModel: String
        /// Empty for none.
        public var secondaryModel: String
        public var audioBandpass: Bool
        public var audioLevel: Bool
        public var endpointSilenceMs: Int
        /// Longest utterance before a caption is forced final (Standard uses `asr.max_utterance_s`).
        public var maxUtteranceS: Int
        public var finalPass: Bool
        public var finalModel: String
        public var finalEffort: String
        /// Comma-separated names and terms likely to come up.
        public var vocabulary: String
        public var noticeAccepted: Bool

        public init(enabled: Bool = false, rtxAddress: String = "", rtxName: String = "",
                    primaryModel: String = Accent.defaultPrimaryModel,
                    secondaryModel: String = Accent.defaultSecondaryModel,
                    audioBandpass: Bool = true, audioLevel: Bool = true, endpointSilenceMs: Int = 500, maxUtteranceS: Int = 8,
                    finalPass: Bool = true, finalModel: String = "", finalEffort: String = "high",
                    vocabulary: String = "", noticeAccepted: Bool = false) {
            self.enabled = enabled; self.rtxAddress = rtxAddress; self.rtxName = rtxName
            self.primaryModel = primaryModel; self.secondaryModel = secondaryModel
            self.audioBandpass = audioBandpass; self.audioLevel = audioLevel
            self.endpointSilenceMs = endpointSilenceMs
            self.maxUtteranceS = maxUtteranceS
            self.finalPass = finalPass; self.finalModel = finalModel; self.finalEffort = finalEffort
            self.vocabulary = vocabulary; self.noticeAccepted = noticeAccepted
        }

        public var effectiveFinalModel: String { finalModel.isEmpty ? Accent.recommendedFinalModel : finalModel }
        /// `rtx_address` as a base URL (`http://host:port`), or nil when unset or unparseable.
        public var agentURL: URL? { Accent.agentURL(rtxAddress) }

        public static func agentURL(_ address: String) -> URL? {
            var a = address.trimmingCharacters(in: .whitespaces)
            guard !a.isEmpty else { return nil }
            if !a.contains("://") { a = "http://" + a }
            guard var c = URLComponents(string: a), let host = c.host, !host.isEmpty else { return nil }
            c.scheme = "http"
            if c.port == nil { c.port = defaultPort }
            c.path = ""; c.query = nil; c.fragment = nil
            return c.url
        }

        enum CodingKeys: String, CodingKey {
            case enabled
            case rtxAddress = "rtx_address"
            case rtxName = "rtx_name"
            case primaryModel = "primary_model"
            case secondaryModel = "secondary_model"
            case audioBandpass = "audio_bandpass"
            case audioLevel = "audio_level"
            case endpointSilenceMs = "endpoint_silence_ms"
            case maxUtteranceS = "max_utterance_s"
            case finalPass = "final_pass"
            case finalModel = "final_model"
            case finalEffort = "final_effort"
            case vocabulary
            case noticeAccepted = "notice_accepted"
        }

        public init(from d: Decoder) throws {
            let c = try d.container(keyedBy: CodingKeys.self); let x = Accent()
            func v<T: Decodable>(_ key: CodingKeys, _ fallback: T) throws -> T {
                try c.decodeIfPresent(T.self, forKey: key) ?? fallback
            }
            enabled = try v(.enabled, x.enabled)
            rtxAddress = try v(.rtxAddress, x.rtxAddress)
            rtxName = try v(.rtxName, x.rtxName)
            primaryModel = try v(.primaryModel, x.primaryModel)
            secondaryModel = try v(.secondaryModel, x.secondaryModel)
            audioBandpass = try v(.audioBandpass, x.audioBandpass)
            audioLevel = try v(.audioLevel, x.audioLevel)
            endpointSilenceMs = try v(.endpointSilenceMs, x.endpointSilenceMs)
            maxUtteranceS = try v(.maxUtteranceS, x.maxUtteranceS)
            finalPass = try v(.finalPass, x.finalPass)
            finalModel = try v(.finalModel, x.finalModel)
            finalEffort = try v(.finalEffort, x.finalEffort)
            vocabulary = try v(.vocabulary, x.vocabulary)
            noticeAccepted = try v(.noticeAccepted, x.noticeAccepted)
        }
    }
}
