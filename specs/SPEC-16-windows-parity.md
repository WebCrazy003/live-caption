# SPEC-16 — Windows parity: what macOS has that Windows does not

**Status:** ⬜ Not started · **Written:** 2026-10-02, against `main` at `6608d48` (the merge of
`windows/stage-b`) · **Depends on:** [SPEC-WINDOWS.md](../SPEC-WINDOWS.md) (the port, B0–B6 done),
[SPEC-11](SPEC-11-interview-assist.md)–[15](SPEC-15-interview-ui-results.md) (Interview Assist on macOS)

> The Windows app captions as well as the Mac one and has its own extras (process loopback,
> CUDA, Send, shortcuts). What it lacks is everything the Mac gained after the port was planned:
> **Interview mode** in full, **captions stored in the database**, and a handful of
> caption-side details. This spec lists that gap as buildable work, in order, with acceptance
> criteria.
>
> **The Mac code is the reference, not SPEC-11..15.** Those specs were written first and the
> code has moved on (§9 lists where). Where this spec and an older spec disagree, this one
> describes what the Mac actually does today.

---

## 0. How to read this

- **W-P0 … W-P5** are the build stages (§8). P0 fixes things that are broken *today* and is
  worth doing even if Interview mode is never ported.
- "Mac:" references are into `app/Sources/` (`LC` = `LocalCaption/`, `Kit` = `LocalCaptionKit/`).
  "Win:" references are into `windows/src/`.
- **Shared** means: same keys, same file/DB format, same strings, asserted by the same
  `testdata/` vectors in both suites. **Never shared code** (SPEC-11 D8).
- Development happens on the Mac; `LocalCaption.Core` and its tests build and run on macOS
  (`net10.0`). The App/WPF layer is written on the Mac and **built and verified on the G15**.

---

## 1. The gap at a glance

| # | Area | Mac | Windows today | Stage |
|---|---|---|---|---|
| 1 | SQLite file readable by the other build | ✅ (own files) | ❌ **either direction fails at open** | P0 |
| 2 | Captions stored in DB (`session_segments`, schema v4) | ✅ DB is truth, files are export | ❌ `.txt` is truth, no segments | P0 |
| 3 | `interview.screenshot_hotkey`, `interview.panel_layout` | ✅ | ❌ dropped on every write | P0 |
| 4 | Auto-copy obeys its toggle | ✅ | ❌ **bug**: copies after most finals with Auto-copy off | P0 |
| 5 | Interview Kit logic (hotkey grammar, AskSelection, prompts, Codex RPC, records, library) | ✅ + vectors | ❌ none; 4 vector folders unasserted | P1 |
| 6 | Codex engine (process, sign-in/out, models, usage, streaming) | ✅ | ❌ | P2 |
| 7 | Global Ask / Screenshot hotkeys, region screenshot, clipboard images | ✅ | ❌ | P2 |
| 8 | CV / skill library, PDF text | ✅ | ❌ | P2 |
| 9 | Mode chooser + privacy notice | ✅ | ❌ | P3 |
| 10 | Preparation stage, interview layout, Answers panel, Coach input | ✅ | ❌ | P3 |
| 11 | End interview sheet, summary, follow-ups, replay | ✅ | ❌ | P3 |
| 12 | Sessions window with detail pane, mode filter, interview delete options | ✅ | ❌ sidebar + opens `.txt` in Notepad | P3 |
| 13 | Settings → Interview / Asking / Prompts / Codex | ✅ | ❌ | P3 |
| 14 | Caption-side details (recovery per session, Retry, prefix field, ranges…) | ✅ | 🟡 | P4 |

Windows-only features (process loopback, auto gain, quick bar, Send, configurable shortcuts,
click-through, theme, bookmarks, Recycle-Bin delete, Velopack) are **not** in scope and must keep
working.

---

## 2. W-P0 — Fix shared formats and the clipboard bug

Do this first; it is small, it is broken today, and every later stage writes to the same DB.

### 2.1 Database interop with the Mac

**Problem.** The Mac (GRDB) records migrations as rows in `grdb_migrations(identifier TEXT NOT
NULL PRIMARY KEY)` and never sets `PRAGMA user_version` (it stays 0). Windows
(`Win: LocalCaption.Core/Data/Store.cs`) trusts `user_version` alone and runs plain
`ALTER`/`CREATE` steps outside a transaction. So:

- Mac DB on Windows → `user_version 0` → v2's `ALTER TABLE sessions ADD COLUMN mode` fails
  "duplicate column" → `Store` ctor throws → app fails at launch (`AppEnvironment.cs` has no fallback).
- Windows DB on Mac → no `grdb_migrations` → GRDB re-runs `v1_sessions` → "table sessions
  already exists" → Mac falls back to a temporary DB.

SPEC-11 §SQLite's claim that the file "is readable by either build" is false today.

**Fix (Windows-side only; the Mac does not change):**

- [ ] Windows keeps the GRDB bookkeeping table as the source of truth. On open:
      `CREATE TABLE IF NOT EXISTS grdb_migrations (identifier TEXT NOT NULL PRIMARY KEY)`.
