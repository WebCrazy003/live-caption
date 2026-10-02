# LocalCaption Windows port — status

As of 2026-10-02 · Live copy: https://claude.ai/code/artifact/6e02595d-07eb-459f-8656-dce5a9a16783

## Summary

Work on the Windows version is paused at the owner's request. The shared groundwork and the interview logic that can be tested on the Mac are done, committed and pushed; nothing Windows-only (hotkeys, screen capture, clipboard, screens) has been started.

- **Committed and pushed to `main`:** everything, including the interview flow logic and the status doc (`docs/WINDOWS-STATUS.md`).
- **Spec:** `specs/SPEC-16-windows-parity.md` lists every gap item and a progress table.
- **Tests on the Mac at the last commit:** Core 143, speech 39 and Interview 132 pass; the full Windows solution compiles with no warnings; the Mac's own Kit suite (102) passes.

## Done and committed

Everything below is on `main`, newest first, and is pushed. Each commit was built and tested on the Mac before it went in.

| Commit | What it delivers | Tests |
| --- | --- | --- |
| `b1d1df3` | SPEC-16 progress table; database notes corrected in SPEC-11 and SPEC-WINDOWS | docs only |
| `369bc84` | New `LocalCaption.Interview` project: the Codex engine (find, launch, lockdown, sign-in/out, models, usage, crash handling) and the CV/skill library (PDF, Markdown, text, four skill slots) | Interview 59 pass; one live run against Codex 0.159.3 with a throwaway profile, signed out |
| `7d42846` | Interview records, library index, interview save/load in the database; `distil-large-v3` loads `large-v3-turbo` on Windows; 75 new shared test cases | Core 139, speech 39, Mac Kit 102 pass |
| `3f4c36f` | Hotkey grammar, ask selection, interview prompts, Codex message format | Core 119 pass |
| `4f20c7e` | One database for both platforms; captions saved in the database; old sessions imported; two missing settings keys added; auto-copy bug fixed; `.gitattributes` | Core 97 pass; Mac conformance pass |

Already pushed before this work: `6608d48` (merge of `windows/stage-b`) and `699431e` (SPEC-16).

## Interview flow logic (last commit)

The interview flow logic is ported, reviewed and committed with the status doc. It has no screens: the WPF layer binds to it on Windows.

- **Covers:** preparation, Ask, typed messages, regenerate, busy policy (interrupt or queue), End interview, summary, follow-ups, reopening a past interview, the startup sweep and the crash-recovery link.
- **Files:** `InterviewController.cs`, `InterviewFlowTypes.cs` and `InterviewRecovery.cs` in `LocalCaption.Interview`, plus six test files.
- **Wired in:** `AppEnvironment.cs` runs the startup sweep and the recovery link.
- **Review fixes applied:**
  - a failed clipboard clear can no longer leave an answer stuck as streaming;
  - clipboard read errors are retried;
  - wrong-thread calls fail at once;
  - a reload mid-answer cannot write into the wrong interview;
  - preparation edits raise change events;
  - the model-fallback notice has its own property;
  - targeted database queries replace full loads;
  - leftover screenshot files are cleared at startup.
- **Left for the WPF layer:** coalescing per-word refresh while an answer streams, and saving interview details on lost focus rather than per keystroke.
- **Leftovers:** five agent worktrees under `.claude/worktrees/` (untracked, safe to delete).

## Integration (committed, 2026-10-02 — not yet run on Windows)

The drafted Interview mode — the Win32 services (`Interview/Platform`) and the WPF screens
(`Interview/Views`, `Answers`, `Sessions`, `Settings`) — is now **wired into the app**. It builds
with no warnings on the Mac and the Core, speech and Interview suites pass; **none of it has run
on Windows yet.**

**Wired:**

