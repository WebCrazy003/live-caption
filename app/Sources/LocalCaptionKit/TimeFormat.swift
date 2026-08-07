import Foundation

/// Time/date formatting shared by the transcript file, per-line stamps, and the UI clock.
public enum TimeFormat {
    /// Seconds → `HH:MM:SS` (used for duration and the elapsed clock).
    public static func clock(_ seconds: Int) -> String {
        let s = max(0, seconds)
        return String(format: "%02d:%02d:%02d", s / 3600, (s % 3600) / 60, s % 60)
    }

    /// Milliseconds → `[HH:MM:SS]` per-line timestamp.
    public static func stamp(ms: Int) -> String { "[\(clock(ms / 1000))]" }

    /// `yyyy-MM-dd_HH-mm-ss` — filename-safe stamp from a start time.
    public static func fileStamp(_ date: Date) -> String { fmt("yyyy-MM-dd_HH-mm-ss", date) }

    /// `yyyy-MM-dd HH:mm:ss` — human header timestamp.
    public static func human(_ date: Date) -> String { fmt("yyyy-MM-dd HH:mm:ss", date) }

    /// `yyyy-MM-dd HH:mm` — short form for auto session names.
    public static func humanShort(_ date: Date) -> String { fmt("yyyy-MM-dd HH:mm", date) }

    /// ISO-8601 UTC — stored in the DB and in segment `created_at`.
    public static func iso(_ date: Date) -> String {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f.string(from: date)
    }

    public static func parseISO(_ s: String) -> Date? {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f.date(from: s)
    }

    private static func fmt(_ pattern: String, _ date: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = pattern
        return f.string(from: date)
    }
}
