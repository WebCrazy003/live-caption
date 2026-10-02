using System.Globalization;

namespace LocalCaption.Core.Interview;

/// <summary>
/// The pure half of the Codex engine (SPEC-12 §Kit vs app split), a 1:1 port of
/// <c>CodexRPC.swift</c>: JSON-RPC message builders, incoming-message decoding, event mapping,
/// usage/model/account parsing and error classification for <c>codex app-server</c> (pinned:
/// 0.159.3). No process, no I/O. Shared with the Mac app through <c>testdata/codex/</c>.
/// </summary>
public static class CodexRpc
{
    /// <summary>The oldest <c>codex</c> CLI this protocol layer supports.</summary>
    public const string MinimumVersion = "0.159.3";

    // ── Lockdown (SPEC-12 §Lockdown, verified in S0.8) ──────────────────────────────────

    /// <summary>Features turned off with <c>--disable</c>, in launch order.</summary>
    public static IReadOnlyList<string> DisabledFeatures { get; } =
    [
        "shell_tool", "unified_exec", "apps", "browser_use", "browser_use_external", "computer_use",
        "image_generation", "multi_agent", "plugins", "tool_suggest", "skill_search", "sleep_tool",
        "in_app_browser", "goals", "hooks",
    ];

    /// <summary>
    /// <c>-c</c> config overrides. Web search stays on (owner decision 2026-10-02): the
    /// discovery-jd skill researches the company with source URLs. Files, commands, edits, MCP
    /// and AGENTS.md stay off.
    /// </summary>
    public static IReadOnlyList<string> ConfigOverrides { get; } =
        ["web_search=\"live\"", "mcp_servers={}", "project_doc_max_bytes=0"];

    /// <summary>Arguments after the <c>codex</c> executable.</summary>
    public static IReadOnlyList<string> LaunchArguments =>
    [
        "app-server",
        .. DisabledFeatures.SelectMany(f => new[] { "--disable", f }),
        .. ConfigOverrides.SelectMany(c => new[] { "-c", c }),
    ];

    /// <summary>The only item types a locked-down turn may produce. Anything else trips the tool-call guard.</summary>
    public static IReadOnlySet<string> AllowedItemTypes { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "userMessage", "agentMessage", "reasoning", "webSearch" };

    // ── Outgoing ────────────────────────────────────────────────────────────────────────

    /// <summary>One element of a <c>turn/start</c> input.</summary>
    public abstract record Input
    {
        private Input() { }

        /// <summary>A text part.</summary>
        public sealed record Text(string Value) : Input;

        /// <summary>An image file on disk, sent by path.</summary>
        public sealed record LocalImage(string Path) : Input;
    }

    /// <summary>A client→server request.</summary>
    public static JsonValue Request(long id, string method, JsonValue @params) =>
        JsonValue.Obj(("jsonrpc", "2.0"), ("id", id), ("method", method), ("params", @params));

    /// <summary>A client→server reply to a server request.</summary>
    private static JsonValue Response(JsonValue id, JsonValue result) =>
        JsonValue.Obj(("jsonrpc", "2.0"), ("id", id), ("result", result));

    /// <summary>A client→server error reply to a server request.</summary>
    private static JsonValue ErrorResponse(JsonValue id, long code, string message) =>
        JsonValue.Obj(("jsonrpc", "2.0"), ("id", id), ("error", JsonValue.Obj(("code", code), ("message", message))));

    /// <summary>The lockdown both <c>thread/start</c> and <c>thread/resume</c> apply (SPEC-11 D4).</summary>
    private const string ApprovalPolicy = "never", Sandbox = "read-only";

    /// <summary>A client→server notification; <paramref name="params"/> defaults to <c>{}</c>.</summary>
    public static JsonValue Notification(string method, JsonValue? @params = null) =>
        JsonValue.Obj(("jsonrpc", "2.0"), ("method", method), ("params", @params ?? JsonValue.Obj()));

