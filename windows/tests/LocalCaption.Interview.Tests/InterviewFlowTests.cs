using LocalCaption.Core.Interview;
using Kind = LocalCaption.Core.Interview.InterviewLibraryIndex.DocumentKind;

namespace LocalCaption.Interview.Tests;

/// <summary>
/// The interview flow against a fake engine (SPEC-13 §Acceptance): library import, the CV
/// upload, the manual skill steps on one thread, profiles, and the coach opening on Start. Port
/// of <c>InterviewFlowTests.swift</c>.
/// </summary>
public sealed class InterviewFlowTests : InterviewTestBase
{
    // ── Library ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LibraryImportsNormaliseAndPersist()
    {
        var cv = ImportCv();
        Write("---\nname: Interview coach\n---\nUse STAR.", "src/coach/SKILL.md");
        Write("Situation, Task, Action, Result.", "src/coach/refs/star.md");
        Write("#!/bin/sh\necho hi", "src/coach/run.sh");
        var imported = Library.ImportSkill(Path.Combine(Tmp, "src", "coach"));
        Assert.Equal(["run.sh"], imported.Ignored);
        Assert.Equal(["SKILL.md", "refs/star.md"], imported.Skill.Files);
        Assert.Equal("Jane Doe\nSwift, 8 years.", Library.Text(cv));
        var reloaded = new InterviewLibrary(Path.Combine(Tmp, "library"));
        Assert.Equal(["Jane CV"], reloaded.Index.Documents.Select(d => d.Title));
        Assert.Equal(["interview-coach"], reloaded.Index.Skills.Select(s => s.Slug));
        // .docx isn't supported any more
        Assert.Throws<DocumentText.Failure>(() => Library.ImportDocument(Write("x", "cv.docx"), Kind.Cv));
    }

    [Fact]
    public Task UploadCvImportsAndSelectsIt() => OnUi(() =>
    {
        var interview = NewController();
        Assert.Null(interview.Draft.CvId);
        interview.UploadCv(Write("Alex Example\nKotlin.", "src/Alex.md"));
        var id = Assert.IsType<string>(interview.Draft.CvId);
        Assert.Equal(Kind.Cv, Library.Document(id)?.Kind);
        Assert.Equal("Alex Example\nKotlin.", Library.Text(id));
        return Task.CompletedTask;
    });

    // ── Skill steps ──────────────────────────────────────────────────────────────────────

