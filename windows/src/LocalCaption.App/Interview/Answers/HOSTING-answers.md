# Answers views — wired (SPEC-16 §5.3–§5.5)

**Status: wired** into the main window by `MainWindow.Interview.cs` (with `InterviewServices.cs`
building the one `InterviewController`). Compile-checked on the Mac; not yet seen on screen.
The hosting instructions that used to be here are now that code; what follows is what it does,
and what still has to be looked at on a real Windows PC.

## How it is hosted

| Piece | Where it lives | Notes |
|---|---|---|
| `InterviewLayoutView` | `MainWindow.xaml` → `StageHost`, shown on the Interview stage | Its `Captions` slot receives the **existing** `CaptionColumn` (caption view + transport bar + nothing else), moved out of `CaptionHome` and back; never duplicated. `Controller` is set only while the stage is shown. |
| `CompactTransport` | built in code (`BuildCompactTransport`) | Icon-only Start / Pause / End interview on the same handlers, enabled like the bar's buttons. |
| `InterviewReplayView` | inside `CaptionColumn`, in place of the caption area | Shown after End interview (`phase == Saved && IsFinished`) with the transport bar still under it. `Interactive = true`, `Transcript = SessionController.CommittedText`. |
| `InterviewReplayView` (read-only) | Sessions window detail pane, via `SessionsHost.CreateReplay` | Own controller (`InterviewServices.OpenSaved`). `AllowSummarize` is a `Func<bool>` the host answers with "Interview mode?" — re-read on each repaint and on the click, and the Sessions window repaints an open replay (`Requery`) on a mode change — so it is false in Caption only mode (summarizing resumes the thread and would start Codex). |
| `EndInterviewDialog` | `MainWindow.OnStop` | After rename → normal save → `SessionSavedAsync(sessionId, committed text)`. |

Layout state (`interview_caption_share_wide`, `interview_answer_share_stacked`,
`interview_captions_hidden`) lives in the Windows-only `ui` config group. The header's −/+
goes through `MainWindow.SetFont` (10–48), which also sizes the captions and the replay.
Hotkey chips and the Ask / camera labels come from `GlobalHotkeys` state. Ctrl+K focuses the
coach box on the Interview stage.

## Not yet seen on screen (verify on the G15)

1. Densities are decided by measuring (`Ui.NaturalWidth`, the `ViewThatFits` stand-in): check the
   header and bottom bar switch cleanly at 100 / 150 / 200 / 300 % and in a ~360 DIP strip,
   with no flicker while an answer streams.
2. Glyphs (What was sent, photo, live coding, font −/+, hide, flag, summarize) on Windows 10 with
   Segoe MDL2 Assets as well as Windows 11.
3. `CoachInput` grows from 1 to 6 lines then scrolls; the placeholder sits on the first line.
4. The answer text is TextBlocks: not selectable (code blocks and the transcript are; Ctrl+C in
   them copies the selection, not "last N"). Copy answer and the Markdown context menu copy everything.
5. GridSplitter double-click does not also start a drag; arrow keys move it and the share is saved.
6. Auto-scroll: a new turn always scrolls to the end; a streaming answer is followed only while
   the list is already at the bottom.
7. Indeterminate `ProgressBar` ("Thinking…", "Writing the summary…") with the app's colours.
8. Theme switch while the panel is open (all brushes are `DynamicResource`; popups included).
9. The caption column moving between the caption-only home and the layout: caption text, scroll
   position, Follow and the Jump button survive the move; the status strip moves above the stage.
