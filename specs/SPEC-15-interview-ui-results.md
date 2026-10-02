# SPEC-15 — Interview window, results & history, Settings

**Status:** 🟢 Built (step 5) — responsive layout, header chips, summary on Stop, Results, history, recovery linking, full Settings → Interview; tests pass, app launches; on-screen check by the user pending. Deviation: the engine is not stopped after 30 s idle when Settings reads usage (it stays up until the interview's summary is done or the app quits) · **Step:** 5 of [SPEC-11](SPEC-11-interview-assist.md) · **Depends on:**
SPEC-11 – SPEC-14, SPEC-06 (session list), SPEC-07 (Settings), SPEC-08 (window)

> Finishes the feature: the interview screen stays usable when the window is small (buttons
> shrink to icons, panes stack), the interview is **summarized** on the same thread when it
> ends, every interview can be reopened from the session list, and **Settings → Interview**
> holds all configuration, including Codex sign-in and **Plus usage**.

---

## Interview screen

### Header (Interview mode)

`[● Recording] Interview · Acme — Senior iOS   [Ready ✓]  [⚠ 5h: 12% left]  [⚠ F8]   00:23:41`

- [ ] Mode/name chip; coach chip (*Coach not started / Starting coach… / Coach ready · <profile> / Coach unavailable*).
- [ ] Usage chip appears **only** when the 5-hour or weekly window has < 20 % left (tooltip: both
      windows and reset times).
- [ ] Hotkey chip appears **only** when registration failed.

### Responsive layout

The interview window often sits in a narrow strip beside the video call. Layout by available
width of the caption area:

| Width | Layout |
|---|---|
| ≥ 820 pt | Captions left · Answers right (side by side), draggable divider |
| 560 – 819 | **Stacked**: Answers on top (60 %), Captions below (40 %), draggable divider |
| < 560 | **One pane** with a segmented toggle *Answers / Captions*; an Ask switches to Answers automatically |

**2026-10-02 (owner):** the divider is draggable in both split layouts (side by side: captions
≥ 220 pt, answers ≥ 300 pt; stacked: answers ≥ 180, captions ≥ 100); double-click resets it
(58 % captions side by side, 60 % answers stacked). The header has a **Hide captions** button —
answers then fill the area at any width. Both the position and the hidden state are remembered.

**2026-10-02, later (owner):** in Interview mode Start / Pause / Stop, Auto-copy and Copy last N
sit under the caption column (at the bottom when captions are hidden or in the one-pane layout);
the font size is in the top bar, beside a labelled **Captions** show/hide button. The
**Preparation** button shows the preparation in full — filling the answer panel — or hides it
for the conversation; it hides itself when recording starts or an answer arrives.

**2026-10-02, latest (owner):** no tabs at any width — below 820 pt the panels are stacked
(answers on top) with a draggable border; the user narrows a panel or hides captions instead.
Start / Pause / Stop, Auto-copy and Copy last N live only inside the captions panel; with
captions hidden, compact Start / Pause / Stop move to the top bar. The *one pane with a toggle*
row above no longer applies.

**Settings → Interview → Layout** (`interview.panel_layout`: `automatic` | `side_by_side` |
`stacked`, default `automatic`; unknown values fall back) fixes the arrangement regardless of
width. The preparation shows the Codex row only when Codex isn't ready (not installed, signed
out, signing in, failed); account and usage details are in Settings → Codex.

### Buttons shrink to icons

Extend the existing transport-bar pattern (`BarDensity` + `ViewThatFits` in
`ActiveSessionView.swift`) to the Answers bottom bar and the header. Densities, roomiest first:

1. **Full** — labelled Ask (`Ask  F8`) beside the type box with Send.
2. **Icon Ask** — Ask becomes icon-only.
3. **Icon everything** — transport and answers bars icon-only; the type box collapses to a
   *keyboard* button that opens a popover with it.

