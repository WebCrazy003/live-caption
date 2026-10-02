using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;

namespace LocalCaption.Interview.Tests;

/// <summary>
/// Records every call; answers each turn with <see cref="Reply"/> — the port of the Swift
/// <c>InterviewFlowTests.FakeEngine</c>.
/// </summary>
internal sealed class FakeEngine : IAnswerEngine
{
    public sealed record Sent(string ThreadId, IReadOnlyList<CodexRpc.Input> Input, string Effort, string? Model,
                              IReadOnlyList<bool> ImageFilesExisted)
    {
        public string? Text => Input.FirstOrDefault() is CodexRpc.Input.Text t ? t.Value : null;
    }

    private readonly Lock _lock = new();
    private readonly List<ThreadConfig> _threads = [];
    private readonly List<Sent> _sent = [];
    private readonly List<string> _archived = [];
    private readonly List<string> _resumed = [];
    private readonly Channel<EngineNotice> _notices = Channel.CreateUnbounded<EngineNotice>();
    private Channel<AnswerEvent>? _held;
    private int _interrupts;
    private int _calls;

    public Func<string, IReadOnlyList<AnswerEvent>> Reply { get; set; } =
        _ => [new AnswerEvent.Delta("Brief"), new AnswerEvent.Delta("ing"), new AnswerEvent.Completed("Briefing\nREADY")];

    /// <summary>When set, a turn whose text matches stays open until <see cref="Release"/> or an interrupt.</summary>
    public Func<string, bool> HoldIf { get; set; } = _ => false;

    /// <summary>Thrown by <see cref="StartThreadAsync"/> when set.</summary>
    public EngineError? FailStart { get; set; }

    /// <summary>When set, <see cref="StartThreadAsync"/> returns only once this completes (a slow <c>thread/start</c>).</summary>
    public TaskCompletionSource? StartGate { get; set; }

    /// <summary>Write each turn's events from a thread-pool thread, a few ms apart, as the real engine's reader does.</summary>
    public bool EventsFromThreadPool { get; set; }

    /// <summary>When true, a turn's events don't begin with <see cref="AnswerEvent.Started"/>.</summary>
    public bool OmitStarted { get; set; }

    /// <summary>When true, <see cref="InterruptAsync"/> is counted but does not end the held turn.</summary>
    public bool IgnoreInterrupts { get; set; }

    public IReadOnlyList<CodexRpc.Model> ModelList { get; set; } =
    [
        new("gpt-6-luna", "GPT-6-Luna", "", false, "medium", ["low", "medium"], true),
        new("gpt-6.1-sol", "GPT-6.1-Sol", "", true, "low", ["low", "medium", "high"], true),
    ];

    public IReadOnlyList<ThreadConfig> Threads { get { lock (_lock) return [.. _threads]; } }
    public IReadOnlyList<Sent> SentTurns { get { lock (_lock) return [.. _sent]; } }
    public IReadOnlyList<string> Archived { get { lock (_lock) return [.. _archived]; } }
    public IReadOnlyList<string> Resumed { get { lock (_lock) return [.. _resumed]; } }
    public int Interrupts { get { lock (_lock) return _interrupts; } }

    /// <summary>Every engine call of any kind.</summary>
    public int Calls { get { lock (_lock) return _calls; } }

    public IReadOnlyList<string> SentTexts => SentTurns.Select(s => s.Text).OfType<string>().ToList();

    public ChannelReader<EngineNotice> Notices => _notices.Reader;

    private void Count()
    {
        lock (_lock) _calls++;
    }

    public void Release(string text = "Done")
    {
        Channel<AnswerEvent>? c;
        lock (_lock)
        {
            c = _held;
            _held = null;
        }
        c?.Writer.TryWrite(new AnswerEvent.Completed(text));
        c?.Writer.TryComplete();
    }

    public Task<EngineStatus> StatusAsync()
    {
        Count();
        return Task.FromResult<EngineStatus>(new EngineStatus.Ready("me@example.com", "plus"));
    }

    public Task<IReadOnlyList<CodexRpc.Model>> ModelsAsync()
    {
        Count();
        return Task.FromResult(ModelList);
    }

    public Task<CodexRpc.Usage> UsageAsync()
    {
        Count();
        return Task.FromResult(new CodexRpc.Usage("plus", [new CodexRpc.Usage.Window(300, 5, null)]));
    }

