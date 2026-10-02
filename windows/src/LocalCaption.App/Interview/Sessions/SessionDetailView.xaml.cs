using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;
using LocalCaption.Core.Transcripts;
using LocalCaption.Interview;

namespace LocalCaption.App.Interview.Sessions;

/// <summary>
/// The Sessions window's right-hand pane: one saved session, read-only (specs/SPEC-16 §5.6,
/// Mac <c>SessionDetailView.swift</c>). Everything comes from the database.
/// </summary>
public partial class SessionDetailView : UserControl
{
    /// <summary>One cell of the details grid.</summary>
    public sealed record DetailItem(string Label, string Value);

    private SessionsHost? _host;
    private long? _sessionId;
    private SessionRecord? _record;
    private InterviewRecord? _interview;

    public SessionDetailView()
    {
        InitializeComponent();
        Unloaded += (_, _) => DisposeReplay();
        ShowPlaceholder("Select a session to see its details.", "");
    }

    /// <summary>Delete… was pressed: the window asks how far to go (shared with the list).</summary>
    public event Action<long>? DeleteRequested;

    /// <summary>Open in Interview Panel ran; the window closes.</summary>
    public event Action? OpenedInPanel;

    /// <summary>The session shown, if any.</summary>
    public long? SessionId => _sessionId;

    public void Attach(SessionsHost host) => _host = host;

    /// <summary>Show <paramref name="sessionId"/> (null: the empty state), re-read from the database.</summary>
    public void ShowSession(long? sessionId)
    {
        _sessionId = sessionId;
        Load();
    }

    /// <summary>Re-read the shown session — after a rename, or a change made elsewhere.</summary>
    public void Reload() => Load();

    /// <summary>
    /// Re-evaluate Open in Interview Panel against the host's blocker, and let the replay
    /// re-ask whether Summarize interview is allowed (it follows the app's mode).
    /// </summary>
    public void RefreshOpenState()
    {
        if (ReplaySlot.Content is Answers.InterviewReplayView replay) replay.Requery();
        if (_host is null || _interview is null || _host.OpenInPanel is null)
        {
            OpenRow.Visibility = Visibility.Collapsed;
            return;
        }

        OpenRow.Visibility = Visibility.Visible;
        var blocker = _host.OpenBlocker();
        OpenButton.IsEnabled = blocker is null;
        OpenButton.ToolTip = blocker ?? "Show this interview in the main window — summary and follow-up prompts";
        BlockerText.Text = blocker ?? "";
        BlockerText.Visibility = blocker is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Load()
    {
        DisposeReplay();
        _record = null;
        _interview = null;

        if (_host is null || _sessionId is not { } id)
        {
            ShowPlaceholder("Select a session to see its details.", "");
            return;
        }

        var store = _host.Store;
        _record = store.Fetch(id);
        if (_record is null)
        {
            ShowPlaceholder("Session not found.", "");
            return;
        }

        Placeholder.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;

        var when = TimeFormat.ParseIso(_record.CreatedAt) is { } at
            ? TimeFormat.Human(at.ToLocalTime())
            : _record.CreatedAt;
        NameText.Text = _record.SessionName;
        NameText.ToolTip = _record.SessionName;
        WhenText.Text = $"{when} · {TimeFormat.Clock(_record.DurationSeconds)}";
        ShowInFolderButton.Visibility = SessionFiles.HasExport(_record) ? Visibility.Visible : Visibility.Collapsed;

        if (_record.IsInterview) _interview = store.Interview(id);

        var items = _interview is null ? [] : DetailsOf(_interview);
        Details.ItemsSource = items;
        Details.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshOpenState();

        // The captions are the database's (session_segments), never the .txt export.
        var text = new Transcript(store.Segments(id)).Body(_host.ShowTimestamps());

        object? replay = _interview is not null && _host.CreateReplay is { } factory ? factory(_interview, text) : null;
        ReplaySlot.Content = replay;
        if (replay is not null)
        {
            ReplaySlot.Visibility = Visibility.Visible;
            Transcript.Visibility = Visibility.Collapsed;
            EmptyTranscript.Visibility = Visibility.Collapsed;
            Transcript.Show("");
        }
        else if (text.Length == 0)
        {
            ReplaySlot.Visibility = Visibility.Collapsed;
            Transcript.Visibility = Visibility.Collapsed;
            EmptyTranscript.Visibility = Visibility.Visible;
            Transcript.Show("");
        }
        else
        {
            ReplaySlot.Visibility = Visibility.Collapsed;
            EmptyTranscript.Visibility = Visibility.Collapsed;
            Transcript.Visibility = Visibility.Visible;
            Transcript.FontSize = Math.Clamp(_host.CaptionFontSize(), 8, 72);
            Transcript.Show(text);
        }
    }

    /// <summary>
    /// The Mac's details grid: Interviewee, Company, Step, Role (the interview's name unless it
    /// is the default "Interview"), Mode (the active profile), CV, Model · effort, Questions
    /// (turns that are not skill steps). Empty values are left out.
    /// </summary>
    public static IReadOnlyList<DetailItem> DetailsOf(InterviewRecord rec)
    {
        var questions = rec.Turns.Count(t => t.Kind != InterviewTurnKind.Skill);
        var mode = InterviewSteps.ProfileFromRaw(rec.ActiveProfile)?.Label() ?? "";
        var step = rec.Setup.Step?.ToString(CultureInfo.InvariantCulture) ?? "";
        var role = rec.Name == "Interview" ? "" : rec.Name;
        DetailItem[] all =
        [
            new("Interviewee", rec.Setup.Candidate ?? ""),
            new("Company", rec.Setup.Company),
            new("Step", step),
            new("Role", role),
            new("Mode", mode),
            new("CV", rec.Setup.CvTitle ?? ""),
            new("Model", rec.Model + " · " + rec.ReasoningEffort),
            new("Questions", questions.ToString(CultureInfo.CurrentCulture)),
        ];
        return all.Where(i => i.Value.Length > 0).ToList();
    }

    private void ShowPlaceholder(string message, string glyph)
    {
        Body.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        PlaceholderText.Text = message;
        PlaceholderGlyph.Text = glyph;
        Details.ItemsSource = null;
        ReplaySlot.Content = null;
        Transcript.Show("");
    }

    private void DisposeReplay()
    {
        if (ReplaySlot.Content is IDisposable disposable) disposable.Dispose();
        ReplaySlot.Content = null;
    }

    // ── actions ──────────────────────────────────────────────────────────────────────────

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (_host?.OpenInPanel is not { } open || _sessionId is not { } id || _interview is null) return;

        // The main window may have changed since the button was last painted.
        if (_host.OpenBlocker() is not null)
        {
            RefreshOpenState();
            return;
        }

        if (_host.HasUnstartedPreparation() &&
            !ConfirmDialog.Ask(Window.GetWindow(this), "Discard the current preparation?",
                "The main window has a preparation that hasn't started recording. Opening this interview deletes it.",
                "Discard and Open"))
            return;

        var saved = _host.Store.Interview(id) ?? _interview;
        open(saved);
        OpenedInPanel?.Invoke();
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e) =>
        SessionShell.ShowInFolder(Window.GetWindow(this), _record?.TranscriptFile);

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_sessionId is { } id) DeleteRequested?.Invoke(id);
    }
}
