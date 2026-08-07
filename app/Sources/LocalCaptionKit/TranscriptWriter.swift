import Foundation

public struct TranscriptSaveResult: Equatable {
    public let txtURL: URL
    public let jsonURL: URL
}

/// Writes a finished transcript to disk: the human `.txt` plus a machine-readable `.json`
/// sidecar (product decision §22.4 = yes). Filenames come from the start time with
/// ` (2)`, ` (3)`… collision suffixes (SPEC.md §12.1).
public enum TranscriptWriter {
    /// JSON sidecar shape — session metadata + the full segment list.
    private struct Sidecar: Encodable {
        let session_name: String
        let started_at: String
        let ended_at: String
        let duration_seconds: Int
        let segments: [TranscriptSegment]
    }

    public static func save(transcript: Transcript,
                            folder: URL,
                            sessionName: String,
                            start: Date,
                            end: Date,
                            durationSeconds: Int,
                            showTimestamps: Bool) throws -> TranscriptSaveResult {
        let fm = FileManager.default
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)

        let base = resolveBase(folder: folder, stamp: TimeFormat.fileStamp(start))
        let txtURL = folder.appendingPathComponent(base + ".txt")
        let jsonURL = folder.appendingPathComponent(base + ".json")

        let text = transcript.fileText(sessionName: sessionName, start: start, end: end,
                                       durationSeconds: durationSeconds, showTimestamps: showTimestamps)
        try Data(text.utf8).write(to: txtURL, options: .atomic)

        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        let sidecar = Sidecar(session_name: sessionName,
                              started_at: TimeFormat.iso(start),
                              ended_at: TimeFormat.iso(end),
                              duration_seconds: durationSeconds,
                              segments: transcript.segments)
        try enc.encode(sidecar).write(to: jsonURL, options: .atomic)

        return TranscriptSaveResult(txtURL: txtURL, jsonURL: jsonURL)
    }

    /// Pick a base filename that collides with neither an existing `.txt` nor `.json`.
    static func resolveBase(folder: URL, stamp: String) -> String {
        let fm = FileManager.default
        func free(_ base: String) -> Bool {
            !fm.fileExists(atPath: folder.appendingPathComponent(base + ".txt").path)
            && !fm.fileExists(atPath: folder.appendingPathComponent(base + ".json").path)
        }
        if free(stamp) { return stamp }
        var n = 2
        while !free("\(stamp) (\(n))") { n += 1 }
        return "\(stamp) (\(n))"
    }
}
