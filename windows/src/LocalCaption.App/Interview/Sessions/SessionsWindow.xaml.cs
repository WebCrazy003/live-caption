using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;

namespace LocalCaption.App.Interview.Sessions;

/// <summary>
/// The Sessions window (specs/SPEC-16 §5.6): search, Show All / Captions / Interviews, the six
/// sorts, rename, Show in folder and delete on the left; the selected session on the right,
/// with Open in Interview Panel for interviews. Replaces opening the <c>.txt</c> in an editor;
/// the main window's sidebar stays as the quick list.
/// </summary>
/// <remarks>
/// <para>Non-modal and single-instance: the host keeps one and re-activates it (Ctrl+L, the
/// toolbar button, double-click in the sidebar → <see cref="Select"/>).</para>
/// <para>The list is re-queried when Show or Sort changes or <see cref="Reload"/> is called;
/// typing in the search box only filters the rows already read, so a keystroke never touches
/// the database.</para>
/// </remarks>
public partial class SessionsWindow : ChromeWindow
{
    private readonly SessionsHost _host;
    private List<SessionRow> _all = [];
    private long? _selected;
    private bool _loading = true;
    private bool _refilling;

    public SessionsWindow(SessionsHost host)
    {
        _host = host;
        InitializeComponent();

        ShowBox.ItemsSource = SessionFilterChoice.All;
        ShowBox.SelectedIndex = 0;
        SortBox.ItemsSource = SessionSortChoice.All;
        SortBox.SelectedIndex = 0;

        Detail.Attach(host);
        Detail.DeleteRequested += id => Delete(id);
        Detail.OpenedInPanel += Close;

        RestorePlacement();
        Activated += (_, _) => RefreshOpenState();
        Closing += OnClosing;
        Loaded += (_, _) => SearchBox.Focus();
        PreviewKeyDown += OnWindowKey;

        _loading = false;
        Reload();
    }

    // ── public surface for the host ──────────────────────────────────────────────────────

    /// <summary>Re-read the list (sessions saved, renamed or deleted elsewhere) and the shown session.</summary>
    public void Reload()
    {
        var filter = (ShowBox.SelectedItem as SessionFilterChoice)?.Filter ?? SessionModeFilter.All;
        var sort = (SortBox.SelectedItem as SessionSortChoice)?.Sort ?? SessionSort.CreatedDesc;
        try
        {
            var sessions = _host.Store.All(sort, null, SessionFilterChoice.Mode(filter));
            // The linked interviews' list columns, for the row subtitles and the search over
            // interview fields — one light query, no turns.
            IReadOnlyList<InterviewListing> interviews =
                filter == SessionModeFilter.Captions ? [] : _host.Store.InterviewListings();
            _all = SessionRow.Build(sessions, interviews);
        }
        catch (Exception e)
        {
            _all = [];
            SessionShell.Report(this, $"The session list could not be read.\n\n{e.Message}");
        }
        ApplySearch();
        Detail.Reload();
    }

