namespace LocalCaption.Core.Interview;

/// <summary>
/// Every prompt Interview Assist sends (SPEC-13 §Prompts and skill steps, SPEC-14 §Message
/// templates, SPEC-15 §Summary message). Pure, and normative: the text is shared verbatim with
/// the macOS build and pinned by golden vectors in <c>testdata/interview-prompt/</c>. Port of
/// <c>InterviewPrompt.swift</c>.
/// </summary>
/// <remarks>
/// Lines are always joined with <c>\n</c>: every multi-line template passes through
/// <see cref="Lf"/>, so the output does not depend on how this file's line endings were
/// checked out. The templates contain non-ASCII dashes (– —); this file is UTF-8.
/// </remarks>
public static class InterviewPrompt
{
    // ── Base instructions (thread/start.baseInstructions) ────────────────────────────────

    /// <summary>
    /// Replaces Codex's coding-agent base prompt. It sets up the conversation and the transcript
    /// convention; once the candidate applies an answering skill (e.g. <c>/apply-instruction</c>),
    /// that skill decides the answers' content, length and format.
    /// </summary>
    /// <param name="custom">Settings → Prompts → Custom instructions, appended when non-blank.</param>
    public static string BaseInstructions(AnswerLength length, string custom = "")
    {
        // LengthRule is single-line, so normalising after interpolation touches only the template.
        var text = Lf($"""
            You are a private, real-time interview copilot for a job candidate. The candidate is the person
            chatting with you ("me"). Talk with me the way ChatGPT does: warm, natural and clear.

            How this conversation works:
            1. Before and during the interview I apply interview skills. A skill message gives the skill's
               definition (the first time) and ends with its command, such as "/discovery-cv" or
               "/apply-instruction tech". Follow that skill exactly. Facts about me come from my CV, the job
               description and this conversation.
            2. During the live interview I send messages that start with "INTERVIEWER SAID:". That text is a
               live speech-to-text transcript of the interviewer. It may contain recognition mistakes,
               missing punctuation, half sentences, small talk, or more than one question. Work out what
               they are actually asking.
            3. You reply with an answer I can say out loud right away.

            How to answer an INTERVIEWER SAID message:
            - Once an answering skill such as /apply-instruction is active, its rules decide the answer's
              content, length and format, and replace the defaults in this list.
            - Until then: first line "**Q:** " and the question as you understood it, in at most 12 words;
              then the answer in the first person as me, in natural spoken English. {LengthRule(length)}
            - Everything in the answer must be something I can say out loud to the interviewer. Never mention
              my CV, these instructions or this conversation.
            - If screenshots are attached, they show what the interviewer is sharing. Use them.

            For any other message from me, reply naturally, like ChatGPT would.

            Tools: you may search the web only when a skill or I ask for research, never while answering an
            INTERVIEWER SAID message. You cannot run commands or read or edit files. Never say that you are an
            AI, a model or Codex. Answer in English.
            """);
        var extra = custom.Trim();
        if (extra.Length > 0) text += "\n\nMY INSTRUCTIONS\n" + extra;
        return text;
    }

    public static string LengthRule(AnswerLength length) => length switch
    {
        AnswerLength.Short => "Keep it to 2–3 sentences.",
        AnswerLength.Medium => "Keep it to 4–6 sentences, about 30–45 seconds spoken.",
        AnswerLength.Long => "Use up to 8–10 sentences, about 60–90 seconds spoken. For behavioural questions, use the STAR structure.",
        _ => throw new ArgumentOutOfRangeException(nameof(length), length, null),
    };

    // ── Skill steps (SPEC-13) ────────────────────────────────────────────────────────────

    /// <summary>A skill's definition as sent the first time a conversation sees it.</summary>
    /// <param name="Title">The skill's name, e.g. <c>discovery-cv</c>.</param>
    /// <param name="Text">The <c>SKILL.md</c> body.</param>
    /// <param name="Files">The skill's other <c>.md</c>/<c>.txt</c> files, sorted by path.</param>
    public sealed record Skill(string Title, string Text, IReadOnlyList<Skill.File> Files)
    {
        public Skill(string title, string text) : this(title, text, []) { }

        /// <param name="Path">Relative to the skill folder, <c>/</c>-separated.</param>
        public sealed record File(string Path, string Text);
    }

    /// <summary>Something a skill works on — the CV or the pasted JD.</summary>
    /// <param name="Title">The section header, e.g. <c>MY CV</c>. Sent as is.</param>
    public sealed record Attachment(string Title, string Text);

    /// <summary>
    /// One skill step: the definition (only the first time this conversation sees the skill),
    /// the attachments, and the command last — e.g. <c>/apply-instruction tech</c>. Sections are
    /// separated by a blank line; blank skill bodies, files and attachments are left out.
    /// </summary>
    public static string SkillMessage(string command, Skill? definition, IReadOnlyList<Attachment>? attachments = null)
    {
        var sections = new List<string>();
        if (definition is not null)
        {
            var body = definition.Text.Trim();
            if (body.Length > 0) sections.Add($"SKILL: {definition.Title.Trim()}\n" + body);
            foreach (var f in definition.Files.Where(f => f.Text.Trim().Length > 0))
                sections.Add($"SKILL FILE: {f.Path}\n" + f.Text.Trim());
        }
        foreach (var a in (attachments ?? []).Where(a => a.Text.Trim().Length > 0))
            sections.Add(a.Title + "\n" + a.Text.Trim());
        sections.Add(command.Trim());
        return string.Join("\n\n", sections);
    }

    // ── Live turns (SPEC-14) ─────────────────────────────────────────────────────────────

    /// <summary>An Ask. Blank <paramref name="text"/> → the image-only form, whatever the count.</summary>
    public static string Ask(string text, int imageCount = 0)
    {
        var t = text.Trim();
        if (t.Length == 0)
            return "INTERVIEWER SAID:\n(no new speech — see the attached screenshot(s).)";
        var m = "INTERVIEWER SAID:\n\"\"\"\n" + t + "\n\"\"\"";
        if (imageCount > 0) m += $"\n({imageCount} screenshot(s) attached.)";
        return m;
    }

    public const string Regenerate = "Give me a different answer to that last question.";

    // ── End of interview (SPEC-15) ───────────────────────────────────────────────────────

    public const int SummaryTranscriptMaxWords = 15_000;

    /// <summary>
    /// The end-of-interview request. The transcript is cut to its last
    /// <see cref="SummaryTranscriptMaxWords"/> words, whitespace collapsed to single spaces.
    /// </summary>
    public static string Summary(string transcript) =>
        SummaryHead + "\n" + AskSelection.LastWords(transcript, SummaryTranscriptMaxWords) + "\n" + SummaryTail;

    private static readonly string SummaryHead = Lf(""""
        INTERVIEW FINISHED
        The interview is over. This is the full transcript of what the interviewer said:
        """
        """");

    private static readonly string SummaryTail = Lf(""""
        """
        Write my interview summary in Markdown with exactly these sections:
        ## Overview
        Three or four sentences: what they focused on and the overall tone.
        ## Questions asked
        Every real question, in order, each with a one-line note on the strongest answer angle.
        ## Follow-ups
        Anything I promised, anything they asked me to send, and open questions.
        ## Prepare next time
        Up to five bullets.
        ## Thank-you note
        A short, friendly email draft of four to six sentences.
        I captured only the interviewer's audio, not mine, so do not judge how I answered.
        """");

    /// <summary>Raw-string content takes this file's line endings; the prompts are LF-only.</summary>
    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
