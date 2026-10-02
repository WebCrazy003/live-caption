using System.Windows;
using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;

namespace LocalCaption.App.Interview.Sessions;

/// <summary>
/// What the Sessions window needs from the app (specs/SPEC-16 §5.6). The window reads and
/// writes the database itself; everything that touches the main window, Codex or the config
/// goes through here, so the window never reaches into <c>MainWindow</c>.
/// </summary>
/// <remarks>
/// Every callback is invoked on the UI thread. All are optional except <see cref="Store"/>;
/// a missing one disables the feature it serves (no Open in Interview Panel without
/// <see cref="OpenInPanel"/>, no replay without <see cref="CreateReplay"/> — the transcript is
/// shown instead).
/// </remarks>
public sealed class SessionsHost
{
    /// <summary>The app's store (<c>AppEnvironment.Store</c>). Used on the UI thread only.</summary>
    public required Store Store { get; init; }

    /// <summary><c>caption.show_timestamps</c>, read each time a transcript is rebuilt.</summary>
    public Func<bool> ShowTimestamps { get; init; } = () => false;

    /// <summary><c>caption.font_size</c> in points, for the read-only transcript.</summary>
    public Func<double> CaptionFontSize { get; init; } = () => 18;

    /// <summary>
    /// Why Open in Interview Panel can't run now, or null when it can. The Mac's
    /// <c>openInPanelBlocker</c>: "Stop the current session first" / "Wait until the speech
    /// model is ready" / "Wait for the current answer to finish". Re-read whenever the window
    /// is activated or <see cref="SessionsWindow.RefreshOpenState"/> is called.
    /// </summary>
    public Func<string?> OpenBlocker { get; init; } = () => null;

    /// <summary>
    /// <c>InterviewController.HasUnstartedPreparation</c>: true asks "Discard the current
    /// preparation?" before <see cref="OpenInPanel"/> runs.
    /// </summary>
    public Func<bool> HasUnstartedPreparation { get; init; } = () => false;

    /// <summary>
    /// Show a saved interview in the main window's interview panel. The host discards an
    /// unstarted preparation first (the user has already confirmed), switches to Interview
    /// mode, calls <c>InterviewController.Load</c> and opens the session's captions — the Mac's
    /// <c>AppEnvironment.openInPanel</c>. The record was just re-read from the database. The
    /// Sessions window closes itself afterwards.
    /// </summary>
    public Action<InterviewRecord>? OpenInPanel { get; init; }

    /// <summary>
    /// Archive a deleted interview's Codex thread (<c>thread/archive</c>, so the CV does not
    /// linger in Codex's session store). Fire and forget: the rows are already gone. The app uses
    /// <c>InterviewServices.ArchiveThreadAsync</c>, which leaves the thread (and logs it) in
    /// Caption only mode unless codex is already running — that mode never starts Codex.
    /// </summary>
    public Action<string>? ArchiveThread { get; init; }

    /// <summary>
    /// Build the read-only replay for a saved interview (<c>InterviewReplayView</c>): the record
    /// and the session's caption transcript (rebuilt from <c>session_segments</c>, honouring
    /// <c>show_timestamps</c>). The result goes into a <c>ContentPresenter</c>; return null to
    /// show the transcript instead. When the returned object is <see cref="IDisposable"/> it is
    /// disposed when the selection changes or the window closes.
    /// </summary>
    public Func<InterviewRecord, string, object?>? CreateReplay { get; init; }

    /// <summary>A session was renamed or deleted here: refresh the main window's sidebar.</summary>
    public Action? SessionsChanged { get; init; }

    /// <summary>
    /// The window's last restore bounds, in device-independent pixels, or null for the default
    /// (980 × 640, centred on the owner). Off-screen bounds are ignored.
    /// </summary>
    public Func<Rect?>? LoadPlacement { get; init; }

    /// <summary>Remember the window's restore bounds; called when it closes.</summary>
    public Action<Rect>? SavePlacement { get; init; }
}
