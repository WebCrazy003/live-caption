# SPEC-11 — Interview Assist: overview & shared contract

**Status:** 🟢 Steps 0–5 built on macOS (Windows: config group + migration only) · **Depends on:** SPEC-04 (finals), SPEC-05 (Active Session layout),
SPEC-07 (Settings), SPEC-08 (clipboard) · **Extends:** SPEC.md §9, §12, §15 — a **new feature**,
not in SPEC.md v2.0 · **Build steps:** this spec + [12](SPEC-12-codex-engine.md) →
[13](SPEC-13-interview-prep.md) → [14](SPEC-14-live-ask.md) → [15](SPEC-15-interview-ui-results.md)

> While a live interview is being captioned, the user presses a hotkey (default **F8**). The
> interviewer's latest words (plus, optionally, screenshots on the clipboard) go to a **ChatGPT
> model through the Codex CLI**, in a conversation the user prepares by running their interview skills with
> the user's CV, the JD, an interview skill and their own instructions. The answer streams into a
> panel next to the captions. When the interview ends, the app summarizes it and keeps the
> history.

This file is the **umbrella**: the user need, the decisions, the architecture, every config key,
every on-disk format, the Windows compatibility contract, and the build order. Steps 12–15 hold
the per-area detail and acceptance criteria.

---

## Why (the user need)

In a live interview the user understands the question (captions help) but needs a
**reference answer fast**, grounded in *their* CV and *this* JD, phrased the way they would say
it. Typing the question into ChatGPT by hand is too slow and visibly distracting. So:

- **One key press** sends the question. No typing, no window switching.
- **Prepared context.** CV, JD, interview skills and instructions are loaded **before** the
  interview, once, so each answer costs only the question.
- **One conversation.** Prep, instructions and every question live in **one** thread, so later
  answers stay consistent with earlier ones ("as I said about the migration project…").
- **ChatGPT-like answers.** Conversational, friendly, first-person, speakable — not the terse
  coding-agent style Codex uses by default.
- **A record afterwards.** The Q&A history is saved and summarized when the interview ends.

---

## Decisions (settled with the user)

