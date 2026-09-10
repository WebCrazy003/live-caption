import Foundation

/// A dedicated serial executor for fsync. Completion means the segment is durable;
/// callers may then publish it as final without blocking the UI thread.
public actor JournalWriter {
    private let journal: Journal
    public init(sessionId: UUID, directory: URL = AppPaths.journal) throws {
        journal = try Journal(sessionId: sessionId, directory: directory)
    }
    public func append(_ segment: TranscriptSegment) throws { try journal.append(segment) }
    public func deleteFile() { journal.deleteFile() }
}
