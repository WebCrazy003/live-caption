# SPEC-13 — Interview library & preparation

**Status:** 🟢 Built (step 3) — library, mode picker, privacy notice, Prepare on one thread, extra prep turns; flow tests pass, app launches; on-screen check by the user pending · **Step:** 3 of [SPEC-11](SPEC-11-interview-assist.md) · **Depends on:**
SPEC-11 (config, layout, records), SPEC-12 (engine)

> Before the interview: the user keeps a **library** of interview skills, CVs, JDs and notes,
> picks **Interview** mode, fills in a short setup, and presses **Prepare**. The app opens the
> interview's single Codex thread and sends one prep message containing everything. The
> model's **briefing** streams in, the user may chat with it further, and the thread is then
> ready for live questions.

---

## Mode picker

- [ ] On the Active Session screen, before Start (phases `.ready` / `.saved` / `.failed` without
      an unsaved session), a segmented control: **Caption only | Interview**. Bound to
      `interview.mode`. Hidden while recording (the mode is fixed for a session).
- [ ] **Caption only** = today's screen, unchanged.
- [ ] Choosing **Interview** the first time shows the privacy notice (SPEC-11 §Privacy). Cancel →
      back to Caption only.
- [ ] Interview mode replaces the caption-area placeholder with the **Prepare panel** until the
      interview is prepared; after that, the Answers panel (SPEC-14) takes the right side.

## Library

Managed from **Settings → Interview → Library…** and from the Prepare panel ("Manage…"). Stored
as SPEC-11 §On-disk layout.

### Skills

- [ ] **Import** a folder containing `SKILL.md`, or a single `.md` file (wrapped into
      `skills/<slug>/SKILL.md`). Copied into the library; the original is not referenced again.
- [ ] Parse optional YAML front matter (`name`, `description`) for the title; fall back to the
      file/folder name.
- [ ] Also include every `.md` / `.txt` file in the skill folder (recursively, sorted by path) as
      reference text. Other files (scripts, images, binaries) are **ignored, with a visible
      note** — Codex can't run or read them under the lockdown, and the app inlines text only.
- [ ] Per-skill cap: 60 000 characters. Over → import still succeeds, the Prepare panel warns.
- [ ] Rename, view (read-only text), re-import (replace), delete.

### Documents (CV, JD, notes)

- [ ] **Import** `.pdf`, `.docx`, `.md`, `.txt`, or **Paste text**. Choose `kind`: CV, Job
      description, Notes.
- [ ] Text extraction into `text.txt` (this is what gets sent):
  - `.pdf` — macOS PDFKit page text, pages joined with blank lines. No text layer → error
    "This PDF is a scanned image; paste the text instead." (OCR is out of scope.) PDFKit
    letter-spaces large headings ("S t e v e  O n y e" — seen in S0); harmless to the model, and
    the editable-text step below lets the user tidy it.
  - `.docx` — macOS `NSAttributedString(url:, documentType: .officeOpenXML)` → plain string.
  - `.md` / `.txt` — read as UTF-8 (fallback Windows-1252), normalise line endings to `\n`.
- [ ] After import, show the extracted text in an **editable** view so the user can fix
      extraction mistakes; saving rewrites `text.txt` only.
- [ ] Rename, change kind, delete. Deleting a document used by a past interview doesn't break
      that interview's record (records keep ids; the Results view says "document deleted").

### Pure parts (Kit)

`InterviewLibrary` (index model, slug rules, merge-preserving rewrite), `SkillFile` (front matter
parse, reference-file ordering, size accounting). Slug = lowercase ASCII, `[a-z0-9-]`, collapsed
dashes, ` (2)` → `-2` on collision — mirrored on Windows.

## Prepare panel

Shown in Interview mode until the interview is prepared.

