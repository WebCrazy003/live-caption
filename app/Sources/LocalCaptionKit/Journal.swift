import Foundation

/// A session recovered from a leftover journal after a crash/quit (SPEC.md §9.3).
public struct RecoveredSession: Equatable {
    public let sessionId: UUID
    public let url: URL
    public let segments: [TranscriptSegment]
    public var startedAt: Date? { segments.first.flatMap { TimeFormat.parseISO($0.createdAt) } }
}

/// Append-only crash-recovery journal: each finalized segment is written as one JSON line
/// to `journal/<session_id>.jsonl` and fsync'd, so a crash mid-session loses nothing.
/// Deleted on clean Stop.
public final class Journal {
    public let sessionId: UUID
    private let url: URL
    private var handle: FileHandle?

    public init(sessionId: UUID, directory: URL = AppPaths.journal) throws {
        self.sessionId = sessionId
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        self.url = directory.appendingPathComponent("\(sessionId.uuidString).jsonl")
        if !FileManager.default.fileExists(atPath: url.path) {
            FileManager.default.createFile(atPath: url.path, contents: nil)
        }
        self.handle = try FileHandle(forWritingTo: url)
        try handle?.seekToEnd()
    }

    /// Append one segment as a JSON line and flush to disk.
    public func append(_ segment: TranscriptSegment) throws {
        guard let handle else { return }
        var data = try JSONEncoder().encode(segment)
        data.append(0x0A) // newline
        try handle.write(contentsOf: data)
        try handle.synchronize()
    }

    /// Delete the journal (called on clean Stop, after the transcript is saved).
    public func deleteFile() {
        try? handle?.close(); handle = nil
        try? FileManager.default.removeItem(at: url)
    }

    /// Scan for leftover journals to offer for recovery on launch.
    public static func pending(in directory: URL = AppPaths.journal) -> [RecoveredSession] {
        let fm = FileManager.default
        guard let items = try? fm.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) else { return [] }
        let dec = JSONDecoder()
        return items.filter { $0.pathExtension == "jsonl" }.compactMap { u in
            guard let content = try? String(contentsOf: u, encoding: .utf8) else { return nil }
            let segs = content.split(separator: "\n").compactMap { line -> TranscriptSegment? in
                try? dec.decode(TranscriptSegment.self, from: Data(line.utf8))
            }
            let id = UUID(uuidString: u.deletingPathExtension().lastPathComponent) ?? UUID()
            return RecoveredSession(sessionId: id, url: u, segments: segs)
        }
    }

    /// Remove a specific journal file by URL (used when discarding a recovery).
    public static func remove(at url: URL) {
        try? FileManager.default.removeItem(at: url)
    }
}
