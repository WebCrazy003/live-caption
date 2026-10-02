using System.Windows.Threading;
using LocalCaption.Interview;

namespace LocalCaption.App.Interview.Views.Prep;

/// <summary>
/// What <see cref="CodexStatusRow"/> shows, read from <see cref="CodexService"/> (Mac
/// <c>CodexStatusRow</c>). Nothing here starts Codex: every engine call is behind a button,
/// except <see cref="CheckIfUnchecked"/>, which the row only calls when its host opted in.
/// </summary>
internal sealed class CodexStatusViewModel : PrepObservable
{
    private readonly CodexService _codex;
    private readonly PrepRefresher _refresher;
    private string? _commandError;

    public CodexStatusViewModel(CodexService codex, Dispatcher dispatcher)
    {
        _codex = codex;
        _refresher = new PrepRefresher(dispatcher, Refresh);
        SignInCommand = new PrepAsyncCommand(SignInAsync, onError: Fail);
        CancelCommand = new PrepAsyncCommand(() => _codex.CancelSignInAsync(), onError: Fail);
        CheckCommand = new PrepAsyncCommand(CheckAsync, onError: Fail);
    }

    public PrepAsyncCommand SignInCommand { get; }
    public PrepAsyncCommand CancelCommand { get; }
    public PrepAsyncCommand CheckCommand { get; }

    /// <summary>Start listening (the row is on screen).</summary>
    public void Attach()
    {
        _codex.Changed -= OnCodexChanged;
        _codex.Changed += OnCodexChanged;
        Refresh();
    }

    /// <summary>Stop listening (the row left the screen): <see cref="CodexService"/> is app-wide and outlives it.</summary>
    public void Detach() => _codex.Changed -= OnCodexChanged;

    /// <summary>The Mac row's <c>.task { if status == nil { refresh() } }</c>, for hosts that want it.</summary>
    public void CheckIfUnchecked()
    {
        if (_codex.Status is null && !_codex.Checking && CheckCommand.CanExecute(null)) CheckCommand.Execute(null);
    }

    private void OnCodexChanged(object? sender, EventArgs e) => _refresher.Request();

    // ── state ────────────────────────────────────────────────────────────────────────────

    /// <summary>"Checking Codex…" with a spinner.</summary>
    public bool IsChecking => _codex.Checking;

    /// <summary>Never checked, and nothing checking: the row offers Check now instead of spinning forever.</summary>
    public bool IsUnchecked => _codex.Status is null && !_codex.Checking;

    /// <summary>A status to show (not while a check runs, as on the Mac).</summary>
    public bool HasStatus => _codex.Status is not null && !_codex.Checking;

    public bool IsReady => _codex.IsReady;

    public string StatusText => _codex.Status?.Summary ?? "";

    private bool SignedOut => _codex.Status is EngineStatus.SignedOut;

    public bool ShowSignIn => HasStatus && SignedOut && _codex.SignIn is null;

    public bool ShowCancel => HasStatus && SignedOut && _codex.SignIn is not null;

    public bool ShowCheckAgain => HasStatus && !SignedOut && !_codex.IsReady;

    public bool ShowBrowserHint => _codex.SignIn is not null;

    /// <summary>The sign-in problem, or a button's own failure.</summary>
    public string? ErrorText => _codex.SignInError ?? _commandError;

    public bool HasError => ErrorText is not null;

    public bool ShowUsage => _codex.IsReady && _codex.Usage?.Lowest is not null;

    public string UsageText => _codex.Usage?.Lowest is { } w ? UsageLine.For(w) : "";

    /// <summary>Below 20 % in any window: the line turns orange.</summary>
    public bool UsageIsLow => _codex.UsageIsLow;

    public void Refresh()
    {
        RaiseAll();
        SignInCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        CheckCommand.RaiseCanExecuteChanged();
    }

    // ── buttons ──────────────────────────────────────────────────────────────────────────

    private async Task SignInAsync()
    {
        _commandError = null;
        await _codex.StartSignInAsync();
    }

    private async Task CheckAsync()
    {
        _commandError = null;
        Refresh();
        await _codex.RefreshAsync();
    }

    private void Fail(Exception e)
    {
        _commandError = e.Message;
        Refresh();
    }
}
