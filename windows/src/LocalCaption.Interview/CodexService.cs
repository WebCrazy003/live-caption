using System.Diagnostics;
using System.Globalization;
using LocalCaption.Core.Interview;

namespace LocalCaption.Interview;

/// <summary>
/// App-wide view of the answer engine: sign-in state, Plus usage, the model list, and in-app
/// ChatGPT sign-in (SPEC-12 §Sign-in, §Usage) — the port of <c>CodexService.swift</c>. The
/// single consumer of the engine's notices, so Settings and the interview screen see the same
/// values.
/// </summary>
/// <remarks>
/// Not bound to a UI framework: state is read from the properties and <see cref="Changed"/> is
/// raised (on whichever thread made the change) after any of them changes; a WPF view model
/// marshals it to the dispatcher. A handler that throws is logged and skipped. Nothing here starts Codex by itself — the caller decides when
/// (SPEC-16 §4.1: Caption only mode never uses Codex).
/// </remarks>
public sealed class CodexService : IDisposable
{
    private readonly Action<Uri> _openUrl;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private EngineStatus? _status;
    private bool _checking;
    private bool _checkAgain;
    private CodexRpc.Usage? _usage;
    private DateTimeOffset? _usageUpdatedAt;
    private IReadOnlyList<CodexRpc.Model> _models = [];
    private LoginTicket? _signIn;
    private string? _signInError;

    /// <param name="engine">The engine; this service reads its <see cref="IAnswerEngine.Notices"/>.</param>
    /// <param name="openUrl">Opens the sign-in page; <c>null</c>: the default browser.</param>
    public CodexService(IAnswerEngine engine, Action<Uri>? openUrl = null)
    {
        Engine = engine;
        _openUrl = openUrl ?? OpenInBrowser;
        _ = ReadNoticesAsync(_stop.Token);
    }

    /// <summary>Raised after any state property changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The engine behind this service.</summary>
    public IAnswerEngine Engine { get; }

    /// <summary><c>null</c> until the first check.</summary>
    public EngineStatus? Status { get { lock (_gate) return _status; } }

    /// <summary>A <see cref="RefreshAsync"/> is running.</summary>
    public bool Checking { get { lock (_gate) return _checking; } }

    /// <summary>The last usage read or pushed.</summary>
    public CodexRpc.Usage? Usage { get { lock (_gate) return _usage; } }

    /// <summary>When <see cref="Usage"/> last changed.</summary>
    public DateTimeOffset? UsageUpdatedAt { get { lock (_gate) return _usageUpdatedAt; } }

    /// <summary>The model list from the last successful read.</summary>
    public IReadOnlyList<CodexRpc.Model> Models { get { lock (_gate) return _models; } }

    /// <summary>The sign-in in progress, if any.</summary>
    public LoginTicket? SignIn { get { lock (_gate) return _signIn; } }

    /// <summary>The last sign-in or sign-out problem, for Settings; the UI may clear it.</summary>
    public string? SignInError
    {
        get { lock (_gate) return _signInError; }
        set
        {
            lock (_gate) _signInError = value;
            OnChanged();
        }
    }

    /// <summary>Signed in and usable.</summary>
    public bool IsReady => Status?.IsReady == true;

