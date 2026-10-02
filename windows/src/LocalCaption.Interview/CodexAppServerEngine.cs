using System.Text;
using System.Threading.Channels;
using LocalCaption.Core;
using LocalCaption.Core.Interview;

namespace LocalCaption.Interview;

/// <summary>
/// <see cref="IAnswerEngine"/> over a long-lived, locked-down <c>codex app-server</c> child
/// process (SPEC-12, SPEC-16 §4.1) — the port of <c>CodexAppServerEngine.swift</c>.
/// </summary>
/// <remarks>
/// <para>One process per app run, started lazily on first use. Requests are JSON-RPC over stdio;
/// one turn at a time. If the process dies, the in-flight turn fails with
/// <see cref="EngineError.Crashed"/>, the next call restarts it once and re-applies the lockdown
/// to known threads with <c>thread/resume</c>; a second crash leaves the engine failed (captions
/// are never affected).</para>
/// <para>The Swift actor becomes one lock: state changes happen under it, awaits happen outside
/// it, so calls interleave at the same points the actor's would. Incoming lines are handled in
/// order on the reader task.</para>
/// </remarks>
public sealed class CodexAppServerEngine : IAnswerEngine, IAsyncDisposable
{
    /// <summary>Starts the server process; tests return a scripted transport instead.</summary>
    public delegate ILineTransport TransportFactory(CodexCommand codex, IReadOnlyList<string> arguments, string cwd,
                                                    IReadOnlyDictionary<string, string> environment);

    /// <summary>Finds <c>codex</c> for a configured path (<c>interview.codex_path</c>).</summary>
    public delegate LocateResult Locator(string configuredPath);

    /// <summary>The engine's clocks, injectable so tests run fast.</summary>
    public sealed record Timing
    {
        /// <summary>A request without a reply fails after this.</summary>
        public TimeSpan Request { get; init; } = TimeSpan.FromSeconds(15);

        /// <summary>No text this long after the request → <see cref="AnswerEvent.Slow"/>.</summary>
        public TimeSpan SlowAfter { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>A turn still running this long after the request is interrupted and fails with <see cref="EngineError.Timeout"/>.</summary>
        public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromSeconds(120);

        /// <summary>How long an interrupt waits for <c>turn/start</c> to report the turn id.</summary>
        public TimeSpan InterruptGrace { get; init; } = TimeSpan.FromSeconds(2);
    }

    /// <summary>Where the engine works, and its test seams. The defaults are the app's real paths.</summary>
    public sealed record Options
    {
        /// <summary>Codex's <c>cwd</c>, kept empty (<c>interview\workspace</c>).</summary>
        public string Workspace { get; init; } = AppPaths.Workspace;

        /// <summary>The dedicated <c>CODEX_HOME</c> (<c>interview\codex-home</c>).</summary>
        public string CodexHome { get; init; } = AppPaths.CodexHome;

        /// <summary>Where stderr goes (<c>interview\codex.log</c>); <c>null</c> discards it.</summary>
        public string? StderrLog { get; init; } = AppPaths.CodexLog;

        /// <summary>The clocks.</summary>
        public Timing Timing { get; init; } = new();

        /// <summary><c>null</c>: <see cref="CodexLocator.Locate"/>.</summary>
        public Locator? Locate { get; init; }

        /// <summary><c>null</c>: a real <see cref="ProcessLineTransport"/>.</summary>
        public TransportFactory? MakeTransport { get; init; }
    }

    /// <summary>A request waiting for its reply. <see cref="Timer"/> fires the timeout; disposed when the request ends.</summary>
    private sealed class Pending(Action<JsonValue>? onResult, TimeSpan timeout)
    {
        public readonly TaskCompletionSource<JsonValue> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Timer = new(timeout);
        public readonly Action<JsonValue>? OnResult = onResult;
    }

    private sealed class ActiveTurn(string threadId, ChannelWriter<AnswerEvent> events)
    {
        public readonly string ThreadId = threadId;
        public readonly ChannelWriter<AnswerEvent> Events = events;

