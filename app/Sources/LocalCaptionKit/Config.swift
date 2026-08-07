import Foundation

/// Versioned application configuration (SPEC.md §12.2, schema_version 2).
///
/// Design notes:
/// - **Merge-defaults on load:** every field decodes via `decodeIfPresent ?? default`,
///   so a config file missing keys is completed with defaults rather than rejected.
/// - **Corrupt → repair:** an unparseable file is backed up to `config.json.bak-<ts>`
///   and replaced with defaults (see `loadOrRepair`).
/// - **Atomic writes:** `write(to:)` uses `Data.write(options: .atomic)` (temp + rename).
/// - **Mic/BlackHole keys dropped:** the app captures system audio via ScreenCaptureKit
///   (decision B3), so v1's `audio.system_device` key is intentionally absent.
public struct Config: Codable, Equatable {
    public static let currentSchemaVersion = 2

    public var schemaVersion: Int
    public var general: General
    public var audio: Audio
    public var asr: ASR
    public var caption: Caption
    public var window: Window
    public var clipboard: Clipboard

    public init(
        schemaVersion: Int = Config.currentSchemaVersion,
        general: General = General(),
        audio: Audio = Audio(),
        asr: ASR = ASR(),
        caption: Caption = Caption(),
        window: Window = Window(),
        clipboard: Clipboard = Clipboard()
    ) {
        self.schemaVersion = schemaVersion
        self.general = general
        self.audio = audio
        self.asr = asr
        self.caption = caption
        self.window = window
        self.clipboard = clipboard
    }

    enum CodingKeys: String, CodingKey {
        case schemaVersion = "schema_version"
        case general, audio, asr, caption, window, clipboard
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = Config()
        schemaVersion = try c.decodeIfPresent(Int.self, forKey: .schemaVersion) ?? d.schemaVersion
        general = try c.decodeIfPresent(General.self, forKey: .general) ?? d.general
        audio = try c.decodeIfPresent(Audio.self, forKey: .audio) ?? d.audio
        asr = try c.decodeIfPresent(ASR.self, forKey: .asr) ?? d.asr
        caption = try c.decodeIfPresent(Caption.self, forKey: .caption) ?? d.caption
        window = try c.decodeIfPresent(Window.self, forKey: .window) ?? d.window
        clipboard = try c.decodeIfPresent(Clipboard.self, forKey: .clipboard) ?? d.clipboard
    }

    // MARK: Groups

    public struct General: Codable, Equatable {
        public var transcriptFolder: String
        public var sessionNamePrefix: String
        public init(transcriptFolder: String = AppPaths.transcripts.path,
                    sessionNamePrefix: String = "Interview ") {
            self.transcriptFolder = transcriptFolder
            self.sessionNamePrefix = sessionNamePrefix
        }
        enum CodingKeys: String, CodingKey {
            case transcriptFolder = "transcript_folder"
            case sessionNamePrefix = "session_name_prefix"
        }
        public init(from d: Decoder) throws {
            let c = try d.container(keyedBy: CodingKeys.self); let x = General()
            transcriptFolder = try c.decodeIfPresent(String.self, forKey: .transcriptFolder) ?? x.transcriptFolder
            sessionNamePrefix = try c.decodeIfPresent(String.self, forKey: .sessionNamePrefix) ?? x.sessionNamePrefix
        }
    }

    public struct Audio: Codable, Equatable {
        /// 0…3 (SPEC.md §15). No device UID — ScreenCaptureKit needs no device selection.
        public var vadSensitivity: Int
        public init(vadSensitivity: Int = 2) { self.vadSensitivity = vadSensitivity }
        enum CodingKeys: String, CodingKey { case vadSensitivity = "vad_sensitivity" }
        public init(from d: Decoder) throws {
            let c = try d.container(keyedBy: CodingKeys.self); let x = Audio()
            vadSensitivity = try c.decodeIfPresent(Int.self, forKey: .vadSensitivity) ?? x.vadSensitivity
        }
    }

    public struct ASR: Codable, Equatable {
        public var interimModel: String
        public var finalModel: String
        public var endpointSilenceMs: Int
        public var interimIntervalMs: Int
        public var maxUtteranceS: Int
        public init(interimModel: String = "tiny.en",
                    finalModel: String = "small.en",
                    endpointSilenceMs: Int = 600,
                    interimIntervalMs: Int = 500,
                    maxUtteranceS: Int = 20) {
            self.interimModel = interimModel
            self.finalModel = finalModel
            self.endpointSilenceMs = endpointSilenceMs
            self.interimIntervalMs = interimIntervalMs
            self.maxUtteranceS = maxUtteranceS
        }
        enum CodingKeys: String, CodingKey {
            case interimModel = "interim_model"
            case finalModel = "final_model"
            case endpointSilenceMs = "endpoint_silence_ms"
            case interimIntervalMs = "interim_interval_ms"
            case maxUtteranceS = "max_utterance_s"
        }
        public init(from d: Decoder) throws {
            let c = try d.container(keyedBy: CodingKeys.self); let x = ASR()
            interimModel = try c.decodeIfPresent(String.self, forKey: .interimModel) ?? x.interimModel
            finalModel = try c.decodeIfPresent(String.self, forKey: .finalModel) ?? x.finalModel
            endpointSilenceMs = try c.decodeIfPresent(Int.self, forKey: .endpointSilenceMs) ?? x.endpointSilenceMs
            interimIntervalMs = try c.decodeIfPresent(Int.self, forKey: .interimIntervalMs) ?? x.interimIntervalMs
            maxUtteranceS = try c.decodeIfPresent(Int.self, forKey: .maxUtteranceS) ?? x.maxUtteranceS
        }
    }

