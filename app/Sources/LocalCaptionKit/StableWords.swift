import Foundation

/// Accent mode's streaming words (owner, 2026-10-07): the moving caption is re-read every
/// interval, and a word counts as stable once two reads in a row agree on it and everything
/// before it. Stable words show as normal text, 2–3 at a time; only the changing tail stays muted.
/// The final caption at the pause replaces both.
public struct StableWords: Sendable {
    private var last: [String] = []
    private var stableCount = 0

    public init() {}

    public mutating func reset() { last = []; stableCount = 0 }

    /// Feed the latest moving caption; returns the stable words and the changing tail.
    public mutating func update(_ text: String) -> (stable: String, tail: String) {
        let words = text.split(whereSeparator: \.isWhitespace).map(String.init)
        let keys = words.map(Self.key)
        let agreed = zip(last.map(Self.key), keys).prefix { $0 == $1 }.count
        // Earlier stable words stay stable while the new read still starts with them; a read that
        // doesn't (a final took that utterance away, or the model rewrote it) starts over.
        let stillPrefix = stableCount <= keys.count && stableCount <= last.count
            && Array(keys.prefix(stableCount)) == last.prefix(stableCount).map(Self.key)
        stableCount = max(agreed, stillPrefix ? stableCount : 0)
        last = words
        return (words.prefix(stableCount).joined(separator: " "),
                words.dropFirst(stableCount).joined(separator: " "))
    }

    /// Case and punctuation don't make a word unstable ("dog" then "dog.").
    static func key(_ word: String) -> String {
        word.lowercased().filter { $0.isLetter || $0.isNumber || $0 == "'" }
    }
}
