using System.Globalization;
using System.Threading.Channels;
using LocalCaption.Core.Interview;

namespace LocalCaption.Interview;

/// <summary>
/// The answer engine interface (SPEC-12 §The interface), the port of <c>AnswerEngine.swift</c>.
/// Codex today; an OpenAI-API engine can implement it later without touching the interview flow
/// or UI (SPEC-11 D2).
/// </summary>
public interface IAnswerEngine
{
    /// <summary>Installed? Version ok? Signed in? Never throws; starts the process if needed.</summary>
    Task<EngineStatus> StatusAsync();

    /// <summary>The visible models (<c>model/list</c>).</summary>
    /// <exception cref="EngineException">The engine could not answer.</exception>
    Task<IReadOnlyList<CodexRpc.Model>> ModelsAsync();

    /// <summary>The account's rate-limit windows (<c>account/rateLimits/read</c>).</summary>
    /// <exception cref="EngineException">The engine could not answer.</exception>
    Task<CodexRpc.Usage> UsageAsync();

    /// <summary>A new locked-down thread; returns its id.</summary>
    /// <exception cref="EngineException">The engine could not answer.</exception>
    Task<string> StartThreadAsync(ThreadConfig config);

    /// <summary>Re-open a stored thread with the same lockdown.</summary>
    /// <exception cref="EngineException">The engine could not answer.</exception>
    Task ResumeThreadAsync(string id, ThreadConfig config);

    /// <summary>
    /// One turn. Effort is per turn: Codex only accepts it on <c>turn/start</c>, and it persists.
    /// <paramref name="model"/> (when non-null) switches the thread's model from this turn on.
    /// The turn starts at once, whether or not the stream is read; the stream always ends with
    /// exactly one of <see cref="AnswerEvent.Completed"/>, <see cref="AnswerEvent.Interrupted"/>
    /// or <see cref="AnswerEvent.Failed"/>, and never throws.
    /// </summary>
    IAsyncEnumerable<AnswerEvent> Send(string threadId, IReadOnlyList<CodexRpc.Input> input, string effort,
                                       string? model = null);

    /// <summary>Interrupt the thread's running turn, if any.</summary>
    Task InterruptAsync(string threadId);

    /// <summary>Archive a thread so the CV doesn't linger in Codex's session store. Best effort.</summary>
    Task ArchiveThreadAsync(string id);

    /// <summary>Start ChatGPT sign-in; completion arrives as an <see cref="EngineNotice.LoginCompleted"/>.</summary>
    /// <exception cref="EngineException">The engine could not answer.</exception>
    Task<LoginTicket> StartLoginAsync();

    /// <summary>Cancel a sign-in started by <see cref="StartLoginAsync"/>. Best effort.</summary>
    Task CancelLoginAsync(LoginTicket ticket);

    /// <summary>Sign this engine's Codex home out of ChatGPT.</summary>
    /// <exception cref="EngineException">The engine could not answer.</exception>
    Task LogoutAsync();

    /// <summary>
    /// Usage updates and login completion, while the engine runs. Single consumer; holds the
    /// newest 16 when nobody reads.
    /// </summary>
    ChannelReader<EngineNotice> Notices { get; }

    /// <summary>Stop the process: close its stdin, kill it after 2 s. A later call starts it again.</summary>
    Task ShutdownAsync();
}

/// <summary>What a thread is opened with.</summary>
/// <param name="Model">The model id.</param>
/// <param name="BaseInstructions">The interview-copilot prompt (<c>InterviewPrompt.BaseInstructions</c>).</param>
public sealed record ThreadConfig(string Model, string BaseInstructions);

/// <summary>A started ChatGPT sign-in: open <paramref name="AuthUrl"/> in the default browser.</summary>
public sealed record LoginTicket(string LoginId, Uri AuthUrl);

/// <summary>Whether the engine can answer, and as whom.</summary>
public abstract record EngineStatus
{
    private EngineStatus() { }

    /// <summary>No <c>codex</c> found.</summary>
    public sealed record NotInstalled : EngineStatus;

    /// <summary>Only <c>codex</c> builds older than <see cref="CodexRpc.MinimumVersion"/> found.</summary>
    public sealed record TooOld(string Version) : EngineStatus;

    /// <summary>Codex runs but its home has no ChatGPT sign-in.</summary>
    public sealed record SignedOut : EngineStatus;

    /// <summary>Signed in. <paramref name="Plan"/> is the plan type, or the auth type for a non-ChatGPT account.</summary>
    public sealed record Ready(string? Email, string? Plan) : EngineStatus;

    /// <summary>Codex could not be started or asked.</summary>
    public sealed record Failed(string Message) : EngineStatus;

    /// <summary>Signed in and usable.</summary>
    public bool IsReady => this is Ready;

    /// <summary>One line for Settings and the interview setup, with this platform's install hints.</summary>
    public string Summary => SummaryFor(OperatingSystem.IsWindows());