    public Task<string> StartThreadAsync(ThreadConfig config)
    {
        Count();
        if (FailStart is { } error) return Task.FromException<string>(new EngineException(error));
        string id;
        lock (_lock)
        {
            _threads.Add(config);
            id = $"thr{_threads.Count}";
        }
        return StartGate is { } gate ? gate.Task.ContinueWith(_ => id, TaskScheduler.Default) : Task.FromResult(id);
    }

    public Task ResumeThreadAsync(string id, ThreadConfig config)
    {
        Count();
        lock (_lock) _resumed.Add(id);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<AnswerEvent> Send(string threadId, IReadOnlyList<CodexRpc.Input> input, string effort,
                                              string? model = null)
    {
        Count();
        var existed = input.OfType<CodexRpc.Input.LocalImage>().Select(i => File.Exists(i.Path)).ToList();
        var sent = new Sent(threadId, input, effort, model, existed);
        lock (_lock) _sent.Add(sent);
        var text = sent.Text ?? "";
        var channel = Channel.CreateUnbounded<AnswerEvent>();
        if (HoldIf(text))
        {
            channel.Writer.TryWrite(new AnswerEvent.Started("u"));
            channel.Writer.TryWrite(new AnswerEvent.Delta("Partial"));
            lock (_lock) _held = channel;
            return channel.Reader.ReadAllAsync();
        }
        List<AnswerEvent> events = OmitStarted ? [.. Reply(text)] : [new AnswerEvent.Started("u"), .. Reply(text)];
        if (EventsFromThreadPool)
        {
            _ = Task.Run(async () =>
            {
                foreach (var e in events)
                {
                    await Task.Delay(5).ConfigureAwait(false);
                    channel.Writer.TryWrite(e);
                }
                channel.Writer.TryComplete();
            });
        }
        else
        {
            foreach (var e in events) channel.Writer.TryWrite(e);
            channel.Writer.TryComplete();
        }
        return channel.Reader.ReadAllAsync();
    }

    public Task InterruptAsync(string threadId)
    {
        Count();
        Channel<AnswerEvent>? c;
        lock (_lock)
        {
            _interrupts++;
            if (IgnoreInterrupts) return Task.CompletedTask;
            c = _held;
            _held = null;
        }
        c?.Writer.TryWrite(new AnswerEvent.Interrupted("Partial"));
        c?.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public Task ArchiveThreadAsync(string id)
    {
        Count();
        lock (_lock) _archived.Add(id);
        return Task.CompletedTask;
    }

    public Task<LoginTicket> StartLoginAsync()
    {
        Count();
        return Task.FromException<LoginTicket>(new EngineException(new EngineError.Other("n/a")));
    }

    public Task CancelLoginAsync(LoginTicket ticket)
    {
        Count();
        return Task.CompletedTask;
    }

    public Task LogoutAsync()
    {
        Count();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync()
    {
        Count();
        return Task.CompletedTask;
    }
}

/// <summary>A private clipboard: a sequence number and the images on it.</summary>
internal sealed class FakeClipboard : IClipboardImages
{
    private List<byte[]> _images = [];

    public long SequenceNumber { get; private set; } = 100;
    public int Reads { get; private set; }
    public int Clears { get; private set; }
    public int ImageCount => _images.Count;

    /// <summary>How many of the next <see cref="ReadImages"/> calls throw (the clipboard held by another process).</summary>
    public int FailReads { get; set; }

    /// <summary>How many of the next <see cref="ClearIfUnchanged"/> calls throw.</summary>
    public int FailClears { get; set; }

    public bool ContainsImages => _images.Count > 0;

    public void CopyImages(params byte[][] images)
    {
        _images = [.. images];
        SequenceNumber++;
    }

    public void CopyText()
    {
        _images = [];
        SequenceNumber++;
    }

    public IReadOnlyList<byte[]> ReadImages()
    {
        Reads++;
        if (FailReads > 0)
        {
            FailReads--;
            throw new IOException("The clipboard is in use by another process.");
        }
        return [.. _images];
    }

    public bool ClearIfUnchanged(long sequence)
    {
        if (FailClears > 0)
        {
            FailClears--;
            throw new IOException("The clipboard is in use by another process.");
        }
        if (sequence != SequenceNumber) return false;
        _images = [];
        SequenceNumber++;
        Clears++;
        return true;
    }
}

/// <summary>The area selector: returns <see cref="Next"/> (null = Esc).</summary>
internal sealed class FakeScreen : IScreenCapture
{
    public Func<byte[]?> Next { get; set; } = () => null;
    public int Calls { get; private set; }

    /// <summary>When set, the selector faults with it.</summary>
    public Exception? Failure { get; set; }

    public Task<byte[]?> SelectAreaAsync()
    {
        Calls++;
        return Failure is { } e ? Task.FromException<byte[]?>(e) : Task.FromResult(Next());
    }
}

/// <summary>
/// A UI thread for tests: one dedicated thread running a message loop, with itself as the
/// <see cref="SynchronizationContext"/> — what the WPF dispatcher gives the controller in the app.
/// </summary>
internal sealed class UiThread : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public UiThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "test UI thread" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    /// <summary>Exceptions thrown by posted callbacks (none expected).</summary>
    public ConcurrentQueue<Exception> Unhandled { get; } = new();

    private void Loop()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            try { callback(state); }
            catch (Exception e) { Unhandled.Enqueue(e); }
        }
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        try { _queue.Add((d, state)); }
        catch (InvalidOperationException) { }   // disposed: a late continuation is dropped
    }

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Run <paramref name="body"/> on the UI thread; completes when it does.</summary>
    public Task Run(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ => _ = RunAsync(), null);
        return done.Task;

