import Foundation

/// LocalAgreement-2 interim stabilization (SPEC.md §8.2, glossary): commit only the tokens
/// agreed by the two most recent hypotheses, keeping the rest provisional, so the committed
/// prefix of the live (dimmed) line never flickers backward. Reset per utterance.
public struct LocalAgreement {
    private var previous: [String] = []
    public private(set) var committed: [String] = []

    public init() {}

    public mutating func reset() { previous = []; committed = [] }

    /// Feed the latest hypothesis (word list). The committed prefix grows to the common
    /// prefix of the last two hypotheses and is monotonic (never shrinks).
    /// Returns the committed prefix and the provisional tail.
    @discardableResult
    public mutating func update(_ hypothesis: [String]) -> (committed: [String], provisional: [String]) {
        let agreed = Self.commonPrefix(previous, hypothesis)
        if agreed.count > committed.count { committed = agreed }
        previous = hypothesis

        // Provisional tail = whatever the current hypothesis has beyond the committed prefix.
        let tail = hypothesis.count > committed.count
            ? Array(hypothesis[committed.count...])
            : []
        return (committed, tail)
    }

    static func commonPrefix(_ a: [String], _ b: [String]) -> [String] {
        var out: [String] = []
        for (x, y) in zip(a, b) {
            if x == y { out.append(x) } else { break }
        }
        return out
    }
}
