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

    [JsonPropertyName("send")] public SendGroup Send { get; set; } = new();
    [JsonPropertyName("shortcuts")] public ShortcutsGroup Shortcuts { get; set; } = new();
    [JsonPropertyName("summary")] public SummaryGroup Summary { get; set; } = new();
    [JsonPropertyName("ui")] public UiGroup Ui { get; set; } = new();
    [JsonPropertyName("window")] public WindowGroup Window { get; set; } = new();

    // ── Groups ───────────────────────────────────────────────────────────────────────────

    public sealed record AsrGroup
    {
        /// <summary>Windows-only (§9.2): <c>auto</c> | <c>cuda</c> | <c>cpu</c>.</summary>
        [JsonPropertyName("backend")] public string Backend { get; set; } = "auto";

        [JsonPropertyName("endpoint_silence_ms")] public int EndpointSilenceMs { get; set; } = 600;

        /// <summary>
        /// Windows-only. Beam width for the final model; 0 or 1 is greedy decoding, which is
        /// what §5 specified and what the default stays. 5 is whisper.cpp's own "careful"
        /// setting: it keeps several candidate transcripts alive and commits to the best
        /// whole phrase rather than the best next word — slower, and better on the rare
        /// words greedy decoding gets wrong first, which are mostly names.
        /// </summary>
        [JsonPropertyName("final_beam_size")] public int FinalBeamSize { get; set; }
        [JsonPropertyName("final_model")] public string FinalModel { get; set; } = "large-v3-turbo";
        [JsonPropertyName("interim_interval_ms")] public int InterimIntervalMs { get; set; } = 500;
        [JsonPropertyName("interim_model")] public string InterimModel { get; set; } = "tiny.en";
        [JsonPropertyName("max_utterance_s")] public int MaxUtteranceS { get; set; } = 20;

        /// <summary>Windows-only (§9.2). 0 = physical cores (8 on the target machine).</summary>
        [JsonPropertyName("threads")] public int Threads { get; set; }

        /// <summary>
        /// Windows-only. Names and terms the final model should expect — people, companies,
        /// jargon — comma-separated. Handed to whisper.cpp as its initial prompt, which biases
        /// spelling towards what it has been shown. Empty changes nothing.
        /// </summary>
        [JsonPropertyName("vocabulary")] public string Vocabulary { get; set; } = "";
    }

    public sealed record AudioGroup
    {
        /// <summary>
        /// Windows-only. Bring quiet audio up to the level speech normally has before it
        /// reaches the speech gate (<see cref="LocalCaption.Core.Audio.AutoGain"/>). On by
        /// default: without it, turning the speakers down turns the captions off.
        /// </summary>
        [JsonPropertyName("auto_gain")] public bool AutoGain { get; set; } = true;

        /// <summary>
        /// Windows-only (§4.1): <c>process</c> (default) | <c>endpoint</c> | <c>auto</c>.
        /// <c>auto</c> captures the one recognised meeting app, browser or virtual machine that
        /// is playing when capture begins, and the whole output device when that is not
        /// clear-cut. Anything unrecognised is treated as <c>endpoint</c>.
        /// </summary>
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
    /// Windows-only. Getting the last question out of this app and into the tool that answers
    /// it, without the clipboard-and-alt-tab in between. Off by default, and every part of
    /// it stays on this machine: the "address" is loopback only.
    /// </summary>
    public sealed record SendGroup
    {
        /// <summary>Also send each turn by itself once it ends. Never applies to <c>window</c>.</summary>
        [JsonPropertyName("auto")] public bool Auto { get; set; }

        /// <summary>Where <c>file</c> appends to.</summary>
        [JsonPropertyName("file")] public string File { get; set; } = Path.Combine(AppPaths.Root, "live.txt");

        /// <summary>The loopback port <c>address</c> listens on.</summary>
        [JsonPropertyName("port")] public int Port { get; set; } = 17653;

        /// <summary>Press Enter (or the page's send button) after the text goes in.</summary>
        [JsonPropertyName("submit")] public bool Submit { get; set; }

        /// <summary><c>off</c> | <c>window</c> (paste into it) | <c>address</c> (browser extension, or your own tool) | <c>file</c>.</summary>
        [JsonPropertyName("target")] public string Target { get; set; } = "off";

        /// <summary>Wrapped around what is sent; <c>{text}</c> is the question.</summary>
        [JsonPropertyName("template")] public string Template { get; set; } = "{text}";

        /// <summary>How much quiet ends a turn. Also what "copy last question" goes by.</summary>
        [JsonPropertyName("turn_gap_ms")] public int TurnGapMs { get; set; } = 2500;

        /// <summary><c>window</c>: part of the title of the window to paste into.</summary>
        [JsonPropertyName("window_title")] public string WindowTitle { get; set; } = "";
    }

    /// <summary>One programmable shortcut: a gesture such as <c>Ctrl+Alt+P</c>, or empty for unbound.</summary>
    /// <remarks>
    /// <c>global</c> registers it system-wide, so it fires while the meeting app has focus —
    /// which during a call is always. Windows will not register a global gesture without a
    /// modifier, so <c>Space</c> can only ever be a window shortcut.
    /// </remarks>
    public sealed record Shortcut
    {
        public Shortcut() { }
        public Shortcut(string keys, bool global = false) { Keys = keys; Global = global; }

        [JsonPropertyName("global")] public bool Global { get; set; }
        [JsonPropertyName("keys")] public string Keys { get; set; } = "";
    }

    /// <summary>
    /// Windows-only. A property per action rather than a dictionary, deliberately: records
    /// compare by value and a dictionary member would not, which would break the
    /// write→read identity the conformance vectors assert. The defaults are §7.5's keys.
    /// </summary>
    public sealed record ShortcutsGroup
    {
        [JsonPropertyName("bookmark")] public Shortcut Bookmark { get; set; } = new("Ctrl+M");
        [JsonPropertyName("copy_all")] public Shortcut CopyAll { get; set; } = new("Ctrl+Shift+C");
        [JsonPropertyName("copy_last_n")] public Shortcut CopyLastN { get; set; } = new("Ctrl+C");

        // Function keys for the three that are wanted mid-call. They type nothing, no browser
        // or meeting app claims them, and — unlike anything built on Ctrl+Alt — no virtual
        // machine uses them to release the keyboard.
        [JsonPropertyName("copy_last_question")] public Shortcut CopyLastQuestion { get; set; } = new("F8");
        [JsonPropertyName("focus_search")] public Shortcut FocusSearch { get; set; } = new("Ctrl+F");
        [JsonPropertyName("font_bigger")] public Shortcut FontBigger { get; set; } = new("Ctrl+=");
        [JsonPropertyName("font_smaller")] public Shortcut FontSmaller { get; set; } = new("Ctrl+-");
        [JsonPropertyName("jump_latest")] public Shortcut JumpLatest { get; set; } = new("Ctrl+J");
        [JsonPropertyName("pause_resume")] public Shortcut PauseResume { get; set; } = new("Space");
        [JsonPropertyName("send_last_question")] public Shortcut SendLastQuestion { get; set; } = new("F9");
        [JsonPropertyName("settings")] public Shortcut Settings { get; set; } = new("Ctrl+,");
        [JsonPropertyName("start")] public Shortcut Start { get; set; } = new("Ctrl+N");
        [JsonPropertyName("stop")] public Shortcut Stop { get; set; } = new("Ctrl+.");
        [JsonPropertyName("toggle_click_through")] public Shortcut ToggleClickThrough { get; set; } = new("F7");
        [JsonPropertyName("toggle_pin")] public Shortcut TogglePin { get; set; } = new("Ctrl+T");
        [JsonPropertyName("toggle_recording")] public Shortcut ToggleRecording { get; set; } = new("");
        [JsonPropertyName("toggle_sidebar")] public Shortcut ToggleSidebar { get; set; } = new("Ctrl+B");
        [JsonPropertyName("toggle_theme")] public Shortcut ToggleTheme { get; set; } = new("Ctrl+Shift+L");
    }

    /// <summary>Windows-only shell state: theme, sidebar, and the pin.</summary>
    public sealed record UiGroup
    {
        /// <summary>
        /// Keep the window above the meeting app. Separate from the reserved
        /// <c>window.always_on_top</c>, whose default of <c>true</c> exists for macOS
        /// interchange (§7.3) and would pin every existing install the moment it was honoured.
        /// </summary>
        [JsonPropertyName("pin_on_top")] public bool PinOnTop { get; set; }

        [JsonPropertyName("sidebar_collapsed")] public bool SidebarCollapsed { get; set; }
        [JsonPropertyName("sidebar_width")] public double SidebarWidth { get; set; } = 260;

        /// <summary><c>system</c> (follow Windows) | <c>dark</c> | <c>light</c>.</summary>
        [JsonPropertyName("theme")] public string Theme { get; set; } = "system";
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

        /// <summary>
        /// How solid the window's background is, 0.1–1.0. §7.3 cut this for the remote-desktop
        /// workflow; it is back for the local one — captions laid over a video call — and its
        /// default of 1.0 means a config that never touches it sees no change. Only the
        /// grounds fade; text stays fully opaque, which is what makes it usable.
        /// </summary>
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