Every control that loses its text keeps its **tooltip** and **accessibility label** (same rule as
today's transport bar). No control is ever clipped or hidden without an alternative.

## Ending the interview

- [ ] In Interview mode the Stop button reads **End interview**. It runs the existing save path
      unchanged (transcript `.txt` + `.json` + DB row); the save **never waits** for anything here.
- [ ] Then the session row gets `mode = 'interview'`, and the interview row gets `session_id`,
      `ended_at` and a copy of the transcript (SPEC-11 §SQLite).
- [ ] If an answer is streaming at Stop, let it finish (cap 30 s, then interrupt).
- [ ] Then a sheet asks (owner, 2026-10-02): **Summarize the interview**, or **send a follow-up
      prompt** (e.g. "draft a thank-you email"), or *Not now*. Nothing is summarized
      automatically (the `summarize_on_end` setting is gone).
- [ ] The session area then shows the replay view in **wrap-up** mode: the conversation keeps a
      follow-up box, and **Summarize interview** stays available. Follow-ups are `typed` turns on
      the same thread.
- [ ] Summarizing sends the **summary turn** with `prep_reasoning_effort`, streams into the replay
      view, writes `summary.md`, `summary.status = "done"`. Failure → `failed`, retryable; from
      history it first `thread/resume`s the same `thread_id`.

### Summary message (Kit `InterviewPrompt`, golden-tested)

```
INTERVIEW FINISHED
The interview is over. This is the full transcript of what the interviewer said:
"""
{transcript}
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
```

`{transcript}` = the session's committed transcript, last 15 000 words if longer.

### Replay view (read-only)

Shown in the Active Session area after Stop in Interview mode, and when an interview is opened
from the sessions list (owner, 2026-10-02: "restores transcript / AI Q&A history, read only").

- [ ] **Summary** on top (collapsible; streams in; **Generate summary** when missing or failed).
- [ ] Below, side by side ≥ 700 pt: **Transcript · CV · JD** (left, switchable; the CV is the
      `cv.txt` snapshot Discovery CV saved, so it survives library changes) and the **AI
      conversation** (right) —
      every turn as a read-only card: skill steps collapsed, questions and answers expanded,
      *Interrupted/Failed* marks, screenshot thumbnails, "what was sent". Narrower: a
      *Conversation / Transcript* toggle.
- [ ] Toolbar: Read-only badge, active profile, Copy Q&A (Markdown). Everything shown comes from
      the database, screenshots included.
- [ ] Old records made with the Prepare button show their briefing above the conversation.

## History

- [ ] **Session list** (SPEC-06): interview sessions show a briefcase icon; a filter
      *All / Captions / Interviews*. Search also matches the interview's company and role.
- [ ] **Opening** an interview session shows the replay view (the `TranscriptViewer` switches to it
      when `mode = 'interview'`). Caption sessions show the transcript as before.
- [ ] Opening a past session never disturbs the live one: the session and interview controllers
      belong to `AppEnvironment`, not to the session screen.
- [ ] **Delete** — from the session's context menu, the **⌫** key on the selected session, or the
      **Delete…** button in the session view's header — all open the same confirmation (SPEC-06),
      which offers, checked by default: *"Also delete the interview
      data (CV text, Q&A, screenshots)"* → deletes the interview's rows and archives the Codex
      thread (SPEC-12).
- [ ] **Crash recovery:** `interview.json` stores `capture_session_uuid` (the journal session id).
      When the existing recovery flow saves a recovered session, it links the matching interview
      record. On launch, any turn left `streaming` becomes `failed` ("app closed"); a saved
      interview with no summary shows *Generate summary*.

## Settings → Interview

A new section in `SettingsView`, mirroring the existing sections. All keys are SPEC-11's.

| Group | Contents |
|---|---|
| **Codex** | Status line (path, version, *Signed in as … (Plus)* / signed out / not installed / too old). **Sign in**, or the copyable `codex login` command (SPEC-12). Path override with *Choose…*. **Test** button (handshake + model list, shows the round-trip time). |
| **Usage** | 5-hour window and weekly window: remaining % bar, *resets at 14:20* (local time), plan type, last updated, **Refresh**. Read on open and on Refresh; if no engine is running, start one for the read and stop it after 30 s idle. |
| **Model** | Model picker from `model/list` (name + description; default `gpt-6-luna`); answer effort; prep/summary effort (both pickers list only the chosen model's supported efforts); answer length. |
| **Hotkey** | Record shortcut, Reset to F8, Mac F-key hint, registration status. |
| **Sending** | Send mode (*Everything since my last ask* / *Last N sentences* + stepper); max words; when busy (*Interrupt and answer the new question* / *Queue it*). |
| **Screenshots** | Include clipboard images (off by default, with a one-line privacy note); remove them from the clipboard after sending. |
| **Prompts** | Custom instructions (multi-line; appended to the coach's base instructions). |
| **Library** | *Open library…* (SPEC-13). |
| **Privacy** | What Interview mode sends to OpenAI, and *Show the notice again*. |

Out-of-range values clamp, as in SPEC-07.

## Acceptance

- At 900, 700 and 420 pt wide, the interview screen shows side-by-side, stacked and one-pane
  layouts; at the narrowest width every control is still reachable, with tooltips.
- Stop saves the transcript immediately; the summary streams in afterwards; `summary.md` has the
  five sections; the summary turn is on the same `thread_id` as the skill steps and all asks.
- Offline at Stop → transcript saved, summary *failed*, **Generate summary** later succeeds on
  the same thread.
- The interview appears in the session list with the icon and filter; reopening shows Summary,
  Q&A, Briefing and Transcript; deleting removes the interview's rows.
- Settings → Interview shows Codex status, both usage windows with reset times, and a model list
  read live from Codex; changing the hotkey takes effect without restarting.
- Killing the app mid-answer, then relaunching: the session recovers as today, the interview
  links to it, the in-flight turn shows *failed (app closed)*.
