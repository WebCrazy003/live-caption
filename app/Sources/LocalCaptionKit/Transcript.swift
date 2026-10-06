import Foundation

/// One finalized caption segment (SPEC.md §9.1). Session-relative timing in ms.
/// Interim/provisional captions are never stored.
public struct TranscriptSegment: Codable, Equatable, Identifiable {
    public let id: UUID
    /// The raw result of the (primary) speech model.
    public var text: String
    public var tStartMs: Int
    public var tEndMs: Int
    public var createdAt: String   // ISO-8601
    /// Accent mode (SPEC-18): the secondary model's text, the live Codex correction, and the
    /// final pass's correction. All absent in Standard mode.
    public var altText: String?
    public var liveText: String?
    public var finalText: String?

    public init(id: UUID = UUID(), text: String, tStartMs: Int, tEndMs: Int, createdAt: String,
                altText: String? = nil, liveText: String? = nil, finalText: String? = nil) {
        self.id = id; self.text = text
        self.tStartMs = tStartMs; self.tEndMs = tEndMs
        self.createdAt = createdAt
        self.altText = altText; self.liveText = liveText; self.finalText = finalText
    }

    /// What to show and export: the final pass, else the live correction, else the raw text.
    public var bestText: String { finalText ?? liveText ?? text }

    enum CodingKeys: String, CodingKey {
        case id, text
        case tStartMs = "t_start_ms"
        case tEndMs = "t_end_ms"
        case createdAt = "created_at"
        case altText = "alt_text"
        case liveText = "live_text"
        case finalText = "final_text"
    }
}

/// A later addition to a segment, keyed by its start time (unique within a session): the
/// secondary model's text arrives after the caption, corrections a few seconds later still.
/// Written to the crash-recovery journal as its own line.
public struct SegmentPatch: Codable, Equatable, Sendable {
    public var tStartMs: Int
    public var altText: String?
    public var liveText: String?
    public var finalText: String?

    public init(tStartMs: Int, altText: String? = nil, liveText: String? = nil, finalText: String? = nil) {
        self.tStartMs = tStartMs; self.altText = altText; self.liveText = liveText; self.finalText = finalText
    }

    /// Marks a journal line as a patch rather than a segment.
    private var patch = true

    enum CodingKeys: String, CodingKey {
        case patch
        case tStartMs = "t_start_ms"
        case altText = "alt_text"
        case liveText = "live_text"
        case finalText = "final_text"
    }

    public func apply(to s: inout TranscriptSegment) {
        if let altText { s.altText = altText }
        if let liveText { s.liveText = liveText }
        if let finalText { s.finalText = finalText }
    }
}

/// Ordered list of final segments + rendering to the on-disk `.txt` format (SPEC.md §12.1).
public struct Transcript: Equatable {
    public private(set) var segments: [TranscriptSegment] = []
    public init(segments: [TranscriptSegment] = []) { self.segments = segments }

    public var isEmpty: Bool { segments.isEmpty }
    public mutating func append(_ s: TranscriptSegment) { segments.append(s) }

    /// Apply a patch to the segment starting at its time; false when there is none.
    @discardableResult
    public mutating func apply(_ patch: SegmentPatch) -> Bool {
        guard let i = segments.lastIndex(where: { $0.tStartMs == patch.tStartMs }) else { return false }
        patch.apply(to: &segments[i])
        return true
    }

    /// Body lines, optionally prefixed with `[HH:MM:SS]` from each segment's start.
    public func body(showTimestamps: Bool) -> String {
        segments.map { seg in
            showTimestamps ? "\(TimeFormat.stamp(ms: seg.tStartMs)) \(seg.bestText)" : seg.bestText
        }.joined(separator: "\n")
    }

    /// Full file content: header block + blank line + body (SPEC.md §12.1).
    public func fileText(sessionName: String, start: Date, end: Date,
                         durationSeconds: Int, showTimestamps: Bool) -> String {
        let header = """
        Session: \(sessionName)
        Start:   \(TimeFormat.human(start))
        End:     \(TimeFormat.human(end))
        Duration: \(TimeFormat.clock(durationSeconds))
        """
        return header + "\n\n" + body(showTimestamps: showTimestamps) + "\n"
    }
}
