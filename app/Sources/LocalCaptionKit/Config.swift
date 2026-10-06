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
    public var summary: Summary
    public var interview: Interview
    public var accent: Accent

    public init(
        schemaVersion: Int = Config.currentSchemaVersion,
        general: General = General(),
        audio: Audio = Audio(),
        asr: ASR = ASR(),
        caption: Caption = Caption(),
        window: Window = Window(),
        clipboard: Clipboard = Clipboard(),
        summary: Summary = Summary(),
        interview: Interview = Interview(),
        accent: Accent = Accent()
    ) {
        self.schemaVersion = schemaVersion
        self.general = general
        self.audio = audio
        self.asr = asr
        self.caption = caption
        self.window = window
        self.clipboard = clipboard
        self.summary = summary
        self.interview = interview
        self.accent = accent
    }

    enum CodingKeys: String, CodingKey {
        case schemaVersion = "schema_version"
        case general, audio, asr, caption, window, clipboard, summary, interview, accent
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
        // Merge-default (SPEC-10): a config file without `summary` gets defaults — no schema bump.
        summary = try c.decodeIfPresent(Summary.self, forKey: .summary) ?? d.summary
        // Merge-default (SPEC-11), same treatment as `summary`: no schema bump.
        interview = try c.decodeIfPresent(Interview.self, forKey: .interview) ?? d.interview
        // Merge-default (SPEC-18), same treatment again.
        accent = try c.decodeIfPresent(Accent.self, forKey: .accent) ?? d.accent
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
        /// What to save as an audio file beside the transcript. Off by default (owner, 2026-10-06).
        public var recordSource: RecordSource
        public init(vadSensitivity: Int = 2, recordSource: RecordSource = .off) {
            self.vadSensitivity = vadSensitivity
            self.recordSource = recordSource
        }
        enum CodingKeys: String, CodingKey {
            case vadSensitivity = "vad_sensitivity"
            case recordSource = "record_source"
        }
        public init(from d: Decoder) throws {
            let c = try d.container(keyedBy: CodingKeys.self); let x = Audio()
            vadSensitivity = try c.decodeIfPresent(Int.self, forKey: .vadSensitivity) ?? x.vadSensitivity
            // An unknown value (a newer build's) reads as off rather than failing the whole config.
            recordSource = (try? c.decodeIfPresent(RecordSource.self, forKey: .recordSource)) ?? x.recordSource
        }
    }

    /// Audio recording source. Captions always come from the call audio; this only picks what
    /// is saved as the session's `.m4a`.
    public enum RecordSource: String, Codable, CaseIterable, Sendable {
        case off
        /// The system/call audio the captions are made from.
        case call
        /// The default input device (your own voice). Needs the Microphone permission.
        case microphone
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
                    width: Double = 860, height: Double = 620, x: Double? = nil, y: Double? = nil) {
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

    /// Reserved: the Live AI Summary ("Key points", SPEC-10) was removed on 2026-10-02. The group
    /// stays in the schema, unused, so `config.json` keeps round-tripping with the Windows build,
    /// which keeps it for the same reason (SPEC-WINDOWS §9.2).
    public struct Summary: Codable, Equatable {
        public var enabled: Bool
        public var wordsPerSummary: Int
        public var maxBullets: Int
        public var model: String        // model id the local server was started with
        public var serverURL: String    // localhost only; never leaves the machine
        public init(enabled: Bool = true,
                    wordsPerSummary: Int = 90,
                    maxBullets: Int = 0,
                    model: String = "mlx-community/Llama-3.2-1B-Instruct-4bit",
                    serverURL: String = "http://127.0.0.1:8765") {
            self.enabled = enabled
            self.wordsPerSummary = wordsPerSummary
            self.maxBullets = maxBullets
            self.model = model
            self.serverURL = serverURL
        }
        enum CodingKeys: String, CodingKey {
            case enabled
            case wordsPerSummary = "words_per_summary"
            case maxBullets = "max_bullets"
            case model
            case serverURL = "server_url"
        }
        public init(from d: Decoder) throws {
            let c = try d.container(keyedBy: CodingKeys.self); let x = Summary()
            enabled = try c.decodeIfPresent(Bool.self, forKey: .enabled) ?? x.enabled
            wordsPerSummary = try c.decodeIfPresent(Int.self, forKey: .wordsPerSummary) ?? x.wordsPerSummary
            maxBullets = try c.decodeIfPresent(Int.self, forKey: .maxBullets) ?? x.maxBullets
            model = try c.decodeIfPresent(String.self, forKey: .model) ?? x.model
            serverURL = try c.decodeIfPresent(String.self, forKey: .serverURL) ?? x.serverURL
        }
    }

    /// Interview Assist (SPEC-11). Answers come from a locked-down `codex app-server` thread; these
    /// keys are shared verbatim with the Windows build so `config.json` stays interchangeable.
    public struct Interview: Codable, Equatable {
        public enum Mode: String, Codable, CaseIterable, Sendable { case caption, interview }
        public enum AnswerLength: String, Codable, CaseIterable, Sendable { case short, medium, long }
        public enum SendMode: String, Codable, CaseIterable, Sendable {
            case sinceLastAsk = "since_last_ask"
            case lastSentences = "last_sentences"
        }
        public enum BusyPolicy: String, Codable, CaseIterable, Sendable { case interrupt, queue }
        /// How the captions and answers panels sit (owner, 2026-10-02): side by side, stacked
        /// (answers on top), or by window width.
        public enum PanelLayout: String, Codable, CaseIterable, Sendable {
            case automatic, sideBySide = "side_by_side", stacked
        }

        /// The S0-recommended model, used when `model` is empty (SPEC-12 §S0).
        public static let recommendedModel = "gpt-6-luna"

        public var mode: Mode
        public var privacyAcknowledged: Bool
        public var engine: String
        public var codexPath: String
        public var model: String
        public var reasoningEffort: String
        public var prepReasoningEffort: String
        public var answerLength: AnswerLength
        public var customInstructions: String
        public var hotkey: String
        /// Select an area of the screen and add it to the prompt (owner, 2026-10-02).
        public var screenshotHotkey: String
        public var sendMode: SendMode
        public var sendSentences: Int
        public var maxWords: Int
        public var includeClipboardImages: Bool
        public var clearClipboardImagesAfterSend: Bool
        public var busyPolicy: BusyPolicy
        public var panelLayout: PanelLayout

        public init(mode: Mode = .caption,
                    privacyAcknowledged: Bool = false,
                    engine: String = "codex",
                    codexPath: String = "",
                    model: String = "",
                    reasoningEffort: String = "low",
                    prepReasoningEffort: String = "medium",
                    answerLength: AnswerLength = .medium,
                    customInstructions: String = "",
                    hotkey: String = Hotkey.defaultString,
                    screenshotHotkey: String = Hotkey.defaultScreenshotString,
                    sendMode: SendMode = .sinceLastAsk,
                    sendSentences: Int = 3,
                    maxWords: Int = 400,
                    includeClipboardImages: Bool = false,
                    clearClipboardImagesAfterSend: Bool = true,
                    busyPolicy: BusyPolicy = .interrupt,
                    panelLayout: PanelLayout = .automatic) {
            self.mode = mode; self.privacyAcknowledged = privacyAcknowledged
            self.engine = engine; self.codexPath = codexPath; self.model = model
            self.reasoningEffort = reasoningEffort; self.prepReasoningEffort = prepReasoningEffort
            self.answerLength = answerLength; self.customInstructions = customInstructions
            self.hotkey = hotkey
            self.screenshotHotkey = screenshotHotkey
            self.sendMode = sendMode; self.sendSentences = sendSentences; self.maxWords = maxWords
            self.includeClipboardImages = includeClipboardImages
            self.clearClipboardImagesAfterSend = clearClipboardImagesAfterSend
            self.busyPolicy = busyPolicy
            self.panelLayout = panelLayout
        }

        /// The model to request: the configured one, or the S0 default.
        public var effectiveModel: String { model.isEmpty ? Interview.recommendedModel : model }

        enum CodingKeys: String, CodingKey {
            case mode
            case privacyAcknowledged = "privacy_acknowledged"
            case engine
            case codexPath = "codex_path"
            case model
            case reasoningEffort = "reasoning_effort"
            case prepReasoningEffort = "prep_reasoning_effort"
            case answerLength = "answer_length"
            case customInstructions = "custom_instructions"
            case hotkey
            case screenshotHotkey = "screenshot_hotkey"
            case sendMode = "send_mode"
            case sendSentences = "send_sentences"
            case maxWords = "max_words"
            case includeClipboardImages = "include_clipboard_images"
            case clearClipboardImagesAfterSend = "clear_clipboard_images_after_send"
            case busyPolicy = "busy_policy"
            case panelLayout = "panel_layout"
        }

        public init(from d: Decoder) throws {
            let c = try d.container(keyedBy: CodingKeys.self); let x = Interview()
            // An enum key holding a string this build doesn't know (a newer build's value) falls
            // back to the default instead of tripping the corrupt→repair path. A wrong JSON
            // *type* still throws and repairs, like every other key.
            func value<T: RawRepresentable>(_ key: CodingKeys, _ fallback: T) throws -> T where T.RawValue == String {
                guard let raw = try c.decodeIfPresent(String.self, forKey: key) else { return fallback }
                return T(rawValue: raw) ?? fallback
            }
            mode = try value(.mode, x.mode)
            privacyAcknowledged = try c.decodeIfPresent(Bool.self, forKey: .privacyAcknowledged) ?? x.privacyAcknowledged
            engine = try c.decodeIfPresent(String.self, forKey: .engine) ?? x.engine
            codexPath = try c.decodeIfPresent(String.self, forKey: .codexPath) ?? x.codexPath
            model = try c.decodeIfPresent(String.self, forKey: .model) ?? x.model
            reasoningEffort = try c.decodeIfPresent(String.self, forKey: .reasoningEffort) ?? x.reasoningEffort
            prepReasoningEffort = try c.decodeIfPresent(String.self, forKey: .prepReasoningEffort) ?? x.prepReasoningEffort
            answerLength = try value(.answerLength, x.answerLength)
            customInstructions = try c.decodeIfPresent(String.self, forKey: .customInstructions) ?? x.customInstructions
            hotkey = try c.decodeIfPresent(String.self, forKey: .hotkey) ?? x.hotkey
            screenshotHotkey = try c.decodeIfPresent(String.self, forKey: .screenshotHotkey) ?? x.screenshotHotkey
            sendMode = try value(.sendMode, x.sendMode)
            sendSentences = try c.decodeIfPresent(Int.self, forKey: .sendSentences) ?? x.sendSentences
            maxWords = try c.decodeIfPresent(Int.self, forKey: .maxWords) ?? x.maxWords
            includeClipboardImages = try c.decodeIfPresent(Bool.self, forKey: .includeClipboardImages) ?? x.includeClipboardImages
            clearClipboardImagesAfterSend = try c.decodeIfPresent(Bool.self, forKey: .clearClipboardImagesAfterSend) ?? x.clearClipboardImagesAfterSend
            busyPolicy = try value(.busyPolicy, x.busyPolicy)
            panelLayout = try value(.panelLayout, x.panelLayout)
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
