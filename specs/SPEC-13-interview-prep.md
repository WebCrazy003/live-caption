# SPEC-13 — Interview library & skill steps

**Status:** 🟢 Rebuilt 2026-10-02 around the owner's skill sequence (replaces the one-shot Prepare
button) · **Step:** 3 of [SPEC-11](SPEC-11-interview-assist.md) · **Depends on:** SPEC-11 (config,
layout, records), SPEC-12 (engine)

> Before (and during) the interview the user runs their interview **skills** by hand, as turns on
> the interview's single Codex thread: **Discovery CV** on the selected CV, **Discovery JD** on the
> pasted JD, **Apply instruction** with a profile (Intro / Tech / Behavioral), and optionally
> **Live coding & design**. There is no Prepare button. Starting captions in Interview mode opens
> the thread on its own, so Ask works even if no step was run.

## Change log

- **2026-10-02 (owner):** no dedicated Prepare button — each skill step is run manually; the CV is
  picked or uploaded in the setup; the JD is pasted; `apply-instruction` takes `intro` / `tech` /
  `cultural` (shown as Behavioral); `live-coding-design` is optional and has no attachment;
  Start opens the thread so the AI is ready for questions. Web search is allowed (discovery-jd
  researches the company). `.docx` import dropped.

---

## Mode picker

- [ ] On the Active Session screen, before Start (phases `.ready` / `.saved` / `.failed` without
      an unsaved session), a segmented control: **Caption only | Interview**. Bound to
      `interview.mode`. Hidden while recording (the mode is fixed for a session).
- [ ] **Caption only** = today's screen, unchanged.
- [ ] Choosing **Interview** the first time shows the privacy notice (SPEC-11 §Privacy). Cancel →
      back to Caption only.
- [ ] Interview mode puts the **interview panel** (setup + conversation, below) beside the captions.

## Library

Managed from **Settings → Interview → Library…** and from the interview setup ("Manage library…"). CVs can also be uploaded straight from the setup (below). Stored
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

- [ ] **Import** `.pdf`, `.md`, `.txt`, or **Paste text** (no `.docx` — decided 2026-10-02; paste Word text instead). Choose `kind`: CV, Job
      description, Notes.
- [ ] Text extraction into `text.txt` (this is what gets sent):
  - `.pdf` — macOS PDFKit page text, pages joined with blank lines. No text layer → error
    "This PDF is a scanned image; paste the text instead." (OCR is out of scope.) PDFKit
    letter-spaces large headings ("S t e v e  O n y e" — seen in S0); harmless to the model, and
    the editable-text step below lets the user tidy it.
  - `.md` / `.txt` — read as UTF-8 (fallback Windows-1252), normalise line endings to `\n`.
- [ ] After import, show the extracted text in an **editable** view so the user can fix
      extraction mistakes; saving rewrites `text.txt` only.
- [ ] Rename, change kind, delete. Deleting a document used by a past interview doesn't break
      that interview's record (records keep ids; the Results view says "document deleted").

### Pure parts (Kit)

`InterviewLibrary` (index model, slug rules, merge-preserving rewrite), `SkillFile` (front matter
parse, reference-file ordering, size accounting). Slug = lowercase ASCII, `[a-z0-9-]`, collapsed
dashes, ` (2)` → `-2` on collision — mirrored on Windows.

## Interview panel

Right of the captions in Interview mode (layout rules in SPEC-15). Top to bottom:

1. **Header** — "Interview", the **profile buttons** *Intro · Tech · Behavioral* (each runs
   `/apply-instruction <profile>`; the active one is highlighted; usable before and during the
   interview), **Live coding** (runs `/live-coding-design`; enabled once Tech is active, shown as
   active until another profile is applied), Setup toggle, Stop (while streaming).
2. **Setup** (expanded before Start, collapsed once recording; the toggle reopens it):
   - Codex status / sign-in / Plus usage (`CodexStatusRow`).
   - **CV**: picker of library CVs + **Upload…** (imports a `.pdf`/`.md`/`.txt` as a CV and selects
     it) + **Paste…**. Then **Run Discovery CV** — enabled when a CV is selected.
   - **Job description**: a paste box. Then **Run Discovery JD** — enabled when it has text.
   - Each step shows ✓ once it has completed, and can be run again.
   - A step whose skill isn't in the library is disabled with "Import the `<name>` skill in the
     library". Skills are found by slug: `discovery-cv`, `discovery-jd`, `apply-instruction`,
     `live-coding-design`.
