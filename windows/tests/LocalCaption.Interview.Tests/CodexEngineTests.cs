using LocalCaption.Core.Interview;
using static LocalCaption.Interview.Tests.FakeServer;

namespace LocalCaption.Interview.Tests;

/// <summary>
/// <see cref="CodexAppServerEngine"/> against a scripted server (SPEC-12 §Acceptance): no real
/// process. A 1:1 port of <c>app/Tests/LocalCaptionTests/CodexEngineTests.swift</c>.
/// </summary>
public sealed class CodexEngineTests : IDisposable
{
    private readonly List<FakeServer> _servers = [];
    private readonly Lock _serversGate = new();
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lc-engine-" + Guid.NewGuid().ToString("N"));
    private static readonly ThreadConfig Cfg = new("gpt-6-luna", "Coach.");

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private FakeServer Server(int i)
    {
        lock (_serversGate) return _servers[i];
    }

    private int ServerCount
    {
        get { lock (_serversGate) return _servers.Count; }
    }

    private static JsonValue O(params ReadOnlySpan<(string, JsonValue)> members) => JsonValue.Obj(members);

    /// <summary>Default script: a healthy server that answers every turn with "Hello".</summary>
    private static void Healthy(FakeServer s, Func<JsonValue?, IEnumerable<string>>? turnEvents = null)
    {
        s.OnMessage = (method, id, p) => method switch
        {
            "initialize" => [Reply(id, O(("userAgent", "fake/0.159.3")))],
            "thread/start" => [Reply(id, O(("thread", O(("id", "thr")))))],
            "thread/resume" => [Reply(id, O(("thread", O(("id", p["threadId"] ?? JsonValue.Null.Instance)))))],
            "account/read" => [Reply(id, O(("account", O(("type", "chatgpt"), ("email", "me@example.com"), ("planType", "plus")))))],
            "account/rateLimits/read" => [Reply(id, O(("rateLimits", O(
                ("primary", O(("usedPercent", 97), ("windowDurationMins", 300), ("resetsAt", 1_800_000_000L)))))))],
            "turn/interrupt" =>
            [
                Reply(id, O()),
                Note("turn/completed", O(("threadId", "thr"), ("turn", O(("id", "u1"), ("status", "interrupted"))))),
            ],
            "turn/start" =>
            [
                Reply(id, O(("turn", O(("id", "u1"), ("status", "inProgress"))))),
                .. turnEvents?.Invoke(id) ??
                [
                    Note("turn/started", O(("threadId", "thr"), ("turn", O(("id", "u1"))))),
                    Note("item/started", O(("threadId", "thr"), ("turnId", "u1"), ("item", O(("type", "userMessage"))))),
                    Note("item/started", O(("threadId", "thr"), ("turnId", "u1"), ("item", O(("type", "agentMessage"))))),
                    Note("item/agentMessage/delta", O(("threadId", "thr"), ("turnId", "u1"), ("delta", "Hel"))),
                    Note("item/agentMessage/delta", O(("threadId", "thr"), ("turnId", "u1"), ("delta", "lo"))),
                    Note("item/completed", O(("threadId", "thr"), ("turnId", "u1"),
                        ("item", O(("type", "agentMessage"), ("text", "Hello"))))),
                    Note("turn/completed", O(("threadId", "thr"), ("turn", O(("id", "u1"), ("status", "completed"))))),
                ],
            ],
            _ => [Reply(id, O())],
        };
    }

    private CodexAppServerEngine Engine(Action<FakeServer> configure, CodexAppServerEngine.Timing? timing = null,
                                        CodexAppServerEngine.Locator? locate = null)
    {
        var found = new LocateResult.Found(CodexCommand.Direct("/fake/codex"), [0, 159, 3]);
        return new CodexAppServerEngine(() => "", new CodexAppServerEngine.Options
        {
            Workspace = Path.Combine(_tmp, "workspace"),
            CodexHome = Path.Combine(_tmp, "codex-home"),
            StderrLog = null,
            Timing = timing ?? new CodexAppServerEngine.Timing(),
            Locate = locate ?? (_ => found),
            MakeTransport = (_, args, _, env) =>
            {
                Assert.Equal(CodexRpc.LaunchArguments, args);
                Assert.EndsWith("codex-home", env["CODEX_HOME"]);
                var s = new FakeServer();
                configure(s);
                lock (_serversGate) _servers.Add(s);
                return s;
            },
        });
    }