    /// <summary>
    /// Show <paramref name="sessionId"/> (the sidebar's double-click). Clears a search or a
    /// filter that hides it.
    /// </summary>
    public void Select(long sessionId)
    {
        if (!_all.Any(r => r.Id == sessionId))
        {
            _loading = true;
            SearchBox.Text = "";
            ShowBox.SelectedIndex = 0;
            _loading = false;
            Reload();
        }
        if (_all.FirstOrDefault(r => r.Id == sessionId) is { } row)
        {
            List.SelectedItem = row;
            List.ScrollIntoView(row);
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Re-evaluate Open in Interview Panel. The host calls this when recording, the speech
    /// model or the interview controller's busy state changes; activation does it too.
    /// </summary>
    public void RefreshOpenState() => Detail.RefreshOpenState();

    // ── list ─────────────────────────────────────────────────────────────────────────────

    private void ApplySearch()
    {
        var query = SearchBox.Text.Trim();
        var rows = query.Length == 0 ? _all : _all.Where(r => r.Matches(query)).ToList();

        // Repopulating clears the selection for a moment; the detail pane should not reload
        // (and rebuild its replay) for that, only when the selection really changes.
        _refilling = true;
        try
        {
            List.ItemsSource = rows;
            List.SelectedItem = _selected is { } id ? rows.FirstOrDefault(r => r.Id == id) : null;
        }
        finally
        {
            _refilling = false;
        }
        if (List.SelectedItem is null && _selected is not null)
        {
            _selected = null;
            Detail.ShowSession(null);
        }

        EmptyList.Text = query.Length == 0 ? "No sessions yet" : "No matches";
        EmptyList.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) ApplySearch();
    }

    private void OnQueryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) Reload();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refilling) return;
        var id = (List.SelectedItem as SessionRow)?.Id;
        if (id == _selected) return;
        _selected = id;
        Detail.ShowSession(id);
    }

    private void OnItemRightClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { IsSelected: false } item) item.IsSelected = true;
    }

    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Delete when Current() is { } row:
                Delete(row.Id);
                e.Handled = true;
                break;
            case Key.F2 when Current() is { } row:
                Rename(row);
                e.Handled = true;
                break;
        }
    }

    private SessionRow? Current() => List.SelectedItem as SessionRow;

    /// <summary>Ctrl+F: back to the search box, as in Explorer.</summary>
    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        var row = Current();
        MenuRename.IsEnabled = MenuDelete.IsEnabled = row is not null;
        MenuShow.IsEnabled = row is not null && SessionFiles.HasExport(row.Record);
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (Current() is { } row) Rename(row);
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (Current() is { } row) SessionShell.ShowInFolder(this, row.Record.TranscriptFile);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Current() is { } row) Delete(row.Id);
    }

    // ── actions ──────────────────────────────────────────────────────────────────────────

    private void Rename(SessionRow row)
    {
        if (RenameDialog.Ask(this, row.Name) is not { } name || name == row.Name) return;
        try
        {
            // The row's name only; the transcript file keeps its name (SPEC.md §10, C9).
            _host.Store.Rename(row.Id, name);
        }
        catch (Exception ex)
        {
            SessionShell.Report(this, $"The session could not be renamed.\n\n{ex.Message}");
            return;
        }
        Reload();
        _host.SessionsChanged?.Invoke();
    }

    /// <summary>
    /// The §5.6 delete: ask, then remove the row (its caption segments cascade), the transcript
    /// files (Recycle Bin) and the interview data, as chosen.
    /// </summary>
    private void Delete(long id)
    {
        var store = _host.Store;
        if (store.Fetch(id) is not { } record)
        {
            Reload();
            return;
        }

        var hasFile = SessionFiles.HasExport(record);
        var choice = SessionDeleteDialog.Ask(this, record.SessionName, record.IsInterview, hasFile);
        if (choice == SessionDeleteDialog.Choice.Cancel) return;

        var alsoFile = hasFile && choice is SessionDeleteDialog.Choice.SessionAndFile
                                         or SessionDeleteDialog.Choice.SessionInterviewAndFile;
        var alsoInterview = choice is SessionDeleteDialog.Choice.SessionAndInterview
                                   or SessionDeleteDialog.Choice.SessionInterviewAndFile;

        try
        {
            // Read the interviews before the row goes: interviews.session_id is ON DELETE SET
            // NULL, so afterwards they can no longer be found by session. (The Mac reads it
            // after the delete and so never finds it — SPEC-16 §9 material.) Every interview
            // linked to the session, not just the newest, or older ones would be orphaned.
            IReadOnlyList<InterviewRecord> saved = alsoInterview ? store.Interviews(id) : [];

            // Files first: if the transcript will not go to the Recycle Bin (open in an
            // editor), nothing is deleted and the row still points at it.
            if (alsoFile && !SessionShell.RecycleTranscript(record.TranscriptFile!))
            {
                SessionShell.Report(this,
                    "The transcript file could not be moved to the Recycle Bin — is it open somewhere? Nothing was deleted.");
                return;
            }

            store.Delete(id);
            foreach (var interview in saved)
            {
                // Rows, turns and screenshots go together; the Codex thread is archived too,
                // so the CV doesn't linger in its session store (SPEC-12 §Threads & turns) —
                // the host leaves it in Caption only mode rather than start Codex.
                store.DeleteInterview(interview.Id);
                if (interview.ThreadId is { Length: > 0 } thread) _host.ArchiveThread?.Invoke(thread);
            }
        }
        catch (Exception ex)
        {
            SessionShell.Report(this, $"The session could not be deleted.\n\n{ex.Message}");
        }

        if (_selected == id)
        {
            _selected = null;
            Detail.ShowSession(null);
        }
        Reload();
        _host.SessionsChanged?.Invoke();
    }

    // ── placement ────────────────────────────────────────────────────────────────────────

    private void RestorePlacement()
    {
        if (_host.LoadPlacement?.Invoke() is not { } bounds) return;
        if (double.IsNaN(bounds.Width) || bounds.Width < MinWidth || bounds.Height < MinHeight) return;
        if (!OnAMonitor(bounds)) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_host.SavePlacement is not { } save) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty && !double.IsNaN(bounds.Width) && bounds.Width > 0) save(bounds);
    }

    /// <summary>
    /// The saved frame must land on a connected display (docked/undocked laptops), as
    /// <see cref="WindowPlacement"/> insists for the main window. The DIP rectangle is scaled by
    /// the main window's DPI for the monitor test — approximate across mixed-DPI monitors, which
    /// errs towards centring.
    /// </summary>
    private static bool OnAMonitor(Rect bounds)
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.IntersectsWith(bounds)) return false;

        var scale = Application.Current?.MainWindow is { } main ? VisualTreeHelper.GetDpi(main).DpiScaleX : 1.0;
        // The title bar's middle has to be reachable, or the window cannot be dragged back.
        var native = new NativeRect
        {
            Left = (int)((bounds.Left + 40) * scale),
            Top = (int)(bounds.Top * scale),
            Right = (int)((bounds.Right - 40) * scale),
            Bottom = (int)((bounds.Top + 38) * scale),
        };
        try
        {
            return MonitorFromRect(ref native, 0 /* MONITOR_DEFAULTTONULL */) != IntPtr.Zero;
        }
        catch (Exception)
        {
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
}
