using LocalCaption.Core;

namespace LocalCaption.Interview;

/// <summary>
/// The owner's skill sequence (SPEC-13 §Skill steps), found in the library by slug.
/// <c>InterviewController.Step</c> in Swift.
/// </summary>
public enum InterviewStep
{
    /// <summary><c>discovery-cv</c></summary>
    DiscoveryCv,
    /// <summary><c>discovery-jd</c></summary>
    DiscoveryJd,
    /// <summary><c>apply-instruction</c></summary>
    ApplyInstruction,
    /// <summary><c>live-coding-design</c></summary>
    LiveCoding,
}

/// <summary>
/// <c>apply-instruction</c> profiles. <see cref="Cultural"/> is the skill's name for the
/// behavioral profile. <c>InterviewController.Profile</c> in Swift.
/// </summary>
public enum InterviewProfile
{
    /// <summary><c>intro</c> — "Intro".</summary>
    Intro,
    /// <summary><c>tech</c> — "Tech".</summary>
    Tech,
    /// <summary><c>cultural</c> — "Behavioral".</summary>
    Cultural,
}

/// <summary>Spellings of <see cref="InterviewStep"/> and <see cref="InterviewProfile"/>.</summary>
public static class InterviewSteps
{
    /// <summary>Every step, in the order Start preparation runs them (Swift's <c>allCases</c>).</summary>
    public static IReadOnlyList<InterviewStep> All { get; } =
        [InterviewStep.DiscoveryCv, InterviewStep.DiscoveryJd, InterviewStep.ApplyInstruction, InterviewStep.LiveCoding];

    /// <summary>Every profile, in picker order.</summary>
    public static IReadOnlyList<InterviewProfile> Profiles { get; } =
        [InterviewProfile.Intro, InterviewProfile.Tech, InterviewProfile.Cultural];

    /// <summary>The skill slot and slash command, e.g. <c>discovery-cv</c>.</summary>
    public static string Slug(this InterviewStep step) => step switch
    {
        InterviewStep.DiscoveryCv => "discovery-cv",
        InterviewStep.DiscoveryJd => "discovery-jd",
        InterviewStep.ApplyInstruction => "apply-instruction",
        InterviewStep.LiveCoding => "live-coding-design",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
    };

    /// <summary>The step's name in the preparation panel.</summary>
    public static string Title(this InterviewStep step) => step switch
    {
        InterviewStep.DiscoveryCv => "Discovery CV",
        InterviewStep.DiscoveryJd => "Discovery JD",
        InterviewStep.ApplyInstruction => "Apply instruction",
        InterviewStep.LiveCoding => "Live coding & design",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
    };

    /// <summary>The step with this slug (Swift's <c>Step(rawValue:)</c>).</summary>
    public static InterviewStep? StepFromSlug(string? slug) => All.Cast<InterviewStep?>().FirstOrDefault(s => s!.Value.Slug() == slug);

