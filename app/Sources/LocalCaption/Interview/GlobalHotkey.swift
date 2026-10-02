import Foundation
import AppKit
import Carbon.HIToolbox
import LocalCaptionKit

/// A system-wide hotkey, registered with Carbon's `RegisterEventHotKey` (SPEC-14 §Global
/// hotkey). It fires while Zoom/Meet/Teams has focus, and needs no Accessibility or Input
/// Monitoring permission. One instance per action: Ask (`shared`) and Screenshot.
@MainActor
final class GlobalHotkey: ObservableObject {
    static let shared = GlobalHotkey(id: 1)
    /// Select an area of the screen and add it to the prompt (owner, 2026-10-02).
    static let screenshot = GlobalHotkey(id: 2)

    enum State: Equatable {
        case off
        case registered(Hotkey)
        /// Another app owns the combination, or this Mac has no such key.
        case unavailable(Hotkey, reason: String)
    }

    @Published private(set) var state: State = .off
    var onPress: (() -> Void)?

    private let id: UInt32
    private var hotKeyRef: EventHotKeyRef?
    private static var handlerRef: EventHandlerRef?
    private static let signature: OSType = 0x4C43_4150   // 'LCAP'

    private init(id: UInt32) { self.id = id }

    func register(_ hotkey: Hotkey) {
        unregister()
        Self.installHandlerIfNeeded()
        guard let code = HotkeyKeys.keyCode(for: hotkey.key) else {
            state = .unavailable(hotkey, reason: "\(hotkey.key) isn't on a Mac keyboard")
            return
        }
        var ref: EventHotKeyRef?
        let status = RegisterEventHotKey(UInt32(code), HotkeyKeys.carbonModifiers(hotkey.modifiers),
                                         EventHotKeyID(signature: Self.signature, id: id),
                                         GetApplicationEventTarget(), 0, &ref)
        if status == noErr, let ref {
            hotKeyRef = ref
            state = .registered(hotkey)
        } else {
            state = .unavailable(hotkey, reason: status == eventHotKeyExistsErr
                                 ? "another app is using \(hotkey)" : "macOS refused \(hotkey) (\(status))")
        }
    }

    func unregister() {
        if let hotKeyRef { UnregisterEventHotKey(hotKeyRef) }
        hotKeyRef = nil
        state = .off
    }

    /// One handler for every instance; the event's hotkey id says which one was pressed.
    private static func installHandlerIfNeeded() {
        guard handlerRef == nil else { return }
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ -> OSStatus in
            var hk = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &hk)
            let id = hk.id
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    let target = id == GlobalHotkey.screenshot.id ? GlobalHotkey.screenshot : GlobalHotkey.shared
                    target.onPress?()
                }
            }
            return noErr
        }, 1, &spec, nil, &handlerRef)
    }
}

/// Grammar key names ↔ macOS virtual key codes (SPEC-11 §Hotkey string grammar).
enum HotkeyKeys {
    private static let table: [String: Int] = {
        var t: [String: Int] = [
            "F1": kVK_F1, "F2": kVK_F2, "F3": kVK_F3, "F4": kVK_F4, "F5": kVK_F5, "F6": kVK_F6,
            "F7": kVK_F7, "F8": kVK_F8, "F9": kVK_F9, "F10": kVK_F10, "F11": kVK_F11, "F12": kVK_F12,
            "F13": kVK_F13, "F14": kVK_F14, "F15": kVK_F15, "F16": kVK_F16, "F17": kVK_F17,
            "F18": kVK_F18, "F19": kVK_F19, "F20": kVK_F20,
            "Space": kVK_Space, "Enter": kVK_Return, "Tab": kVK_Tab,
            "Up": kVK_UpArrow, "Down": kVK_DownArrow, "Left": kVK_LeftArrow, "Right": kVK_RightArrow,
            "PageUp": kVK_PageUp, "PageDown": kVK_PageDown, "Home": kVK_Home, "End": kVK_End,
            "0": kVK_ANSI_0, "1": kVK_ANSI_1, "2": kVK_ANSI_2, "3": kVK_ANSI_3, "4": kVK_ANSI_4,
            "5": kVK_ANSI_5, "6": kVK_ANSI_6, "7": kVK_ANSI_7, "8": kVK_ANSI_8, "9": kVK_ANSI_9,
        ]
        let letters: [(String, Int)] = [
            ("A", kVK_ANSI_A), ("B", kVK_ANSI_B), ("C", kVK_ANSI_C), ("D", kVK_ANSI_D), ("E", kVK_ANSI_E),
            ("F", kVK_ANSI_F), ("G", kVK_ANSI_G), ("H", kVK_ANSI_H), ("I", kVK_ANSI_I), ("J", kVK_ANSI_J),
            ("K", kVK_ANSI_K), ("L", kVK_ANSI_L), ("M", kVK_ANSI_M), ("N", kVK_ANSI_N), ("O", kVK_ANSI_O),
            ("P", kVK_ANSI_P), ("Q", kVK_ANSI_Q), ("R", kVK_ANSI_R), ("S", kVK_ANSI_S), ("T", kVK_ANSI_T),
            ("U", kVK_ANSI_U), ("V", kVK_ANSI_V), ("W", kVK_ANSI_W), ("X", kVK_ANSI_X), ("Y", kVK_ANSI_Y),
            ("Z", kVK_ANSI_Z),
        ]
        for (k, v) in letters { t[k] = v }
        return t
    }()
    private static let reverse: [Int: String] = Dictionary(uniqueKeysWithValues: table.map { ($1, $0) })

    static func keyCode(for key: String) -> Int? { table[key] }
    static func keyName(for code: UInt16) -> String? { reverse[Int(code)] }

    static func carbonModifiers(_ m: Set<Hotkey.Modifier>) -> UInt32 {
        var out: UInt32 = 0
        if m.contains(.cmd) { out |= UInt32(cmdKey) }
        if m.contains(.alt) { out |= UInt32(optionKey) }
        if m.contains(.ctrl) { out |= UInt32(controlKey) }
        if m.contains(.shift) { out |= UInt32(shiftKey) }
        return out
    }

    /// A key-down from the Settings recorder → grammar string (validated by the caller).
    static func string(for event: NSEvent) -> String? {
        guard let key = keyName(for: event.keyCode) else { return nil }
        var parts: [String] = []
        let f = event.modifierFlags
        if f.contains(.control) { parts.append("Ctrl") }
        if f.contains(.option) { parts.append("Alt") }
        if f.contains(.shift) { parts.append("Shift") }
        if f.contains(.command) { parts.append("Cmd") }
        return (parts + [key]).joined(separator: "+")
    }
}
