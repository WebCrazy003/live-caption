using System.Globalization;
using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;
using LocalCaption.Core.Transcripts;

namespace LocalCaption.App.Interview.Sessions;

/// <summary>Show All / Captions / Interviews (the Mac's <c>ModeFilter</c>).</summary>
public enum SessionModeFilter { All, Captions, Interviews }

/// <summary>One row of the Sessions list: the record and what the row shows.</summary>
/// <remarks>Immutable; a reload builds new rows.</remarks>
public sealed class SessionRow
{
    private SessionRow(SessionRecord record, string? subtitle, string searchText)
    {
        Record = record;
        Subtitle = subtitle;
        SearchText = searchText;
        When = TimeFormat.ParseIso(record.CreatedAt) is { } at
            ? TimeFormat.HumanShort(at.ToLocalTime())
            : record.CreatedAt;
        Duration = TimeFormat.Clock(record.DurationSeconds);
    }

    public SessionRecord Record { get; }
    public long Id => Record.Id ?? -1;
    public string Name => Record.SessionName;
    public bool IsInterview => Record.IsInterview;

    /// <summary>"interviewee · company · step N" (else the interview's name), interviews only.</summary>
    public string? Subtitle { get; }

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    /// <summary>Created, in the reader's own time zone.</summary>
    public string When { get; }

    /// <summary><c>HH:MM:SS</c>, as the Mac's <c>TimeFormat.clock</c>.</summary>
    public string Duration { get; }

    /// <summary>The interview's searchable text: interviewee, company, name, CV title, JD.</summary>
    internal string SearchText { get; }

    /// <summary>Name or interview text contains <paramref name="query"/>, case-insensitively.</summary>
    internal bool Matches(string query) =>
        Contains(Name, query) || (SearchText.Length > 0 && Contains(SearchText, query));

    private static bool Contains(string text, string query) =>
        CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, query,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth) >= 0;

    /// <summary>
    /// The rows for <paramref name="sessions"/>, joined with the linked interviews' list columns
    /// (<see cref="Store.InterviewListings"/>) — the Mac's <c>SessionListView.reload()</c>. The
    /// first (newest) interview linked to a session wins.
    /// </summary>
    internal static List<SessionRow> Build(IReadOnlyList<SessionRecord> sessions,
                                           IReadOnlyList<InterviewListing> interviews)
    {
        var subtitles = new Dictionary<long, string>();
        var texts = new Dictionary<long, string>();
        foreach (var r in interviews)
        {
            var sid = r.SessionId;
            if (texts.ContainsKey(sid)) continue;
            var who = new[]
            {
                r.Candidate ?? "",
                r.Company,
                r.Step is { } step ? $"step {step.ToString(CultureInfo.InvariantCulture)}" : "",
            }.Where(s => s.Length > 0).ToList();
            subtitles[sid] = who.Count > 1 ? string.Join(" · ", who) : r.Name;
            texts[sid] = string.Join("\n", r.Candidate ?? "", r.Company, r.Name, r.CvTitle ?? "", r.JdText ?? "");
        }

        return sessions.Select(s =>
        {
            var id = s.Id ?? -1;
            return new SessionRow(s,
                s.IsInterview && subtitles.TryGetValue(id, out var sub) ? sub : null,
                texts.TryGetValue(id, out var text) ? text : "");
        }).ToList();
    }
}

/// <summary>A sort choice, with the Mac's label.</summary>
public sealed record SessionSortChoice(string Label, SessionSort Sort)
{
    public static IReadOnlyList<SessionSortChoice> All { get; } =
    [
        new("Newest first", SessionSort.CreatedDesc),
        new("Oldest first", SessionSort.CreatedAsc),
        new("Name (A–Z)", SessionSort.NameAsc),
        new("Name (Z–A)", SessionSort.NameDesc),
        new("Longest first", SessionSort.DurationDesc),
        new("Shortest first", SessionSort.DurationAsc),
    ];
}

/// <summary>A Show choice, with the Mac's label.</summary>
public sealed record SessionFilterChoice(string Label, SessionModeFilter Filter)
{
    public static IReadOnlyList<SessionFilterChoice> All { get; } =
    [
        new("All", SessionModeFilter.All),
        new("Captions", SessionModeFilter.Captions),
        new("Interviews", SessionModeFilter.Interviews),
    ];

    /// <summary>The <c>sessions.mode</c> to query, or null for all.</summary>
    public static string? Mode(SessionModeFilter filter) => filter switch
    {
        SessionModeFilter.Captions => SessionRecord.CaptionMode,
        SessionModeFilter.Interviews => SessionRecord.InterviewMode,
        _ => null,
    };
}
