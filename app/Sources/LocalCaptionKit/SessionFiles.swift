import Foundation

/// Filesystem operations on a session's saved transcript artifacts.
public enum SessionFiles {
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
}
