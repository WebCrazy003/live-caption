import Foundation

/// Reads a saved transcript back into segments — used once to move sessions saved by earlier
/// builds into the database. Prefers the `.json` sidecar (exact segments and timings); falls back
/// to the `.txt` body, one segment per line, with `[HH:MM:SS]` prefixes read as start times.
public enum TranscriptFileReader {
    private struct Sidecar: Decodable { let segments: [TranscriptSegment] }

    /// Nil when neither file can be read.
    public static func segments(txtPath: String, createdAt: String) -> [TranscriptSegment]? {
        let txt = URL(fileURLWithPath: txtPath)
        let json = txt.deletingPathExtension().appendingPathExtension("json")
        if let data = try? Data(contentsOf: json), let sidecar = try? JSONDecoder().decode(Sidecar.self, from: data) {
            return sidecar.segments
        }
        guard let text = try? String(contentsOf: txt, encoding: .utf8) else { return nil }
        return segments(fromText: text, createdAt: createdAt)
    }

    /// The `.txt` body (after the header's blank line), one segment per non-empty line.
    public static func segments(fromText text: String, createdAt: String) -> [TranscriptSegment] {
        let lines = text.components(separatedBy: "\n")
        let bodyStart = (lines.firstIndex { $0.trimmingCharacters(in: .whitespaces).isEmpty }).map { $0 + 1 } ?? 0
        return lines[bodyStart...].compactMap { raw in
            var line = raw.trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty else { return nil }
            var startMs = 0
            if line.hasPrefix("["), let close = line.firstIndex(of: "]"), let ms = parseClock(line[line.index(after: line.startIndex)..<close]) {
                startMs = ms
                line = line[line.index(after: close)...].trimmingCharacters(in: .whitespaces)
            }
            return TranscriptSegment(text: line, tStartMs: startMs, tEndMs: startMs, createdAt: createdAt)
        }
    }

    /// `HH:MM:SS` → milliseconds.
    private static func parseClock(_ s: Substring) -> Int? {
        let parts = s.split(separator: ":").compactMap { Int($0) }
        guard parts.count == 3 else { return nil }
        return ((parts[0] * 60 + parts[1]) * 60 + parts[2]) * 1000
    }
}
