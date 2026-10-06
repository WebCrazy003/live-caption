import Foundation

/// Filesystem operations on a session's saved transcript artifacts.
public enum SessionFiles {
    /// The audio recording's extension (AAC in MPEG-4).
    public static let audioExtension = "m4a"

    /// Delete a transcript `.txt` and its sibling `.json` sidecar together (SPEC-06 delete).
    /// Returns the URLs that were actually removed.
    @discardableResult
    public static func deleteTranscript(atTxtPath path: String) -> [URL] {
        let txt = URL(fileURLWithPath: path)
        let json = txt.deletingPathExtension().appendingPathExtension("json")
        let fm = FileManager.default
        var removed: [URL] = []
        for url in [txt, json] where fm.fileExists(atPath: url.path) {
            if (try? fm.removeItem(at: url)) != nil { removed.append(url) }
        }
        return removed
    }

    /// Delete a session's audio recording. Returns whether a file was removed.
    @discardableResult
    public static func deleteAudio(atPath path: String) -> Bool {
        (try? FileManager.default.removeItem(atPath: path)) != nil
    }

    // MARK: Recordings in progress

    /// Where a session's audio is written while it records: `recordings/<session uuid>.m4a`.
    /// The uuid is the journal's, so a crashed session's audio can be found again.
    public static func recordingURL(sessionId: UUID, directory: URL = AppPaths.recordings) -> URL {
        directory.appendingPathComponent(sessionId.uuidString).appendingPathExtension(audioExtension)
    }

    /// Move a finished recording to `folder/<base>.m4a`, adding ` (2)`, ` (3)`… if that name is
    /// taken. Returns where it landed.
    public static func placeRecording(_ source: URL, folder: URL, base: String) throws -> URL {
        let fm = FileManager.default
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        func target(_ name: String) -> URL { folder.appendingPathComponent(name + "." + audioExtension) }
        var url = target(base)
        var n = 2
        while fm.fileExists(atPath: url.path) { url = target("\(base) (\(n))"); n += 1 }
        try fm.moveItem(at: source, to: url)
        return url
    }
}
