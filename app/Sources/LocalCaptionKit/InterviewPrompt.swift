import Foundation

/// Every prompt Interview Assist sends (SPEC-13 §Prompts, SPEC-14 §Message templates, SPEC-15
/// §Summary message). Pure, and normative: the text is shared verbatim with the Windows build and
/// pinned by golden vectors in `testdata/interview-prompt/`.
public enum InterviewPrompt {
    // MARK: Base instructions (thread/start.baseInstructions)

    /// Replaces Codex's coding-agent base prompt. S0 showed this alone makes answers
    /// conversational and first person, and keeps the model from reaching for tools.
    public static func baseInstructions(length: Config.Interview.AnswerLength) -> String {
        """
        You are a private, real-time interview coach for a job candidate. The candidate is the person
        chatting with you ("me"). Talk with me the way ChatGPT does: warm, natural, conversational and
        clear.

        How this conversation works:
        1. First I give you my interview setup: the company and role, my instructions, interview skills,
           my CV, the job description and notes. That setup is your only source of facts about me.
        2. During the live interview I send messages that start with "INTERVIEWER SAID:". That text is a
           live speech-to-text transcript of the interviewer. It may contain recognition mistakes,
           missing punctuation, half sentences, small talk, or more than one question. Work out what
           they are actually asking.
        3. You reply with an answer I can say out loud right away.

        How to answer an INTERVIEWER SAID message:
        - First line: "**Q:** " and the question as you understood it, in at most 12 words.
        - Then the answer, in the first person as me, in natural spoken English. \(lengthRule(length))
        - Then, only if it helps, "**Key points:**" and at most 3 short bullets I can glance at.
        - Use only facts from my setup and this conversation. Never invent employers, job titles, dates,
          numbers or projects. If my background does not cover the question, answer honestly in the first
          person and bridge from what I do have ("I haven't used X directly, but in my work on Y…").
        - Everything in the answer must be something I can say out loud to the interviewer. Never mention
          my CV, my setup, these instructions or this conversation.
        - If there is no real question yet (small talk, a statement, noise), reply with one short line I
          could say, or "(no question yet)".
        - For a coding or technical question, explain briefly in words first; add code only if it truly
          helps.
        - If screenshots are attached, they show what the interviewer is sharing. Use them.

        For any other message from me, reply naturally, like ChatGPT would.

        Follow my instructions and my interview skills unless they conflict with these rules. You have
        no tools: never try to run commands, read or edit files, or browse the web. Never say that you
        are an AI, a model or Codex. Answer in English.
        """
    }

    public static func lengthRule(_ length: Config.Interview.AnswerLength) -> String {
        switch length {
        case .short: return "Keep it to 2–3 sentences."
        case .medium: return "Keep it to 4–6 sentences, about 30–45 seconds spoken."
        case .long: return "Use up to 8–10 sentences, about 60–90 seconds spoken. For behavioural questions, use the STAR structure."
        }
    }

    // MARK: Prep message (first turn)

    public struct Skill: Equatable, Sendable {
        public struct File: Equatable, Sendable {
            public let path: String
            public let text: String
            public init(path: String, text: String) { self.path = path; self.text = text }
        }
        public let title: String
        public let text: String          // SKILL.md
        public let files: [File]         // other .md/.txt files, sorted by path
        public init(title: String, text: String, files: [File] = []) {
            self.title = title; self.text = text; self.files = files
        }
    }

    public struct Note: Equatable, Sendable {
        public let title: String
        public let text: String
        public init(title: String, text: String) { self.title = title; self.text = text }
    }

    public struct Setup: Equatable, Sendable {
        public var company = ""
        public var role = ""
        public var instructions = ""
        public var skills: [Skill] = []
        public var cv = ""
        public var jobDescription = ""
        public var notes: [Note] = []
        public init(company: String = "", role: String = "", instructions: String = "",
                    skills: [Skill] = [], cv: String = "", jobDescription: String = "", notes: [Note] = []) {
            self.company = company; self.role = role; self.instructions = instructions
            self.skills = skills; self.cv = cv; self.jobDescription = jobDescription; self.notes = notes
        }
    }

    public static let prepTaskWithSkill =
        "Use the interview skill above to prepare me for this interview, using my CV and the job description. Keep the briefing under 400 words unless the skill says otherwise. End with the line READY."
    public static let prepTaskWithoutSkill =
        "Prepare me for this interview. Give me: (1) three lines on how my background fits this role, (2) the eight questions I am most likely to be asked, each with a one-line answer angle from my CV, (3) two questions I could ask them. Keep it under 400 words. End with the line READY."

    /// Sections appear only when non-empty, in the order SPEC-13 fixes; bodies are trimmed and
    /// separated by one blank line.
    public static func prepMessage(_ s: Setup) -> String {
        var sections: [String] = []
        func add(_ header: String, _ body: String) {
            let b = trim(body)
            if !b.isEmpty { sections.append(header + "\n" + b) }
        }
        let setupLines = [
            trim(s.company).isEmpty ? nil : "Company: \(trim(s.company))",
            trim(s.role).isEmpty ? nil : "Role: \(trim(s.role))",
        ].compactMap { $0 }
        if !setupLines.isEmpty { sections.append("INTERVIEW SETUP\n" + setupLines.joined(separator: "\n")) }
        add("MY INSTRUCTIONS", s.instructions)
        for skill in s.skills {
            add("INTERVIEW SKILL: \(trim(skill.title))", skill.text)
            for f in skill.files { add("SKILL FILE: \(f.path)", f.text) }
        }
        add("MY CV", s.cv)
        add("JOB DESCRIPTION", s.jobDescription)
        for n in s.notes { add("NOTES: \(trim(n.title))", n.text) }
        sections.append("TASK\n" + (s.skills.isEmpty ? prepTaskWithoutSkill : prepTaskWithSkill))
        return sections.joined(separator: "\n\n")
    }

    // MARK: Live turns (SPEC-14)

    /// An Ask. `text` empty with images → the image-only form.
    public static func ask(_ text: String, imageCount: Int = 0) -> String {
        let t = trim(text)
        if t.isEmpty {
            return "INTERVIEWER SAID:\n(no new speech — see the attached screenshot(s).)"
        }
        var m = "INTERVIEWER SAID:\n\"\"\"\n\(t)\n\"\"\""
        if imageCount > 0 { m += "\n(\(imageCount) screenshot(s) attached.)" }
        return m
    }

    public static let regenerate = "Give me a different answer to that last question."

    // MARK: End of interview (SPEC-15)

    public static let summaryTranscriptMaxWords = 15_000

    public static func summary(transcript: String) -> String {
        let t = AskSelection.lastWords(transcript, summaryTranscriptMaxWords)
        return """
        INTERVIEW FINISHED
        The interview is over. This is the full transcript of what the interviewer said:
        \"\"\"
        \(t)
        \"\"\"
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
        """
    }

    private static func trim(_ s: String) -> String { s.trimmingCharacters(in: .whitespacesAndNewlines) }
}