| Field | Default |
|---|---|
| Interview name | `"<company> — <role>"`, else the session name prefix + time |
| Company, Role | from the most recent interview |
| CV | most recent CV used (picker of `kind = cv`) |
| Job description | picker of `kind = jd` **or** paste box (stored inline in the record as `jd_text_inline`) |
| Notes | multi-select of `kind = notes` |
| Skills | multi-select; default = last used |
| Instructions | prefilled from `interview.custom_instructions`; editable per interview |
| Answer length | from config |
| Model, Effort | from config; per-interview override (stored in the record) |

- [ ] A size line under the form: *"≈ 9 400 tokens of context"* (chars ÷ 4). Over 150 000
      characters → Prepare disabled with the reason.
- [ ] Engine status line (from `AnswerEngine.status()`): not installed / too old / signed out
      each disable Prepare and link to Settings → Interview.
- [ ] Usage line: 5-hour window remaining (SPEC-12). Under 20 % → amber warning; it does not block.
- [ ] **Prepare** →
  1. start the engine (if not running) and `thread/start` with the base instructions below;
  2. create the interview folder and `interview.json` (`prep.status = "running"`);
  3. send the **prep message** with `prep_reasoning_effort`;
  4. stream the briefing into the panel (rendered Markdown);
  5. on completion: `prep.status = "done"`, store `briefing`; the panel shows **Ready**.
- [ ] After Ready, a text box *"Ask the coach before the interview…"* sends extra turns on the
      **same thread** (counted in `prep.extra_turns`, stored as turns of kind `typed`).
- [ ] Changing any setup field after Ready → banner "Setup changed — Prepare again to apply"
      (starts a **new** thread; the old record is kept as a draft and deleted if never started).
- [ ] **Start is allowed before Ready** (captions matter more than answers). The Ask controls stay
      disabled with "Preparing…" until Ready. Prep keeps running while recording.
- [ ] Prep failure → error with **Retry** (same thread if it exists).

## Prompts (Kit `InterviewPrompt`, golden-tested, shared with Windows)

The text below is normative. `{…}` are substitutions; whitespace and line breaks are exact in
the golden vectors (`testdata/interview-prompt/`).

### Base instructions (`thread/start.baseInstructions`)

```
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
- Then the answer, in the first person as me, in natural spoken English. {length_rule}
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
```

`{length_rule}` by `answer_length`:
- `short` → `Keep it to 2–3 sentences.`
- `medium` → `Keep it to 4–6 sentences, about 30–45 seconds spoken.`
- `long` → `Use up to 8–10 sentences, about 60–90 seconds spoken. For behavioural questions, use the STAR structure.`

### Prep message (first turn)

Sections appear only when non-empty, in this order:

```
INTERVIEW SETUP
Company: {company}
Role: {role}

MY INSTRUCTIONS
{instructions}

INTERVIEW SKILL: {skill_title}
{SKILL.md text}

SKILL FILE: {relative_path}
{file text}

MY CV
{cv text}

JOB DESCRIPTION
{jd text}

NOTES: {title}
{notes text}

TASK
{task}
```

`{task}`:
- with ≥ 1 skill → `Use the interview skill above to prepare me for this interview, using my CV and the job description. Keep the briefing under 400 words unless the skill says otherwise. End with the line READY.`
- with no skill → `Prepare me for this interview. Give me: (1) three lines on how my background fits this role, (2) the eight questions I am most likely to be asked, each with a one-line answer angle from my CV, (3) two questions I could ask them. Keep it under 400 words. End with the line READY.`

## Acceptance

- Import a skill folder (with an extra script and a `.md` reference), a PDF CV and a pasted JD;
  the script is listed as ignored, the reference is included, the PDF text is editable.
- Prepare streams a briefing ending in `READY`; `interview.json` has `prep.status = "done"` and
  the `thread_id`; the thread's first turn contains exactly the golden prep message.
- An extra prep message goes to the same `thread_id`.
- With Codex missing / signed out, Prepare is disabled with the right reason and a Settings link.
- Start works before Ready; Ask stays disabled until Ready.
- Golden vectors for base instructions (×3 lengths) and the prep message (with/without skill,
  with/without notes) pass on macOS.