    public struct Caption: Codable, Equatable {
        public var fontSize: Int
        public var autoScroll: Bool
        public var showTimestamps: Bool
        public init(fontSize: Int = 18, autoScroll: Bool = true, showTimestamps: Bool = false) {
            self.fontSize = fontSize; self.autoScroll = autoScroll; self.showTimestamps = showTimestamps
        }
        enum CodingKeys: String, CodingKey {
            case fontSize = "font_size"
            case autoScroll = "auto_scroll"
            case showTimestamps = "show_timestamps"
        }
        public init(from d: Decoder) throws {
            let c = try d.container(keyedBy: CodingKeys.self); let x = Caption()
            fontSize = try c.decodeIfPresent(Int.self, forKey: .fontSize) ?? x.fontSize
            autoScroll = try c.decodeIfPresent(Bool.self, forKey: .autoScroll) ?? x.autoScroll
            showTimestamps = try c.decodeIfPresent(Bool.self, forKey: .showTimestamps) ?? x.showTimestamps
        }
    }

    public struct Window: Codable, Equatable {
        public var alwaysOnTop: Bool
        public var opacity: Double
        public var width: Double
        public var height: Double
        public var x: Double?
        public var y: Double?
        public init(alwaysOnTop: Bool = true, opacity: Double = 1.0,
                    width: Double = 480, height: Double = 640, x: Double? = nil, y: Double? = nil) {
            self.alwaysOnTop = alwaysOnTop; self.opacity = opacity
            self.width = width; self.height = height; self.x = x; self.y = y
        }
        enum CodingKeys: String, CodingKey {
            case alwaysOnTop = "always_on_top"
            case opacity, width, height, x, y
        }
        public init(from d: Decoder) throws {
            let c = try d.container(keyedBy: CodingKeys.self); let x = Window()
            alwaysOnTop = try c.decodeIfPresent(Bool.self, forKey: .alwaysOnTop) ?? x.alwaysOnTop
            opacity = try c.decodeIfPresent(Double.self, forKey: .opacity) ?? x.opacity
            width = try c.decodeIfPresent(Double.self, forKey: .width) ?? x.width
            height = try c.decodeIfPresent(Double.self, forKey: .height) ?? x.height
            self.x = try c.decodeIfPresent(Double.self, forKey: .x) ?? x.x
            self.y = try c.decodeIfPresent(Double.self, forKey: .y) ?? x.y
        }
    }

    public struct Clipboard: Codable, Equatable {
        public var autoUpdate: Bool
        public var recentSentences: Int
        public var autoCopySelection: Bool
        public init(autoUpdate: Bool = false, recentSentences: Int = 10, autoCopySelection: Bool = false) {
            self.autoUpdate = autoUpdate; self.recentSentences = recentSentences
            self.autoCopySelection = autoCopySelection
        }
        enum CodingKeys: String, CodingKey {
            case autoUpdate = "auto_update"
            case recentSentences = "recent_sentences"
            case autoCopySelection = "auto_copy_selection"
        }
        public init(from d: Decoder) throws {
            let c = try d.container(keyedBy: CodingKeys.self); let x = Clipboard()
            autoUpdate = try c.decodeIfPresent(Bool.self, forKey: .autoUpdate) ?? x.autoUpdate
            recentSentences = try c.decodeIfPresent(Int.self, forKey: .recentSentences) ?? x.recentSentences
            autoCopySelection = try c.decodeIfPresent(Bool.self, forKey: .autoCopySelection) ?? x.autoCopySelection
        }
    }
}

// MARK: - Persistence

extension Config {
    /// Load config, completing missing keys with defaults and migrating older schemas.
    /// On a missing file: write defaults and return them. On a corrupt file: back it up
    /// to `config.json.bak-<ts>`, write defaults, and return them with `repaired == true`.
    public static func loadOrRepair(from url: URL = AppPaths.configFile) -> (config: Config, repaired: Bool) {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else {
            let cfg = Config()
            try? cfg.write(to: url)
            return (cfg, false)
        }
        do {
            let data = try Data(contentsOf: url)
            var cfg = try JSONDecoder().decode(Config.self, from: data)
            if cfg.schemaVersion < Config.currentSchemaVersion {
                cfg.migrate()
                try? cfg.write(to: url)
            }
            return (cfg, false)
        } catch {
            let stamp = Config.backupStamp()
            let backup = url.deletingLastPathComponent()
                .appendingPathComponent("config.json.bak-\(stamp)")
            try? fm.copyItem(at: url, to: backup)
            let cfg = Config()
            try? cfg.write(to: url)
            return (cfg, true)
        }
    }

    /// Atomic write (temp file + rename) with stable, pretty, snake_cased output.
    public func write(to url: URL = AppPaths.configFile) throws {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try enc.encode(self)
        try data.write(to: url, options: .atomic)
    }

    /// Migration hook keyed on `schema_version`. v1 had no distinct on-disk shape here,
    /// so migrating is just bumping the version; add real steps as the schema evolves.
    mutating func migrate() {
        // if schemaVersion < 2 { ...transform... }
        schemaVersion = Config.currentSchemaVersion
    }

    static func backupStamp() -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyyMMdd-HHmmss"
        return f.string(from: Date())
    }
}