    [Fact]
    public Task StepsRunOnOneThreadAndSendEachDefinitionOnce() => OnUi(async () =>
    {
        ImportSkills();
        var interview = NewController();
        interview.Draft = interview.Draft with { CvId = ImportCv(), JobDescription = "Senior iOS Engineer\nAcme, London." };
        InterviewPrompt.Skill? Skill(string slug) => Library.PromptSkill(Library.SkillBySlug(slug)!.Id);

        await interview.RunAsync(InterviewStep.DiscoveryCv);
        Assert.Equal(InterviewPrompt.SkillMessage("/discovery-cv", Skill("discovery-cv"),
                                                  [new("MY CV", "Jane Doe\nSwift, 8 years.")]),
                     Engine.SentTexts[^1]);
        Assert.Equal("medium", Engine.SentTurns[^1].Effort);   // skill steps use prep_reasoning_effort
        Assert.Equal(InterviewPrompt.BaseInstructions(AnswerLength.Medium), Engine.Threads[0].BaseInstructions);

        await interview.RunAsync(InterviewStep.DiscoveryCv);
        // definition only the first time
        Assert.Equal(InterviewPrompt.SkillMessage("/discovery-cv", null, [new("MY CV", "Jane Doe\nSwift, 8 years.")]),
                     Engine.SentTexts[^1]);

        await interview.RunAsync(InterviewStep.DiscoveryJd);
        Assert.EndsWith("JOB DESCRIPTION\nSenior iOS Engineer\nAcme, London.\n\n/discovery-jd", Engine.SentTexts[^1]);
        Assert.Equal("Senior iOS Engineer", interview.Record?.Name);

        Assert.NotNull(interview.Blocker(InterviewStep.LiveCoding));   // live coding waits for Tech
        await interview.RunAsync(InterviewStep.ApplyInstruction, InterviewProfile.Tech);
        Assert.EndsWith("\n\n/apply-instruction tech", Engine.SentTexts[^1]);
        Assert.Equal(InterviewProfile.Tech, interview.ActiveProfile);
        Assert.Null(interview.Blocker(InterviewStep.LiveCoding));
        await interview.RunAsync(InterviewStep.LiveCoding);
        Assert.Equal(InterviewPrompt.SkillMessage("/live-coding-design", Skill("live-coding-design")), Engine.SentTexts[^1]);
        Assert.True(interview.LiveCodingActive);

        await interview.RunAsync(InterviewStep.ApplyInstruction, InterviewProfile.Cultural);
        Assert.Equal("/apply-instruction cultural", Engine.SentTexts[^1]);   // switching profile resends no definition
        Assert.Equal(InterviewProfile.Cultural, interview.ActiveProfile);
        Assert.False(interview.LiveCodingActive);                          // a new profile replaces live coding

        Assert.Single(Engine.Threads);
        Assert.Equal(["thr1"], Engine.SentTurns.Select(s => s.ThreadId).Distinct());
        var rec = Saved(interview);
        Assert.Equal(Enumerable.Repeat(InterviewTurnKind.Skill, 6), rec.Turns.Select(t => t.Kind));
        Assert.Equal(["/discovery-cv", "/discovery-cv", "/discovery-jd", "/apply-instruction tech",
                      "/live-coding-design", "/apply-instruction cultural"], rec.Turns.Select(t => t.Question));
        Assert.True(interview.IsDone(InterviewStep.DiscoveryCv) && interview.IsDone(InterviewStep.DiscoveryJd));
    });

    [Fact]
    public Task StepBlockers() => OnUi(() =>
    {
        var interview = NewController();
        Assert.Equal("Load the discovery-cv skill in Settings → Skills", interview.Blocker(InterviewStep.DiscoveryCv));
        Assert.False(interview.AllSkillsLoaded);
        Assert.Equal(4, interview.MissingSkills.Count);
        ImportSkills();
        Assert.True(interview.AllSkillsLoaded);
        Assert.Equal("Select or upload a CV", interview.Blocker(InterviewStep.DiscoveryCv));
        Assert.Equal("Paste the job description", interview.Blocker(InterviewStep.DiscoveryJd));
        Assert.Null(interview.Blocker(InterviewStep.ApplyInstruction));
        Assert.Equal("Apply the Tech profile first", interview.Blocker(InterviewStep.LiveCoding));
        return Task.CompletedTask;
    });

    [Fact]
    public void StepsAndProfilesAreSpelledAsOnTheMac()
    {
        Assert.Equal(["discovery-cv", "discovery-jd", "apply-instruction", "live-coding-design"],
                     InterviewSteps.All.Select(s => s.Slug()));
        Assert.Equal(["Discovery CV", "Discovery JD", "Apply instruction", "Live coding & design"],
                     InterviewSteps.All.Select(s => s.Title()));
        Assert.Equal(["intro", "tech", "cultural"], InterviewSteps.Profiles.Select(p => p.Raw()));
        Assert.Equal(["Intro", "Tech", "Behavioral"], InterviewSteps.Profiles.Select(p => p.Label()));
        Assert.Equal(InterviewProfile.Cultural, InterviewSteps.ProfileFromRaw("cultural"));
        Assert.Null(InterviewSteps.ProfileFromRaw("behavioral"));
        Assert.Equal(InterviewStep.LiveCoding, InterviewSteps.StepFromSlug("live-coding-design"));
    }

    [Fact]
    public void TheRecordIsNamedAfterTheJdsFirstLine()
    {
        Assert.Equal("Interview", InterviewController.NameFromJd(""));
        Assert.Equal("Interview", InterviewController.NameFromJd(" \n\t\n"));
        Assert.Equal("Senior iOS Engineer", InterviewController.NameFromJd("\n  Senior iOS Engineer  \r\nAcme"));
        Assert.Equal(new string('x', 60), InterviewController.NameFromJd(new string('x', 80)));
    }

