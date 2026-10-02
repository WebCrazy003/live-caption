using LocalCaption.Core.Interview;
using static LocalCaption.Core.Interview.CodexRpc;

namespace LocalCaption.Core.Tests;

/// <summary>
/// <c>testdata/codex/</c> — the Codex app-server contract (SPEC-12), asserted exactly as
/// <c>app/Tests/LocalCaptionKitTests/CodexRPCTests.swift</c> asserts it on the Mac. Events and
/// responses are mostly real codex-cli 0.159.3 output, so a protocol change shows up here.
/// </summary>
public class CodexRpcTests
{
    private static JsonValue Load(string name) =>
        JsonValue.Parse(File.ReadAllText(Path.Combine(Vectors.Root, "codex", name)));

    private static IReadOnlyList<JsonValue> Cases(string name) =>
        Load(name)["cases"]?.ArrayValue ?? throw new InvalidOperationException($"{name}: cases");

    private static JsonValue Str(string? s) => s is null ? JsonValue.Null.Instance : s;

    private static JsonValue Num(long? n) => n is { } v ? v : JsonValue.Null.Instance;

    // ── Requests ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RequestBuilders()
    {
        foreach (var c in Cases("requests.json"))
        {
            var a = c["args"] ?? JsonValue.Obj();
            string S(string k) => a[k]?.StringValue ?? "";
            var build = c["build"]?.StringValue;
            JsonValue built = build switch
            {
                "initialize" => InitializeParams(S("name"), S("title"), S("version")),
                "thread_start" => ThreadStartParams(S("model"), S("cwd"), S("base_instructions")),
                "thread_resume" => ThreadResumeParams(S("thread_id"), S("model"), S("cwd"), S("base_instructions")),
                "turn_start" => TurnStartParams(S("thread_id"),
                    (a["input"]?.ArrayValue ?? []).Select(i => i["type"]?.StringValue == "localImage"
                        ? (Input)new Input.LocalImage(i["path"]?.StringValue ?? "")
                        : new Input.Text(i["text"]?.StringValue ?? "")),
                    S("effort"), a["model"]?.StringValue),
                "turn_interrupt" => TurnInterruptParams(S("thread_id"), S("turn_id")),
                "thread_archive" => ThreadArchiveParams(S("thread_id")),
                "model_list" => ModelListParams,
                "login_start" => LoginStartParams,
                "login_cancel" => LoginCancelParams(S("login_id")),
                "logout" => LogoutParams,
                "decline" => DeclineResponse(a["id"] ?? JsonValue.Null.Instance, S("method")),
                _ => throw new InvalidOperationException($"unknown builder {build}"),
            };
            Assert.True((c["expect"] ?? JsonValue.Null.Instance).Equals(built),
                $"requests.json: {build}\n  expected: {c["expect"]}\n  actual:   {built}");
        }
        var args = (Load("requests.json")["launch_arguments"]?.ArrayValue ?? [])
            .Select(v => v.StringValue).OfType<string>().ToList();
        Assert.True(args.Count > 0, "launch_arguments");
        Assert.Equal(args, LaunchArguments);
    }

    [Fact]
    public void RequestLineIsSingleLineJson()
    {
        var line = Request(3, "turn/start", TurnStartParams("t", [new Input.Text("a\nb")], "low")).Line();
        Assert.DoesNotContain("\n", line);
        Assert.IsType<Incoming.ServerRequest>(Decode(line)); // an outgoing request has id + method
    }

    // ── Decode ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DecodeVectors()
    {
        foreach (var c in Cases("decode.json"))
        {
            var line = c["line"]?.StringValue ?? "";
            var decoded = Decode(line);
            if (c["expect"] is not { } expect || expect is JsonValue.Null)
            {
                Assert.True(decoded is null, $"decode.json: {line} → {decoded}");
                continue;
            }
            switch (expect["class"]?.StringValue, decoded)
            {
                case ("response", Incoming.Response r):
                    Assert.Equal(expect["id"]?.IntValue, r.Id);
                    break;
                case ("error", Incoming.Error e):
                    Assert.Equal(expect["id"]?.IntValue, e.Id);
                    Assert.Equal(expect["code"]?.IntValue, e.Code);
                    Assert.Equal(expect["message"]?.StringValue, e.Message);
                    break;
                case ("server_request", Incoming.ServerRequest s):
                    Assert.Equal(expect["method"]?.StringValue, s.Method);
                    break;
                case ("notification", Incoming.Notification n):
                    Assert.Equal(expect["method"]?.StringValue, n.Method);
                    break;
                default:
                    Assert.Fail($"decode.json: {line} → {decoded}");
                    break;
            }
        }
    }

