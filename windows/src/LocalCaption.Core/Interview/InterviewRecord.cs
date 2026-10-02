using System.Text.Json;
using System.Text.Json.Serialization;
using LocalCaption.Core.Transcripts;

namespace LocalCaption.Core.Interview;

/// <summary>Where a job (legacy prep, summary) stands. <c>Status</c> in Swift.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InterviewStatus>))]
public enum InterviewStatus
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("done")] Done,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("skipped")] Skipped,
}

/// <summary>What started a turn. <c>TurnKind</c> in Swift.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InterviewTurnKind>))]
public enum InterviewTurnKind
{
    [JsonStringEnumMemberName("ask")] Ask,
    [JsonStringEnumMemberName("typed")] Typed,
    [JsonStringEnumMemberName("quick")] Quick,
    [JsonStringEnumMemberName("regenerate")] Regenerate,
    [JsonStringEnumMemberName("skill")] Skill,
}

/// <summary>Where one answer stands. <c>TurnStatus</c> in Swift.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InterviewTurnStatus>))]
public enum InterviewTurnStatus
{
    [JsonStringEnumMemberName("streaming")] Streaming,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("interrupted")] Interrupted,
    [JsonStringEnumMemberName("failed")] Failed,
}

/// <summary>
/// One interview (specs/SPEC-11 §On-disk layout, schema 1) — the port of macOS's
/// <c>InterviewRecord</c>. It lives in the <c>interviews</c> / <c>interview_turns</c> tables
/// (<see cref="Data.Store.SaveInterview"/>); the JSON shape is the macOS
/// <c>interview.json</c> one, kept identical so a record can be moved between the builds.
/// </summary>
/// <remarks>
/// <para>Swift nests <c>Setup</c>, <c>Prep</c>, <c>Turn</c>, <c>Summary</c> and the three
/// enums inside the record. C# cannot nest a type under the same name as a property, so here
/// they are namespace-level types prefixed <c>Interview</c>.</para>
/// <para>Properties are declared in the sorted-key order Swift writes them in, and every
/// on-disk name is spelled out. Optional values are omitted when null, as Swift's
/// synthesized <c>Codable</c> does.</para>
/// <para>Unlike Swift's <c>Equatable</c>, record equality compares the lists by reference;
/// compare the JSON when you need value equality.</para>
/// </remarks>
public sealed record InterviewRecord
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>A new record with an uppercase UUID id (as Swift's <c>UUID().uuidString</c>).</summary>
    public InterviewRecord() { }

    public InterviewRecord(string name, string createdAt, string model, string reasoningEffort,
                           InterviewSetup setup, string? id = null, string engine = "codex")
    {
        Id = id ?? NewId();
        Name = name;
        CreatedAt = createdAt;
        Engine = engine;
        Model = model;
        ReasoningEffort = reasoningEffort;
        Setup = setup;
    }

    [JsonPropertyName("capture_session_uuid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CaptureSessionUuid { get; set; }

    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";

    /// <summary>The CV text Discovery CV sent (a snapshot, so history survives library changes).</summary>
    [JsonPropertyName("cv_text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CvText { get; set; }

    [JsonPropertyName("ended_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndedAt { get; set; }

    [JsonPropertyName("engine")] public string Engine { get; set; } = "codex";
    [JsonPropertyName("id")] public string Id { get; set; } = NewId();
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("prep")] public InterviewPrep Prep { get; set; } = new();
    [JsonPropertyName("reasoning_effort")] public string ReasoningEffort { get; set; } = "";
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("session_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SessionId { get; set; }

    [JsonPropertyName("setup")] public InterviewSetup Setup { get; set; } = new();

    [JsonPropertyName("started_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StartedAt { get; set; }

    [JsonPropertyName("summary")] public InterviewSummary Summary { get; set; } = new();

    /// <summary>The summary Markdown, once generated.</summary>
    [JsonPropertyName("summary_text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SummaryText { get; set; }

    [JsonPropertyName("thread_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; set; }

    /// <summary>The interviewer transcript, copied in when the interview ends.</summary>
    [JsonPropertyName("transcript"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Transcript { get; set; }

    [JsonPropertyName("turns")] public List<InterviewTurn> Turns { get; set; } = [];

    private static string NewId() => Guid.NewGuid().ToString("D").ToUpperInvariant();

    // ── Naming ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>&lt;interviewee&gt;-&lt;company&gt;-&lt;step&gt;-&lt;yyyy-MM-dd&gt;</c> (owner,
    /// 2026-10-02), skipping empty parts; null when neither the interviewee nor the company is
    /// known. The day is local (<see cref="TimeFormat.Day"/>).
    /// </summary>
    public static string? SessionName(string? candidate, string? company, int? step, DateTimeOffset date)
    {
        var who = SwiftTrim(candidate);
        var where = SwiftTrim(company);
        if (who.Length == 0 && where.Length == 0) return null;
        string[] parts = [who, where, step?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                          TimeFormat.Day(date)];
        return string.Join("-", parts.Where(p => p.Length > 0));
    }

    /// <summary>This interview's session name, from its setup.</summary>
    public string? SessionName(DateTimeOffset date) =>
        SessionName(Setup.Candidate, Setup.Company, Setup.Step, date);

    /// <summary>
    /// Swift's <c>trimmingCharacters(in: .whitespacesAndNewlines)</c>: Unicode Z* plus
    /// U+0009–U+000D and U+0085 — the same set as <see cref="char.IsWhiteSpace(char)"/>.
    /// </summary>
    private static string SwiftTrim(string? s) => (s ?? "").Trim();

    // ── Turns ────────────────────────────────────────────────────────────────────────────

    /// <summary>Next turn number (1-based).</summary>
    [JsonIgnore]
    public int NextTurnNumber => (Turns.Count == 0 ? 0 : Turns.Max(t => t.N)) + 1;

    /// <summary>
    /// Skill turns the model actually received (completed or interrupted), by command name —
    /// a skill's definition is sent only the first time (specs/SPEC-13 §Skill steps).
    /// </summary>
    [JsonIgnore]
    public IReadOnlySet<string> SkillsReceived => Turns
        .Where(t => t.Kind == InterviewTurnKind.Skill &&
                    t.Status is InterviewTurnStatus.Completed or InterviewTurnStatus.Interrupted)
        .Select(t => SkillName(t.Question))
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    /// <summary><c>/apply-instruction tech</c> → <c>"apply-instruction"</c>.</summary>
    public static string? SkillName(string command)
    {
        if (!command.StartsWith('/')) return null;
        return command[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private const string ApplyInstruction = "/apply-instruction ";

    /// <summary>The <c>apply-instruction</c> profile in force: the last one that completed.</summary>
    [JsonIgnore]
    public string? ActiveProfile => Turns
        .LastOrDefault(t => t.Kind == InterviewTurnKind.Skill && t.Status == InterviewTurnStatus.Completed &&
                            t.Question.StartsWith(ApplyInstruction, StringComparison.Ordinal))
        ?.Question[ApplyInstruction.Length..];

    /// <summary><c>live-coding-design</c> is active until the next <c>apply-instruction</c> replaces it.</summary>
    [JsonIgnore]
    public bool LiveCodingActive
    {
        get
        {
            for (var i = Turns.Count - 1; i >= 0; i--)
            {
                var t = Turns[i];
                if (t.Kind != InterviewTurnKind.Skill || t.Status != InterviewTurnStatus.Completed) continue;
                if (t.Question.StartsWith("/apply-instruction", StringComparison.Ordinal)) return false;
                if (t.Question.StartsWith("/live-coding-design", StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// On launch: any turn still <c>streaming</c> was cut off by a quit or crash
    /// (specs/SPEC-15 §History). Running prep and summary jobs fail too. Returns whether
    /// anything changed.
    /// </summary>
    public bool FailInterruptedTurns(string reason = "app closed")
    {
        var changed = false;
        foreach (var t in Turns.Where(t => t.Status == InterviewTurnStatus.Streaming))
        {
            t.Status = InterviewTurnStatus.Failed;
            t.Error = reason;
            changed = true;
        }
        if (Prep.Status == InterviewStatus.Running) { Prep.Status = InterviewStatus.Failed; changed = true; }
        if (Summary.Status == InterviewStatus.Running) { Summary.Status = InterviewStatus.Failed; changed = true; }
        return changed;
    }

    // ── Export ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// "Copy all Q&amp;A as Markdown" (specs/SPEC-15 §Results view): every turn in order, with
    /// its time into the interview when known and an Interrupted/Failed mark. Pinned by
    /// <c>testdata/records/qa-markdown.json</c>.
    /// </summary>
    public string QaMarkdown()
    {
        var output = new List<string> { $"# {Name}" };
        var who = string.Join(" — ", new[] { Setup.Company, Setup.Role }.Where(s => s.Length > 0));
        if (who.Length > 0 && who != Name) output.Add(who);
        foreach (var t in Turns)
        {
            var head = $"## {t.N}. ";
            if (t.AudioToMs is { } ms) head += $"{TimeFormat.Stamp(ms)} ";
            head += t.Kind switch
            {
                InterviewTurnKind.Typed => $"You: {t.Question}",
                InterviewTurnKind.Quick => $"Quick: {t.Question}",
                InterviewTurnKind.Skill => $"Skill: {t.Question}",
                InterviewTurnKind.Ask or InterviewTurnKind.Regenerate => t.Question,
                _ => throw new ArgumentOutOfRangeException(nameof(t), t.Kind, null),
            };
            output.Add(head);
            if (t.Images.Count > 0) output.Add($"_{t.Images.Count} screenshot(s)_");
            output.Add(t.Answer.Length == 0 ? "_(no answer)_" : t.Answer);
            if (t.Status == InterviewTurnStatus.Interrupted) output.Add("_(interrupted)_");
            if (t.Status == InterviewTurnStatus.Failed)
                output.Add($"_(failed{(t.Error is null ? "" : $": {t.Error}")})_");
        }
        return string.Join("\n\n", output);
    }

    /// <summary>The on-disk (and database) spelling of an enum value, e.g. <c>"completed"</c>.</summary>
    internal static string Raw<T>(T value) where T : struct, Enum => RawNames<T>.Name[value];

    /// <summary>The enum value spelled <paramref name="raw"/>, or null for anything unknown.</summary>
    internal static T? Parse<T>(string? raw) where T : struct, Enum =>
        raw is not null && RawNames<T>.Value.TryGetValue(raw, out var v) ? v : null;

    /// <summary>Raw names taken from the JSON attributes, so there is one spelling of each.</summary>
    private static class RawNames<T> where T : struct, Enum
    {
        public static readonly Dictionary<T, string> Name =
            Enum.GetValues<T>().ToDictionary(v => v, v => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(v))!);
        public static readonly Dictionary<string, T> Value =
            Name.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
    }
}

/// <summary>What the interview is for. <c>InterviewRecord.Setup</c> in Swift.</summary>
public sealed record InterviewSetup
{
    /// <summary>
    /// <c>short</c> | <c>medium</c> | <c>long</c> — kept as the raw string, as Swift does;
    /// <c>Config.Interview.AnswerLength</c> interprets it.
    /// </summary>
    [JsonPropertyName("answer_length")] public string AnswerLength { get; set; } = "medium";

    /// <summary>The interviewee — the person this interview is for.</summary>
    [JsonPropertyName("candidate"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Candidate { get; set; }

    [JsonPropertyName("company")] public string Company { get; set; } = "";

    /// <summary>The CV's title when Discovery CV ran.</summary>
    [JsonPropertyName("cv_title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CvTitle { get; set; }

    [JsonPropertyName("document_ids")] public List<string> DocumentIds { get; set; } = [];
    [JsonPropertyName("instructions")] public string Instructions { get; set; } = "";

    [JsonPropertyName("jd_text_inline"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JdTextInline { get; set; }

    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("skill_ids")] public List<string> SkillIds { get; set; } = [];

    /// <summary>Which round with this company: 1, 2, 3…</summary>
    [JsonPropertyName("step"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Step { get; set; }
}

/// <summary>
/// Legacy (schema-1 records made with the one-shot Prepare button). Interviews now run their
/// skills as <c>skill</c> turns; new records leave this at its defaults.
/// <c>InterviewRecord.Prep</c> in Swift.
/// </summary>
public sealed record InterviewPrep
{
    [JsonPropertyName("briefing")] public string Briefing { get; set; } = "";

    [JsonPropertyName("completed_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompletedAt { get; set; }

    [JsonPropertyName("extra_turns")] public int ExtraTurns { get; set; }
    [JsonPropertyName("status")] public InterviewStatus Status { get; set; } = InterviewStatus.Pending;
}

/// <summary>One question and its answer. <c>InterviewRecord.Turn</c> in Swift.</summary>
public sealed record InterviewTurn
{
    [JsonPropertyName("answer")] public string Answer { get; set; } = "";
    [JsonPropertyName("asked_at")] public string AskedAt { get; set; } = "";

    [JsonPropertyName("audio_from_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AudioFromMs { get; set; }

    [JsonPropertyName("audio_to_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AudioToMs { get; set; }

    [JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    /// <summary>Screenshot names in <c>interview_images</c> (<c>&lt;turn&gt;-&lt;i&gt;.png</c>).</summary>
    [JsonPropertyName("images")] public List<string> Images { get; set; } = [];

    [JsonPropertyName("kind")] public InterviewTurnKind Kind { get; set; }
    [JsonPropertyName("n")] public int N { get; set; }
    [JsonPropertyName("question")] public string Question { get; set; } = "";
    [JsonPropertyName("status")] public InterviewTurnStatus Status { get; set; } = InterviewTurnStatus.Streaming;

    [JsonPropertyName("total_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalMs { get; set; }

    [JsonPropertyName("ttft_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TtftMs { get; set; }
}

/// <summary>The end-of-interview summary job. <c>InterviewRecord.Summary</c> in Swift.</summary>
public sealed record InterviewSummary
{
    [JsonPropertyName("completed_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompletedAt { get; set; }

    [JsonPropertyName("file"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; set; }

    [JsonPropertyName("status")] public InterviewStatus Status { get; set; } = InterviewStatus.Pending;
}