    // ── Settings → Skills slots ──────────────────────────────────────────────────────────

    [Fact]
    public void SkillSlotsTakeAnyFileNameAndReplace()
    {
        var first = Write("---\nname: My CV analyser\n---\nv1", "src/whatever.md");
        var loaded = Library.LoadSkill("discovery-cv", first);
        Assert.Equal("discovery-cv", loaded.Skill.Slug);   // the slot name becomes the slug
        Assert.Equal("---\nname: My CV analyser\n---\nv1", Library.PromptSkill(loaded.Skill.Id)?.Text);

        var second = Write("---\nname: discovery-cv\n---\nv2", "src/folder/SKILL.md");
        Library.LoadSkill("discovery-cv", Path.GetDirectoryName(second)!);
        Assert.Single(Library.Index.Skills, s => s.Slug == "discovery-cv");   // replaced, not added
        Assert.Equal("---\nname: discovery-cv\n---\nv2", Library.PromptSkill(Library.SkillBySlug("discovery-cv")!.Id)?.Text);

        Library.RemoveSkill("discovery-cv");
        Assert.Null(Library.SkillBySlug("discovery-cv"));
        Assert.Empty(new InterviewLibrary(Path.Combine(Tmp, "library")).Index.Skills);
    }

    // ── Start preparation ────────────────────────────────────────────────────────────────

    private void ReadyDraft(InterviewController interview, bool liveCoding = false)
    {
        interview.Draft = interview.Draft with
        {
            Candidate = "victor", Company = "Peloton", CvId = ImportCv(), JobDescription = "Senior iOS Engineer",
            Profile = InterviewProfile.Tech, LiveCoding = liveCoding,
        };
    }

    [Fact]
    public Task StartPreparationRunsThePlanInOrder() => OnUi(async () =>
    {
        ImportSkills();
        var interview = NewController();
        ReadyDraft(interview, liveCoding: true);
        Assert.Null(interview.PreparationBlocker);
        Assert.False(interview.IsPrepared);

        await interview.StartPreparationAsync();

        Assert.Equal(["/discovery-cv", "/discovery-jd", "/apply-instruction tech", "/live-coding-design"],
                     interview.Turns.Select(t => t.Question));
        Assert.True(interview.IsPrepared);
        Assert.Null(interview.PreparationFailedAt);
        Assert.Equal(["medium"], Engine.SentTurns.Select(s => s.Effort).Distinct());   // the preparation effort
        Assert.Single(Engine.Threads);
        Assert.False(interview.Preparing);
    });

    [Fact]
    public Task PreparationBlockers() => OnUi(() =>
    {
        var interview = NewController();
        Assert.Equal("Load the 4 skills in Settings → Interview → Skills", interview.PreparationBlocker);
        ImportSkills();
        Assert.Equal("Enter the interviewee's name", interview.PreparationBlocker);
        interview.Draft = interview.Draft with { Candidate = "  " };
        Assert.Equal("Enter the interviewee's name", interview.PreparationBlocker);   // blank doesn't count
        interview.Draft = interview.Draft with { Candidate = "victor" };
        Assert.Equal("Enter the company", interview.PreparationBlocker);
        interview.Draft = interview.Draft with { Company = "Peloton" };
        Assert.Equal("Choose or upload a CV (①)", interview.PreparationBlocker);
        interview.Draft = interview.Draft with { CvId = ImportCv() };
        Assert.Equal("Paste the job description (②)", interview.PreparationBlocker);
        interview.Draft = interview.Draft with { JobDescription = "JD" };
        Assert.Equal("Choose a mode (③)", interview.PreparationBlocker);
        interview.Draft = interview.Draft with { Profile = InterviewProfile.Intro, LiveCoding = true };
        Assert.Equal("Live coding needs the Tech mode (③)", interview.PreparationBlocker);
        interview.Draft = interview.Draft with { Profile = InterviewProfile.Tech };
        Assert.Null(interview.PreparationBlocker);
        return Task.CompletedTask;
    });