    // ── Events ──────────────────────────────────────────────────────────────────────────

    private static JsonValue Json(TurnFailure? f)
    {
        if (f is null) return JsonValue.Null.Instance;
        var kind = f.Kind switch
        {
            FailureKind.UsageLimit => "usage_limit",
            FailureKind.SignedOut => "signed_out",
            FailureKind.Network => "network",
            FailureKind.ContextFull => "context_full",
            _ => "other",
        };
        return JsonValue.Obj(("message", f.Message), ("kind", kind));
    }

    private static JsonValue Json(Usage u) => JsonValue.Obj(
        ("plan_type", Str(u.PlanType)),
        ("windows", new JsonValue.Array(u.Windows.Select(w => (JsonValue)JsonValue.Obj(
            ("minutes", Num(w.Minutes)), ("used_percent", w.UsedPercent), ("resets_at", Num(w.ResetsAt)))).ToArray())));

    private static JsonValue Json(Event e) => e switch
    {
        Event.TurnStarted x => JsonValue.Obj(("event", "turn_started"), ("thread_id", x.ThreadId), ("turn_id", x.TurnId)),
        Event.ItemStarted x => JsonValue.Obj(("event", "item_started"), ("thread_id", x.ThreadId),
            ("turn_id", x.TurnId), ("type", x.Type)),
        Event.AgentDelta x => JsonValue.Obj(("event", "agent_delta"), ("thread_id", x.ThreadId),
            ("turn_id", x.TurnId), ("delta", x.Delta)),
        Event.AgentMessageCompleted x => JsonValue.Obj(("event", "agent_message_completed"), ("thread_id", x.ThreadId),
            ("turn_id", x.TurnId), ("text", x.Text)),
        Event.TurnCompleted x => JsonValue.Obj(("event", "turn_completed"), ("thread_id", x.ThreadId),
            ("turn_id", x.TurnId), ("status", x.Status), ("failure", Json(x.Failure))),
        Event.Error x => JsonValue.Obj(("event", "error"), ("thread_id", x.ThreadId), ("turn_id", x.TurnId),
            ("will_retry", x.WillRetry), ("failure", Json(x.Failure))),
        Event.RateLimitsUpdated x => JsonValue.Obj(("event", "rate_limits_updated"), ("usage", Json(x.Usage))),
        Event.LoginCompleted x => JsonValue.Obj(("event", "login_completed"), ("login_id", Str(x.LoginId)),
            ("success", x.Success), ("error", Str(x.ErrorMessage))),
        Event.AccountUpdated => JsonValue.Obj(("event", "account_updated")),
        Event.Other x => JsonValue.Obj(("event", "other"), ("method", x.Method)),
        _ => throw new InvalidOperationException($"unmapped event {e.GetType().Name}"),
    };

    [Fact]
    public void EventVectors()
    {
        foreach (var c in Cases("events.json"))
        {
            var name = c["name"]?.StringValue ?? "?";
            var message = c["message"] ?? throw new InvalidOperationException($"{name}: message");
            // Through the same path the engine uses: serialize to a line, decode, map.
            if (Decode(message.Line()) is not Incoming.Notification n)
            {
                Assert.Fail($"{name}: not decoded as a notification");
                return;
            }
            var got = Json(EventFor(n.Method, n.Params));
            Assert.True(got.Equals(c["expect"]), $"{name}\n  expected: {c["expect"]}\n  actual:   {got}");
        }
    }

    [Fact]
    public void ToolGuardAllowsChatItemsAndWebSearchOnly()
    {
        // Web search is allowed since 2026-10-02 (discovery-jd); commands, edits, MCP stay blocked.
        Assert.True(AllowedItemTypes.SetEquals(["userMessage", "agentMessage", "reasoning", "webSearch"]));
        foreach (var blocked in new[] { "commandExecution", "fileChange", "mcpToolCall", "dynamicToolCall", "imageGeneration" })
            Assert.False(AllowedItemTypes.Contains(blocked), blocked);
    }