- **Composition** — `App` builds `InterviewServices` after crash recovery: `CodexAppServerEngine`
  (app paths, `interview.codex_path` read per launch), `CodexService`, `InterviewLibrary`, the one
  `InterviewController` (with `ScreenCapture`, `ClipboardImages`, the system beep) and
  `GlobalHotkeys`; disposed on exit (hotkeys off, clipboard owner gone, codex shut down — the Job
  Object covers a hard exit). `LocalCaption.App` now references `LocalCaption.Interview` directly.
- **Caption only never touches Codex.** Codex is checked once when Interview mode is entered, or
  from a Settings → Codex button. Sessions-window deletes leave a thread unarchived (logged) in
  Caption only mode unless codex is already running (`CodexAppServerEngine.IsRunning`); its
  read-only replay hides Summarize in Caption only mode.
- **Main window** (`MainWindow.Interview.cs`) — the mode chooser on every launch (last mode marked);
  the privacy notice before the first Interview; header **Change mode** (only when nothing is
  recording, unsaved or saving); stage 1 preparation alone (no quick bar, captions, transport or
  hotkeys); stage 2 interview layout whose captions slot gets the *existing* caption column
  (caption view + transport bar, moved, not copied); **End interview** = rename → normal save →
  `SessionSavedAsync` → End-interview dialog → interactive replay above the transport bar. Start
  names the session from the interview details and calls `RecordingStarted`; Start waits for the
  four skills in Interview mode. Caption only mode keeps its layout (status strip under the
  captions); in Interview mode the status strip sits above the stage.
- **Hotkeys** — `GlobalHotkeys.Update(ask, screenshot, active)` with active = Interview mode chosen,
  preparation not showing, phase not Saved, Settings not open; re-evaluated on every refresh.
  F8 → Ask, F9 → screenshot; "F8 unavailable" chips; registration errors shown in Settings → Asking.