- [ ] Migrations are identified by the **Mac's identifiers**, in order:
      `v1_sessions`, `v2_interview`, `v3_interview_store`, `v4_segments_and_details`.
- [ ] A migration is "applied" if its identifier is in `grdb_migrations` **or** the legacy
      `user_version` says so (Windows DBs created before this change: 1 → v1, 2 → v2, 3 → v3).
- [ ] Each migration step is **idempotent** — probe `sqlite_master` / `pragma_table_info`
      before every `CREATE`/`ALTER` — and each runs **in its own transaction** together with the
      `INSERT INTO grdb_migrations` row for its identifier.
- [ ] After migrating, also set `PRAGMA user_version = 4` (harmless to the Mac, keeps old
      Windows logic meaningful).
- [ ] Unknown identifiers in `grdb_migrations` (a newer Mac build) are ignored, not an error;
      unknown columns are tolerated (`SELECT` names columns explicitly).
- [ ] Fix SPEC-11 §SQLite and SPEC-WINDOWS §9.3 wording to describe this.

### 2.2 Schema v4 — `v4_segments_and_details`

- [ ] `session_segments(session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
      n INTEGER NOT NULL, text TEXT NOT NULL, t_start_ms INTEGER NOT NULL, t_end_ms INTEGER NOT NULL,
      created_at TEXT NOT NULL, PRIMARY KEY(session_id, n))` — exactly the Mac DDL
      (`Kit/Store.swift:48-63`).
- [ ] `interviews` gains `candidate_name TEXT`, `company TEXT`, `interview_step INTEGER`.
- [ ] `PRAGMA foreign_keys = ON` (already), WAL (already).

### 2.3 Captions live in the database (SPEC.md §12.3, as on Mac since `4923b22`)

Mac order (`LC/Session/SessionController.swift:221-254`, recovery the same):
**DB row + all segments in one transaction first** → if that fails, keep the journal and report
the save as failed → then write the `.txt`/`.json` export, whose failure is only a warning.

Windows today does the reverse and swallows DB errors (`Win: Session/SessionController.cs:517-570`,
`AppEnvironment.cs:96-124`).

- [ ] Port `Store.insert(_:segments:)`, `segments(sessionId)`, `segmentCount`,
      `setTranscriptFile`, and `all(mode:)` (optional mode filter, already partly there).
- [ ] Save and recovery follow the Mac order above. A failed DB write keeps the journal.
- [ ] Port `TranscriptFileReader` (`.json` sidecar first, else `.txt` lines with `[HH:MM:SS]`
      prefixes) and `Store.importTranscriptFiles`: **once at launch**, sessions with no segments
      get them from their files.
- [ ] Session list "Refresh → clear rows whose `.txt` is missing" (`Win: MainWindow.xaml.cs:918-949`)
      must change: a missing export file is no longer data loss. Only offer to clear rows that have
      **neither** segments **nor** a file.
- [ ] Update the `Store.cs` doc comment ("Transcript text never lives in SQLite") and SPEC-WINDOWS §9.3.

### 2.4 Config keys

- [ ] `Config.InterviewGroup` gains `screenshot_hotkey` (default `"F9"`) and `panel_layout`
      (`automatic` | `side_by_side` | `stacked`, default `automatic`, unknown → default via the
      existing `Known(...)` fallback).
- [ ] Add helpers matching Kit: `ClampedSendSentences` (1–20), `ClampedMaxWords` (50–2000),
      `AskMode`, `EffectiveModel` (`""` → `RecommendedModel` = `"gpt-6-luna"`).
- [ ] Extend vectors: `testdata/config/interview-group-defaults.json` gains
      `interview.screenshot_hotkey: "F9"` and `interview.panel_layout: "automatic"`;
      `interview-unknown-enum-falls-back.json` gains a `panel_layout` case. (Both suites must
      still pass — the Mac already behaves this way.)

### 2.5 Auto-copy bug

`Win: Session/SessionController.cs:499` —
`if (!AutoUpdate && interim.Length > 0) return;` uses "empty interim" to mean "manual Copy
button". But `OnFinalized` passes the current hypothesis, which is usually empty after a final,
so **with Auto-copy off Windows still writes the last N sentences to the clipboard after most
finals.** That undermines the clipboard-privacy position of SPEC-WINDOWS §12.1.

- [ ] Split into `CopyLastNManual()` (always copies) and `AutoCopyLastN(string interim)`
      (returns unless `clipboard.auto_update`), as the Mac does (`LC/Session/SessionController.swift:57-64`).
- [ ] Unit test: Auto-copy off → no clipboard write on `OnSpeechEnded` or `OnFinalized`; manual
      copy still works.

### 2.6 Repo hygiene

- [ ] Add `.gitattributes`: `* text=auto`, and `*.cs`, `*.swift`, `testdata/** text eol=lf`.
      The interview prompt templates are golden-tested byte for byte; a CRLF checkout on Windows
      (`core.autocrlf=true`) would put `\r\n` into C# raw strings and fail every prompt vector.