    /// <summary><c>initialize</c>.</summary>
    public static JsonValue InitializeParams(string name, string title, string version) =>
        JsonValue.Obj(
            ("clientInfo", JsonValue.Obj(("name", name), ("title", title), ("version", version))),
            ("capabilities", JsonValue.Obj(("experimentalApi", true))));

    /// <summary><c>thread/start</c>: the locked-down chat thread (SPEC-12 §Threads &amp; turns).</summary>
    public static JsonValue ThreadStartParams(string model, string cwd, string baseInstructions, bool ephemeral = false) =>
        JsonValue.Obj(
            ("model", model),
            ("cwd", cwd),
            ("approvalPolicy", ApprovalPolicy),
            ("sandbox", Sandbox),
            ("baseInstructions", baseInstructions),
            ("ephemeral", ephemeral),
            ("serviceName", "localcaption"));

    /// <summary><c>thread/resume</c> re-applies the same lockdown to a stored thread.</summary>
    public static JsonValue ThreadResumeParams(string threadId, string model, string cwd, string baseInstructions) =>
        JsonValue.Obj(
            ("threadId", threadId),
            ("model", model),
            ("cwd", cwd),
            ("approvalPolicy", ApprovalPolicy),
            ("sandbox", Sandbox),
            ("baseInstructions", baseInstructions));

    /// <summary>
    /// <c>turn/start</c>. <paramref name="effort"/> goes on every turn: Codex persists a turn's
    /// effort to later turns. <paramref name="model"/>, when given, switches the thread's model
    /// from this turn on (the picker changed).
    /// </summary>
    public static JsonValue TurnStartParams(string threadId, IEnumerable<Input> input, string effort, string? model = null)
    {
        var items = input.Select(i => (JsonValue)(i switch
        {
            Input.Text t => JsonValue.Obj(("type", "text"), ("text", t.Value)),
            Input.LocalImage l => JsonValue.Obj(("type", "localImage"), ("path", l.Path)),
            _ => throw new ArgumentOutOfRangeException(nameof(input), i, "unknown input"),
        })).ToArray();
        var members = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
        {
            ["threadId"] = threadId,
            ["input"] = new JsonValue.Array(items),
            ["effort"] = effort,
        };
        if (model is not null) members["model"] = model;
        return new JsonValue.Object(members);
    }

    /// <summary><c>turn/interrupt</c>.</summary>
    public static JsonValue TurnInterruptParams(string threadId, string turnId) =>
        JsonValue.Obj(("threadId", threadId), ("turnId", turnId));

    /// <summary><c>thread/archive</c>.</summary>
    public static JsonValue ThreadArchiveParams(string threadId) => JsonValue.Obj(("threadId", threadId));

    /// <summary><c>model/list</c>.</summary>
    public static JsonValue ModelListParams { get; } = JsonValue.Obj(("includeHidden", false));

    /// <summary><c>account/login/start</c>.</summary>
    public static JsonValue LoginStartParams { get; } = JsonValue.Obj(("type", "chatgpt"));

    /// <summary><c>account/login/cancel</c>.</summary>
    public static JsonValue LoginCancelParams(string loginId) => JsonValue.Obj(("loginId", loginId));

    /// <summary><c>account/logout</c> takes <c>params: null</c> (0.159.3 schema).</summary>
    public static JsonValue LogoutParams { get; } = JsonValue.Null.Instance;

