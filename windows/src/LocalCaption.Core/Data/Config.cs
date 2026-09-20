using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalCaption.Core.Data;

/// <summary>
/// Versioned application configuration (SPEC.md §12.2, SPEC-WINDOWS.md §9.2,
/// <c>schema_version</c> 2).
/// </summary>
/// <remarks>
/// <para><b>Merge-defaults on load.</b> Every property carries its default as an
/// initialiser. <c>System.Text.Json</c> only assigns properties that are actually present
/// in the document, so a config missing keys is completed with defaults rather than
/// rejected — the C# equivalent of Swift's <c>decodeIfPresent ?? default</c>. A key of the
/// wrong type still throws, which is what sends it down the repair path.</para>
/// <para><b>Cross-platform interchange.</b> Keys and defaults match the macOS build so a
/// <c>config.json</c> copied between the two round-trips without loss. The
/// <c>summary</c> group is retained for that reason even though the Live AI Summary is out
/// of scope for Windows v1 — it merely defaults to <c>enabled: false</c> here and is hidden
/// in Settings.</para>
/// <para>Properties are declared in alphabetical order to mirror Swift's
/// <c>.sortedKeys</c> output, so the two platforms' files diff cleanly.</para>
/// <para>Every on-disk name is spelled out with <see cref="JsonPropertyNameAttribute"/>
/// rather than left to a naming policy, so the file format is a property of the type
/// itself and cannot be silently changed by whichever options a caller happens to pass.</para>
/// </remarks>
public sealed record Config
{
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("asr")] public AsrGroup Asr { get; set; } = new();
    [JsonPropertyName("audio")] public AudioGroup Audio { get; set; } = new();
    [JsonPropertyName("caption")] public CaptionGroup Caption { get; set; } = new();
    [JsonPropertyName("clipboard")] public ClipboardGroup Clipboard { get; set; } = new();
    [JsonPropertyName("general")] public GeneralGroup General { get; set; } = new();

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("summary")] public SummaryGroup Summary { get; set; } = new();
    [JsonPropertyName("window")] public WindowGroup Window { get; set; } = new();

    // ── Groups ───────────────────────────────────────────────────────────────────────────

    public sealed record AsrGroup
    {
        /// <summary>Windows-only (§9.2): <c>auto</c> | <c>cuda</c> | <c>cpu</c>.</summary>
        [JsonPropertyName("backend")] public string Backend { get; set; } = "auto";

        [JsonPropertyName("endpoint_silence_ms")] public int EndpointSilenceMs { get; set; } = 600;
        [JsonPropertyName("final_model")] public string FinalModel { get; set; } = "large-v3-turbo";
        [JsonPropertyName("interim_interval_ms")] public int InterimIntervalMs { get; set; } = 500;
        [JsonPropertyName("interim_model")] public string InterimModel { get; set; } = "tiny.en";
        [JsonPropertyName("max_utterance_s")] public int MaxUtteranceS { get; set; } = 20;

        /// <summary>Windows-only (§9.2). 0 = physical cores (8 on the target machine).</summary>
        [JsonPropertyName("threads")] public int Threads { get; set; }
    }

    public sealed record AudioGroup
    {
        /// <summary>Windows-only (§4.1): <c>process</c> (default) | <c>endpoint</c>.</summary>
        [JsonPropertyName("capture_mode")] public string CaptureMode { get; set; } = "process";

        /// <summary>Windows-only (§4.6). Mode B only; null follows the system default.</summary>
        [JsonPropertyName("output_device")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OutputDevice { get; set; }

        /// <summary>Windows-only (§4.5). Executable name, re-resolved at Start.</summary>
        [JsonPropertyName("target_process")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TargetProcess { get; set; }

        /// <summary>0…3 (SPEC.md §15).</summary>
        [JsonPropertyName("vad_sensitivity")] public int VadSensitivity { get; set; } = 2;
    }

    public sealed record CaptionGroup
    {
        [JsonPropertyName("auto_scroll")] public bool AutoScroll { get; set; } = true;
        [JsonPropertyName("font_size")] public int FontSize { get; set; } = 18;
        [JsonPropertyName("show_timestamps")] public bool ShowTimestamps { get; set; }
    }

    public sealed record ClipboardGroup
    {
        [JsonPropertyName("auto_copy_selection")] public bool AutoCopySelection { get; set; }
        [JsonPropertyName("auto_update")] public bool AutoUpdate { get; set; }
        [JsonPropertyName("recent_sentences")] public int RecentSentences { get; set; } = 10;
    }

    public sealed record GeneralGroup
    {
        [JsonPropertyName("session_name_prefix")] public string SessionNamePrefix { get; set; } = "Interview ";
        [JsonPropertyName("transcript_folder")] public string TranscriptFolder { get; set; } = AppPaths.Transcripts;
    }

    /// <summary>
    /// Live AI Summary (SPEC-10). Out of scope for Windows v1 (§1.3) — the group is kept so
    /// the two platforms' config files stay interchangeable, and defaults to disabled. When
    /// it is wanted, §16.7 points <c>server_url</c> at a bundled <c>llama-server.exe</c>.
    /// </summary>
    public sealed record SummaryGroup
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("max_bullets")] public int MaxBullets { get; set; }
        [JsonPropertyName("model")] public string Model { get; set; } = "Llama-3.2-1B-Instruct-Q4_K_M";
        [JsonPropertyName("server_url")] public string ServerUrl { get; set; } = "http://127.0.0.1:8765";
        [JsonPropertyName("words_per_summary")] public int WordsPerSummary { get; set; } = 90;
    }

    public sealed record WindowGroup
    {
        /// <summary>Retained for config interchange; cut from the Windows UI (§7.3).</summary>
        [JsonPropertyName("always_on_top")] public bool AlwaysOnTop { get; set; } = true;

        [JsonPropertyName("height")] public double Height { get; set; } = 620;

        /// <summary>Retained for config interchange; cut from the Windows UI (§7.3).</summary>
        [JsonPropertyName("opacity")] public double Opacity { get; set; } = 1.0;

        [JsonPropertyName("width")] public double Width { get; set; } = 860;

        [JsonPropertyName("x"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? X { get; set; }
        [JsonPropertyName("y"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Y { get; set; }
    }

    // ── Persistence ──────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Load the config, completing missing keys with defaults and migrating older schemas.
    /// A missing file is not an error — defaults are written and returned. A corrupt file is
    /// backed up to <c>config.json.bak-&lt;ts&gt;</c> and replaced with defaults, returning
    /// <c>Repaired = true</c>. The app must never refuse to start because of its own config.
    /// </summary>
    public static (Config Config, bool Repaired) LoadOrRepair(string? path = null)
    {
        path ??= AppPaths.ConfigFile;

        if (!File.Exists(path))
        {
            var fresh = new Config();
            TryWrite(fresh, path);
            return (fresh, false);
        }

        try
        {
            var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), ReadOptions)
                         ?? throw new JsonException("config decoded to null");
            if (config.SchemaVersion < CurrentSchemaVersion)
            {
                config.Migrate();
                TryWrite(config, path);
            }
            return (config, false);
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            var backup = Path.Combine(Path.GetDirectoryName(path) ?? ".",
                                      $"config.json.bak-{BackupStamp()}");
            try { File.Copy(path, backup, overwrite: false); } catch (IOException) { /* keep the first */ }
            var fresh = new Config();
            TryWrite(fresh, path);
            return (fresh, true);
        }
    }

    /// <summary>Atomic write — temp file then rename — so a crash mid-write cannot truncate it.</summary>
    public void Write(string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, WriteOptions), Files.Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }

    private static void TryWrite(Config config, string path)
    {
        try { config.Write(path); } catch (IOException) { /* read-only volume: run from memory */ }
    }

    /// <summary>
    /// Migration hook keyed on <c>schema_version</c>. v1 had no distinct on-disk shape, so
    /// today this is a version bump; real steps go here as the schema evolves.
    /// </summary>
    private void Migrate() => SchemaVersion = CurrentSchemaVersion;

    internal static string BackupStamp() =>
        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
}