    [Fact]
    public Task PreparationStopsAtAFailureAndContinuesFromThere() => OnUi(async () =>
    {
        ImportSkills();
        var interview = NewController();
        ReadyDraft(interview);
        Engine.Reply = text => text.EndsWith("/discovery-jd", StringComparison.Ordinal)
            ? [new AnswerEvent.Failed(new EngineError.Network("offline"), "")]
            : [new AnswerEvent.Completed("ok")];

        await interview.StartPreparationAsync();
        Assert.Equal(InterviewStep.DiscoveryJd, interview.PreparationFailedAt);
        Assert.Equal(["/discovery-cv", "/discovery-jd"], interview.Turns.Select(t => t.Question));   // stops at the failure
        Assert.False(interview.IsPrepared);
        Assert.True(interview.ShowingPreparation);   // a failed preparation stays on screen
        Assert.Equal("Discovery JD didn't finish: Network problem: offline", interview.Status);

        Engine.Reply = _ => [new AnswerEvent.Completed("ok")];
        await interview.StartPreparationAsync(resume: true);
        // Continue resumes at the failed step, not from the start
        Assert.Equal(["/discovery-cv", "/discovery-jd", "/discovery-jd", "/apply-instruction tech"],
                     interview.Turns.Select(t => t.Question));
        Assert.True(interview.IsPrepared);
        Assert.Null(interview.PreparationFailedAt);
        Assert.False(interview.ShowingPreparation);   // completing it opens the captions and answers

        interview.ShowingPreparation = true;          // back via the top bar
        interview.RecordingStarted(Guid.NewGuid(), DateTimeOffset.Now);
        Assert.False(interview.ShowingPreparation);   // Start leaves the preparation
        interview.ResetForNewInterview();
        Assert.True(interview.ShowingPreparation);    // a new interview begins with the preparation
    });

    [Fact]
    public Task ChangingTheModelSwitchesTheNextTurn() => OnUi(async () =>
    {
        var interview = NewController();
        await interview.SendTypedAsync("hello");
        Assert.Null(Engine.SentTurns[^1].Model);   // unchanged model → not resent
        Assert.Equal(InterviewConfig.RecommendedModel, Engine.Threads[0].Model);

        Config.Model = "gpt-6.1-sol";
        await interview.SendTypedAsync("again");
        Assert.Equal("gpt-6.1-sol", Engine.SentTurns[^1].Model);
        Assert.Equal("gpt-6.1-sol", interview.Record?.Model);
        await interview.SendTypedAsync("once more");
        Assert.Null(Engine.SentTurns[^1].Model);   // Codex keeps it; no need to resend
        Assert.Equal("gpt-6.1-sol", Saved(interview).Model);
    });

    [Fact]
    public Task DiscoveryCvSnapshotsTheCvForHistory() => OnUi(async () =>
    {
        ImportSkills();
        var interview = NewController();
        interview.Draft = interview.Draft with { CvId = ImportCv(), JobDescription = "Role X" };
        await interview.RunAsync(InterviewStep.DiscoveryCv);
        await interview.RunAsync(InterviewStep.DiscoveryJd);
        // The library changes later (here: a library that no longer has the CV)…
        Library = new InterviewLibrary(Path.Combine(Tmp, "other-library"));
        var reopened = NewController(Saved(interview));
        // …history still shows the CV the coach saw
        Assert.Equal("Jane Doe\nSwift, 8 years.", reopened.CvText);
        Assert.Equal("Jane CV", reopened.CvTitle);
        Assert.Equal("Role X", reopened.JdText);
    });

    // ── The coach without any step ───────────────────────────────────────────────────────