    /// <summary>
    /// The answer to a server→client request: always "no", in the shape each method expects
    /// (0.159.3 schema). With approvals <c>never</c> none should arrive; one that does is a
    /// lockdown breach the engine logs. Unknown methods get a JSON-RPC error.
    /// </summary>
    public static JsonValue DeclineResponse(JsonValue id, string method)
    {
        JsonValue result;
        switch (method)
        {
            case "item/commandExecution/requestApproval" or "item/fileChange/requestApproval":
                result = JsonValue.Obj(("decision", "decline"));
                break;
            case "applyPatchApproval" or "execCommandApproval":
                result = JsonValue.Obj(("decision", "abort"));
                break;
            case "item/permissions/requestApproval":
                result = JsonValue.Obj(("permissions", JsonValue.Obj()));
                break;
            case "item/tool/requestUserInput":
                result = JsonValue.Obj(("answers", JsonValue.Obj()));
                break;
            case "item/tool/call":
                result = JsonValue.Obj(("contentItems", JsonValue.Arr()), ("success", false));
                break;
            case "mcpServer/elicitation/request":
                result = JsonValue.Obj(("action", "decline"));
                break;
            default:
                return ErrorResponse(id, -32601, "Not supported by LocalCaption");
        }
        return Response(id, result);
    }

    // ── Incoming ────────────────────────────────────────────────────────────────────────

    /// <summary>One decoded stdout line.</summary>
    public abstract record Incoming
    {
        private Incoming() { }

        /// <summary>A successful reply to one of our requests.</summary>
        public sealed record Response(long Id, JsonValue Result) : Incoming;

        /// <summary>A JSON-RPC error reply to one of our requests.</summary>
        public sealed record Error(long Id, long Code, string Message) : Incoming;

        /// <summary>A server notification (no id).</summary>
        public sealed record Notification(string Method, JsonValue Params) : Incoming;

        /// <summary>A server→client request (id and method); answer with <see cref="DeclineResponse"/>.</summary>
        public sealed record ServerRequest(JsonValue Id, string Method, JsonValue Params) : Incoming;
    }