    /// <summary>What <c>/apply-instruction</c> is sent with: <c>intro</c>, <c>tech</c>, <c>cultural</c>.</summary>
    public static string Raw(this InterviewProfile profile) => profile switch
    {
        InterviewProfile.Intro => "intro",
        InterviewProfile.Tech => "tech",
        InterviewProfile.Cultural => "cultural",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    /// <summary>The profile's button label: Intro, Tech, Behavioral.</summary>
    public static string Label(this InterviewProfile profile) => profile switch
    {
        InterviewProfile.Intro => "Intro",
        InterviewProfile.Tech => "Tech",
        InterviewProfile.Cultural => "Behavioral",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    /// <summary>The profile spelled <paramref name="raw"/> (Swift's <c>Profile(rawValue:)</c>), else null.</summary>
    public static InterviewProfile? ProfileFromRaw(string? raw) =>
        Profiles.Cast<InterviewProfile?>().FirstOrDefault(p => p!.Value.Raw() == raw);
}

/// <summary>Where the interview's Codex thread stands. <c>ThreadState</c> in Swift.</summary>
public abstract record InterviewThreadState
{
    private InterviewThreadState() { }

    /// <summary>Not opened yet ("Coach not started").</summary>
    public sealed record None : InterviewThreadState;

    /// <summary><c>thread/start</c> in flight ("Starting coach…").</summary>
    public sealed record Opening : InterviewThreadState;

    /// <summary>Ready for turns ("Coach ready").</summary>
    public sealed record Open : InterviewThreadState;

    /// <summary>Could not open ("Coach unavailable"); <paramref name="Message"/> says why. Retryable.</summary>
    public sealed record Failed(string Message) : InterviewThreadState;
}

/// <summary>
/// The preparation form (SPEC-13 §Preparation): what Start preparation will run.
/// <c>InterviewController.Draft</c> in Swift. Immutable: the form replaces it —
/// <c>interview.Draft = interview.Draft with { Company = text }</c> — and the setter raises
/// <see cref="InterviewController.Changed"/>. Call <see cref="InterviewController.DetailsChanged"/>
/// when an edit of the interviewee, company or step is committed.
/// </summary>
public sealed record InterviewDraft
{
    /// <summary>The interviewee (owner, 2026-10-02) — names the session.</summary>
    public string Candidate { get; init; } = "";

    /// <summary>The company — names the session.</summary>
    public string Company { get; init; } = "";

    /// <summary>Which round with this company (1–20 in the form).</summary>
    public int Step { get; init; } = 1;

    /// <summary>① The library CV.</summary>
    public string? CvId { get; init; }

    /// <summary>② The pasted job description.</summary>
    public string JobDescription { get; init; } = "";

    /// <summary>③ Chosen here, applied by Start preparation.</summary>
    public InterviewProfile? Profile { get; init; }

    /// <summary>④ Optional.</summary>
    public bool LiveCoding { get; init; }
}

/// <summary>A screenshot waiting in the current prompt (PNG bytes).</summary>
public sealed record PendingImage(Guid Id, byte[] Png)
{
    /// <summary>A new tray entry with a fresh id.</summary>
    public PendingImage(byte[] png) : this(Guid.NewGuid(), png) { }
}

/// <summary>
/// Region screenshot (the Screenshot hotkey / camera button, SPEC-16 §4.3). The Windows app
/// implements it with its own overlay; the controller only receives the PNG.
/// </summary>
/// <remarks>
/// Implementations must not throw (or fault the task) — a capture that fails is a cancel.
/// The controller tolerates one that does anyway: the failure is logged and treated as Esc.
/// </remarks>
public interface IScreenCapture
{
    /// <summary>Let the user select an area; the PNG, or <c>null</c> when they cancelled (Esc).</summary>
    Task<byte[]?> SelectAreaAsync();
}

/// <summary>
/// The clipboard as the screenshot tray needs it (SPEC-16 §4.4). The platform side reads
/// images only (never text), at most 4 per read, sources over 20 MB skipped, re-encoded as PNG
/// with the longest side ≤ 2048 px. The tray rules (cap, first poll, setting, clear after send)
/// are the controller's.
/// </summary>
/// <remarks>
/// Implementations must not throw — but on Windows another process can hold the clipboard
/// (Clipboard History, Office, a clipboard manager), so the controller tolerates every member
/// throwing: a failed <see cref="ReadImages"/> is retried on the next poll, a failed
/// <see cref="ClearIfUnchanged"/> leaves the clipboard as it is; both are logged.
/// </remarks>
public interface IClipboardImages
{
    /// <summary>Changes whenever anything is copied (<c>GetClipboardSequenceNumber</c>); reading it never opens the clipboard.</summary>
    long SequenceNumber { get; }

    /// <summary>
    /// Whether the clipboard offers an image format now (<c>IsClipboardFormatAvailable</c>) —
    /// cheap: never opens the clipboard and never decodes. Asked instead of
    /// <see cref="ReadImages"/> when the tray is full.
    /// </summary>
    bool ContainsImages { get; }

    /// <summary>The images on the clipboard now, as PNGs (≤ 4, re-encoded, ≤ 2048 px). Empty when there are none.</summary>
    IReadOnlyList<byte[]> ReadImages();

    /// <summary>
    /// Empty the clipboard, but only if <see cref="SequenceNumber"/> is still
    /// <paramref name="sequence"/> — the check is the implementation's, so the controller does
    /// not repeat it. Returns whether it emptied it.
    /// </summary>
    bool ClearIfUnchanged(long sequence);
}

/// <summary>What <see cref="InterviewController"/> takes from its host besides the services; every value has the app's default.</summary>
public sealed record InterviewControllerOptions
{
    /// <summary>Where short-lived PNGs for Codex's <c>localImage</c> input go (deleted after the turn).</summary>
    public string OutboxRoot { get; init; } = AppPaths.Outbox;

    /// <summary><c>busy_policy = interrupt</c>: how long to wait for the interrupted answer to stop (Swift: 2.5 s).</summary>
    public TimeSpan BusyInterruptWait { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>End interview: how long a streaming answer may finish before it is interrupted (Swift: 30 s).</summary>
    public TimeSpan EndAnswerWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>End interview: how long to wait after that interrupt (Swift: 2.5 s).</summary>
    public TimeSpan EndInterruptGrace { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>The system beep (Swift <c>NSSound.beep()</c>) when an Ask cannot open the thread; <c>null</c>: silent.</summary>
    public Action? Beep { get; init; }
}