        /// <summary>Cancelled when the turn ends; disposed by <see cref="WatchdogAsync"/>, its only listener.</summary>
        public readonly CancellationTokenSource Watchdog = new();

        public string? TurnId;
        public readonly StringBuilder Text = new();
        public bool GotText;
        public bool ThinkingSent;
        public string? BlockedItem;
        public CodexRpc.TurnFailure? Failure;
    }

    private readonly Func<string> _codexPath;
    private readonly string _workspace;
    private readonly string _codexHome;
    private readonly Locator _locate;
    private readonly TransportFactory _makeTransport;
    private readonly Timing _timing;
    private readonly Channel<EngineNotice> _notices = Channel.CreateBounded<EngineNotice>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly Lock _gate = new();
    private ILineTransport? _transport;
    private Task? _launching;
    private long _nextId;
    private readonly Dictionary<long, Pending> _pending = [];
    private int _unexpectedExits;

    /// <summary>Bumped by <see cref="ShutdownAsync"/>; a launch that began under an older value is abandoned.</summary>
    private long _generation;

    /// <summary>Set by <see cref="DisposeAsync"/>: nothing starts again.</summary>
    private bool _disposed;
    private readonly Dictionary<string, ThreadConfig> _threads = [];
    private CodexRpc.Usage? _lastUsage;
    private ActiveTurn? _active;

    /// <param name="codexPath">Reads <c>interview.codex_path</c> at each launch.</param>
    /// <param name="options"><c>null</c>: the app's real paths and clocks.</param>
    public CodexAppServerEngine(Func<string> codexPath, Options? options = null)
    {
        options ??= new Options();
        _codexPath = codexPath;
        _workspace = options.Workspace;
        _codexHome = options.CodexHome;
        _timing = options.Timing;
        _locate = options.Locate ?? CodexLocator.Locate;
        var stderrLog = options.StderrLog;
        _makeTransport = options.MakeTransport ?? ((codex, args, cwd, env) =>
            new ProcessLineTransport(codex.FileName, codex.Arguments(args), cwd, env, stderrLog));
    }

    /// <inheritdoc />
    public ChannelReader<EngineNotice> Notices => _notices.Reader;

    /// <summary>
    /// A <c>codex</c> process is up (or launching) now. Reading it never starts one — the host
    /// asks this before work that would otherwise start Codex in Caption only mode (SPEC-16 §4.1).
    /// </summary>
    public bool IsRunning
    {
        get { lock (_gate) return !_disposed && (_transport is not null || _launching is not null); }
    }