| # | Decision | Why |
|---|---|---|
| D1 | **Engine = Codex CLI (`codex app-server`)**, signed in with the user's **ChatGPT Plus** account | No extra cost over Plus. `app-server` is a long-lived JSON-RPC process, so there is no per-question process start-up. |
| D2 | **Engine behind an interface** (`AnswerEngine`) | If S0 shows Codex is too slow, an OpenAI-API engine drops in without touching the UI or the flow (the fallback discussed with the user). |
| D3 | **One Codex thread per interview** | Prep, instructions, questions and the end-of-interview summary are all turns on the same thread. |
| D4 | **Codex is locked down to a chat model** | Empty working folder, read-only sandbox, approvals `never`, shell / file-edit / MCP tools disabled. It never reads files, runs commands or edits anything. **Web search is allowed** (owner, 2026-10-02) because the discovery-jd skill researches the company ([SPEC-12 §Lockdown](SPEC-12-codex-engine.md#lockdown)). |
| D5 | **GPT-style answers** | Codex's coding base instructions are **replaced** (`baseInstructions`) with an interview-coach prompt. S0 showed this alone gives conversational, first-person answers (`personality` is deprecated in Codex 0.159 and no longer selects a style). Default model **`gpt-6-luna` at `low`** (S0: 1.3 s to first word). |
| D6 | **The app inlines all context as text** | Skill, CV and JD text are put into each skill-step message by the app; Codex never opens a file. This works with the lockdown, and would work unchanged with an API engine (D2). |
| D7 | **Two modes: Caption only / Interview** | Caption only is today's app, byte-for-byte unchanged, and keeps the on-device guarantee. Interview mode is explicit opt-in. |
| D8 | **macOS first, Windows later**; both share config keys, file formats, prompt text and test vectors, never code | Same approach as the existing port ([SPEC-WINDOWS.md](../SPEC-WINDOWS.md) §6.1). |

## ⚠ Privacy: this feature changes a product invariant

STATUS.md says *"On-device only… nothing else leaves the machine"*, and SPEC.md §17 #8 says the
app *"never reads the clipboard."* **Interview mode breaks both, on purpose and only in that
mode:**

- The question text, the CV, the JD, skill text, instructions and any clipboard screenshots are
  sent to **OpenAI**, and Codex may search the web for skill research (through the `codex` process; the app itself still makes no network calls —
  ATS stays localhost-only).
- The clipboard is **read**, but only for images, only on an Ask, and only if
  `interview.include_clipboard_images` is on (default **off**).

Required in the product:
- [ ] **Caption only mode keeps both invariants exactly** — no Codex process, no clipboard read.
- [ ] The first time Interview mode is chosen, a one-time notice states what is sent and to whom;
      Interview mode cannot be used until it is acknowledged (`interview.privacy_acknowledged`).
- [ ] SPEC.md §17 #8 and the STATUS.md invariant are amended to say "in Caption only mode."
      Windows SPEC §1.2 gets the same amendment when the feature is ported.

---

## Architecture

```mermaid
flowchart LR
  subgraph App[LocalCaption app]
    SC[SessionController\nfinals + interim] --> SEL[AskSelection\n(Kit, pure)]
    HK[Global hotkey\nF8] --> IC
    CB[Clipboard images\n(opt-in)] --> IC
    SEL --> IC[InterviewController]
    LIB[Interview library\nskills · CV · JD · prompts] --> PB[InterviewPrompt\n(Kit, pure)]
    PB --> IC
    IC --> AE{{AnswerEngine}}
    IC --> REC[InterviewRecord\ninterview.json]
    IC --> UI[Answers panel]
  end
  AE --> CX[CodexAppServerEngine\nJSON-RPC over stdio]
  CX --> P[codex app-server\nchild process]
  P -->|ChatGPT sign-in| OAI[(OpenAI)]
```

**Kit (pure, unit-tested, mirrored on Windows):** config group, hotkey string parser,
`AskSelection`, `InterviewPrompt` (all prompt text), JSON-RPC message builders and event parser,
`InterviewRecord` model + writer, library index model.

**App target (platform-specific):** `codex` process + stdio transport, global hotkey, clipboard
image read/clear, PDF text extraction, UI.

### Interview lifecycle

```
Mode = Interview
  → Setup:     pick/upload the CV, paste the JD; run skill steps by hand, in any order:
               /discovery-cv · /discovery-jd · /apply-instruction intro|tech|cultural
               · /live-coding-design (optional) — each a turn on the interview's thread,
               which opens on first need (codex app-server → thread/start, locked down)
  → Start:     captions run exactly as today (SPEC-04/05); the thread opens now if no
               step has, so Ask works with no setup at all
  → Ask (F8):  AskSelection(text since last ask | last N sentences) + clipboard images
               → turn/start on the same thread → answer streams into the Answers panel
  → End:       "End interview" (Stop in Interview mode) saves the transcript as today, then
               asks: Summarize (summary turn → summary.md) or a follow-up prompt — both
               on the same thread; nothing is summarized automatically
```

---

## Config — new `interview` group

Added to `config.json` like `summary` was (SPEC-10): merge-defaults on load, **no schema bump**,
old files load with defaults. Snake_case keys; identical on both platforms. An enum key holding
a string this build doesn't know falls back to that key's default (no repair); a wrong JSON
*type* repairs the file like any other key.

| Key | Type | Default | Notes |
|---|---|---|---|
| `mode` | `"caption"` \| `"interview"` | `"caption"` | Last-used mode; the mode picker writes it. |
| `privacy_acknowledged` | bool | `false` | Set by the one-time notice. |
| `engine` | `"codex"` | `"codex"` | Reserved for `"openai_api"` (D2). |
| `codex_path` | string | `""` | `""` = auto-detect ([SPEC-12](SPEC-12-codex-engine.md#finding-codex)). Platform-specific → `"$default"` in vectors. |
| `model` | string | `""` | `""` = the S0 recommended default, **`gpt-6-luna`** (falls back to the server's `isDefault` model if absent). Picker lists `model/list`. |
| `reasoning_effort` | string | `"low"` | For **answers**. Values come from the model's `supportedReasoningEfforts`. |
| `prep_reasoning_effort` | string | `"medium"` | For skill steps and the summary (not time-critical). |
| `answer_length` | `"short"` \| `"medium"` \| `"long"` | `"medium"` | 2–3 / 4–6 / 8–10 spoken sentences. |
| `custom_instructions` | string | `""` | Global answer instructions, prefilled into every new interview's setup. |
| `hotkey` | string | `"F8"` | Grammar below. |
| `send_mode` | `"since_last_ask"` \| `"last_sentences"` | `"since_last_ask"` | [SPEC-14 §What gets sent](SPEC-14-live-ask.md#what-gets-sent). |
| `send_sentences` | int | `3` | For `last_sentences`. Clamp 1…20. |
| `max_words` | int | `400` | Hard cap on one Ask's transcript text. Clamp 50…2000. |
| `include_clipboard_images` | bool | `false` | Read images from the clipboard on Ask. |
| `clear_clipboard_images_after_send` | bool | `true` | Remove those images from the clipboard once sent. |
| `busy_policy` | `"interrupt"` \| `"queue"` | `"interrupt"` | What an Ask does while an answer is still streaming. |

`quick_prompts` was removed on 2026-10-02 (owner: not needed), as were `show_key_points` and
`summarize_on_end`; an old `config.json` holding them loads fine — unknown keys are ignored.

### Hotkey string grammar (shared)

```
hotkey   = *(modifier "+") key
modifier = "Ctrl" | "Alt" | "Shift" | "Cmd"        ; Cmd = ⌘ on macOS, Win key on Windows
key      = "F1".."F24" | "A".."Z" | "0".."9" | "Space" | "Enter" | "Tab"
         | "Up" | "Down" | "Left" | "Right" | "PageUp" | "PageDown" | "Home" | "End"
```

Case-insensitive on read, with the aliases `Control`, `Option`/`Opt`, `Command`/`Win` and
`Return`; canonical on write (modifiers in the order Ctrl, Alt, Shift, Cmd; duplicates
collapse). A key other than F1–F24 needs a modifier **other than Shift** — a bare global `K`,
`Space` or arrow would swallow that key in every app. Invalid or empty → the default `F8` (and
Settings shows why). A combination the OS refuses to
register is reported in Settings and in the Interview header, never crashes.

---

## On-disk layout

Everything under the existing app root (macOS `~/Library/Application Support/LocalCaption/`,
Windows `%LOCALAPPDATA%\LocalCaption\`). **Not** in the transcript folder — it holds the CV.

```
LocalCaption/
└── interview/
    ├── library/
    │   ├── index.json                   ← documents + skills + metadata (schema below)
    │   ├── skills/<slug>/SKILL.md       ← imported skill (+ any .md/.txt it ships)
    │   └── documents/<slug>/
    │       ├── original.<pdf|md|txt>
    │       └── text.txt                 ← extracted text the prompt actually uses
    ├── interviews/…                     ← legacy folders (earlier builds); imported once into
    │                                       the database, then left untouched
    ├── outbox/                          ← short-lived PNGs for Codex's localImage input,
    │                                       deleted when each turn ends
    ├── workspace/                       ← Codex `cwd`. MUST stay empty.
    └── codex-home/                      ← dedicated CODEX_HOME (if S0 confirms; SPEC-12)
```

### `library/index.json` (schema 1)

```json
{
  "schema_version": 1,
  "documents": [
    { "id": "uuid", "slug": "cv-2026", "kind": "cv", "title": "CV 2026",
      "original": "original.pdf", "chars": 8123, "added_at": "2026-10-01T09:00:00Z" }
  ],
  "skills": [
    { "id": "uuid", "slug": "interview-coach", "title": "Interview coach",
      "files": ["SKILL.md", "star-examples.md"], "chars": 5210, "added_at": "…" }
  ]
}
```

`kind` ∈ `cv` | `jd` | `notes`. Unknown top-level keys are preserved on rewrite; a corrupt
file is backed up to `index.json.bak-<ts>` and replaced with an empty index.

### `interview.json` (schema 1) — legacy

**Since 2026-10-02 interviews live in the database** (§SQLite). `interview.json` is the shape
earlier builds wrote to folders; it is still the in-memory model (`InterviewRecord`) and the
import format for those folders.

```json
{
  "schema_version": 1,
  "id": "uuid",
  "name": "Acme — Senior iOS",
  "session_id": 42,
  "capture_session_uuid": "uuid",
  "created_at": "…", "started_at": "…", "ended_at": "…",
  "engine": "codex", "model": "…", "reasoning_effort": "low", "thread_id": "thr_…",
  "setup": {
    "company": "Acme", "role": "Senior iOS Engineer",
    "skill_ids": ["uuid"], "document_ids": ["uuid", "uuid"],
    "jd_text_inline": null,
    "instructions": "…", "answer_length": "medium"
  },
  "prep": { "status": "done", "briefing": "markdown…", "extra_turns": 1, "completed_at": "…" },
  "turns": [
    { "n": 1, "kind": "ask", "question": "…", "audio_from_ms": 0, "audio_to_ms": 61200,
      "images": ["attachments/1-1.png"], "answer": "markdown…",
      "status": "completed", "asked_at": "…", "ttft_ms": 1840, "total_ms": 6210 }
  ],
  "summary": { "status": "done", "file": "summary.md", "completed_at": "…" }
}
```

- `session_id` is null until Stop saves the session; `capture_session_uuid` is the journal's
  session id, so crash recovery can link the record (SPEC-15 §History).
- `kind` ∈ `ask` (hotkey/button) | `typed` | `quick` | `regenerate` | `skill` (a skill step; `question` is the command, e.g. `/apply-instruction tech`).
- `prep` is legacy (records made with the old Prepare button); new records leave it at its defaults.
- `status` ∈ `streaming` | `completed` | `interrupted` | `failed`.
- Optional fields that are null are omitted when written.
- `ttft_ms` = press → first answer text; `total_ms` = press → turn completed. Kept for tuning.
- Written **atomically after every state change** (turn started, completed, failed). A crash
  loses at most the in-flight answer text; the thread itself survives in Codex and can be
  resumed with `thread_id`.

### SQLite

Two migrations on the existing `localcaption.db`; the Windows `Store` applies the same DDL
(`user_version` 2 and 3) so a database file is readable by either build.

- **`v2_interview`** — `sessions` gains `mode TEXT NOT NULL DEFAULT 'caption'` and
  `interview_dir TEXT NULL` (the latter unused since v3; the link is `interviews.session_id`).
- **`v3_interview_store`** (owner, 2026-10-02: "a local DB storing all interview session data") —
  every interview's record, turns, CV/JD/summary/transcript text and screenshots:

```sql
CREATE TABLE interviews (
    id TEXT PRIMARY KEY,
    session_id INTEGER REFERENCES sessions(id) ON DELETE SET NULL,
    name TEXT NOT NULL,
    created_at TEXT NOT NULL,
    started_at TEXT,
    ended_at TEXT,
    capture_session_uuid TEXT,
    engine TEXT NOT NULL,
    model TEXT NOT NULL,
    reasoning_effort TEXT NOT NULL,
    thread_id TEXT,
    cv_document_id TEXT,
    cv_title TEXT,
    cv_text TEXT,
    jd_text TEXT,
    instructions TEXT NOT NULL DEFAULT '',
    answer_length TEXT NOT NULL DEFAULT 'medium',
    skill_ids TEXT NOT NULL DEFAULT '[]',
    legacy_briefing TEXT,
    summary_status TEXT NOT NULL DEFAULT 'pending',
    summary_text TEXT,
    summary_completed_at TEXT,
    transcript TEXT
);
CREATE INDEX idx_interviews_session ON interviews(session_id);
CREATE INDEX idx_interviews_capture ON interviews(capture_session_uuid);
CREATE TABLE interview_turns (
    interview_id TEXT NOT NULL REFERENCES interviews(id) ON DELETE CASCADE,
    n INTEGER NOT NULL,
    kind TEXT NOT NULL,
    question TEXT NOT NULL,
    answer TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL,
    error TEXT,
    audio_from_ms INTEGER,
    audio_to_ms INTEGER,
    images TEXT NOT NULL DEFAULT '[]',
    asked_at TEXT NOT NULL,
    ttft_ms INTEGER,
    total_ms INTEGER,
    PRIMARY KEY (interview_id, n)
);
CREATE TABLE interview_images (
    interview_id TEXT NOT NULL REFERENCES interviews(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    turn_n INTEGER,
    png BLOB NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (interview_id, name)
);
```

- One row per interview; turns are replaced as a set on each save; screenshots are PNG bytes,
  named `<turn>-<n>.png`, and are not touched by record saves.
- Deleting an interview cascades to its turns and screenshots. Deleting its session leaves the
  interview, unlinked.
- On launch, folders under `interview/interviews/` that aren't in the database yet are imported
  (`interview.json`, `summary.md`, `cv.txt`, `attachments/*.png`) and left in place.
- **`v4_segments_and_details`** (owner, 2026-10-02: "make sure all save on database") — every
  session's caption segments, and the interview details (SPEC-13 §Interview panel):

```sql
CREATE TABLE session_segments (
    session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    n INTEGER NOT NULL,
    text TEXT NOT NULL,
    t_start_ms INTEGER NOT NULL,
    t_end_ms INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (session_id, n)
);
ALTER TABLE interviews ADD COLUMN candidate_name TEXT;
ALTER TABLE interviews ADD COLUMN company TEXT;
ALTER TABLE interviews ADD COLUMN interview_step INTEGER;
```

- Stop writes the session row and its segments in one transaction, then the `.txt`/`.json`
  export; a failed export only warns (the database is the record), a failed database write keeps
  the journal. Crash recovery saves the same way.
- On launch, sessions without segments are filled once from their `.json` sidecar (else the
  `.txt` body, one segment per line, `[HH:MM:SS]` read as the start); the files are left in place.
- **Windows:** not yet migrated — its `Store` still stops at v3.

---

## Windows compatibility contract

The Windows app ([SPEC-WINDOWS.md](../SPEC-WINDOWS.md)) gets this feature later. To keep that
cheap, the macOS build must keep everything **shared** below in the portable form.

| Area | Shared (must match byte-for-byte or by vector) | Platform-specific |
|---|---|---|
| Config | `interview` keys, defaults, merge/repair | default `codex_path` |
| Hotkey | string grammar + parser vectors | macOS Carbon `RegisterEventHotKey`; Windows `RegisterHotKey` |
| Selection | `AskSelection` algorithm + vectors | — |
| Prompts | every template in `InterviewPrompt`, golden outputs | — |
| Codex protocol | request builders + event parser, golden JSON | spawning (`Process` vs `System.Diagnostics.Process`; on Windows the npm shim is `codex.cmd` → spawn via `cmd.exe /c` or the `node` entry) |
| Records | `interview.json`, `index.json`, folder layout, file names | root path |
| Clipboard images | behaviour (which types, PNG re-encode, removal rule) | `NSPasteboard` vs `Clipboard` (`CF_DIB`/`CF_DIBV5`, PNG, `CF_HDROP` image files) |
| Documents | `text.txt` extraction result is what is sent | PDF: PDFKit vs a .NET library (PdfPig). No DOCX (decided) |

**New conformance vectors** (asserted by both suites, per `testdata/README.md` rules):

```
testdata/
├── config/interview-group-defaults.json   + extend round-trip-is-stable.json
├── hotkey/parse.json                      "ctrl+shift+f8" → "Ctrl+Shift+F8"; "Hyper+X" → invalid
├── ask/                                   segments + interim + mode + mark → text + new mark
├── interview-prompt/                      inputs → exact prompt strings
└── codex/                                 params → request JSON; notification JSON → events
```

Windows must also add the `interview` group to its `Config` **now** (defaults only, feature
hidden) — the same treatment as `summary` (§9.2) — so `config.json` keeps round-tripping between
machines before the feature is ported. Windows-specific risks to verify on the G15:

- **W-I1** Codex's Windows sandbox differs from macOS Seatbelt. The lockdown must not depend on
  the sandbox alone — tools are disabled by config **and** by the engine's tool-call guard
  (SPEC-12), so it holds even if `readOnly` is weaker there.
- **W-I2** F-keys on laptop keyboards (the G15's Fn-lock) — same caveat as Mac media keys.
- **W-I3** Remote operation over Jump Desktop (SPEC-WINDOWS §4.7): a global hotkey may be
  captured by the remote client. Keep the on-screen Ask button first-class.

---

## Build steps

| Step | Spec | Delivers | Gate to the next step |
|---|---|---|---|
| **0** ✅ | [12 §S0](SPEC-12-codex-engine.md#s0--spike-gate) | `spike/codex-answer-spike/`: measured latency, lockdown, tone, images, usage read | **Passed 2026-10-01** — [RESULTS.md](../spike/codex-answer-spike/RESULTS.md) |
| 1 ✅ | **11** (this) | Kit: config group, hotkey parser, `AskSelection`, `InterviewPrompt`, records, vectors; DB migration | `swift test` green incl. new vectors |
| 2 ✅ | [12](SPEC-12-codex-engine.md) | `CodexAppServerEngine`: process, JSON-RPC, lockdown, streaming, models, usage, sign-in | Engine passes its scripted fake-server tests + a live smoke run |
| 3 ✅ | [13](SPEC-13-interview-prep.md) | Library, mode picker, CV upload, manual skill steps (rebuilt 2026-10-02) | Skill steps on one thread |
| 4 ✅ | [14](SPEC-14-live-ask.md) | Hotkey, selection, screenshots, Answers panel, typed prompts | F8 during a real call → answer streaming |
| 5 ✅ | [15](SPEC-15-interview-ui-results.md) | Compact layout, end-of-interview summary, history, Settings (usage) | Full acceptance below |

Step 1 can start before S0 finishes (it is engine-independent). Steps 2–5 wait for S0.

---

## Acceptance (feature-level)

- Caption only mode is unchanged: no `codex` process, no clipboard reads, all existing tests and
  vectors still pass.
- From an empty library, the user can upload a CV, paste a JD, run the skill steps, start
  captions, press F8 and see an answer begin streaming within the S0 latency budget — and F8
  also works after Start with no step run.
- Every skill step, Ask and the summary go to the **same** `thread_id`.
- During a whole interview Codex executes **no** command, edits **no** file and calls **no** MCP
  tool (verified from the event stream; SPEC-12). Web search is allowed.
- After Stop, a summary appears and the interview (transcript beside the full AI conversation,
  summary) can be reopened read-only from the session list.
- An old `config.json` loads with `interview` defaults; a Mac `config.json` round-trips through
  the Windows build without losing the `interview` group.

## Non-goals (this feature, v1)

- **No OpenAI-API engine** (designed for by D2; built only if S0 fails or later on request).
- **No microphone capture.** Only the interviewer's audio is captured, so the summary cannot
  judge what the user actually said.
- **No auto-ask** (detecting a question and asking without a key press).
- **No speaker diarization.**
- **No Windows build** of this feature yet — only the shared contract and the config group.

## Blockers / risks

| ID | Risk | Owner spec |
|---|---|---|
| **B9** | Codex is too slow to first word (agent overhead, reasoning) | 12 §S0 |
| **B10** | `app-server` protocol changes between Codex releases (parts are marked experimental) | 12 — pin a tested version, check `--version` at start |
| **B11** | Plus usage limit hit mid-interview | 12 (usage read), 15 (warning before Start) |
| **B12** | Lockdown is incomplete — a tool runs, or the user's own `~/.codex` config/AGENTS.md/MCP servers leak in | 12 (dedicated `CODEX_HOME`, config overrides, tool-call guard) |
| **B13** | Transcription errors produce a wrong question → a confident wrong answer | 14 (prompt tells the model the text is noisy and to restate the question it answered) |
| **B14** | Codex ChatGPT sign-in is for Codex use; confirm powering this app's Q&A through it is acceptable under OpenAI's terms | User — before relying on it |

## Open decisions

1. ✅ **Busy policy default** — `interrupt` (latest question wins). Confirmed by the user 2026-10-02.
2. ✅ **Key points in Interview mode** — moot: the Key points feature (SPEC-10) was removed on
   2026-10-02, and `show_key_points` with it.
3. **Custom prompts interpretation** — this spec reads "custom prompts" as (a) global + per-interview
   instructions, (b) saved quick-prompt buttons, (c) a typed-message box. → Owner, 2026-10-02:
   no quick-prompt buttons; keep custom instructions and the typed box.
4. ✅ **Answer language** — English only. Confirmed by the user 2026-10-02.