    /// <summary>
    /// Status, then (when signed in) the model list and usage. Called while a refresh is already
    /// running, it returns at once and the running one checks again when it finishes — a sign-in
    /// that completes mid-check is not lost to a status read that started before it.
    /// </summary>
    public async Task RefreshAsync()
    {
        lock (_gate)
        {
            if (_checking)
            {
                _checkAgain = true;
                return;
            }
            _checking = true;
        }
        OnChanged();
        try
        {
            while (true)
            {
                await RefreshOnceAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    // Checked and cleared together, so a request arriving now either re-runs
                    // this loop or (once _checking is false) starts its own refresh.
                    if (!_checkAgain)
                    {
                        _checking = false;
                        break;
                    }
                    _checkAgain = false;
                }
            }
        }
        catch
        {
            lock (_gate)
            {
                _checking = false;
                _checkAgain = false;
            }
            OnChanged();
            throw;
        }
        OnChanged();
    }

    private async Task RefreshOnceAsync()
    {
        var status = await Engine.StatusAsync().ConfigureAwait(false);
        lock (_gate) _status = status;
        OnChanged();
        if (!status.IsReady) return;
        try
        {
            var models = await Engine.ModelsAsync().ConfigureAwait(false);
            lock (_gate) _models = models;
            OnChanged();
        }
        catch (Exception) { } // Swift: `try?` — keep the old list
        await RefreshUsageAsync().ConfigureAwait(false);
    }

    /// <summary>Re-read usage; keeps the old value on failure.</summary>
    public async Task RefreshUsageAsync()
    {
        try
        {
            var usage = await Engine.UsageAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _usage = usage;
                _usageUpdatedAt = DateTimeOffset.Now;
            }
            OnChanged();
        }
        catch (Exception) { } // Swift: `try?` — keep the old value
    }

    /// <summary>Open ChatGPT sign-in in the browser; completion arrives as a notice.</summary>
    public async Task StartSignInAsync()
    {
        lock (_gate) _signInError = null;
        OnChanged();
        try
        {
            var ticket = await Engine.StartLoginAsync().ConfigureAwait(false);
            lock (_gate) _signIn = ticket;
            OnChanged();
            _openUrl(ticket.AuthUrl);
        }
        catch (Exception e)
        {
            lock (_gate) _signInError = e.Message;
            OnChanged();
        }
    }

    /// <summary>Settings → Codex → Sign out. Leaves the engine running, signed out.</summary>
    public async Task SignOutAsync()
    {
        lock (_gate) _signInError = null;
        OnChanged();
        try
        {
            await Engine.LogoutAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _usage = null;
                _usageUpdatedAt = null;
            }
        }
        catch (Exception e)
        {
            lock (_gate) _signInError = $"Couldn't sign out: {e.Message}";
        }
        OnChanged();
        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>Cancel the sign-in in progress, if any.</summary>
    public async Task CancelSignInAsync()
    {
        LoginTicket? ticket;
        lock (_gate)
        {
            ticket = _signIn;
            _signIn = null;
        }
        if (ticket is null) return;
        OnChanged();
        await Engine.CancelLoginAsync(ticket).ConfigureAwait(false);
    }

    /// <summary>
    /// The configured model if Codex offers it, else the S0 default
    /// (<see cref="InterviewConfig.RecommendedModel"/>), else Codex's default, else the configured one.
    /// </summary>
    public string ResolvedModel(string configured) => Resolve(InterviewConfig.EffectiveModel(configured));

    /// <summary>
    /// The line to show once when <see cref="ResolvedModel"/> does not use the configured model
    /// (SPEC-16 §9.4: the Mac falls back silently); <c>null</c> when it does.
    /// </summary>
    public string? ModelFallbackNotice(string configured)
    {
        var want = InterviewConfig.EffectiveModel(configured);
        var resolved = Resolve(want);
        return resolved == want ? null : $"Codex doesn't offer {want} any more — using {resolved}.";
    }

    private string Resolve(string want)
    {
        var models = Models;
        if (models.Count == 0 || models.Any(m => m.Id == want)) return want;
        return models.FirstOrDefault(m => m.Id == InterviewConfig.RecommendedModel)?.Id
               ?? models.FirstOrDefault(m => m.IsDefault)?.Id
               ?? want;
    }

    /// <summary>The model's reasoning efforts; low/medium/high when it isn't listed.</summary>
    public IReadOnlyList<string> Efforts(string model) =>
        Models.FirstOrDefault(m => m.Id == model)?.Efforts ?? ["low", "medium", "high"];

    /// <summary>Whether the model takes images; <c>true</c> when it isn't listed.</summary>
    public bool AcceptsImages(string model) => Models.FirstOrDefault(m => m.Id == model)?.AcceptsImages ?? true;

    /// <summary>Below 20 % left in any window → warn (SPEC-13, SPEC-15).</summary>
    public bool UsageIsLow => (Usage?.Lowest?.RemainingPercent ?? 100) < 20;

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task ReadNoticesAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var notice in Engine.Notices.ReadAllAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(notice).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    InterviewLog.Write($"handling {notice.GetType().Name} failed: {e.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(EngineNotice notice)
    {
        switch (notice)
        {
            case EngineNotice.Usage(var usage):
                lock (_gate)
                {
                    _usage = usage;
                    _usageUpdatedAt = DateTimeOffset.Now;
                }
                OnChanged();
                break;
            case EngineNotice.LoginCompleted(var success, var error):
                lock (_gate)
                {
                    _signIn = null;
                    if (!success) _signInError = error ?? "Sign-in did not complete.";
                }
                OnChanged();
                await RefreshAsync().ConfigureAwait(false);
                break;
            case EngineNotice.AccountChanged:
                await RefreshAsync().ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Raise <see cref="Changed"/>; a throwing handler is logged and the rest still run.</summary>
    private void OnChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception e)
            {
                InterviewLog.Write($"a Changed handler failed: {e.Message}");
            }
        }
    }

    /// <summary>The default browser, for an <c>https</c> sign-in page only.</summary>
    private static void OpenInBrowser(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps) return;
        ProcessStartInfo info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }
            : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [url.AbsoluteUri]);
        try
        {
            using var _ = Process.Start(info);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            InterviewLog.Write($"could not open the sign-in page: {e.Message}");
        }
    }
}

/// <summary>The one-line usage summary Settings and the interview header show.</summary>
public static class UsageLine
{
    /// <summary><c>5-hour: 82% left · resets 14:20</c> — the reset time alone today, with the weekday otherwise.</summary>
    public static string For(CodexRpc.Usage.Window window) =>
        For(window, DateTimeOffset.Now, TimeZoneInfo.Local, CultureInfo.CurrentCulture);

    /// <summary><see cref="For(CodexRpc.Usage.Window)"/> at a given time, zone and culture.</summary>
    public static string For(CodexRpc.Usage.Window window, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var line = $"{window.Label}: {window.RemainingPercent}% left";
        if (window.ResetsAt is not { } at) return line;
        var resets = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(at), zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var time = resets.ToString("t", culture);
        return line + " · resets " + (resets.Date == today ? time : resets.ToString("ddd", culture) + " " + time);
    }
}
