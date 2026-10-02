using System.Threading.Channels;
using LocalCaption.Core.Interview;

namespace LocalCaption.Interview.Tests;

/// <summary>
/// A fake <c>codex app-server</c>: records what the engine sends and answers via a script —
/// the port of <c>CodexEngineTests.FakeServer</c>.
/// </summary>
internal sealed class FakeServer : ILineTransport
{
    /// <summary>(method, id, params) → lines to emit. Responses must echo the request id: use <see cref="Reply"/>.</summary>
    public delegate IEnumerable<string> Script(string method, JsonValue? id, JsonValue @params);

    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly Lock _gate = new();
    private readonly List<JsonValue> _sent = [];
    private int _terminated;

    public Script OnMessage { get; set; } = (_, _, _) => [];

    /// <summary>Runs on the sender's thread before a line is recorded — a test can block a write here.</summary>
    public Action<JsonValue>? BeforeSend { get; set; }

    /// <summary><see cref="Terminate"/> has been called.</summary>
    public bool Terminated => Volatile.Read(ref _terminated) != 0;

    public IAsyncEnumerable<string> Lines => _lines.Reader.ReadAllAsync();

    public IReadOnlyList<JsonValue> Sent
    {
        get { lock (_gate) return [.. _sent]; }
    }

    public IReadOnlyList<JsonValue> SentMethod(string method) =>
        Sent.Where(m => m["method"]?.StringValue == method).ToList();

    public void Send(string line)
    {
        var msg = JsonValue.Parse(line);
        BeforeSend?.Invoke(msg);
        lock (_gate) _sent.Add(msg);
        if (msg["method"]?.StringValue is not { } method) return;
        foreach (var output in OnMessage(method, msg["id"], msg["params"] ?? JsonValue.Null.Instance)) Push(output);
    }

    public void Push(string line) => _lines.Writer.TryWrite(line);

    public void Crash() => _lines.Writer.TryComplete();

    /// <summary>Reading stdout fails (the line stream throws) while the "process" keeps running.</summary>
    public void FailReading(Exception error) => _lines.Writer.TryComplete(error);

    public void Terminate()
    {
        Volatile.Write(ref _terminated, 1);
        _lines.Writer.TryComplete();
    }

    public static string Reply(JsonValue? id, JsonValue result) =>
        JsonValue.Obj(("id", id ?? JsonValue.Null.Instance), ("result", result)).Line();

    public static string Note(string method, JsonValue @params) =>
        JsonValue.Obj(("method", method), ("params", @params)).Line();
}
