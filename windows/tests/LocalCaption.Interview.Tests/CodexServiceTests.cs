using System.Threading.Channels;
using LocalCaption.Core.Interview;

namespace LocalCaption.Interview.Tests;

/// <summary><see cref="CodexService"/> over a stub engine.</summary>
public sealed class CodexServiceTests
{
    private sealed class StubEngine : IAnswerEngine
    {
        public readonly Channel<EngineNotice> NoticeChannel = Channel.CreateUnbounded<EngineNotice>();
        public EngineStatus Status = new EngineStatus.Ready("me@example.com", "plus");
        public IReadOnlyList<CodexRpc.Model> ModelList = [];
        public CodexRpc.Usage UsageValue = new("plus", [new CodexRpc.Usage.Window(300, 50, null)]);
        public bool LogoutFails;
        public int StatusCalls;
        public readonly List<string> Calls = [];

        /// <summary>When set, a status read takes its answer now but returns only once this completes.</summary>
        public volatile TaskCompletionSource? StatusGate;

        public ChannelReader<EngineNotice> Notices => NoticeChannel.Reader;

        public async Task<EngineStatus> StatusAsync()
        {
            Interlocked.Increment(ref StatusCalls);
            var status = Status;
            if (StatusGate is { } gate) await gate.Task;
            return status;
        }

        public Task<IReadOnlyList<CodexRpc.Model>> ModelsAsync() => Task.FromResult(ModelList);
        public Task<CodexRpc.Usage> UsageAsync() => Task.FromResult(UsageValue);
        public Task<string> StartThreadAsync(ThreadConfig config) => Task.FromResult("thr");
        public Task ResumeThreadAsync(string id, ThreadConfig config) => Task.CompletedTask;

        public IAsyncEnumerable<AnswerEvent> Send(string threadId, IReadOnlyList<CodexRpc.Input> input, string effort,
                                                  string? model = null) => AsyncEnumerable.Empty<AnswerEvent>();

        public Task InterruptAsync(string threadId) => Task.CompletedTask;
        public Task ArchiveThreadAsync(string id) => Task.CompletedTask;

        public Task<LoginTicket> StartLoginAsync()
        {
            lock (Calls) Calls.Add("login");
            return Task.FromResult(new LoginTicket("l1", new Uri("https://auth.openai.com/oauth/authorize?x=1")));
        }

        public Task CancelLoginAsync(LoginTicket ticket)
        {
            lock (Calls) Calls.Add("cancel:" + ticket.LoginId);
            return Task.CompletedTask;
        }

        public Task LogoutAsync()
        {
            if (LogoutFails) throw new EngineException(new EngineError.Rpc("nope"));
            Status = new EngineStatus.SignedOut();
            return Task.CompletedTask;
        }

        public Task ShutdownAsync() => Task.CompletedTask;
    }

    private static CodexRpc.Model Model(string id, bool isDefault = false, bool images = true) =>
        new(id, id, "", isDefault, "low", ["low", "medium"], images);

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task RefreshReadsStatusModelsAndUsageAndRaisesChanged()
    {
        var engine = new StubEngine { ModelList = [Model("gpt-6-luna")] };
        using var service = new CodexService(engine, _ => { });
        var changes = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref changes);
        Assert.Null(service.Status);
        await service.RefreshAsync();
        Assert.True(service.IsReady);
        Assert.Single(service.Models);
        Assert.Equal(engine.UsageValue, service.Usage);
        Assert.NotNull(service.UsageUpdatedAt);
        Assert.False(service.Checking);
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task SignedOutRefreshSkipsModelsAndUsage()
    {
        var engine = new StubEngine { Status = new EngineStatus.SignedOut(), ModelList = [Model("gpt-6-luna")] };
        using var service = new CodexService(engine, _ => { });
        await service.RefreshAsync();
        Assert.False(service.IsReady);
        Assert.Empty(service.Models);
        Assert.Null(service.Usage);
    }

    [Fact]
    public async Task ResolvedModelFallsBackFromConfiguredToRecommendedToDefault()
    {
        var engine = new StubEngine { ModelList = [Model("gpt-6-luna"), Model("gpt-6.1-sol", isDefault: true)] };
        using var service = new CodexService(engine, _ => { });
        Assert.Equal("anything", service.ResolvedModel("anything")); // nothing listed yet: trust config
        await service.RefreshAsync();
        Assert.Equal("gpt-6.1-sol", service.ResolvedModel("gpt-6.1-sol"));
        Assert.Equal("gpt-6-luna", service.ResolvedModel(""));
        Assert.Equal("gpt-6-luna", service.ResolvedModel("gone-model"));
        Assert.Null(service.ModelFallbackNotice("gpt-6.1-sol"));
        Assert.Equal("Codex doesn't offer gone-model any more — using gpt-6-luna.", service.ModelFallbackNotice("gone-model"));

        engine.ModelList = [Model("other"), Model("house", isDefault: true)];
        await service.RefreshAsync();
        Assert.Equal("house", service.ResolvedModel("gone-model"));
        engine.ModelList = [Model("other")];
        await service.RefreshAsync();
        Assert.Equal("gone-model", service.ResolvedModel("gone-model"));
    }

    [Fact]
    public async Task EffortsAndImagesDefaultWhenTheModelIsUnknown()
    {
        var engine = new StubEngine { ModelList = [Model("text-only", images: false)] };
        using var service = new CodexService(engine, _ => { });
        await service.RefreshAsync();
        Assert.Equal(["low", "medium"], service.Efforts("text-only"));
        Assert.Equal(["low", "medium", "high"], service.Efforts("unknown"));
        Assert.False(service.AcceptsImages("text-only"));
        Assert.True(service.AcceptsImages("unknown"));
    }