    // ── IAnswerEngine ───────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Locates codex only when no process is up, and hands what it found to the launch, so a
    /// cold start probes <c>--version</c> once and a refresh while running probes nothing.
    /// </remarks>
    public async Task<EngineStatus> StatusAsync()
    {
        try
        {
            LocateResult.Found? found = null;
            bool running;
            lock (_gate) running = _transport is not null || _launching is not null;
            if (!running)
            {
                var configured = _codexPath();
                found = Found(await Task.Run(() => _locate(configured)).ConfigureAwait(false));
            }
            await EnsureStartedAsync(found).ConfigureAwait(false);
            return CodexRpc.AccountFrom(await RequestAsync("account/read", JsonValue.Obj()).ConfigureAwait(false)) switch
            {
                CodexRpc.Account.ChatGpt(var email, var plan) => new EngineStatus.Ready(email, plan),
                CodexRpc.Account.Other(var type) => new EngineStatus.Ready(null, type),
                _ => new EngineStatus.SignedOut(),
            };
        }
        catch (EngineException e)
        {
            return e.Error switch
            {
                EngineError.NotInstalled => new EngineStatus.NotInstalled(),
                EngineError.TooOld(var v) => new EngineStatus.TooOld(v),
                _ => new EngineStatus.Failed(e.Error.Message),
            };
        }
        catch (Exception e)
        {
            return new EngineStatus.Failed(e.Message);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodexRpc.Model>> ModelsAsync()
    {
        await EnsureStartedAsync().ConfigureAwait(false);
        return CodexRpc.Models(await RequestAsync("model/list", CodexRpc.ModelListParams).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<CodexRpc.Usage> UsageAsync()
    {
        await EnsureStartedAsync().ConfigureAwait(false);
        var usage = CodexRpc.UsageFromRead(await RequestAsync("account/rateLimits/read", JsonValue.Obj()).ConfigureAwait(false));
        lock (_gate) _lastUsage = usage;
        return usage;
    }

    /// <inheritdoc />
    public async Task<string> StartThreadAsync(ThreadConfig config)
    {
        await EnsureStartedAsync().ConfigureAwait(false);
        PrepareWorkspace();
        var result = await RequestAsync("thread/start",
            CodexRpc.ThreadStartParams(config.Model, _workspace, config.BaseInstructions)).ConfigureAwait(false);
        if (result["thread"]?["id"]?.StringValue is not { } id)
            throw new EngineException(new EngineError.Rpc("thread/start returned no id"));
        lock (_gate) _threads[id] = config;
        return id;
    }

    /// <inheritdoc />
    public async Task ResumeThreadAsync(string id, ThreadConfig config)
    {
        await EnsureStartedAsync().ConfigureAwait(false);
        PrepareWorkspace();
        await ResumeRequestAsync(id, config).ConfigureAwait(false);
        lock (_gate) _threads[id] = config;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AnswerEvent> Send(string threadId, IReadOnlyList<CodexRpc.Input> input, string effort,
                                              string? model = null)
    {
        var events = Channel.CreateUnbounded<AnswerEvent>(new UnboundedChannelOptions { SingleReader = true });
        _ = Task.Run(() => BeginTurnAsync(threadId, input, effort, model, events.Writer));
        return events.Reader.ReadAllAsync();
    }

    /// <inheritdoc />
    public async Task InterruptAsync(string threadId)
    {
        ActiveTurn? turn;
        lock (_gate) turn = _active;
        if (turn is null || turn.ThreadId != threadId) return;
        // turn/start may still be in flight; give it a moment to report the turn id.
        var deadline = DateTime.UtcNow + _timing.InterruptGrace;
        string? turnId;
        while (true)
        {
            lock (_gate)
            {
                if (_active != turn) return;
                turnId = turn.TurnId;
            }
            if (turnId is not null || DateTime.UtcNow >= deadline) break;
            await Task.Delay(50).ConfigureAwait(false);
        }
        if (turnId is null) return;
        try
        {
            await RequestAsync("turn/interrupt", CodexRpc.TurnInterruptParams(threadId, turnId)).ConfigureAwait(false);
        }
        catch (EngineException) { }
    }

    /// <inheritdoc />
    public async Task ArchiveThreadAsync(string id)
    {
        lock (_gate) _threads.Remove(id);
        try
        {
            await EnsureStartedAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }
        try
        {
            await RequestAsync("thread/archive", CodexRpc.ThreadArchiveParams(id)).ConfigureAwait(false);
        }
        catch (EngineException) { }
    }

    /// <inheritdoc />
    public async Task<LoginTicket> StartLoginAsync()
    {
        await EnsureStartedAsync().ConfigureAwait(false);
        var r = await RequestAsync("account/login/start", CodexRpc.LoginStartParams).ConfigureAwait(false);
        if (r["loginId"]?.StringValue is not { } id || r["authUrl"]?.StringValue is not { } s
            || !Uri.TryCreate(s, UriKind.Absolute, out var url))
            throw new EngineException(new EngineError.Rpc("Sign-in is not available from this Codex version."));
        return new LoginTicket(id, url);
    }

    /// <summary>
    /// Signs out LocalCaption's own Codex home (<c>interview\codex-home</c>) only — the user's
    /// Codex CLI and editor sign-ins live elsewhere and are untouched.
    /// </summary>
    public async Task LogoutAsync()
    {
        await EnsureStartedAsync().ConfigureAwait(false);
        await RequestAsync("account/logout", CodexRpc.LogoutParams).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CancelLoginAsync(LoginTicket ticket)
    {
        try
        {
            await RequestAsync("account/login/cancel", CodexRpc.LoginCancelParams(ticket.LoginId)).ConfigureAwait(false);
        }
        catch (EngineException) { }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A launch still in progress is abandoned: its process is terminated and its callers get
    /// <see cref="EngineError.Crashed"/>. Not counted as an unexpected exit.
    /// </remarks>
    public Task ShutdownAsync()
    {
        ILineTransport? transport;
        lock (_gate)
        {
            _generation++;
            _launching = null;
            transport = _transport;
            _transport = null;
            FailAll(new EngineError.Crashed());
        }
        transport?.Terminate();
        return Task.CompletedTask;
    }

    /// <summary><see cref="ShutdownAsync"/>, for good: later calls fail with <see cref="EngineError.Crashed"/> instead of relaunching.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await ShutdownAsync().ConfigureAwait(false);
    }

    // ── Process lifecycle ───────────────────────────────────────────────────────────────

    /// <param name="found">What <see cref="StatusAsync"/> just located, so a launch need not probe again.</param>
    private async Task EnsureStartedAsync(LocateResult.Found? found = null)
    {
        Task launching;
        var owner = false;
        lock (_gate)
        {
            if (_disposed) throw new EngineException(new EngineError.Crashed());
            if (_launching is { } running) launching = running;
            else if (_transport is not null) return;
            else
            {
                var generation = _generation;
                launching = _launching = Task.Run(() => LaunchAsync(generation, found));
                owner = true;
            }
        }
        try
        {
            await launching.ConfigureAwait(false);
        }
        finally
        {
            if (owner)
                lock (_gate)
                    if (_launching == launching) _launching = null;
        }
    }

    /// <summary>
    /// The one place a locate result becomes an error: not installed / too old throw what the
    /// launch fails with (and <see cref="StatusAsync"/> reports).
    /// </summary>
    private static LocateResult.Found Found(LocateResult result) => result switch
    {
        LocateResult.Found f => f,
        LocateResult.TooOld(var v) => throw new EngineException(new EngineError.TooOld(v)),
        _ => throw new EngineException(new EngineError.NotInstalled()),
    };

    /// <param name="generation">The <see cref="_generation"/> this launch belongs to; a shutdown since abandons it.</param>
    private async Task LaunchAsync(long generation, LocateResult.Found? found)
    {
        lock (_gate)
            if (_unexpectedExits >= 2) throw new EngineException(new EngineError.Crashed());
        found ??= Found(_locate(_codexPath()));
        Directory.CreateDirectory(_codexHome);
        PrepareWorkspace();
        var transport = _makeTransport(found.Command, CodexRpc.LaunchArguments, _workspace,
            CodexLocator.Environment(found.Command, _codexHome));
        bool abandoned;
        lock (_gate)
        {
            // ShutdownAsync or DisposeAsync ran while locating or spawning: don't revive.
            abandoned = _disposed || _generation != generation;
            if (!abandoned) _transport = transport;
        }
        if (abandoned)
        {
            transport.Terminate();
            throw new EngineException(new EngineError.Crashed());
        }
        _ = Task.Run(() => ReadLoopAsync(transport));
        try
        {
            await RequestAsync("initialize",
                CodexRpc.InitializeParams("localcaption", "LocalCaption", AppInfo.Version())).ConfigureAwait(false);
            transport.Send(CodexRpc.Notification("initialized").Line());
        }
        catch (Exception e)
        {
            // A hung or failing handshake counts like a crash, so a codex that never answers
            // isn't relaunched forever. (A crash or shutdown has already let go of the transport.)
            var exits = 0;
            lock (_gate)
            {
                if (_transport == transport)
                {
                    _transport = null;
                    exits = ++_unexpectedExits;
                }
            }
            transport.Terminate();
            if (exits > 0) InterviewLog.Write($"codex app-server did not initialize: {e.Message} ({exits})");
            throw;
        }
        // After a restart, re-apply the lockdown to threads this run already knows.
        KeyValuePair<string, ThreadConfig>[] known;
        lock (_gate) known = [.. _threads];
        foreach (var (id, cfg) in known)
        {
            lock (_gate)
                if (_generation != generation) return;
            try
            {
                await ResumeRequestAsync(id, cfg).ConfigureAwait(false);
            }
            catch (EngineException) { }
        }
    }

    private Task<JsonValue> ResumeRequestAsync(string id, ThreadConfig config) =>
        RequestAsync("thread/resume", CodexRpc.ThreadResumeParams(id, config.Model, _workspace, config.BaseInstructions));

    private async Task ReadLoopAsync(ILineTransport transport)
    {
        try
        {
            await foreach (var line in transport.Lines.ConfigureAwait(false)) Handle(line, transport);
        }
        catch (Exception e)
        {
            InterviewLog.Write($"reading from codex failed: {e.Message}");
        }
        // However the stream ended — EOF, exit, or a read error — make sure the process goes
        // too, or a codex that is still running would be orphaned while the next call starts another.
        transport.Terminate();
        TransportEnded(transport);
    }

    private void TransportEnded(ILineTransport transport)
    {
        int exits;
        lock (_gate)
        {
            if (!ReferenceEquals(transport, _transport)) return; // shut down, or already replaced
            _transport = null;
            exits = ++_unexpectedExits;
            FailAll(new EngineError.Crashed());
        }
        InterviewLog.Write($"codex app-server exited unexpectedly ({exits})");
    }

    /// <summary>Fail every waiting request and the running turn. Under the lock.</summary>
    private void FailAll(EngineError error)
    {
        var waiting = _pending.Values.ToList();
        _pending.Clear();
        foreach (var p in waiting)
        {
            p.Timer.Dispose();
            p.Result.TrySetException(new EngineException(error));
        }
        if (_active is { } a) FinishActive(new AnswerEvent.Failed(error, a.Text.ToString()));
    }

    /// <summary>
    /// The workspace is Codex's <c>cwd</c> and must stay empty (SPEC-12 §Lockdown). App-owned, so
    /// anything found in it is removed — and logged, since something put it there.
    /// </summary>
    private void PrepareWorkspace()
    {
        Directory.CreateDirectory(_workspace);
        foreach (var entry in new DirectoryInfo(_workspace).EnumerateFileSystemInfos())
        {
            InterviewLog.Write($"workspace was not empty: removing {entry.Name}");
            if (entry is DirectoryInfo dir && dir.LinkTarget is null)
            {
                dir.Delete(recursive: true);
            }
            else
            {
                if (entry is FileInfo { IsReadOnly: true } file) file.IsReadOnly = false;
                entry.Delete();
            }
        }
    }

    // ── JSON-RPC ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Send a request and wait for its reply. <paramref name="onResult"/> runs on the reader,
    /// under the lock, as the reply is handled — before any later line — so the turn's
    /// <c>Started</c> event always precedes its first delta.
    /// </summary>
    private Task<JsonValue> RequestAsync(string method, JsonValue @params, Action<JsonValue>? onResult = null)
    {
        ILineTransport? transport;
        long id;
        Pending pending;
        lock (_gate)
        {
            transport = _transport;
            if (transport is null) return Task.FromException<JsonValue>(new EngineException(new EngineError.Crashed()));
            id = ++_nextId;
            pending = new Pending(onResult, _timing.Request);
            _pending[id] = pending;
            // Registered before the send: the reply may be handled before Send returns.
            pending.Timer.Token.Register(() => TimedOut(id, method, pending));
        }
        try
        {
            transport.Send(CodexRpc.Request(id, method, @params).Line());
        }
        catch (Exception)
        {
            lock (_gate)
                if (_pending.Remove(id)) pending.Timer.Dispose();
            return Task.FromException<JsonValue>(new EngineException(new EngineError.Crashed()));
        }
        return pending.Result.Task;
    }

    private void TimedOut(long id, string method, Pending pending)
    {
        lock (_gate)
        {
            if (!_pending.Remove(id)) return;
        }
        pending.Timer.Dispose();
        pending.Result.TrySetException(new EngineException(new EngineError.Rpc($"{method} timed out")));
    }

    private void Handle(string line, ILineTransport transport)
    {
        switch (CodexRpc.Decode(line))
        {
            case CodexRpc.Incoming.Response(var id, var result):
                lock (_gate)
                {
                    if (!_pending.Remove(id, out var p)) return;
                    p.Timer.Dispose();
                    p.OnResult?.Invoke(result);
                    p.Result.TrySetResult(result);
                }
                break;
            case CodexRpc.Incoming.Error(var id, _, var message):
                lock (_gate)
                {
                    if (!_pending.Remove(id, out var p)) return;
                    p.Timer.Dispose();
                    p.Result.TrySetException(new EngineException(new EngineError.Rpc(message)));
                }
                break;
            case CodexRpc.Incoming.ServerRequest(var id, var method, _):
            {
                // Approvals are `never`, so none should arrive. Decline and record the breach.
                // Written off the reader: if codex's stdin pipe is full because codex is itself
                // blocked writing to us, a write here would never return and nothing more be read.
                InterviewLog.Write($"lockdown breach: server request {method}");
                var decline = CodexRpc.DeclineResponse(id, method).Line();
                _ = Task.Run(() =>
                {
                    try
                    {
                        transport.Send(decline);
                    }
                    catch (EngineException) { }
                });
                break;
            }
            case CodexRpc.Incoming.Notification(var method, var @params):
                lock (_gate) HandleEvent(CodexRpc.EventFor(method, @params));
                break;
        }
    }

    // ── Turns ───────────────────────────────────────────────────────────────────────────

    private async Task BeginTurnAsync(string threadId, IReadOnlyList<CodexRpc.Input> input, string effort, string? model,
                                      ChannelWriter<AnswerEvent> events)
    {
        try
        {
            await EnsureStartedAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            events.TryWrite(new AnswerEvent.Failed(ToEngineError(e), ""));
            events.TryComplete();
            return;
        }
        var turn = new ActiveTurn(threadId, events);
        lock (_gate)
        {
            if (_active is not null)
            {
                turn.Watchdog.Dispose();
                events.TryWrite(new AnswerEvent.Failed(new EngineError.Busy(), ""));
                events.TryComplete();
                return;
            }
            _active = turn;
        }
        _ = WatchdogAsync(turn);
        try
        {
            await RequestAsync("turn/start", CodexRpc.TurnStartParams(threadId, input, effort, model), result =>
            {
                if (_active != turn || result["turn"]?["id"]?.StringValue is not { } id) return;
                turn.TurnId ??= id;
                turn.Events.TryWrite(new AnswerEvent.Started(id));
            }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                if (_active != turn) return;
                FinishActive(new AnswerEvent.Failed(ToEngineError(e), ""));
            }
        }
    }

    /// <summary>
    /// Slow, then give up. Exits only once the turn has ended (FinishActive clears
    /// <c>_active</c> and cancels under the lock), so disposing the token source here is safe.
    /// </summary>
    private async Task WatchdogAsync(ActiveTurn turn)
    {
        var token = turn.Watchdog.Token;
        try
        {
            try
            {
                await Task.Delay(_timing.SlowAfter, token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_active == turn && !turn.GotText) turn.Events.TryWrite(new AnswerEvent.Slow());
                }
                var rest = _timing.GiveUpAfter - _timing.SlowAfter;
                await Task.Delay(rest > TimeSpan.Zero ? rest : TimeSpan.Zero, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            lock (_gate)
                if (_active != turn) return;
            await InterruptAsync(turn.ThreadId).ConfigureAwait(false);
            lock (_gate)
            {
                if (_active != turn) return;
                FinishActive(new AnswerEvent.Failed(new EngineError.Timeout(), turn.Text.ToString()));
            }
        }
        finally
        {
            turn.Watchdog.Dispose();
        }
    }

    /// <summary>The event belongs to the running turn. Under the lock.</summary>
    private bool IsActive(string threadId, string turnId) =>
        _active is { } a && a.ThreadId == threadId && (a.TurnId is null || turnId.Length == 0 || a.TurnId == turnId);

    /// <summary>Act on one notification. Under the lock.</summary>
    private void HandleEvent(CodexRpc.Event e)
    {
        switch (e)
        {
            case CodexRpc.Event.RateLimitsUpdated(var usage):
                _lastUsage = usage;
                _notices.Writer.TryWrite(new EngineNotice.Usage(usage));
                break;
            case CodexRpc.Event.LoginCompleted(_, var success, var error):
                _notices.Writer.TryWrite(new EngineNotice.LoginCompleted(success, error));
                break;
            case CodexRpc.Event.AccountUpdated:
                _notices.Writer.TryWrite(new EngineNotice.AccountChanged());
                break;
            case CodexRpc.Event.TurnStarted(var t, var u):
                if (_active is { } started && started.ThreadId == t && started.TurnId is null) started.TurnId = u;
                break;
            case CodexRpc.Event.ItemStarted(var t, var u, var type):
            {
                if (!IsActive(t, u)) return;
                var a = _active!;
                if (!CodexRpc.AllowedItemTypes.Contains(type))
                {
                    // The tool-call guard (SPEC-12 §Lockdown): stop the turn the moment a tool appears.
                    InterviewLog.Write($"lockdown breach: item {type} — interrupting");
                    if (a.BlockedItem is null)
                    {
                        a.BlockedItem = type;
                        _ = Task.Run(() => InterruptAsync(t));
                    }
                }
                else if (type == "reasoning" && !a.ThinkingSent && !a.GotText)
                {
                    a.ThinkingSent = true;
                    a.Events.TryWrite(new AnswerEvent.Thinking());
                }
                break;
            }
            case CodexRpc.Event.AgentDelta(var t, var u, var delta):
            {
                if (!IsActive(t, u) || _active!.BlockedItem is not null) return;
                var a = _active;
                a.Text.Append(delta);
                a.GotText = true;
                a.Events.TryWrite(new AnswerEvent.Delta(delta));
                break;
            }
            case CodexRpc.Event.AgentMessageCompleted(var t, var u, var text):
                if (!IsActive(t, u) || text.Length == 0) return;
                _active!.Text.Clear().Append(text);
                break;
            case CodexRpc.Event.Error(var t, var u, var failure, var willRetry):
                if (willRetry || !IsActive(t, u)) return;
                _active!.Failure = failure;
                break;
            case CodexRpc.Event.TurnCompleted(var t, var u, var status, var failure):
            {
                if (!IsActive(t, u)) return;
                var a = _active!;
                if (a.BlockedItem is { } blocked)
                    FinishActive(new AnswerEvent.Failed(new EngineError.BlockedTool(blocked), ""));
                else if (status == "completed")
                    FinishActive(new AnswerEvent.Completed(a.Text.ToString()));
                else if (status == "interrupted")
                    FinishActive(new AnswerEvent.Interrupted(a.Text.ToString()));
                else
                    FinishActive(new AnswerEvent.Failed(ErrorFor(failure ?? a.Failure), a.Text.ToString()));
                break;
            }
        }
    }

    /// <summary>End the running turn with <paramref name="last"/>. Under the lock.</summary>
    private void FinishActive(AnswerEvent last)
    {
        if (_active is not { } a) return;
        _active = null;
        a.Watchdog.Cancel();
        a.Events.TryWrite(last);
        a.Events.TryComplete();
    }

    /// <summary>A turn's failure → the error the UI shows. Under the lock (reads the last usage).</summary>
    private EngineError ErrorFor(CodexRpc.TurnFailure? failure)
    {
        if (failure is null) return new EngineError.Other("The turn failed.");
        return failure.Kind switch
        {
            CodexRpc.FailureKind.UsageLimit => new EngineError.UsageLimit(
                _lastUsage?.Lowest?.ResetsAt is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : null),
            CodexRpc.FailureKind.SignedOut => new EngineError.SignedOut(),
            CodexRpc.FailureKind.Network => new EngineError.Network(failure.Message),
            _ => new EngineError.Other(failure.Message),
        };
    }

    private static EngineError ToEngineError(Exception e) =>
        e is EngineException ee ? ee.Error : new EngineError.Other(e.Message);
}