    /// <summary>Decode one line of stdout. <c>null</c> for blank or non-JSON lines.</summary>
    public static Incoming? Decode(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || JsonValue.TryParse(trimmed) is not JsonValue.Object o) return null;
        var method = o["method"]?.StringValue;
        var @params = o["params"] ?? JsonValue.Obj();
        if (o["id"] is { } id and not JsonValue.Null)
        {
            if (method is not null) return new Incoming.ServerRequest(id, method, @params);
            if (id.IntValue is not { } n) return null;
            if (o["error"] is { } err and not JsonValue.Null)
                return new Incoming.Error(n, err["code"]?.IntValue ?? 0, err["message"]?.StringValue ?? "");
            return new Incoming.Response(n, o["result"] ?? JsonValue.Null.Instance);
        }
        return method is not null ? new Incoming.Notification(method, @params) : null;
    }

    // ── Events ──────────────────────────────────────────────────────────────────────────

    /// <summary>Why a turn failed: Codex's message plus the class the UI reacts to.</summary>
    public sealed record TurnFailure(string Message, FailureKind Kind);

    /// <summary>The failure classes the engine distinguishes.</summary>
    public enum FailureKind
    {
        /// <summary>Plan usage or rate limit reached.</summary>
        UsageLimit,
        /// <summary>The ChatGPT sign-in is gone.</summary>
        SignedOut,
        /// <summary>Connection or stream trouble, or the server is overloaded.</summary>
        Network,
        /// <summary>The thread no longer fits the model's context window.</summary>
        ContextFull,
        /// <summary>Anything else.</summary>
        Other,
    }

    /// <summary>A server notification, mapped to what the engine acts on.</summary>
    public abstract record Event
    {
        private Event() { }

        /// <summary><c>turn/started</c>.</summary>
        public sealed record TurnStarted(string ThreadId, string TurnId) : Event;

        /// <summary><c>item/started</c>; <paramref name="Type"/> feeds the tool-call guard.</summary>
        public sealed record ItemStarted(string ThreadId, string TurnId, string Type) : Event;

        /// <summary><c>item/agentMessage/delta</c>.</summary>
        public sealed record AgentDelta(string ThreadId, string TurnId, string Delta) : Event;

        /// <summary><c>item/completed</c> for an agent message.</summary>
        public sealed record AgentMessageCompleted(string ThreadId, string TurnId, string Text) : Event;

        /// <summary><c>turn/completed</c>; <paramref name="Failure"/> when the turn carried an error.</summary>
        public sealed record TurnCompleted(string ThreadId, string TurnId, string Status, TurnFailure? Failure) : Event;

        /// <summary>The <c>error</c> notification.</summary>
        public sealed record Error(string ThreadId, string TurnId, TurnFailure Failure, bool WillRetry) : Event;

        /// <summary><c>account/rateLimits/updated</c>.</summary>
        public sealed record RateLimitsUpdated(Usage Usage) : Event;

        /// <summary><c>account/login/completed</c>; <paramref name="ErrorMessage"/> is the payload's <c>error</c>.</summary>
        public sealed record LoginCompleted(string? LoginId, bool Success, string? ErrorMessage) : Event;

        /// <summary><c>account/updated</c>.</summary>
        public sealed record AccountUpdated : Event;

        /// <summary>Any other notification.</summary>
        public sealed record Other(string Method) : Event;
    }

    /// <summary>Map a notification to an <see cref="Event"/> (Swift: <c>event(method:params:)</c>).</summary>
    public static Event EventFor(string method, JsonValue p)
    {
        var thread = p["threadId"]?.StringValue ?? "";
        switch (method)
        {
            case "turn/started":
                return new Event.TurnStarted(thread, p["turn"]?["id"]?.StringValue ?? "");
            case "item/started":
                return new Event.ItemStarted(thread, p["turnId"]?.StringValue ?? "",
                    p["item"]?["type"]?.StringValue ?? "");
            case "item/agentMessage/delta":
                return new Event.AgentDelta(thread, p["turnId"]?.StringValue ?? "", p["delta"]?.StringValue ?? "");
            case "item/completed":
                if (p["item"]?["type"]?.StringValue != "agentMessage") return new Event.Other(method);
                return new Event.AgentMessageCompleted(thread, p["turnId"]?.StringValue ?? "",
                    p["item"]?["text"]?.StringValue ?? "");
            case "turn/completed":
                var turn = p["turn"] ?? JsonValue.Null.Instance;
                return new Event.TurnCompleted(thread, turn["id"]?.StringValue ?? "",
                    turn["status"]?.StringValue ?? "failed",
                    turn["error"] is { } turnError ? Failure(turnError) : null);
            case "error":
                return new Event.Error(thread, p["turnId"]?.StringValue ?? "",
                    (p["error"] is { } error ? Failure(error) : null) ?? new TurnFailure("Unknown error", FailureKind.Other),
                    p["willRetry"]?.BoolValue ?? false);
            case "account/rateLimits/updated":
                return new Event.RateLimitsUpdated(UsageFromSnapshot(p["rateLimits"] ?? JsonValue.Null.Instance, null));
            case "account/login/completed":
                return new Event.LoginCompleted(p["loginId"]?.StringValue, p["success"]?.BoolValue ?? false,
                    p["error"]?.StringValue);
            case "account/updated":
                return new Event.AccountUpdated();
            default:
                return new Event.Other(method);
        }
    }

    /// <summary>
    /// <c>TurnError</c> → failure kind, from <c>codexErrorInfo</c> (a string, or a single-key
    /// object whose key is the code). <c>null</c> unless <paramref name="error"/> is an object.
    /// </summary>
    public static TurnFailure? Failure(JsonValue error)
    {
        if (error is not JsonValue.Object) return null;
        var message = error["message"]?.StringValue ?? "Unknown error";
        var info = error["codexErrorInfo"] ?? JsonValue.Null.Instance;
        var code = info.StringValue ?? info.ObjectValue?.Keys.FirstOrDefault();
        var kind = code switch
        {
            "usageLimitExceeded" or "rateLimitExceeded" or "sessionBudgetExceeded" => FailureKind.UsageLimit,
            "unauthorized" => FailureKind.SignedOut,
            "httpConnectionFailed" or "responseStreamConnectionFailed" or "responseStreamDisconnected"
                or "serverOverloaded" => FailureKind.Network,
            "contextWindowExceeded" => FailureKind.ContextFull,
            _ => FailureKind.Other,
        };
        return new TurnFailure(message, kind);
    }

    // ── Usage (Plus limits) ─────────────────────────────────────────────────────────────

    /// <summary>The account's rate-limit windows.</summary>
    /// <param name="PlanType">e.g. <c>plus</c>.</param>
    /// <param name="Windows">Shortest window first (5-hour before weekly).</param>
    public sealed record Usage(string? PlanType, IReadOnlyList<Usage.Window> Windows)
    {
        /// <summary>One rate-limit window.</summary>
        /// <param name="Minutes">The window's length.</param>
        /// <param name="UsedPercent">How much of it is used.</param>
        /// <param name="ResetsAt">Unix seconds, UTC.</param>
        public sealed record Window(long? Minutes, long UsedPercent, long? ResetsAt)
        {
            /// <summary><c>100 - UsedPercent</c>, never below 0.</summary>
            public long RemainingPercent => Math.Max(0, 100 - UsedPercent);

            /// <summary>Classify by duration, never by the <c>primary</c>/<c>secondary</c> slot (SPEC-12 §Usage).</summary>
            public string Label => Minutes switch
            {
                300 => "5-hour",
                10080 => "Weekly",
                { } m => m % 1440 == 0
                    ? (m / 1440).ToString(CultureInfo.InvariantCulture) + "-day"
                    : (m / 60).ToString(CultureInfo.InvariantCulture) + "-hour",
                null => "Usage",
            };
        }

        /// <summary>The tightest window — what a "running low" warning should look at.</summary>
        public Window? Lowest => Windows.MinBy(w => w.RemainingPercent);

        /// <inheritdoc />
        public bool Equals(Usage? other) =>
            other is not null && PlanType == other.PlanType && Windows.SequenceEqual(other.Windows);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(PlanType);
            foreach (var w in Windows) hash.Add(w);
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// <c>account/rateLimits/read</c> result → usage. Prefers the <c>codex</c> bucket (S0), then
    /// the single-bucket <c>rateLimits</c> view.
    /// </summary>
    public static Usage UsageFromRead(JsonValue result)
    {
        var snapshot = result["rateLimitsByLimitId"]?["codex"] ?? result["rateLimits"] ?? JsonValue.Null.Instance;
        return UsageFromSnapshot(snapshot, null);
    }

    /// <summary>A rate-limit snapshot → usage; <paramref name="plan"/> overrides the snapshot's <c>planType</c>.</summary>
    public static Usage UsageFromSnapshot(JsonValue s, string? plan)
    {
        var windows = new[] { "primary", "secondary" }
            .Select(slot => s[slot] is JsonValue.Object w && w["usedPercent"]?.IntValue is { } used
                ? new Usage.Window(w["windowDurationMins"]?.IntValue, used, w["resetsAt"]?.IntValue)
                : null)
            .OfType<Usage.Window>()
            .OrderBy(w => w.Minutes ?? long.MaxValue)
            .ToArray();
        return new Usage(plan ?? s["planType"]?.StringValue, windows);
    }

    // ── Account ─────────────────────────────────────────────────────────────────────────

    /// <summary>Who Codex is signed in as.</summary>
    public abstract record Account
    {
        private Account() { }

        /// <summary>No account.</summary>
        public sealed record SignedOut : Account;

        /// <summary>A ChatGPT sign-in.</summary>
        public sealed record ChatGpt(string? Email, string? Plan) : Account;

        /// <summary>Another auth mode (e.g. an API key).</summary>
        public sealed record Other(string Type) : Account;
    }

    /// <summary><c>account/read</c> result → account. <c>account: null</c> means signed out.</summary>
    public static Account AccountFrom(JsonValue result)
    {
        if (result["account"] is not JsonValue.Object a) return new Account.SignedOut();
        var type = a["type"]?.StringValue ?? "";
        if (type == "chatgpt") return new Account.ChatGpt(a["email"]?.StringValue, a["planType"]?.StringValue);
        return new Account.Other(type);
    }

    // ── Models ──────────────────────────────────────────────────────────────────────────

    /// <summary>One entry of the model picker.</summary>
    public sealed record Model(
        string Id, string DisplayName, string Description, bool IsDefault,
        string? DefaultEffort, IReadOnlyList<string> Efforts, bool AcceptsImages)
    {
        /// <inheritdoc />
        public bool Equals(Model? other) =>
            other is not null && Id == other.Id && DisplayName == other.DisplayName
            && Description == other.Description && IsDefault == other.IsDefault
            && DefaultEffort == other.DefaultEffort && Efforts.SequenceEqual(other.Efforts)
            && AcceptsImages == other.AcceptsImages;

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Id);
            hash.Add(DisplayName);
            hash.Add(Description);
            hash.Add(IsDefault);
            hash.Add(DefaultEffort);
            foreach (var e in Efforts) hash.Add(e);
            hash.Add(AcceptsImages);
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// <c>model/list</c> result → visible models (hidden ones dropped). <c>inputModalities</c>
    /// defaults to text+image per schema.
    /// </summary>
    public static IReadOnlyList<Model> Models(JsonValue result)
    {
        var models = new List<Model>();
        foreach (var m in result["data"]?.ArrayValue ?? [])
        {
            if (m["id"]?.StringValue is not { } id || m["hidden"]?.BoolValue == true) continue;
            var modalities = m["inputModalities"]?.ArrayValue?.Select(v => v.StringValue).OfType<string>().ToList()
                             ?? ["text", "image"];
            models.Add(new Model(
                id,
                m["displayName"]?.StringValue ?? id,
                m["description"]?.StringValue ?? "",
                m["isDefault"]?.BoolValue ?? false,
                m["defaultReasoningEffort"]?.StringValue,
                m["supportedReasoningEfforts"]?.ArrayValue?
                    .Select(e => e["reasoningEffort"]?.StringValue).OfType<string>().ToArray() ?? [],
                modalities.Contains("image")));
        }
        return models;
    }

    // ── Version ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>codex-cli 0.159.3</c> → <c>[0, 159, 3]</c>: the last whitespace-separated token that
    /// starts with a digit, with any <c>-prerelease</c> / <c>+build</c> suffix dropped.
    /// <c>null</c> when there is none or a part is not a number.
    /// </summary>
    public static IReadOnlyList<long>? Version(string output)
    {
        var token = SwiftText.SplitOnWhitespace(output).LastOrDefault(t => char.IsNumber(t[0]));
        if (token is null) return null;
        var core = token.Split(['-', '+'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? token;
        var parts = new List<long>();
        foreach (var part in core.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var digits = new string(part.TakeWhile(char.IsNumber).ToArray());
            if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
            parts.Add(n);
        }
        return parts.Count == 0 ? null : parts;
    }

    private static readonly IReadOnlyList<long> Minimum = Version(MinimumVersion)!;

    /// <summary>Whether <paramref name="version"/> is at least <see cref="MinimumVersion"/> (missing parts are 0).</summary>
    public static bool IsSupported(IReadOnlyList<long> version)
    {
        var minimum = Minimum;
        for (var i = 0; i < Math.Max(version.Count, minimum.Count); i++)
        {
            long a = i < version.Count ? version[i] : 0, b = i < minimum.Count ? minimum[i] : 0;
            if (a != b) return a > b;
        }
        return true;
    }
}