    [Fact]
    public async Task UsageIsLowBelowTwentyPercentInAnyWindow()
    {
        var engine = new StubEngine
        {
            UsageValue = new CodexRpc.Usage("plus", [new CodexRpc.Usage.Window(300, 81, null), new CodexRpc.Usage.Window(10080, 10, null)]),
        };
        using var service = new CodexService(engine, _ => { });
        Assert.False(service.UsageIsLow); // unknown usage is not low
        await service.RefreshUsageAsync();
        Assert.True(service.UsageIsLow);
        engine.UsageValue = new CodexRpc.Usage("plus", [new CodexRpc.Usage.Window(300, 80, null)]);
        await service.RefreshUsageAsync();
        Assert.False(service.UsageIsLow);
    }

    [Fact]
    public async Task SignInOpensTheBrowserAndALoginNoticeEndsIt()
    {
        var engine = new StubEngine { Status = new EngineStatus.SignedOut() };
        var opened = new List<Uri>();
        using var service = new CodexService(engine, opened.Add);
        await service.StartSignInAsync();
        Assert.Equal("l1", service.SignIn?.LoginId);
        Assert.Equal("auth.openai.com", Assert.Single(opened).Host);

        var calls = engine.StatusCalls;
        engine.NoticeChannel.Writer.TryWrite(new EngineNotice.LoginCompleted(false, null));
        await WaitFor(() => service.SignIn is null && engine.StatusCalls > calls && !service.Checking);
        Assert.Equal("Sign-in did not complete.", service.SignInError);
    }

    [Fact]
    public async Task CancelSignInCancelsTheTicketOnce()
    {
        var engine = new StubEngine();
        using var service = new CodexService(engine, _ => { });
        await service.StartSignInAsync();
        await service.CancelSignInAsync();
        await service.CancelSignInAsync();
        Assert.Null(service.SignIn);
        Assert.Equal(["login", "cancel:l1"], engine.Calls);
    }

    [Fact]
    public async Task SignOutClearsUsageAndReportsFailures()
    {
        var engine = new StubEngine();
        using var service = new CodexService(engine, _ => { });
        await service.RefreshAsync();
        Assert.NotNull(service.Usage);
        await service.SignOutAsync();
        Assert.Null(service.Usage);
        Assert.Equal(new EngineStatus.SignedOut(), service.Status);

        engine.LogoutFails = true;
        await service.SignOutAsync();
        Assert.Equal("Couldn't sign out: nope", service.SignInError);
    }

    [Fact]
    public async Task UsageAndAccountNoticesUpdateState()
    {
        var engine = new StubEngine();
        using var service = new CodexService(engine, _ => { });
        var pushed = new CodexRpc.Usage(null, [new CodexRpc.Usage.Window(300, 5, null)]);
        engine.NoticeChannel.Writer.TryWrite(new EngineNotice.Usage(pushed));
        await WaitFor(() => service.Usage == pushed);
        engine.NoticeChannel.Writer.TryWrite(new EngineNotice.AccountChanged());
        await WaitFor(() => service.Status is not null && !service.Checking);
    }

    [Fact]
    public async Task ThrowingChangedHandlerDoesNotStopNotices()
    {
        var engine = new StubEngine();
        using var service = new CodexService(engine, _ => { });
        var calls = 0;
        service.Changed += (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("view model bug");
        };
        var first = new CodexRpc.Usage(null, [new CodexRpc.Usage.Window(300, 5, null)]);
        engine.NoticeChannel.Writer.TryWrite(new EngineNotice.Usage(first));
        await WaitFor(() => service.Usage == first);

        // The notice loop survived the throw: later notices are still handled.
        var second = new CodexRpc.Usage(null, [new CodexRpc.Usage.Window(300, 6, null)]);
        engine.NoticeChannel.Writer.TryWrite(new EngineNotice.Usage(second));
        engine.NoticeChannel.Writer.TryWrite(new EngineNotice.AccountChanged());
        await WaitFor(() => service.Status is not null && !service.Checking);
        Assert.Equal(engine.UsageValue, service.Usage); // the refresh re-read usage after the pushed one
        Assert.True(Volatile.Read(ref calls) > 2);

        // A refresh called directly is not left stuck in Checking by the throwing handler either.
        await service.RefreshAsync();
        Assert.False(service.Checking);
    }

    [Fact]
    public async Task LoginCompletedDuringARefreshIsNotLost()
    {
        var engine = new StubEngine { Status = new EngineStatus.SignedOut() };
        using var service = new CodexService(engine, _ => { });
        await service.StartSignInAsync();
        Assert.NotNull(service.SignIn);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StatusGate = gate;
        var refresh = service.RefreshAsync(); // reads "signed out"…
        await WaitFor(() => Volatile.Read(ref engine.StatusCalls) == 1);

        engine.Status = new EngineStatus.Ready("me@example.com", "plus"); // …then the sign-in completes
        engine.NoticeChannel.Writer.TryWrite(new EngineNotice.LoginCompleted(true, null));
        await WaitFor(() => service.SignIn is null);

        engine.StatusGate = null;
        gate.SetResult();
        await refresh;
        Assert.Equal(new EngineStatus.Ready("me@example.com", "plus"), service.Status); // re-checked, not stuck signed out
        Assert.Equal(2, Volatile.Read(ref engine.StatusCalls));
        Assert.False(service.Checking);
        Assert.Null(service.SignInError);
    }
}
