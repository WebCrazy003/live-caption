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

## Not started (needs the Windows machine)

Nothing below can be run or checked on the Mac, so all of it waits for the G15. Section numbers refer to SPEC-16.

| Area | Work | SPEC-16 |
| --- | --- | --- |
| Build check | Pull `main`, `dotnet build`, `dotnet test`, including the Windows-only test suite | §11 |
| Codex on Windows | Confirm npm's `codex.exe` launches, the Job Object kills it on exit, the read-only lockdown holds | §4.1, §10 |
| Win32 services | F8/F9 global hotkeys, region screenshot tool, clipboard images (implement `IScreenCapture` and `IClipboardImages`) | §4.2–§4.4 |
| WPF screens | Mode chooser and privacy notice, preparation page, interview layout and answers panel, End interview and replay, Sessions window, Settings → Interview / Asking / Prompts / Codex | §5.1–§5.7 |
| Caption-mode items | Per-session recovery, Retry buttons, "Saved — show in folder", name-prefix field, folder picker, detection-setting limits, durations in the list, font range 10–48, minimum window size, auto-copy on selection | §6 (C1–C12; C10 done) |
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