- **Sessions window** — Ctrl+L, a title-bar button, and double-click in the sidebar (the context
  menu's "Open transcript" stays the `.txt` export). Bounds in `ui.sessions_window`. List from the
  new `Store.InterviewListings()`; delete takes every interview of the session
  (`Store.Interviews(sessionId)`). **Open in Interview Panel** via the new
  `SessionController.CanOpenSaved` / `OpenSaved(record, segments)` (port of the Mac's).
- **Settings** — Interview, Asking, Prompts and Codex pages, edit-a-copy / Save / Cancel; skills and
  Codex buttons act at once; reopens on the last page (`ui.settings_page`).
- **Config** — Windows-only `ui` keys `interview_answer_share_stacked`,
  `interview_caption_share_wide`, `interview_captions_hidden`, `sessions_window`, `settings_page`.
- **Fixes** — a busy clipboard now throws `ClipboardBusyException` so the controller keeps the old
  sequence and retries (≤ 5 polls) instead of losing the image; font size 10–48 everywhere (C9);
  the crash-recovery path still links the interview to the recovered session.

**Check first on a real Windows PC** (in this order):

1. **Launch** — the app starts, the mode chooser shows with the last mode marked, Enter picks it;
   no `codex.exe` in Task Manager.
2. **Mode chooser** — Interview shows the privacy notice once (Cancel stays on the chooser);
   Change mode is disabled while recording and after a failed save, enabled again after Stop.
3. **Caption session** — Caption only looks and behaves as before: Start, Pause, Stop, status strip
   under the captions, quick bar, Copy, shortcuts (Space, Ctrl+C with/without a selection); still no
   `codex.exe`, clipboard untouched with Auto-copy off.
4. **Interview prep with skills** — load the four skills (Settings → Interview), Start stays
   disabled until then; CV upload (PDF), JD paste, mode; Start preparation runs all steps; Skip /
   Back to the interview; the status strip moves above the stage.
5. **F8 ask during audio** — with Zoom/Teams focused and audio playing, F8 streams an answer;
   first words < 3 s; Pause/Resume and the hidden-captions compact transport work.
6. **F9 screenshot** — the overlay covers every monitor, drag / Space / Esc behave, the main window
   comes back without stealing focus, the tray shows the image.
7. **Clipboard image** — with "auto-add" on, Win+Shift+S adds an image; copy while another app holds
   the clipboard (e.g. clipboard history open) and confirm it still arrives on a later poll.
8. **End + summary** — End interview names the session from the details, the dialog appears,
   Summarize streams into the replay, a follow-up works, Start begins a fresh interview.
9. **Sessions window** — Ctrl+L, the title-bar button and sidebar double-click; search, filters,
   sorts; Open in Interview Panel (blockers, discard-preparation prompt); delete choices (Recycle
   Bin, every interview of the session); in Caption only mode deleting an interview starts no codex;
   bounds remembered.
10. **Settings pages** — the four pages render, Save / Cancel, hotkey recorder (F8/F9 heard while
    Settings is open, duplicates refused), Codex page starts nothing in Caption only mode until a
    button is pressed, reopens on the last page.
11. **Windows 10 VM (1809+)** — everything above that does not need a GPU; MDL2 glyphs, no process
    loopback (endpoint capture), no crash on missing Windows 11 APIs.
12. **ARM64** — publish and launch the ARM64 build; codex locates and starts; hotkeys and overlay.
13. **150 % + 100 % dual monitor** — the screenshot overlay and selection land in the right physical
    pixels on both screens; the interview layout's densities and the Sessions window placement.

## Not started (needs the Windows machine)

Nothing below can be run or checked on the Mac, so all of it waits for the G15. Section numbers refer to SPEC-16.

| Area | Work | SPEC-16 |
| --- | --- | --- |
| Build check | Pull `main`, `dotnet build`, `dotnet test`, including the Windows-only test suite | §11 |
| Codex on Windows | Confirm npm's `codex.exe` launches, the Job Object kills it on exit, the read-only lockdown holds | §4.1, §10 |
| Win32 services | Drafted and wired (see *Integration*); run and verify F8/F9 global hotkeys, region screenshot, clipboard images | §4.2–§4.4 |
| WPF screens | Drafted and wired (see *Integration*); see them on screen and work through the checklist | §5.1–§5.7 |
| Caption-mode items | Per-session recovery, Retry buttons, "Saved — show in folder", name-prefix field, folder picker, detection-setting limits, durations in the list, minimum window size, auto-copy on selection | §6 (C1–C12; C9, C10 done) |
| Tests | Unit test for the auto-copy fix (Windows-only project) | §2.5 |
| Acceptance | One full interview, run locally on the G15 | §11 |

## Decisions recorded

The owner's decisions are written into SPEC-16 so they survive the pause.

- **Caption only mode never uses Codex** on Windows.
- **The user loads all four skills manually**; Start stays disabled until they are loaded.
- **Hotkeys are set manually by the user.** The F8/F9 clash with Windows' own shortcuts is not handled.
- **No remote testing.** Everything is checked locally on the G15, not over Jump Desktop.
- **The Mac does what it can verify; the G15 does the rest.** WPF screens are built on the G15, where they can be seen as they are made.
- **Technical choices made in the port:**
  - JSON keys sort ordinally rather than in the Mac encoder's locale order.
  - PdfPig reads PDFs.
  - A Mac config naming `distil-large-v3` loads `large-v3-turbo` on Windows.

## How to resume

Everything is pushed, so picking this up starts with the build check.

1. On this Mac, .NET 10 is in `~/.dotnet` but not on PATH. Prefix commands with `export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH`.
2. Mac-side checks: `dotnet build windows/LocalCaption.slnx -p:EnableWindowsTargeting=true`, then `dotnet test` on the Core, Asr and Interview test projects.
3. On the G15: pull, build, run all tests, then work through *Not started* in SPEC-16 order.

**Warning:** until the G15 runs this code, do not copy `localcaption.db` between the Mac and an older Windows build. Older Windows builds cannot open a Mac database.