    /// <summary>
    /// <see cref="Summary"/> for a given platform: Windows hints name npm, macOS hints Homebrew
    /// (SPEC-16 §4.1). Everything else is the Mac's wording.
    /// </summary>
    public string SummaryFor(bool windows) => this switch
    {
        NotInstalled => windows
            ? "Codex isn't installed. Install it with npm: npm install -g @openai/codex"
            : "Codex isn't installed. Install it with Homebrew: brew install codex",
        TooOld(var v) =>
            $"Codex {v} is too old — LocalCaption needs {CodexRpc.MinimumVersion} or newer "
            + (windows ? "(npm update -g @openai/codex)." : "(brew upgrade codex)."),
        SignedOut => "Not signed in to ChatGPT. Sign in here or in Settings → Codex.",
        Ready(var email, var plan) => plan is null
            ? $"Signed in as {email ?? "ChatGPT"}"
            : $"Signed in as {email ?? "ChatGPT"} ({Capitalized(plan)})",
        Failed(var message) => $"Codex failed to start: {message}",
        _ => "",
    };

    /// <summary>Foundation's <c>capitalized</c>: each word's first letter upper-case, the rest lower-case.</summary>
    internal static string Capitalized(string s) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
}

/// <summary>One step of a streamed turn.</summary>
public abstract record AnswerEvent
{
    private AnswerEvent() { }

    /// <summary>Codex accepted the turn.</summary>
    public sealed record Started(string TurnId) : AnswerEvent;

    /// <summary>A reasoning item began — show "Thinking…" until text arrives.</summary>
    public sealed record Thinking : AnswerEvent;

    /// <summary>More answer text, in order.</summary>
    public sealed record Delta(string Text) : AnswerEvent;

    /// <summary>No text 30 s after the request — still waiting.</summary>
    public sealed record Slow : AnswerEvent;

    /// <summary>The full answer (Codex's final text, which replaces the streamed deltas).</summary>
    public sealed record Completed(string Text) : AnswerEvent;

    /// <summary>The turn was interrupted; <paramref name="Partial"/> is what had streamed.</summary>
    public sealed record Interrupted(string Partial) : AnswerEvent;

    /// <summary>The turn failed; <paramref name="Partial"/> is what had streamed.</summary>
    public sealed record Failed(EngineError Error, string Partial) : AnswerEvent;
}

/// <summary>Something the engine reports outside a turn.</summary>
public abstract record EngineNotice
{
    private EngineNotice() { }

    /// <summary><c>account/rateLimits/updated</c>.</summary>
    public sealed record Usage(CodexRpc.Usage Value) : EngineNotice;

    /// <summary><c>account/login/completed</c>.</summary>
    public sealed record LoginCompleted(bool Success, string? Error) : EngineNotice;

    /// <summary><c>account/updated</c>: re-read the account.</summary>
    public sealed record AccountChanged : EngineNotice;
}

/// <summary>Why a call or a turn failed, with the line the UI shows (<see cref="Message"/>).</summary>
public abstract record EngineError
{
    private EngineError() { }

    /// <summary>No <c>codex</c> found.</summary>
    public sealed record NotInstalled : EngineError;

    /// <summary>Only an old <c>codex</c> found.</summary>
    public sealed record TooOld(string Version) : EngineError;

    /// <summary>The ChatGPT sign-in is gone.</summary>
    public sealed record SignedOut : EngineError;

    /// <summary>A turn is already running.</summary>
    public sealed record Busy : EngineError;

    /// <summary>Nothing for 2 minutes.</summary>
    public sealed record Timeout : EngineError;

    /// <summary>The process exited (or the engine was shut down) mid-request.</summary>
    public sealed record Crashed : EngineError;

    /// <summary>The plan's usage limit; <paramref name="ResetsAt"/> from the tightest window, when known.</summary>
    public sealed record UsageLimit(DateTimeOffset? ResetsAt) : EngineError;

    /// <summary>Connection trouble.</summary>
    public sealed record Network(string Detail) : EngineError;

    /// <summary>The tool-call guard stopped the turn; <paramref name="ItemType"/> is the offending item.</summary>
    public sealed record BlockedTool(string ItemType) : EngineError;

    /// <summary>A JSON-RPC error or a request that timed out.</summary>
    public sealed record Rpc(string Detail) : EngineError;

    /// <summary>Anything else.</summary>
    public sealed record Other(string Detail) : EngineError;

    /// <summary>The user-facing line (Swift's <c>errorDescription</c>).</summary>
    public string Message => this switch
    {
        NotInstalled => "Codex isn't installed.",
        TooOld(var v) => $"Codex {v} is too old.",
        SignedOut => "Not signed in to ChatGPT.",
        Busy => "Still answering the previous question.",
        Timeout => "No answer after 2 minutes.",
        Crashed => "Codex restarted — press Ask again.",
        UsageLimit(null) => "Plus usage limit reached.",
        UsageLimit({ } at) =>
            $"Plus limit reached — resets at {at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}.",
        Network(var m) => $"Network problem: {m}",
        BlockedTool(var t) => $"Blocked: the model tried to use a tool ({t}).",
        Rpc(var m) => m,
        Other(var m) => m,
        _ => "",
    };
}

/// <summary>An <see cref="EngineError"/> thrown by an engine call; <see cref="Exception.Message"/> is the user-facing line.</summary>
public sealed class EngineException(EngineError error) : Exception(error.Message)
{
    /// <summary>What went wrong.</summary>
    public EngineError Error { get; } = error;
}