- [ ] Update `testdata/README.md`: list `ask/`, `codex/`, `hotkey/`, `interview-prompt/` and the
      Mac suites that assert them (`InterviewConformanceTests.swift`, `CodexRPCTests.swift`).

**P0 acceptance:** a Mac `localcaption.db` copied to Windows opens, lists every session with its
transcript (from segments), and survives a new Windows session; the same file copied back opens
on the Mac with nothing lost. Both suites green. Auto-copy off → clipboard untouched during a
session.

---

## 3. W-P1 — Port the Interview Kit to `LocalCaption.Core`

All of this is plain logic: build and test it **on the Mac**. New namespace
`LocalCaption.Core.Interview`. Each item is a 1:1 port of the named Kit file.

| Port | From (Mac Kit) | Asserted by |
|---|---|---|
| `Hotkey` — grammar parser, canonical form, typed errors (`empty`, `unknown_modifier`, `unknown_key`, `missing_key`, `needs_modifier`); modifiers Ctrl, Alt, Shift, Cmd (aliases control / option, opt / command, **win**); keys F1–F24, A–Z, 0–9, Space, Enter (return), Tab, arrows, PageUp/PageDown, Home, End; non-F keys need a modifier other than Shift; `Resolve(fallback)`; defaults F8 / F9 | `Hotkey.swift` | `testdata/hotkey/parse.json` (21) |
| `AskSelection.Select(segments, interim, mode, mark, pressAudioMs, maxWords)` → text, new mark `(audio_ms, interim_was_sent)`, from/to ms; `LastWords` | `AskSelection.swift` | `testdata/ask/` (8) |
| `InterviewPrompt` — `BaseInstructions(length, custom)`, `LengthRule`, `SkillMessage(command, definition, attachments)`, `Ask(text, imageCount)`, `Regenerate`, `Summary(transcript)` (last 15 000 words, whitespace collapsed) | `InterviewPrompt.swift` | `testdata/interview-prompt/` (17) |
| `CodexRpc` + `JsonValue` — `MinimumVersion` 0.159.3, `DisabledFeatures` (15), config overrides, `LaunchArguments`, `AllowedItemTypes`; all request builders; `DeclineResponse`; `Decode(line)`; `Event(method, params)`; `Failure(...)`; `Usage`/`Window` (labels, remaining, lowest), `Account`, `Models` (hidden dropped, modalities default text+image), `Version`, `IsSupported`. `JsonValue.Line()` = sorted keys, single line, **no escaped slashes** | `CodexRPC.swift` | `testdata/codex/{requests,decode,events,responses}.json` |
| `InterviewRecord` (+ `Status`, `TurnKind`, `TurnStatus`, `Setup`, `Turn`, `Summary`), `SessionName(candidate, company, step, date)` → `who-company-step-yyyy-MM-dd`, `NextTurnNumber`, `SkillsReceived`, `ActiveProfile`, `LiveCodingActive`, `FailInterruptedTurns`, `QaMarkdown()` | `InterviewRecord.swift` | new vectors, §3.1 |
| `InterviewLibraryIndex` (`index.json` schema 1; corrupt → `.bak-<yyyyMMdd-HHmmss>` + empty; atomic write that keeps unknown top-level keys), `LibrarySlug.Make`, `SkillFile` (`SKILL.md`, front-matter `name`, `.md`/`.txt` partition sorted by path, hidden skipped) | `InterviewRecord.swift` | new vectors, §3.1 |
| `InterviewStore` CRUD on `Store`: `SaveInterview` (upsert row + delete/reinsert turns, one transaction), `Interview(id)`, `Interview(sessionId)`, `AllInterviews`, `DeleteInterview`, `MarkInterview(sessionId)`, `AddInterviewImage`, `InterviewImage`, `InterviewImageCount`; JSON-array text for `skill_ids` / `images`; image names `<turn>-<i>.png`; unknown kind → `typed`, unknown status → `failed`; `legacy_briefing` ↔ prep. `ImportLegacyInterview` is **not** needed (no legacy Windows folders exist) | `InterviewStore.swift` | ports of `InterviewStoreTests.swift` |
| `AppPaths`: `Interview`, `Library`, `LibraryIndex`, `Skills`, `Documents`, `Outbox`, `Workspace`, `CodexHome`, `CodexLog` under `<Root>\interview\` | `AppPaths.swift` | — |
| `TimeFormat.Day` (`yyyy-MM-dd`, local) | `TimeFormat.swift` | — |
| `SessionRecord.IsInterview`, mode constants | `SessionRecord.swift` | — |

- [ ] `ConformanceTests` (or a new `InterviewConformanceTests.cs` / `CodexRpcTests.cs`) asserts
      `hotkey/`, `ask/`, `interview-prompt/`, all four `codex/` files.
- [ ] Port the non-vector Mac tests too: request line is single-line JSON, tool guard, version
      gate, usage lowest window (`CodexRPCTests.swift:56,147,186,195`); record round-trip,
      session name, interrupted turns fail on relaunch, `QaMarkdown`, slugs, index repair,
      front matter/partition, summary keeps last 15 000 words (`InterviewKitTests.swift`).
- [ ] Prompt templates contain non-ASCII (– —): source files are UTF-8, LF (§2.6).

### 3.1 New shared vectors (add to `testdata/`, assert in **both** suites)

SPEC-11 calls these shared, but nothing pins them, and some have no exact .NET equivalent:

- [ ] `testdata/library/slug.json` — `LibrarySlug.Make`: diacritics (`é`→`e`), case, full-width
      folding, collisions (`-2`, `-3`), empty → `item`. (.NET: `Normalize(FormKD)` + drop
      `NonSpacingMark` + lower-invariant + `[a-z0-9-]`; the vectors decide.)
- [ ] `testdata/records/session-name.json`, `qa-markdown.json`.
- [ ] `testdata/library/skill-files.json` — front matter, partition order, hidden files.

**P1 acceptance:** every `testdata/` folder is asserted by both suites with no
platform-conditional expectations (SPEC-WINDOWS §21.3).

---

## 4. W-P2 — Windows platform services

### 4.1 Codex process (`LocalCaption.Interview` project, net10.0-windows)

Behaviour matches Mac `CodexProcess.swift` / `CodexAppServerEngine.swift` / `CodexService.swift`:

- [ ] **Locate** `codex`, first one ≥ 0.159.3 wins (older ones skipped; if only old ones →
      "too old", else "not installed"): `interview.codex_path` → `%APPDATA%\npm\codex.cmd` →
      `%LOCALAPPDATA%\Programs\…` / Volta / Scoop shims → `where codex`. Each candidate:
      `--version` with a 5 s timeout, parsed by `CodexRpc.Version`.
- [ ] **Spawn without `cmd.exe /c`.** The npm shim `codex.cmd` mangles the quoting of
      `-c mcp_servers={}` and leaves orphans. Resolve the shim to the package's native
      `codex.exe` under `node_modules\@openai\codex\…` (preferred) or `node <entry>.js`; fall back
      to `cmd.exe /c` only if neither resolves, and log it.
- [ ] Put the child in a **Job Object** with kill-on-close so it dies with the app (fixes the
      Mac's never-called `shutdown()`).
- [ ] Environment: user env + `CODEX_HOME=<Root>\interview\codex-home`. Working directory
      `<Root>\interview\workspace`, emptied before launch and before every `thread/start` /
      `thread/resume` (anything found is logged as an error).
- [ ] stderr → `interview\codex.log`, rotated at 5 MB to `codex.log.1`.
- [ ] Newline-delimited JSON over stdio; UTF-8 **without BOM**; `\n` line terminator.
- [ ] Lazy start; `initialize` (`clientInfo{name:"localcaption", title:"LocalCaption", version}`,
      `capabilities.experimentalApi:true`) then `initialized`; re-resume known threads after a
      restart; 2 unexpected exits → permanent `crashed`.
- [ ] Request timeout 15 s; server→client requests answered with `DeclineResponse` and logged
      as a lockdown breach.
- [ ] One turn at a time (`busy`); watchdog `slow` at 30 s, interrupt + `timeout` at 120 s;
      interrupt waits ≤ 2 s for the turn id; tool-call guard on `item/started` types outside
      `AllowedItemTypes` → interrupt + `blockedTool`.
- [ ] Failure mapping, model resolution (`configured if listed → gpt-6-luna → isDefault →
      configured`), usage (low < 20 %), sign-in (`account/login/start` → open `authUrl` in the
      default browser → wait for `account/login/completed`; Cancel), sign-out (`account/logout`)
      — all as Mac §"Codex engine", error and status strings identical except install hints:
      "Install it with npm: npm install -g @openai/codex" / "npm update -g @openai/codex".
- [ ] **[DECISION, owner 2026-10-02] Caption only mode never uses Codex.** Unlike the Mac
      (where opening Settings → Codex starts the process even in Caption only mode), Windows
      starts it only in Interview mode or when the user presses **Check again** / **Sign in…**
      in Settings → Codex.

### 4.2 Global hotkeys

- [ ] `RegisterHotKey` on a message-only window; two ids (Ask, Screenshot). Canonical `Cmd`
      maps to `MOD_WIN`. Add `MOD_NOREPEAT`.
- [ ] States `off` / `registered` / `unavailable(reason)`; `ERROR_HOTKEY_ALREADY_REGISTERED` →
      "another app is using X". The on-screen Ask button always works.
- [ ] Active only when: mode chosen = Interview, preparation stage **not** showing, phase not
      `saved` (Mac `ActiveSessionView.swift:96-103`). Re-evaluated on every change of those or
      of either hotkey. Unregistered on quit.

### 4.3 Region screenshot (Screenshot hotkey / camera button)

Mac hides itself and runs `screencapture -i`. Windows has no equivalent command, so:

- [ ] Own overlay: hide the main window, show a borderless top-most window per monitor
      (per-monitor DPI aware) over a frozen capture of the desktop; drag selects a rectangle,
      **Space** captures the window under the cursor, **Esc** cancels. Then restore the main
      window **without activating it** (the meeting app keeps focus).
- [ ] Capture with `BitBlt`/`Graphics.CopyFromScreen` from the frozen frame; encode PNG.
- [ ] The image always goes into the tray, regardless of the clipboard setting.
- [ ] No permission needed on Windows. Must work over Jump Desktop (verify, §10 W-I3).

### 4.4 Clipboard images

Same rules as Mac `ClipboardImages.swift`:

- [ ] Poll every 0.5 s while the Answers panel is visible, using
      `GetClipboardSequenceNumber()` (no clipboard open). The first poll only records the number.
- [ ] On change and only if `include_clipboard_images`: read `PNG` format, else `CF_DIBV5` /
      `CF_DIB`, else `CF_HDROP` files whose extension is an image. **Never read text.**
- [ ] ≤ 4 images per read, sources > 20 MB skipped, re-encode PNG with longest side ≤ 2048 px;
      tray holds ≤ 10; "Screenshot added — N in this prompt".
- [ ] Clear after send (`clear_clipboard_images_after_send`): on `started`, only if the
      sequence number is unchanged since the read. Retry `OpenClipboard` briefly (contention,
      SPEC-WINDOWS §12.1).

### 4.5 Documents and skills

- [ ] `DocumentText`: `.pdf` via **UglyToad.PdfPig** (page text, trimmed, pages joined by a
      blank line; no text at all → "This PDF is a scanned image; paste the text instead.");
      `.md .markdown .txt .text` as UTF-8, falling back to Windows-1252; CRLF/CR → LF; trim. Any
      other extension → "“.ext” files aren't supported…". No `.docx` (decided).
- [ ] Library on disk exactly as Mac: `interview\library\index.json`,
      `documents\<slug>\{original.<ext>, text.txt}`, `skills\<slot>\SKILL.md` + reference files.
- [ ] Skill slots `discovery-cv`, `discovery-jd`, `apply-instruction`, `live-coding-design`:
      load a folder with `SKILL.md` or a single `.md`; replaced content removed; folder renamed to
      the slot; ignored files reported.

**P2 acceptance (on the G15):** sign in, list models, show usage, run a skill turn and an Ask
from a test harness; both hotkeys fire while Zoom/Teams has focus; a region screenshot and a
clipboard image each reach a turn; the `codex` process dies when the app is killed.

---

## 5. W-P3 — Interview mode UI (WPF)

Match the Mac's flow and wording; use the existing Windows shell, theme and controls.

### 5.1 Mode chooser and privacy

- [ ] On **every launch** the main window shows two cards, **Caption only** ("…Nothing leaves
      the device.") and **Interview** ("Captions plus an AI coach…"). The last-used one
      (`interview.mode`) is marked "Last used" and is the default (Enter).
- [ ] Choosing Interview with `privacy_acknowledged == false` shows the notice: what is sent
      (CV, JD, skills on a skill step; the interviewer's recent words on each Ask; clipboard
      screenshots only if turned on; **screenshots taken with the Screenshot hotkey** — the Mac
      notice omits this, Windows includes it); Codex is locked down and may search the web.
      **Use Interview mode** / **Cancel**.
- [ ] Header **Change mode** button (shows current mode), enabled only when not recording,
      nothing unsaved, not saving.
- [ ] Amend SPEC-WINDOWS §1.2: the privacy invariants hold **in Caption only mode**.

### 5.2 Stage 1 — Preparation (fills the window; no captions, no transport, no hotkeys)

Top to bottom, as Mac `InterviewSetupSection.swift`:

- [ ] Codex status row (only when not ready / signing in / failed): status text, **Sign in…** /
      Cancel / **Check again**, lowest usage line (orange < 20 %).
- [ ] Missing-skills banner → "Open Settings → Interview → Skills".
- [ ] **Interview**: Interviewee, Company, Step (1–20); live preview
      "Session name: who-company-step-yyyy-MM-dd".
- [ ] **① Discovery CV**: picker of library CVs + **Upload CV…** (PDF / .txt / .md).
- [ ] **② Discovery JD**: multi-line paste box.
- [ ] **③ Apply instruction**: Intro / Tech / Behavioral (sent as `intro` / `tech` / `cultural`).
- [ ] **④ Live coding & design (optional)** checkbox ("needs the Tech mode").
- [ ] Model row: Model ("Recommended (gpt-6-luna)" + listed models), Preparation effort,
      Answer effort (efforts from the model, else low/medium/high). Same config keys as Settings.
- [ ] **Start preparation** / **Prepare again** + "Prepared" seal; running "Preparing — <step>…";
      failure "Stopped at <step>" with **Continue** and **Start over**. Per-part ✓ / ⚠ / spinner.
- [ ] Blockers in order: skills, interviewee, company, CV, JD, mode, live coding without Tech.
- [ ] "Start over" link (unstarted record) → confirm → delete rows + `thread/archive`.
- [ ] Footer **Skip preparation** / **Back to the interview**.
- [ ] Prefill from the last interview: interviewee, CV (if it still exists), profile, live coding.
- [ ] Running a step: `/<slug>` (`/apply-instruction <profile>`); definition sent only the first
      time per thread; CV → `MY CV` attachment + snapshot into the record; JD → `JOB DESCRIPTION`;
      effort `prep_reasoning_effort`.
- [ ] **Start** stays disabled in Interview mode until all four skills are loaded (Mac
      behaviour, including the optional slot). **[DECISION, owner 2026-10-02]** kept: the user
      loads all four skills manually in Settings → Interview → Skills. No bundled skills.

### 5.3 Stage 2 — Interview layout

- [ ] Header: record name chip; coach chip ("Coach not started" / "Starting coach…" /
      "Coach ready · <profile>" / "Coach unavailable"); usage chip when < 20 %; hotkey-unavailable
      chip; **Preparation** button (only when not live); **Captions** show/hide; font −/+
      (applies to answers too); compact Start/Pause/Stop when captions are hidden.
- [ ] Layout from `panel_layout`: `automatic` = side by side at ≥ 820 px, else stacked.
      Side by side: captions first, default 58 %, minimums 220 / 300. Stacked: **answers on top**,
      default 60 %, minimums 160 / 120. Draggable splitter, double-click resets. Never tabs.
      Splitter shares and captions-hidden are per-machine UI state (store in the Windows `ui`
      group, not the shared `interview` group).
- [ ] Caption column keeps the existing Windows caption view + transport bar. In Interview mode
      Stop reads **End interview**.
- [ ] Start names the session from the interview details (else `<prefix><timestamp>`), links
      `capture_session_uuid`, hides the preparation, opens the thread in the background.

### 5.4 Answers panel and asking

- [ ] Header: "Interview", Intro / Tech / Behavioral (each runs `/apply-instruction` at once;
      menu when narrow), **Live coding** (enabled only when Tech is active), Stop while streaming.
- [ ] Orange status line. Cards auto-scroll; latest expanded, others toggle. Card: "Q: …" (from
      the model's `**Q:**` line, else the question / "Skill: <cmd>"), status badge, kind label,
      "Thinking…", **Markdown** (headings, bullets, numbered lists, bold/italic/code — the
      `MarkdownText.swift` subset; a small hand-written FlowDocument builder, no new
      dependency), Interrupted/Failed badge, TTFT "x.x s", **What was sent** (text + image
      thumbnails from the DB), **Regenerate** (latest, not streaming, not a skill), **Copy answer**.
- [ ] Screenshot tray: "N screenshot(s) in this prompt", Clear, 72×48 thumbnails with ×.
- [ ] Bottom bar, three densities: Ask button "Ask F8" with image badge + camera + type field →
      icon-only Ask → type field in a popup.
- [ ] **Coach input**: "Type to the coach…", grows from 1 to 6 lines then scrolls; **Enter** and
      **Ctrl+Enter** send, **Shift+Enter** new line (Windows convention; Mac uses Option-Return);
      Send enabled with text or a tray image.
- [ ] Ask: `AskSelection` over committed finals + live interim + pause-aware audio ms; empty and
      no images → "Nothing new since your last ask" (no thread opened); thread fails → system
      beep + reason; model without image input → text only + notice.
- [ ] Typed / Regenerate / follow-up turns built exactly as Mac `InterviewController.swift:675-691`.
- [ ] `busy_policy`: `interrupt` (stop, wait ≤ 2.5 s, send) or `queue` (one queued request;
      Ask + Ask merge; other kinds replace).
- [ ] Each turn: images saved to `interview_images` + temp file in `interview\outbox\` (deleted
      after the turn); turn persisted on every state change; answer turns use
      `reasoning_effort`, skill and summary turns `prep_reasoning_effort`.

### 5.5 Ending, summary, replay

- [ ] **End interview**: rename the session to the current details → normal save (P0 order) →
      set `ended_at`, `session_id`, `transcript` (finals), `sessions.mode='interview'`; drop queued
      request; wait ≤ 30 s for a streaming answer, then interrupt (2.5 s grace).
- [ ] Sheet: "Interview ended — The transcript is saved. What would you like the coach to do?"
      **Summarize the interview** · follow-up box ("e.g. Draft a thank-you email…") + Send ·
      **Not now** (Esc). Disabled with a note if the thread never opened. No automatic summary.
- [ ] Summary streams into `summary_text`, `summary_status` `done`/`failed`, `summary_completed_at`.
- [ ] Replay view (interactive after End, read-only from Sessions): toolbar (state, "Profile: X",
      **Copy Q&A** = `QaMarkdown`), collapsible Summary with **Summarize interview** when not done,
      left pane Transcript / CV / JD switch (≥ 700 px; narrower → Conversation / Transcript
      switch), conversation cards (skill turns collapsed), follow-up bar in interactive mode.
- [ ] Launch sweep: turns still `streaming` → `failed("app closed")`.
- [ ] Recovery: after `recover()`, link an interview whose `capture_session_uuid` matches the
      journal and has no session; set `ended_at`, mode; rename "<name> (recovered)".

### 5.6 Sessions window

Replaces "open the `.txt` in the default editor" (`Win: MainWindow.xaml.cs:1084-1130`) and gives
SPEC-WINDOWS §7.1's read-only viewer. The existing sidebar stays as the quick list.

- [ ] **Ctrl+L** / toolbar button opens a Sessions window (980×640 default, 720×420 min,
      placement remembered). Double-click in the sidebar opens the session here.
- [ ] List: search (name + interviewee, company, interview name, CV title, JD text); Show
      All / Captions / Interviews; the six sorts; interview rows show an icon and
      "interviewee · company · step N"; context menu Rename… / Show in folder / Delete…; Delete key.
- [ ] Delete: interviews → "Session and Interview Data" (default) / "…, Interview Data and
      Transcript File" / "Session Only (keep interview data)"; captions → "Session Only" /
      "Session and Transcript File". Interview data delete cascades and calls `thread/archive`.
      Keep Windows' Recycle-Bin behaviour for files.
- [ ] Detail: name, created, **duration**; **Open in Interview Panel** (blockers: "Stop the
      current session first" / "Wait until the speech model is ready" / "Wait for the current
      answer to finish"; confirm "Discard the current preparation?"); Show in folder; Delete…;
      details grid (Interviewee, Company, Step, Role, Mode, CV, Model · effort, Questions); then
      the read-only replay, or for caption sessions the transcript **rebuilt from
      `session_segments`** honouring `show_timestamps`.

### 5.7 Settings

Add pages to the existing Settings window (Windows keeps its own page list; these are new):

- [ ] **Interview**: Skills (four slot rows: status, slot name, "title · N characters" /
      "Not loaded", Load…/Replace…, remove, ignored-files notice); Model & answers (Model,
      Answer effort, "Skill steps & summary effort", Answer length Short/Medium/Long); Layout
      (Automatic / Side by side / Stacked); Privacy ("Show the notice again next time").
- [ ] **Asking**: hotkey recorders for Ask and Screenshot (Record…, Esc cancels, reset to F8/F9,
      refuses duplicates and plain typing keys, shows registration errors, Fn-lock hint for bare
      F-keys); Send mode, Sentences (1–20, only for `last_sentences`), At most N words
      (50–2000 step 50), busy policy; Screenshots (auto-add from clipboard; clear after send,
      disabled unless the first is on).
- [ ] **Prompts**: Custom instructions (appended to the base instructions as "MY INSTRUCTIONS"
      when the thread opens — word the caption that way, not "prefilled").
- [ ] **Codex**: status row, Codex path ("Auto-detect"), Check again, **Sign out…** (confirm;
      only when ready; signs out LocalCaption's own `CODEX_HOME` only); Plus usage per window
      (label, % left, bar, reset line), Plan, Updated, Refresh, "Sign in to see your ChatGPT usage."
- [ ] Settings reopens on the last page used.

**P3 acceptance:** the Mac "Interview Mode Guide" (`docs/LocalCaption — Interview Mode Guide.pdf`)
can be followed step by step on Windows with only key-name differences.

---

## 6. W-P4 — Caption-side parity

Smaller gaps in Caption only mode. Each is independent.

| # | Item | Mac | Windows now | Do |
|---|---|---|---|---|
| C1 | Recovery prompt per session | sheet: each journal with time + segment count, **Recover & Save** / **Discard**, **Discard All** (`LC/UI/RecoveryView.swift`) | one dialog, "Save them" / "Not now"; `AppEnvironment.Discard` never called — unwanted journals return every launch | port the per-session sheet |
| C2 | Retry model load | **Retry** under the error (`SessionController.retryPrepare`) | none; restart or change model | add Retry |
| C3 | Retry capture after failure | **Retry capture** + **Retry save** | `ResumeAsync` allows it but the button is disabled | enable Resume when Failed with unsaved data; add Retry save |
| C4 | "Saved ✓ Show in folder" after Stop | inline link | phase just reads "Saved" | add link using `SavedTranscriptPath` |
| C5 | Session name prefix field | Settings → General | config only | add field |
| C6 | Transcript folder picker + writability check | Change… + check | free text, no check | folder picker + write test |
| C7 | Range limits | silence 200–2000 ms step 50; max utterance 5–60 s | unbounded `int.TryParse` reaches the segmenter | clamp in UI **and** in `ApplyTuning` |
| C8 | Duration in session list | shown | not shown | add (sidebar + Sessions window) |
| C9 | Font size range | 10–48 | 12–32 — clamps Mac configs | widen to 10–48 |
| C10 | `distil-large-v3` final model | offered | not in `ModelCatalog`; a Mac config naming it fails to download | map it to its GGUF if one exists, else fall back to `large-v3-turbo` with a notice instead of failing |
| C11 | Minimum window | 360×240 | 400×320 | match if the WPF layout allows (SPEC-WINDOWS §7.3) |
| C12 | Auto-copy selection | toggle exists, does nothing | nothing | SPEC-WINDOWS §19 calls this trivial on Windows: implement it here (copy on mouse-up selection in the caption view when `clipboard.auto_copy_selection`) |

Not gaps (decided, keep as is): Settings as a Save/Cancel dialog (Windows' quick bar is the live
path), transport bar wrapping instead of shedding labels, large-v3-turbo default, the `ui` pin
and background-only opacity, no permission screens.

---

## 7. Shared-format rules for everything above

- `config.json`: Windows must keep reading and writing every Mac key it knows; add new keys in
  the same alphabetical order. Mac drops Windows-only keys on write (`asr.backend`, `audio.*`,
  `send`, `shortcuts`, `ui`, …) — accepted; record it in SPEC-WINDOWS §9.2.
- Windows `ReadOptions` allow comments and trailing commas; the Mac does not. Windows must
  **write** strict JSON (it already does) so a Windows-written file never triggers the Mac repair path.
- DB: Windows writes only Mac-known tables/columns in shared tables. Windows-only data goes in
  `config.json` `ui`/`send`/`shortcuts`, never in new columns of shared tables.
- Paths stored in the DB (`transcript_file`) are platform-specific; code must treat a
  non-existent path as "no export", never as an error.

---

## 8. Build order and estimate

```
W-P0  DB interop + v4 + DB-first save + config keys + auto-copy fix + .gitattributes   Mac (Core) + G15 (save path)   2–3 d
W-P1  Interview Kit port + vectors (hotkey, ask, prompts, codex, records, library, store) Mac                           5–7 d
W-P2  Codex process, hotkeys, region screenshot, clipboard images, PDF/skills          G15 (written on Mac)          5–7 d
W-P3  Interview UI: chooser, preparation, layout, answers, end/replay, Sessions, Settings G15                          8–12 d
W-P4  Caption-side parity C1–C12                                                       G15                            2–3 d
W-P5  Acceptance on the G15 (§11), docs                                                G15                            1–2 d
```

**Critical path:** `P0 → P1 → P2 → P3 → P5`. P4 can go anywhere after P0. Total ≈ 4–6 weeks.

---

## 9. Mac behaviours **not** to copy (and specs to correct)

Port the Mac's behaviour, not its defects:

1. Opening Settings → Codex starts a `codex` process even in Caption only mode → Windows: never (§4.1).
2. `shutdown()` is never called; `codex` outlives the app → Windows: Job Object (§4.1).
3. The privacy notice omits hotkey screenshots → Windows: mention them (§5.1).
4. Silent model fallback in `resolvedModel` → Windows: show a one-time status line when the
   configured model isn't listed and another is used.

Stale spec text to fix when this lands (the code is right, the spec is wrong): SPEC-11 config
table (missing `screenshot_hotkey`, `panel_layout`), §SQLite interop claim, "clipboard read only
on an Ask" (it is polled), records-on-disk description; SPEC-12 interface (`model:`, `slow`,
login/logout/archive); SPEC-13 mode picker, library UI, step order, prefill; SPEC-14 hotkey
activation, F9 screenshot, 2.5 s interrupt wait, type-box keys, 4-per-read cap; SPEC-15 one-pane
layout, minimums, delete UI, history location, Settings table, "Generate summary" wording;
SPEC-WINDOWS §7.3/§20 and `windows/STATUS.md` (pin and opacity are back in a different form;
sort is no longer fixed).

---

## 10. Risks to verify on the G15

| ID | Risk | Check |
|---|---|---|
| W-I1 | Codex's read-only sandbox behaves differently on Windows | the lockdown already rests on disabled features + the tool guard + empty cwd; run the `codex/events` tool-guard case live and a prompt that asks it to read a file |
| W-I2 | Laptop Fn-lock: F8/F9 send media keys | show the Fn hint in the recorder; test on the G15 keyboard |
| W-I3 | Global hotkeys and the region overlay over Jump Desktop | test both from the remote client; if the hotkey doesn't reach the host, the on-screen Ask button is the fallback |
| W-I4 | `codex.cmd` shim resolution across npm / Volta / Scoop | test each locator path; log which one won |
| W-I5 | Slug folding differs between Foundation and .NET | the §3.1 vectors decide |
| W-I6 | Hotkey clash with Windows `shortcuts` (copy/send last question default to F8/F9) | **[DECISION, owner 2026-10-02] not handled** — the user sets hotkeys manually. No defaults change, no clash detection beyond the existing "unavailable" state; the Ask button still works |

---

## 11. Acceptance (feature level, on the G15)

1. A Mac DB and `config.json` moved to Windows open with every session, transcript, interview
   and setting intact; moved back, the same. Both suites green from the same `testdata/`.
2. Caption only mode: no `codex` process, no clipboard read, no network beyond the model
   download (network monitor, SPEC-WINDOWS §17.13). Auto-copy off → clipboard untouched.
3. Interview mode end to end over Jump Desktop: choose mode → acknowledge notice → load four
   skills → prepare (CV upload, JD paste, profile) → Start → F8 during a Zoom call streams an answer
   with first words < 3 s → F9 region screenshot attaches → Coach input sends → switch to Tech and
   run Live coding → End interview → Summarize → follow-up → open it again from the Sessions window
   and continue the thread.
4. Kill the app mid-answer: on relaunch the session recovers, the interview links to it, the
   streaming turn shows "app closed", and no `codex` process is left running.
5. Everything in §6 C1–C12 behaves as the Mac column says.
