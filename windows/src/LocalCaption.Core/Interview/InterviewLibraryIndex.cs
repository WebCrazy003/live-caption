using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LocalCaption.Core.Data;

namespace LocalCaption.Core.Interview;

/// <summary>
/// <c>interview/library/index.json</c> (specs/SPEC-11, schema 1): the user's imported
/// documents and skills. The port of macOS's <c>InterviewLibraryIndex</c>; the file is shared
/// with the Mac build, so its keys are the Mac's.
/// </summary>
/// <remarks>
/// Decoding mirrors Swift's: the three top-level keys are optional, but every field of a
/// document or skill is required and must have the right type — a file that breaks that is
/// corrupt, and <see cref="Load"/> backs it up and starts empty, like <c>config.json</c>.
/// </remarks>
public sealed record InterviewLibraryIndex
{
    public const int CurrentSchemaVersion = 1;

    [JsonConverter(typeof(JsonStringEnumConverter<DocumentKind>))]
    public enum DocumentKind
    {
        [JsonStringEnumMemberName("cv")] Cv,
        [JsonStringEnumMemberName("jd")] Jd,
        [JsonStringEnumMemberName("notes")] Notes,
    }

    /// <summary>An imported CV, job description or notes file, stored as text.</summary>
    public sealed record Document
    {
        [JsonPropertyName("added_at"), JsonRequired] public string AddedAt { get; set; } = "";
        [JsonPropertyName("chars"), JsonRequired] public int Chars { get; set; }
        [JsonPropertyName("id"), JsonRequired] public string Id { get; set; } = NewId();
        [JsonPropertyName("kind"), JsonRequired] public DocumentKind Kind { get; set; }
        /// <summary>The imported file's original name.</summary>
        [JsonPropertyName("original"), JsonRequired] public string Original { get; set; } = "";
        [JsonPropertyName("slug"), JsonRequired] public string Slug { get; set; } = "";
        [JsonPropertyName("title"), JsonRequired] public string Title { get; set; } = "";
    }

    /// <summary>An imported skill folder (<c>SKILL.md</c> plus reference files).</summary>
    public sealed record Skill
    {
        [JsonPropertyName("added_at"), JsonRequired] public string AddedAt { get; set; } = "";
        [JsonPropertyName("chars"), JsonRequired] public int Chars { get; set; }
        /// <summary>Relative paths inside the skill folder.</summary>
        [JsonPropertyName("files"), JsonRequired] public List<string> Files { get; set; } = [];
        [JsonPropertyName("id"), JsonRequired] public string Id { get; set; } = NewId();
        [JsonPropertyName("slug"), JsonRequired] public string Slug { get; set; } = "";
        [JsonPropertyName("title"), JsonRequired] public string Title { get; set; } = "";
    }

    [JsonPropertyName("documents")] public List<Document> Documents { get; set; } = [];
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonPropertyName("skills")] public List<Skill> Skills { get; set; } = [];

    private static string NewId() => Guid.NewGuid().ToString("D").ToUpperInvariant();

    // ── Persistence ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Strict, like Swift's <c>JSONDecoder</c>: no comments or trailing commas, no null for a
    /// non-optional value, required fields present.
    /// </summary>
    private static readonly JsonSerializerOptions ReadOptions = new() { RespectNullableAnnotations = true };

    /// <summary>Indented, UTF-8 left readable and slashes unescaped, as on macOS.</summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The three top-level keys, each optional (Swift's <c>decodeIfPresent ?? default</c>).</summary>
    private sealed record Wire
    {
        [JsonPropertyName("documents")] public List<Document>? Documents { get; set; }
        [JsonPropertyName("schema_version")] public int? SchemaVersion { get; set; }
        [JsonPropertyName("skills")] public List<Skill>? Skills { get; set; }
    }

    /// <summary>
    /// Missing (or unreadable) file → empty index. A corrupt file is copied to
    /// <c>index.json.bak-&lt;yyyyMMdd-HHmmss&gt;</c> beside it and an empty index returned; the
    /// file itself is left for the next <see cref="Write"/> to replace.
    /// </summary>
    public static InterviewLibraryIndex Load(string? path = null)
    {
        path ??= AppPaths.LibraryIndex;
        string text;
        try { text = File.ReadAllText(path, Files.Utf8NoBom); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }

        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(text, ReadOptions) ?? throw new JsonException("null index");
            return new InterviewLibraryIndex
            {
                SchemaVersion = wire.SchemaVersion ?? CurrentSchemaVersion,
                Documents = wire.Documents ?? [],
                Skills = wire.Skills ?? [],
            };
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            Files.BackupCorrupt(path);
            return new();
        }
    }

    /// <summary>
    /// Atomic write (temp file, then rename). Top-level keys this build does not know — from a
    /// newer build on either platform — are carried over from the existing file. Keys are
    /// written sorted, as Swift's <c>.sortedKeys</c> does.
    /// </summary>
    public void Write(string? path = null)
    {
        path ??= AppPaths.LibraryIndex;
        var output = JsonSerializer.SerializeToNode(this, WriteOptions)!.AsObject();
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path, Files.Utf8NoBom)) is JsonObject existing)
                foreach (var (key, value) in existing)
                    if (!output.ContainsKey(key)) output[key] = value?.DeepClone();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }

        var sorted = new JsonObject();
        foreach (var (key, value) in output.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sorted[key] = value?.DeepClone();
        Files.WriteAllTextAtomic(path, sorted.ToJsonString(WriteOptions));
    }
}