    private static async Task<List<AnswerEvent>> Collect(IAsyncEnumerable<AnswerEvent> stream)
    {
        var events = new List<AnswerEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var e in stream.WithCancellation(cts.Token)) events.Add(e);
        return events;
    }

    private static async Task WaitUntil(Func<bool> condition, double timeoutSeconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    private static async Task<EngineStatus> StatusUntil(CodexAppServerEngine e, Func<EngineStatus, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var status = await e.StatusAsync();
        while (!done(status) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            status = await e.StatusAsync();
        }
        return status;
    }

    private static IReadOnlyList<CodexRpc.Input> Text(string s) => [new CodexRpc.Input.Text(s)];

    // ── Tests ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThreadStartIsLockedDownAndTurnStreams()
    {
        var e = Engine(s => Healthy(s));
        var thread = await e.StartThreadAsync(Cfg);
        Assert.Equal("thr", thread);

        var events = await Collect(e.Send(thread, Text("Q?"), "low"));
        Assert.Equal<AnswerEvent>(
            [new AnswerEvent.Started("u1"), new AnswerEvent.Delta("Hel"), new AnswerEvent.Delta("lo"), new AnswerEvent.Completed("Hello")],
            events);

        var s = Server(0);
        Assert.Equal("initialize", s.Sent[0]["method"]?.StringValue);
        Assert.Contains(s.Sent, m => m["method"]?.StringValue == "initialized" && m["id"] is null);
        var start = s.SentMethod("thread/start")[0]["params"]!;
        Assert.Equal("read-only", start["sandbox"]?.StringValue);
        Assert.Equal("never", start["approvalPolicy"]?.StringValue);
        Assert.Equal("Coach.", start["baseInstructions"]?.StringValue);
        Assert.Equal(Path.Combine(_tmp, "workspace"), start["cwd"]?.StringValue);
        Assert.Equal("low", s.SentMethod("turn/start")[0]["params"]?["effort"]?.StringValue); // effort is sent on every turn
    }

    [Fact]
    public async Task ToolItemTripsTheGuard()
    {
        var e = Engine(s => Healthy(s, _ =>
        [
            Note("item/started", O(("threadId", "thr"), ("turnId", "u1"), ("item", O(("type", "commandExecution"))))),
        ]));
        var thread = await e.StartThreadAsync(Cfg);
        var events = await Collect(e.Send(thread, Text("run ls"), "low"));
        Assert.Equal(new AnswerEvent.Failed(new EngineError.BlockedTool("commandExecution"), ""), events[^1]);
        Assert.Single(Server(0).SentMethod("turn/interrupt")); // the guard interrupts the turn
    }

    [Fact]
    public async Task UsageLimitFailureCarriesResetTime()
    {
        var e = Engine(s => Healthy(s, _ =>
        [
            Note("turn/completed", O(("threadId", "thr"), ("turn", O(
                ("id", "u1"), ("status", "failed"),
                ("error", O(("message", "limit"), ("codexErrorInfo", "usageLimitExceeded"))))))),
        ]));
        var usage = await e.UsageAsync();
        Assert.Equal(3, usage.Lowest?.RemainingPercent);
        var thread = await e.StartThreadAsync(Cfg);
        var events = await Collect(e.Send(thread, Text("Q"), "low"));
        Assert.Equal(
            new AnswerEvent.Failed(new EngineError.UsageLimit(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)), ""),
            events[^1]);
    }

    [Fact]
    public async Task CrashFailsTheTurnThenRestartsAndResumes()
    {
        var e = Engine(s => Healthy(s, _ => [])); // turns never complete
        var thread = await e.StartThreadAsync(Cfg);
        var task = Collect(e.Send(thread, Text("Q"), "low"));
        await WaitUntil(() => ServerCount > 0 && Server(0).SentMethod("turn/start").Count > 0);
        Server(0).Crash();
        var events = await task;
        Assert.Equal(new AnswerEvent.Failed(new EngineError.Crashed(), ""), events[^1]);

        // Next use restarts once and re-applies the lockdown to the known thread.
        var status = await e.StatusAsync();
        Assert.Equal(new EngineStatus.Ready("me@example.com", "plus"), status);
        Assert.Equal(2, ServerCount);
        var resume = Server(1).SentMethod("thread/resume")[0]["params"]!;
        Assert.Equal("thr", resume["threadId"]?.StringValue);
        Assert.Equal("read-only", resume["sandbox"]?.StringValue);
    }

    [Fact]
    public async Task ServerRequestsAreDeclined()
    {
        var e = Engine(s => Healthy(s));
        await e.StartThreadAsync(Cfg);
        Server(0).Push("""{"id":"srv-1","method":"item/commandExecution/requestApproval","params":{}}""");
        await WaitUntil(() => Server(0).Sent.Any(m => m["id"]?.StringValue == "srv-1"));
        var reply = Server(0).Sent.FirstOrDefault(m => m["id"]?.StringValue == "srv-1");
        Assert.Equal("decline", reply?["result"]?["decision"]?.StringValue);
    }

    [Fact]
    public async Task SecondTurnWhileBusyIsRejected()
    {
        var e = Engine(s => Healthy(s, _ => []));
        var thread = await e.StartThreadAsync(Cfg);
        var firstTask = Collect(e.Send(thread, Text("A"), "low"));
        await WaitUntil(() => Server(0).SentMethod("turn/start").Count > 0);
        var second = await Collect(e.Send(thread, Text("B"), "low"));
        Assert.Equal<AnswerEvent>([new AnswerEvent.Failed(new EngineError.Busy(), "")], second);
        await e.InterruptAsync(thread);
        var firstEvents = await firstTask;
        Assert.Equal(new AnswerEvent.Interrupted(""), firstEvents[^1]);
    }

    [Fact]
    public async Task SilentTurnGoesSlowThenTimesOut()
    {
        var timing = new CodexAppServerEngine.Timing
        {
            SlowAfter = TimeSpan.FromSeconds(0.1),
            GiveUpAfter = TimeSpan.FromSeconds(0.3),
            InterruptGrace = TimeSpan.FromSeconds(0.1),
        };
        var e = Engine(s =>
        {
            Healthy(s, _ => []);
            var baseScript = s.OnMessage;
            s.OnMessage = (m, id, p) => m == "turn/interrupt" ? [Reply(id, O())] : baseScript(m, id, p);
        }, timing);
        var thread = await e.StartThreadAsync(Cfg);
        var events = await Collect(e.Send(thread, Text("Q"), "low"));
        Assert.Equal<AnswerEvent>(
            [new AnswerEvent.Started("u1"), new AnswerEvent.Slow(), new AnswerEvent.Failed(new EngineError.Timeout(), "")],
            events);
    }

    [Fact]
    public async Task SignOutSendsLogoutAndReportsSignedOut()
    {
        var signedIn = true;
        var e = Engine(s =>
        {
            Healthy(s);
            var baseScript = s.OnMessage;
            s.OnMessage = (m, id, p) =>
            {
                switch (m)
                {
                    case "account/logout":
                        Volatile.Write(ref signedIn, false);
                        return [Reply(id, O())];
                    case "account/read" when !Volatile.Read(ref signedIn):
                        return [Reply(id, O(("account", JsonValue.Null.Instance), ("requiresOpenaiAuth", true)))];
                    default:
                        return baseScript(m, id, p);
                }
            };
        });
        var before = await e.StatusAsync();
        Assert.Equal(new EngineStatus.Ready("me@example.com", "plus"), before);
        await e.LogoutAsync();
        var after = await e.StatusAsync();
        Assert.Equal(new EngineStatus.SignedOut(), after);
        var logout = Server(0).SentMethod("account/logout")[0];
        Assert.Equal(JsonValue.Null.Instance, logout["params"]); // account/logout takes params: null
    }

    [Fact]
    public async Task MissingCodexIsReportedNotHung()
    {
        var e = Engine(_ => Assert.Fail("must not spawn"), locate: _ => new LocateResult.NotInstalled());
        var status = await e.StatusAsync();
        Assert.Equal(new EngineStatus.NotInstalled(), status);
        var events = await Collect(e.Send("x", Text("Q"), "low"));
        Assert.Equal<AnswerEvent>([new AnswerEvent.Failed(new EngineError.NotInstalled(), "")], events);
    }

    [Fact]
    public async Task WorkspaceIsEmptiedBeforeAThreadStarts()
    {
        var ws = Path.Combine(_tmp, "workspace");
        Directory.CreateDirectory(ws);
        await File.WriteAllTextAsync(Path.Combine(ws, "stray.txt"), "x");
        Directory.CreateDirectory(Path.Combine(ws, "nested", "deeper"));
        var e = Engine(s => Healthy(s));
        await e.StartThreadAsync(Cfg);
        Assert.Empty(Directory.EnumerateFileSystemEntries(ws));
    }

    // ── Beyond the Swift suite ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ReasoningItemSendsThinkingOnceBeforeText()
    {
        var e = Engine(s => Healthy(s, _ =>
        [
            Note("item/started", O(("threadId", "thr"), ("turnId", "u1"), ("item", O(("type", "reasoning"))))),
            Note("item/started", O(("threadId", "thr"), ("turnId", "u1"), ("item", O(("type", "reasoning"))))),
            Note("item/agentMessage/delta", O(("threadId", "thr"), ("turnId", "u1"), ("delta", "Hi"))),
            Note("turn/completed", O(("threadId", "thr"), ("turn", O(("id", "u1"), ("status", "completed"))))),
        ]));
        var thread = await e.StartThreadAsync(Cfg);
        var events = await Collect(e.Send(thread, Text("Q"), "low"));
        Assert.Equal<AnswerEvent>(
            [new AnswerEvent.Started("u1"), new AnswerEvent.Thinking(), new AnswerEvent.Delta("Hi"), new AnswerEvent.Completed("Hi")],
            events);
    }

    [Fact]
    public async Task SecondUnexpectedExitLeavesTheEngineCrashed()
    {
        var e = Engine(s => Healthy(s));
        Assert.True((await e.StatusAsync()).IsReady);
        // Poll the status rather than sleeping: the engine notices a crash on its reader, and how
        // soon that runs depends on the machine, not on a fixed delay.
        Server(0).Crash();
        var status = await StatusUntil(e, s => s.IsReady && ServerCount == 2);
        Assert.True(status.IsReady);
        Assert.Equal(2, ServerCount);
        Server(1).Crash();
        status = await StatusUntil(e, s => s is EngineStatus.Failed);
        Assert.Equal(new EngineStatus.Failed("Codex restarted — press Ask again."), status);
        Assert.Equal(2, ServerCount); // no third launch
    }

    [Fact]
    public async Task ShutdownIsNotCountedAsACrash()
    {
        var e = Engine(s => Healthy(s));
        for (var i = 0; i < 3; i++)
        {
            Assert.True((await e.StatusAsync()).IsReady);
            await e.ShutdownAsync();
        }
        Assert.Equal(3, ServerCount);
    }

    [Fact]
    public async Task RequestWithoutReplyTimesOut()
    {
        var e = Engine(s =>
        {
            Healthy(s);
            var baseScript = s.OnMessage;
            s.OnMessage = (m, id, p) => m == "model/list" ? [] : baseScript(m, id, p);
        }, new CodexAppServerEngine.Timing { Request = TimeSpan.FromSeconds(0.2) });
        var error = await Assert.ThrowsAsync<EngineException>(() => e.ModelsAsync());
        Assert.Equal(new EngineError.Rpc("model/list timed out"), error.Error);
    }

    [Fact]
    public async Task NoticesCarryUsageLoginAndAccountChanges()
    {
        var e = Engine(s => Healthy(s));
        await e.StatusAsync();
        Server(0).Push(Note("account/rateLimits/updated", O(("rateLimits", O(
            ("primary", O(("usedPercent", 10), ("windowDurationMins", 300))))))));
        Server(0).Push(Note("account/login/completed", O(("loginId", "l1"), ("success", false), ("error", "denied"))));
        Server(0).Push(Note("account/updated", O()));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var notices = new List<EngineNotice>();
        for (var i = 0; i < 3; i++) notices.Add(await e.Notices.ReadAsync(cts.Token));
        Assert.Equal(new EngineNotice.Usage(new CodexRpc.Usage(null, [new CodexRpc.Usage.Window(300, 10, null)])), notices[0]);
        Assert.Equal(new EngineNotice.LoginCompleted(false, "denied"), notices[1]);
        Assert.Equal(new EngineNotice.AccountChanged(), notices[2]);
    }

    // ── Review regressions ──────────────────────────────────────────────────────────────

    private static readonly EngineStatus CrashedStatus = new EngineStatus.Failed(new EngineError.Crashed().Message);

    private const string ServerRequest = """{"id":"srv-1","method":"item/commandExecution/requestApproval","params":{}}""";

    private static bool IsDecline(JsonValue m) => m["id"]?.StringValue == "srv-1";

    /// <summary>A server whose first spawn blocks until <paramref name="proceed"/> is released.</summary>
    private CodexAppServerEngine EngineBlockingFirstSpawn(SemaphoreSlim spawning, SemaphoreSlim proceed)
    {
        var launches = 0;
        return Engine(s =>
        {
            Healthy(s);
            if (Interlocked.Increment(ref launches) != 1) return;
            spawning.Release();
            proceed.Wait(TimeSpan.FromSeconds(10));
        });
    }

    [Fact]
    public async Task ShutdownDuringLaunchAbandonsTheNewProcess()
    {
        using var spawning = new SemaphoreSlim(0);
        using var proceed = new SemaphoreSlim(0);
        var e = EngineBlockingFirstSpawn(spawning, proceed);
        var status = e.StatusAsync();
        Assert.True(await spawning.WaitAsync(TimeSpan.FromSeconds(5)));
        await e.ShutdownAsync(); // lands between locating/spawning and the launch taking the transport
        proceed.Release();
        Assert.Equal(CrashedStatus, await status);
        Assert.True(Server(0).Terminated);
        Assert.Empty(Server(0).Sent); // never initialized: the process was not revived

        // A shutdown is not a crash: the next call starts a fresh process.
        Assert.True((await e.StatusAsync()).IsReady);
        Assert.Equal(2, ServerCount);
    }

    [Fact]
    public async Task DisposeDuringLaunchStaysDisposed()
    {
        using var spawning = new SemaphoreSlim(0);
        using var proceed = new SemaphoreSlim(0);
        var e = EngineBlockingFirstSpawn(spawning, proceed);
        var status = e.StatusAsync();
        Assert.True(await spawning.WaitAsync(TimeSpan.FromSeconds(5)));
        await e.DisposeAsync();
        proceed.Release();
        Assert.Equal(CrashedStatus, await status);
        Assert.True(Server(0).Terminated);

        Assert.Equal(CrashedStatus, await e.StatusAsync());
        var events = await Collect(e.Send("thr", Text("Q"), "low"));
        Assert.Equal<AnswerEvent>([new AnswerEvent.Failed(new EngineError.Crashed(), "")], events);
        Assert.Equal(1, ServerCount); // nothing relaunched after dispose
    }

    [Fact]
    public async Task LogSubscribersCannotStallOrKillTheReader()
    {
        var seen = new List<string>();
        var release = new ManualResetEventSlim();
        Action<string> collecting = line => { lock (seen) seen.Add(line); };
        Action<string> throwing = _ => throw new InvalidOperationException("subscriber bug");
        Action<string> blocking = line =>
        {
            if (line.Contains("lockdown breach")) release.Wait(TimeSpan.FromSeconds(10)); // a UI Dispatcher.Invoke
        };
        InterviewLog.Message += collecting;
        InterviewLog.Message += throwing;
        InterviewLog.Message += blocking;
        try
        {
            var e = Engine(s => Healthy(s));
            var thread = await e.StartThreadAsync(Cfg);
            Server(0).Push(ServerRequest); // logged on the reader
            await WaitUntil(() => Server(0).Sent.Any(IsDecline));
            Assert.Contains(Server(0).Sent, IsDecline);
            await WaitUntil(() => { lock (seen) return seen.Any(l => l.Contains("lockdown breach")); });
            lock (seen) Assert.Contains(seen, l => l.Contains("lockdown breach"));

            // The reader survived the throwing subscriber and isn't held by the blocked one.
            Assert.True((await e.StatusAsync()).IsReady);
            Assert.Equal(new AnswerEvent.Completed("Hello"), (await Collect(e.Send(thread, Text("Q"), "low")))[^1]);
            Assert.Equal(1, ServerCount);
        }
        finally
        {
            InterviewLog.Message -= collecting;
            InterviewLog.Message -= throwing;
            InterviewLog.Message -= blocking;
            release.Set();
        }
    }

    [Fact]
    public async Task ReadErrorTerminatesTheProcessBeforeTheNextStart()
    {
        var e = Engine(s => Healthy(s));
        Assert.True((await e.StatusAsync()).IsReady);
        Server(0).FailReading(new IOException("pipe broke")); // codex itself is still running
        await WaitUntil(() => Server(0).Terminated);
        Assert.True(Server(0).Terminated); // not orphaned
        Assert.True((await e.StatusAsync()).IsReady);
        Assert.Equal(2, ServerCount);
    }

    [Fact]
    public async Task DeclineIsWrittenOffTheReader()
    {
        var writing = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var e = Engine(s =>
        {
            Healthy(s);
            s.BeforeSend = m =>
            {
                if (!IsDecline(m)) return;
                writing.Set();
                release.Wait(TimeSpan.FromSeconds(10)); // the write blocks, as on a full stdin pipe
            };
        }, new CodexAppServerEngine.Timing { Request = TimeSpan.FromSeconds(3) });
        try
        {
            await e.StartThreadAsync(Cfg);
            Server(0).Push(ServerRequest);
            await WaitUntil(() => writing.IsSet);
            Assert.True(writing.IsSet);
            // While the decline is stuck, replies are still read and handled.
            Assert.Equal(3, (await e.UsageAsync()).Lowest?.RemainingPercent);
        }
        finally
        {
            release.Set();
        }
        await WaitUntil(() => Server(0).Sent.Any(IsDecline));
        Assert.Equal("decline", Server(0).Sent.First(IsDecline)["result"]?["decision"]?.StringValue);
    }

    [Fact]
    public async Task CodexIsLocatedOncePerLaunchAndNotWhileRunning()
    {
        var locates = 0;
        var found = new LocateResult.Found(CodexCommand.Direct("/fake/codex"), [0, 159, 3]);
        var e = Engine(s => Healthy(s), locate: _ =>
        {
            Interlocked.Increment(ref locates);
            return found;
        });
        Assert.True((await e.StatusAsync()).IsReady);
        Assert.Equal(1, Volatile.Read(ref locates)); // the launch reuses what the status check found
        Assert.True((await e.StatusAsync()).IsReady);
        await e.ModelsAsync();
        Assert.Equal(1, Volatile.Read(ref locates)); // running: no --version probes

        Server(0).Crash();
        await WaitUntil(() => Server(0).Terminated);
        await e.StartThreadAsync(Cfg); // a restart outside StatusAsync locates in the launch
        Assert.Equal(2, Volatile.Read(ref locates));
    }

    [Fact]
    public async Task HungInitializeCountsAsAnUnexpectedExit()
    {
        var e = Engine(s =>
        {
            Healthy(s);
            var baseScript = s.OnMessage;
            s.OnMessage = (m, id, p) => m == "initialize" ? [] : baseScript(m, id, p);
        }, new CodexAppServerEngine.Timing { Request = TimeSpan.FromSeconds(0.1) });
        var timedOut = new EngineStatus.Failed("initialize timed out");
        Assert.Equal(timedOut, await e.StatusAsync());
        Assert.True(Server(0).Terminated);
        Assert.Equal(timedOut, await e.StatusAsync());
        Assert.Equal(CrashedStatus, await e.StatusAsync()); // not relaunched forever
        Assert.Equal(2, ServerCount);
    }

    [Fact]
    public async Task LongStreamedAnswerKeepsEveryDeltaInOrder()
    {
        var deltas = Enumerable.Range(0, 500).Select(i => $"w{i} ").ToList();
        var e = Engine(s => Healthy(s, _ =>
        [
            .. deltas.Select(d => Note("item/agentMessage/delta", O(("threadId", "thr"), ("turnId", "u1"), ("delta", d)))),
            Note("turn/completed", O(("threadId", "thr"), ("turn", O(("id", "u1"), ("status", "interrupted"))))),
        ]));
        var thread = await e.StartThreadAsync(Cfg);
        var events = await Collect(e.Send(thread, Text("Q"), "low"));
        Assert.Equal(new AnswerEvent.Interrupted(string.Concat(deltas)), events[^1]);
    }
}
