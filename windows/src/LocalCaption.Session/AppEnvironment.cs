using LocalCaption.Core;
using LocalCaption.Core.Data;
using LocalCaption.Core.Transcripts;

namespace LocalCaption.Session;

/// <summary>
/// The things a session needs that outlive it: configuration, the session index, and the
/// leftover journals found at launch.
/// </summary>
/// <remarks>
/// Port of the macOS <c>AppEnvironment.swift</c>. Created once and handed to every
/// <see cref="SessionController"/>.
/// </remarks>
public sealed class AppEnvironment : IDisposable
{
    private readonly List<RecoveredSession> _pending;

    public AppEnvironment(Config? config = null, Store? store = null)
    {
        if (config is not null)
        {
            Config = config;
        }
        else
        {
            // A corrupt config is repaired rather than fatal — the app must never refuse to
            // start because of its own settings file (§9.2).
            var (loaded, repaired) = Config.LoadOrRepair();
            Config = loaded;
            ConfigWasRepaired = repaired;
        }

        if (store is null)
        {
            Store = new Store();
            // Sessions saved before captions moved into the database get them copied in from
            // their transcript files (specs/SPEC-16 §2.3). Off the UI thread and on its own
            // connection, because a file on an offline network drive can block for seconds.
            _ = Task.Run(() =>
            {
                try { using var importer = new Store(); importer.ImportTranscriptFiles(); }
                catch (Exception) { }   // the sessions stay listed; only their text is missing
            });
        }
        else
        {
            Store = store;
        }

        // §9.4: journals that outlived their session mean the app quit or crashed mid
        // recording. Their captions are on disk and can still become transcripts.
        _pending = [.. Journal.Pending()];
    }

    public Config Config { get; private set; }

    /// <summary>True when the config on disk was unreadable and has been replaced with
    /// defaults, its original kept as <c>config.json.bak-&lt;ts&gt;</c> (§9.2).</summary>
    public bool ConfigWasRepaired { get; }

    public Store Store { get; }

    /// <summary>Unsaved sessions found at launch, offered for recovery (§9.4).</summary>
    public IReadOnlyList<RecoveredSession> PendingRecoveries => _pending;

    /// <summary>
    /// How to put text on the clipboard. Injected because the clipboard belongs to the UI
    /// framework — and because §7.4 requires a retry the WPF layer owns, since Windows 11
    /// Clipboard History, Office and every clipboard manager contend for it.
    /// </summary>
    public Func<string, bool>? Clipboard { get; set; }

    /// <summary>Save the config and keep the in-memory copy in step.</summary>
    public void Update(Config config)
    {
        Config = config;
        config.Write();
    }

    // ── crash recovery (§9.4) ────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuild a transcript from a leftover journal, save it, then remove the journal.
    /// </summary>
    /// <remarks>
    /// The recovered session is named with a <c>(recovered)</c> suffix and timed from its own
    /// segments: the journal is the only record of when it ran, since nothing reached the
    /// database.
    /// </remarks>
    public bool Recover(RecoveredSession session)
    {
        if (session.Segments.Count == 0)
        {
            Discard(session);
            return true;
        }

        var transcript = new Transcript(session.Segments);

        // Back to local time before anything formats it. Segments record `created_at` as ISO
        // UTC (§9.1), and TimeFormat.ParseIso hands back a UTC offset — so a recovered
        // session would be headed and *named* hours away from when it actually ran, while a
        // normally-saved one uses DateTimeOffset.Now. The Swift original cannot hit this:
        // its Date carries no offset and the formatter is local either way.
        var start = (session.StartedAt ?? DateTimeOffset.Now).ToLocalTime();
        var end = (TimeFormat.ParseIso(session.Segments[^1].CreatedAt) ?? start).ToLocalTime();
        var durationMs = session.Segments.Max(s => s.TEndMs);
        var name = Config.General.SessionNamePrefix + TimeFormat.FileStamp(start) + " (recovered)";

        // A recovery that cannot reach the database keeps its journal, to be retried.
        SessionRecord saved;
        try
        {
            saved = Store.Insert(new SessionRecord
            {
                SessionName = name,
                CreatedAt = TimeFormat.Iso(start),
                EndedAt = TimeFormat.Iso(end),
                DurationSeconds = durationMs / 1000,
            }, session.Segments);
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            SessionFiles.Export(Store, saved.Id!.Value, transcript, name, start, end, durationMs / 1000,
                                Config.General.TranscriptFolder, Config.Caption.ShowTimestamps);
        }
        catch (Exception) { /* saved; only the export is missing */ }

        Journal.Remove(session.Path);
        _pending.RemoveAll(p => p.Path == session.Path);
        return true;
    }

    /// <summary>Throw a leftover journal away without saving it.</summary>
    public void Discard(RecoveredSession session)
    {
        Journal.Remove(session.Path);
        _pending.RemoveAll(p => p.Path == session.Path);
    }

    public void Dispose() => Store.Dispose();
}