        async Task RunAsync()
        {
            try
            {
                await body();
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>A temp store, library and outbox, a fake engine, clipboard and screen, and a UI thread.</summary>
public abstract class InterviewTestBase : IDisposable
{
    private protected readonly string Tmp = Path.Combine(Path.GetTempPath(), $"lc-interview-{Guid.NewGuid():N}");
    private protected readonly Store Store;
    private protected readonly FakeEngine Engine = new();
    private protected readonly CodexService Codex;
    private protected readonly FakeClipboard Clipboard = new();
    private protected readonly FakeScreen Screen = new();
    private protected readonly UiThread Ui = new();
    private protected readonly Config.InterviewGroup Config = new() { Mode = "interview" };
    private protected InterviewLibrary Library;
    private protected int Beeps;

    protected InterviewTestBase()
    {
        Directory.CreateDirectory(Tmp);
        Store = new Store(Path.Combine(Tmp, "db.sqlite"));
        Library = new InterviewLibrary(Path.Combine(Tmp, "library"));
        Codex = new CodexService(Engine, _ => { });
    }

    private protected string Outbox => Path.Combine(Tmp, "outbox");

    private protected InterviewControllerOptions Options { get; set; } = new();

    public void Dispose()
    {
        Ui.Dispose();
        Codex.Dispose();
        Store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Tmp, recursive: true); } catch (IOException) { }
        Assert.Empty(Ui.Unhandled);
    }

    /// <summary>Run a test body on the UI thread, after the Codex service has read the fake's models (Swift: <c>await env.codex.refresh()</c>).</summary>
    private protected Task OnUi(Func<Task> body) => Ui.Run(async () =>
    {
        await Codex.RefreshAsync();
        await body();
    });

    /// <summary>A controller on the current (UI) thread.</summary>
    private protected InterviewController NewController(InterviewRecord? existing = null) =>
        new(Store, Library, Codex, () => Config, Screen, Clipboard,
            Options with { OutboxRoot = Outbox, Beep = () => Beeps++ }, existing);

    /// <summary>The interview as stored in the database.</summary>
    private protected InterviewRecord Saved(InterviewController i) =>
        Store.Interview(i.Record?.Id ?? throw new InvalidOperationException("no record"))
        ?? throw new InvalidOperationException("not saved");

    private protected string Write(string text, string relative)
    {
        var path = Path.Combine(Tmp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>The owner's four skills, as importable folders.</summary>
    private protected Dictionary<string, string> ImportSkills(IEnumerable<string>? names = null)
    {
        var ids = new Dictionary<string, string>();
        foreach (var name in names ?? InterviewSteps.All.Select(s => s.Slug()))
        {
            var file = Write($"---\nname: {name}\n---\n# /{name}\nDo the {name} step.", Path.Combine("src", name, "SKILL.md"));
            ids[name] = Library.ImportSkill(Path.GetDirectoryName(file)!).Skill.Id;
        }
        return ids;
    }

    private protected string ImportCv() =>
        Library.ImportDocument(Write("Jane Doe\r\nSwift, 8 years.", Path.Combine("src", "Jane CV.txt")),
                               InterviewLibraryIndex.DocumentKind.Cv).Id;

    /// <summary>Wait (on the UI thread, without blocking it) until <paramref name="condition"/> holds or 5 s pass.</summary>
    private protected static async Task WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
    }

    /// <summary>A few distinct bytes standing in for a PNG (the platform side encodes real ones).</summary>
    private protected static byte[] Png(int tag) => [0x89, (byte)'P', (byte)'N', (byte)'G', (byte)tag];
}
