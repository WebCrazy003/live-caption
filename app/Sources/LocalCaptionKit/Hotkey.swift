import Foundation

/// The Ask hotkey as stored in `interview.hotkey` (SPEC-11 §Hotkey string grammar). Pure and
/// platform-neutral: registering it with the OS is the app's job (Carbon here, `RegisterHotKey`
/// on Windows); parsing is shared through `testdata/hotkey/`.
///
/// ```
/// hotkey   = *(modifier "+") key
/// modifier = "Ctrl" | "Alt" | "Shift" | "Cmd"
/// key      = "F1".."F24" | "A".."Z" | "0".."9" | "Space" | "Enter" | "Tab"
///          | "Up" | "Down" | "Left" | "Right" | "PageUp" | "PageDown" | "Home" | "End"
/// ```
///
/// Read case-insensitively, with the aliases macOS users type (`Control`, `Option`, `Command`,
/// `Return`); written canonically, modifiers in the order Ctrl, Alt, Shift, Cmd.
public struct Hotkey: Equatable, Hashable, Sendable, CustomStringConvertible {
    public enum Modifier: Int, CaseIterable, Comparable, Sendable {
        case ctrl, alt, shift, cmd
        public var name: String { ["Ctrl", "Alt", "Shift", "Cmd"][rawValue] }
        public static func < (a: Modifier, b: Modifier) -> Bool { a.rawValue < b.rawValue }
    }

    public enum ParseError: Error, Equatable, Sendable {
        case empty
        case unknownModifier(String)
        case unknownKey(String)
        case missingKey
        /// A typing key (letter, digit, Space, arrows…) bound globally with no modifier, or with
        /// Shift alone, would swallow that key in every app.
        case needsModifier(String)
    }

    public static let defaultString = "F8"
    public static let `default` = Hotkey(key: "F8", modifiers: [])
    /// The screenshot hotkey's default (owner, 2026-10-02).
    public static let defaultScreenshotString = "F9"
    public static let defaultScreenshot = Hotkey(key: "F9", modifiers: [])

    /// Canonical key name, e.g. `"F8"`, `"K"`, `"Space"`.
    public let key: String
    public let modifiers: Set<Modifier>

    public init(key: String, modifiers: Set<Modifier>) {
        self.key = key
        self.modifiers = modifiers
    }

    public var isFunctionKey: Bool { Hotkey.functionKeys.contains(key) }

    /// `Ctrl+Alt+Shift+Cmd+Key`, modifiers in canonical order.
    public var description: String {
        (modifiers.sorted().map(\.name) + [key]).joined(separator: "+")
    }

    public static func parse(_ string: String) -> Result<Hotkey, ParseError> {
        let parts = string.split(separator: "+", omittingEmptySubsequences: false)
            .map { $0.trimmingCharacters(in: .whitespaces) }
        if parts.allSatisfy(\.isEmpty) { return .failure(.empty) }
        guard let last = parts.last, !last.isEmpty else { return .failure(.missingKey) }

        var mods = Set<Modifier>()
        for token in parts.dropLast() {
            guard let m = modifierAliases[token.lowercased()] else {
                return .failure(token.isEmpty ? .missingKey : .unknownModifier(token))
            }
            mods.insert(m)
        }
        if modifierAliases[last.lowercased()] != nil { return .failure(.missingKey) }
        guard let key = keyNames[last.lowercased()] else { return .failure(.unknownKey(last)) }
        if !functionKeys.contains(key), mods.subtracting([.shift]).isEmpty {
            return .failure(.needsModifier(key))
        }
        return .success(Hotkey(key: key, modifiers: mods))
    }

    /// The configured hotkey, or the default when the string is empty or invalid.
    public static func resolve(_ string: String, fallback: Hotkey = .default) -> Hotkey {
        (try? parse(string).get()) ?? fallback
    }

    // MARK: Tables

    private static let modifierAliases: [String: Modifier] = [
        "ctrl": .ctrl, "control": .ctrl,
        "alt": .alt, "option": .alt, "opt": .alt,
        "shift": .shift,
        "cmd": .cmd, "command": .cmd, "win": .cmd,
    ]

    static let functionKeys: Set<String> = Set((1...24).map { "F\($0)" })

    private static let keyNames: [String: String] = {
        var t: [String: String] = [:]
        for k in functionKeys { t[k.lowercased()] = k }
        for c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789" { t[String(c).lowercased()] = String(c) }
        for k in ["Space", "Enter", "Tab", "Up", "Down", "Left", "Right", "PageUp", "PageDown", "Home", "End"] {
            t[k.lowercased()] = k
        }
        t["return"] = "Enter"
        return t
    }()
}
