import Foundation

/// Every prompt Interview Assist sends (SPEC-13 §Prompts and skill steps, SPEC-14 §Message templates, SPEC-15
/// §Summary message). Pure, and normative: the text is shared verbatim with the Windows build and
/// pinned by golden vectors in `testdata/interview-prompt/`.
public enum InterviewPrompt {
    // MARK: Base instructions (thread/start.baseInstructions)

    /// Replaces Codex's coding-agent base prompt. It sets up the conversation and the transcript
    /// convention; once the candidate applies an answering skill (e.g. `/apply-instruction`), that
    /// skill decides the answers' content, length and format. `custom` is Settings → Prompts →
    /// Custom instructions, appended when non-empty.
    public static func baseInstructions(length: Config.Interview.AnswerLength, custom: String = "") -> String {
        var text = """
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
          then the answer in the first person as me, in natural spoken English. \(lengthRule(length))
        - Everything in the answer must be something I can say out loud to the interviewer. Never mention
          my CV, these instructions or this conversation.
        - If screenshots are attached, they show what the interviewer is sharing. Use them.

        For any other message from me, reply naturally, like ChatGPT would.

        Tools: you may search the web only when a skill or I ask for research, never while answering an
        INTERVIEWER SAID message. You cannot run commands or read or edit files. Never say that you are an
        AI, a model or Codex. Answer in English.
        """
        let extra = trim(custom)
        if !extra.isEmpty { text += "\n\nMY INSTRUCTIONS\n" + extra }
        return text
    }

    public static func lengthRule(_ length: Config.Interview.AnswerLength) -> String {
        switch length {
        case .short: return "Keep it to 2–3 sentences."
        case .medium: return "Keep it to 4–6 sentences, about 30–45 seconds spoken."
        case .long: return "Use up to 8–10 sentences, about 60–90 seconds spoken. For behavioural questions, use the STAR structure."
        }
    }

    // MARK: Skill steps (SPEC-13)

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

    /// Something a skill works on — the CV or the pasted JD.
    public struct Attachment: Equatable, Sendable {
        public let title: String
        public let text: String
        public init(title: String, text: String) { self.title = title; self.text = text }
    }

    /// One skill step: the definition (only the first time this conversation sees the skill), the
    /// attachments, and the command last — e.g. `/apply-instruction tech`.
    public static func skillMessage(command: String, definition: Skill?, attachments: [Attachment] = []) -> String {
        var sections: [String] = []
        if let d = definition {
            let body = trim(d.text)
            if !body.isEmpty { sections.append("SKILL: \(trim(d.title))\n" + body) }
            for f in d.files where !trim(f.text).isEmpty {
                sections.append("SKILL FILE: \(f.path)\n" + trim(f.text))
            }
        }
        for a in attachments where !trim(a.text).isEmpty {
            sections.append(a.title + "\n" + trim(a.text))
        }
        sections.append(trim(command))
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