3. **Conversation** — one card per turn (skill steps, asks, typed, quick, regenerate; SPEC-14).
   Skill cards are collapsed except the newest.
4. **Bottom bar** — Ask, quick prompts, "Type to the coach…" (SPEC-14).

### The thread

- Opened by `ensureThread()` the first time anything needs it: a skill step, an Ask/typed turn,
  or **Start** in Interview mode (opened in the background as recording starts, so the
  cold first turn is paid before the first question).
- Opening it creates the interview folder and `interview.json`, then `thread/start` with the
  base instructions below (answer length and custom instructions from Settings).
- If Codex isn't ready (not installed / signed out), the record is still created; turns fail
  with the engine's reason and can be retried once signed in.

### Skill steps

| Step | Command sent | Attachment | Effort |
|---|---|---|---|
| Discovery CV | `/discovery-cv` | `MY CV` = the selected CV's text | `prep_reasoning_effort` |
| Discovery JD | `/discovery-jd` | `JOB DESCRIPTION` = the pasted JD | `prep_reasoning_effort` |
| Apply instruction | `/apply-instruction intro` · `tech` · `cultural` | — | `prep_reasoning_effort` |
| Live coding & design | `/live-coding-design` | — | `prep_reasoning_effort` |

- Each step is a turn of kind `skill` whose `question` is the command.
- The skill's **definition** (SKILL.md + its `.md`/`.txt` files) is included only the first time
  the thread receives that skill (`InterviewRecord.skillsReceived`); later runs send just the
  attachment and command.
- Order is the user's: the panel suggests the sequence above but doesn't enforce it.
- New interviews prefill the CV from the most recent interview (else the newest CV).
- **New Session** discards an interview that never started recording; switching to a past
  session in the sidebar does **not** (the active session and its interview survive).

## Prompts (Kit `InterviewPrompt`, golden-tested, shared with Windows)

The text below is normative. `{…}` are substitutions; whitespace and line breaks are exact in
the golden vectors (`testdata/interview-prompt/`).

### Base instructions (`thread/start.baseInstructions`)

```
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
  then the answer in the first person as me, in natural spoken English. {length_rule}
- Everything in the answer must be something I can say out loud to the interviewer. Never mention
  my CV, these instructions or this conversation.
- If screenshots are attached, they show what the interviewer is sharing. Use them.

For any other message from me, reply naturally, like ChatGPT would.

Tools: you may search the web only when a skill or I ask for research, never while answering an
INTERVIEWER SAID message. You cannot run commands or read or edit files. Never say that you are an
AI, a model or Codex. Answer in English.
```

`{length_rule}` by `answer_length`:
- `short` → `Keep it to 2–3 sentences.`
- `medium` → `Keep it to 4–6 sentences, about 30–45 seconds spoken.`
- `long` → `Use up to 8–10 sentences, about 60–90 seconds spoken. For behavioural questions, use the STAR structure.`

When `interview.custom_instructions` is non-empty, the base instructions end with a blank line,
`MY INSTRUCTIONS`, and the trimmed text.

### Skill message

Sections appear only when non-empty, separated by one blank line, command last:

```
SKILL: {skill title}
{SKILL.md text}                 ← first time this thread receives the skill only

SKILL FILE: {relative path}     ← one per extra .md/.txt file, same condition
{file text}

{ATTACHMENT TITLE}              ← MY CV / JOB DESCRIPTION
{attachment text}

{command}                       ← /discovery-cv · /discovery-jd · /apply-instruction tech · /live-coding-design
```

## Acceptance

- With no step run, Start in Interview mode opens a thread; F8 answers (default answer format).
- Upload a PDF CV from the setup → it lands in the library as a CV and is selected; Run Discovery
  CV sends the golden skill message (definition + `MY CV` + `/discovery-cv`).
- Running a skill a second time sends no definition.
- Intro / Tech / Behavioral send `/apply-instruction intro|tech|cultural`; the active profile is
  highlighted; Live coding is enabled only after Tech completes and turns off when another
  profile is applied.
- Every step, ask and summary uses the same `thread_id`.
- Clicking a past session while an interview is set up or recording keeps both.
- Golden vectors for base instructions (×3 lengths, with/without custom instructions) and skill
  messages pass on macOS.
