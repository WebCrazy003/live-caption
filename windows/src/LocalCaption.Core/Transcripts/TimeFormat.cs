using System.Globalization;

namespace LocalCaption.Core.Transcripts;

/// <summary>
/// Time and date formatting shared by the transcript file, the per-line stamps and the UI
/// clock.
/// </summary>
/// <remarks>
/// Every format here is pinned to <see cref="CultureInfo.InvariantCulture"/> on purpose.
/// SPEC-WINDOWS.md §6: the ASUS's locale must not be able to reach into
/// <c>HH:mm:ss</c> or the ISO stamps, or transcripts stop being diffable against the macOS
/// output — which is how §17 verifies parity.
/// </remarks>
public static class TimeFormat
{
    /// <summary>Seconds → <c>HH:MM:SS</c> (duration and the elapsed clock).</summary>
    public static string Clock(int seconds)
    {
        var s = Math.Max(0, seconds);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
                             s / 3600, s % 3600 / 60, s % 60);
    }

    /// <summary>Milliseconds → <c>[HH:MM:SS]</c> per-line timestamp.</summary>
    public static string Stamp(int ms) => $"[{Clock(ms / 1000)}]";

    /// <summary><c>yyyy-MM-dd_HH-mm-ss</c> — filename-safe stamp from a start time.</summary>
    public static string FileStamp(DateTimeOffset date) => Fmt("yyyy-MM-dd_HH-mm-ss", date);

    /// <summary><c>yyyy-MM-dd HH:mm:ss</c> — human header timestamp.</summary>
    public static string Human(DateTimeOffset date) => Fmt("yyyy-MM-dd HH:mm:ss", date);

    /// <summary><c>yyyy-MM-dd HH:mm</c> — short form for auto session names.</summary>
    public static string HumanShort(DateTimeOffset date) => Fmt("yyyy-MM-dd HH:mm", date);

    /// <summary>ISO-8601 UTC — stored in the DB and in each segment's <c>created_at</c>.</summary>
    public static string Iso(DateTimeOffset date) =>
        date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseIso(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                out var parsed) ? parsed : null;

    private static string Fmt(string pattern, DateTimeOffset date) =>
        date.ToString(pattern, CultureInfo.InvariantCulture);
}
