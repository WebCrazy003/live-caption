import Foundation

/// One finalized caption segment (SPEC.md §9.1). Session-relative timing in ms.
/// Interim/provisional captions are never stored.
public struct TranscriptSegment: Codable, Equatable, Identifiable {
    public let id: UUID
    public var text: String
    public var tStartMs: Int
    public var tEndMs: Int
    public var createdAt: String   // ISO-8601

    public init(id: UUID = UUID(), text: String, tStartMs: Int, tEndMs: Int, createdAt: String) {
        self.id = id; self.text = text
        self.tStartMs = tStartMs; self.tEndMs = tEndMs
        self.createdAt = createdAt
    }

    enum CodingKeys: String, CodingKey {
        case id, text
        case tStartMs = "t_start_ms"
        case tEndMs = "t_end_ms"
        case createdAt = "created_at"
    }
}

/// Ordered list of final segments + rendering to the on-disk `.txt` format (SPEC.md §12.1).
public struct Transcript: Equatable {
    public private(set) var segments: [TranscriptSegment] = []
    public init(segments: [TranscriptSegment] = []) { self.segments = segments }

    public var isEmpty: Bool { segments.isEmpty }
    public mutating func append(_ s: TranscriptSegment) { segments.append(s) }

    /// Body lines, optionally prefixed with `[HH:MM:SS]` from each segment's start.
    public func body(showTimestamps: Bool) -> String {
        segments.map { seg in
            showTimestamps ? "\(TimeFormat.stamp(ms: seg.tStartMs)) \(seg.text)" : seg.text
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