    // ── Responses ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResponseVectors()
    {
        foreach (var c in Cases("responses.json"))
        {
            var result = c["result"] ?? JsonValue.Null.Instance;
            var expect = c["expect"] ?? JsonValue.Null.Instance;
            switch (c["kind"]?.StringValue)
            {
                case "account":
                    switch (AccountFrom(result))
                    {
                        case Account.SignedOut:
                            Assert.Equal("signed_out", expect["account"]?.StringValue);
                            break;
                        case Account.ChatGpt g:
                            Assert.Equal("chatgpt", expect["account"]?.StringValue);
                            Assert.Equal(expect["email"]?.StringValue, g.Email);
                            Assert.Equal(expect["plan"]?.StringValue, g.Plan);
                            break;
                        case var other:
                            Assert.Fail($"unexpected account {other}");
                            break;
                    }
                    break;
                case "usage":
                    var usage = Json(UsageFromRead(result));
                    Assert.True(usage.Equals(expect), $"usage\n  expected: {expect}\n  actual:   {usage}");
                    break;
                case "models":
                    JsonValue got = new JsonValue.Array(Models(result).Select(m => (JsonValue)JsonValue.Obj(
                        ("id", m.Id), ("display_name", m.DisplayName), ("is_default", m.IsDefault),
                        ("default_effort", Str(m.DefaultEffort)),
                        ("efforts", new JsonValue.Array(m.Efforts.Select(e => (JsonValue)e).ToArray())),
                        ("accepts_images", m.AcceptsImages))).ToArray());
                    Assert.True(got.Equals(expect), $"models\n  expected: {expect}\n  actual:   {got}");
                    break;
                default:
                    Assert.Fail("unknown kind");
                    break;
            }
        }
    }

    // ── Version ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void VersionGate()
    {
        Assert.Equal([0L, 159, 3], Version("codex-cli 0.159.3\n"));
        Assert.Equal([0L, 160, 0], Version("codex-cli 0.160.0-beta.1"));
        Assert.Null(Version("codex-cli"));
        Assert.True(IsSupported([0, 159, 3]));
        Assert.True(IsSupported([1, 0]));
        Assert.False(IsSupported([0, 158, 9]));
    }

    [Fact]
    public void UsageLowestWindow()
    {
        var u = new Usage("plus", [new Usage.Window(300, 10, null), new Usage.Window(10080, 85, null)]);
        Assert.Equal("Weekly", u.Lowest?.Label);
        Assert.Equal(15, u.Lowest?.RemainingPercent);
    }

    // ── Wire format (Windows-only) ──
    // Escaping and number formats were produced by Swift's JSONEncoder([.sortedKeys, .withoutEscapingSlashes]).

    [Fact]
    public void LineSortsKeysOrdinally()
    {
        var value = new JsonValue.Object(new[] { "b", "a10", "B", "a2", "_x" }.ToDictionary(k => k, _ => (JsonValue)0L));
        Assert.Equal("""{"B":0,"_x":0,"a10":0,"a2":0,"b":0}""", value.Line());
    }

    [Fact]
    public void LineEscapesLikeJsonEncoder()
    {
        JsonValue s = "a/b\u0001\u001f\u007f\b\f\n\r\t\"\\é😀\u2028";
        Assert.Equal("\"a/b\\u0001\\u001f\u007f\\b\\f\\n\\r\\t\\\"\\\\é😀\u2028\"", s.Line());
        Assert.Equal("""{"a":[true,false,null,-7,"x"],"b":{}}""",
            JsonValue.Obj(("b", JsonValue.Obj()), ("a", JsonValue.Arr(true, false, JsonValue.Null.Instance, -7L, "x"))).Line());
    }

    [Fact]
    public void ParseMatchesJsonDecoder()
    {
        // Integral numbers decode as integers, like Swift decoding Int64 before Double.
        Assert.Equal(JsonValue.Arr(1L, 100L, 100L, 0L, new JsonValue.Double(1.5), new JsonValue.Double(1e20)),
            JsonValue.Parse("[1.0, 1e2, 1E2, -0, 1.5, 100000000000000000000]"));
        Assert.Equal(JsonValue.Obj(("a", 1L)), JsonValue.Parse("""{"a":1,"a":2}""")); // first key wins
        Assert.Null(JsonValue.TryParse("""["\ud800"]"""));
        Assert.Null(JsonValue.TryParse("[1] x"));
        Assert.Null(JsonValue.TryParse("[1,]"));
        Assert.Equal(new JsonValue.Int(5), JsonValue.TryParse("5"));
    }
}