    [Fact]
    public Task StartOpensTheCoachWithoutAnySteps() => OnUi(async () =>
    {
        Config.AnswerLength = "short";
        Config.CustomInstructions = "Mention measurable results.";
        var interview = NewController();
        interview.RecordingStarted(Guid.NewGuid(), DateTimeOffset.Now);
        var thread = await interview.EnsureThreadAsync();   // joins the open started by Start
        Assert.Equal("thr1", thread);
        Assert.Single(Engine.Threads);
        Assert.Equal(new InterviewThreadState.Open(), interview.ThreadState);
        Assert.Equal(InterviewPrompt.BaseInstructions(AnswerLength.Short, "Mention measurable results."),
                     Engine.Threads[0].BaseInstructions);
        var rec = Saved(interview);
        Assert.NotNull(rec.StartedAt);
        Assert.NotNull(rec.CaptureSessionUuid);
        Assert.Equal("short", rec.Setup.AnswerLength);
    });

    [Fact]
    public Task CoachUnavailableIsReportedAndRetryable() => OnUi(async () =>
    {
        Engine.FailStart = new EngineError.SignedOut();
        var interview = NewController();
        Assert.Null(await interview.EnsureThreadAsync());
        Assert.Equal(new InterviewThreadState.Failed(new EngineError.SignedOut().Message), interview.ThreadState);
        Engine.FailStart = null;
        Assert.Equal("thr1", await interview.EnsureThreadAsync());
        Assert.Equal(new InterviewThreadState.Open(), interview.ThreadState);
    });

    [Fact]
    public Task StartOverDiscardsAnUnstartedInterview() => OnUi(async () =>
    {
        var interview = NewController();
        await interview.SendTypedAsync("hello");
        var id = interview.Record!.Id;
        await interview.DiscardUnstartedAsync();
        interview.ResetForNewInterview();
        Assert.Null(Store.Interview(id));   // its rows are deleted
        Assert.Equal(["thr1"], Engine.Archived);
        Assert.Equal(new InterviewThreadState.None(), interview.ThreadState);
        Assert.Empty(interview.Turns);
    });

    [Fact]
    public Task StartedInterviewIsNotDiscarded() => OnUi(async () =>
    {
        var interview = NewController();
        interview.RecordingStarted(Guid.NewGuid(), DateTimeOffset.Now);
        await interview.EnsureThreadAsync();
        await interview.DiscardUnstartedAsync();
        Assert.NotNull(interview.Record);
        Assert.NotNull(Saved(interview));
        Assert.Empty(Engine.Archived);
    });

    // ── Windows additions ────────────────────────────────────────────────────────────────

    [Fact]
    public Task TheDraftIsPrefilledFromTheLastInterview() => OnUi(async () =>
    {
        ImportSkills();
        var interview = NewController();
        ReadyDraft(interview, liveCoding: true);
        var cv = interview.Draft.CvId;
        await interview.StartPreparationAsync();
        var newer = Library.ImportDocument(Write("Newer CV", "src/Newer.txt"), Kind.Cv).Id;

        var next = NewController();
        Assert.Equal("victor", next.Draft.Candidate);
        Assert.Equal(cv, next.Draft.CvId);
        Assert.NotEqual(newer, next.Draft.CvId);   // the last interview's CV, not the newest
        Assert.Equal(InterviewProfile.Tech, next.Draft.Profile);
        Assert.True(next.Draft.LiveCoding);
        Assert.Equal("", next.Draft.Company);   // company and JD are per interview
    });

    [Fact]
    public Task AModelThatIsNotOfferedIsReplacedWithAOneTimeNotice() => OnUi(async () =>
    {
        Config.Model = "gpt-retired";
        var interview = NewController();
        await interview.EnsureThreadAsync();
        Assert.Equal(InterviewConfig.RecommendedModel, Engine.Threads[0].Model);
        var notice = $"Codex doesn't offer gpt-retired any more — using {InterviewConfig.RecommendedModel}.";
        Assert.Equal(notice, interview.ModelNotice);   // SPEC-16 §9.4: the Mac falls back silently
        Assert.Null(interview.Status);

        // Its own property: later status lines (here: a turn starting and ending) don't overwrite it.
        await interview.SendTypedAsync("hello");
        Assert.Equal(notice, interview.ModelNotice);
    });
}
