# SPEC-14 — Live ask: hotkey, selection, screenshots, Answers panel

**Status:** ⬜ Not started · **Step:** 4 of [SPEC-11](SPEC-11-interview-assist.md) · **Depends on:**
SPEC-11, SPEC-12, SPEC-13 (a prepared thread)

> During the interview the user presses the **Ask** hotkey (default **F8**) or the Ask button.
> The app takes the interviewer's recent words, adds any screenshots on the clipboard if that
> is enabled, sends them as one turn on the interview's thread, and streams the answer into the
> **Answers** panel. The user can also type to the coach or use quick-prompt buttons, all on the
> same thread.

---

## Global hotkey

The interview runs in Zoom/Meet/Teams, so the key must work **when LocalCaption is not focused**.

- [ ] Registered while the Active Session screen is in **Interview** mode (prepared or not);
      unregistered in Caption only mode, after Stop, and on quit.
- [ ] **macOS:** Carbon `RegisterEventHotKey` + `InstallEventHandler`. Needs **no** Accessibility
      or Input Monitoring permission. Map the SPEC-11 grammar to `kVK_*` codes and
      `cmdKey | optionKey | controlKey | shiftKey`.
- [ ] Registration failure (e.g. `eventHotKeyExistsErr`, another app owns the combination) →
      the Interview header shows "Hotkey F8 unavailable — change it in Settings"; the on-screen
      Ask button still works.
- [ ] **Mac F-key caveat:** on Apple keyboards F8 is the play/pause media key; apps receive F8
      only with **fn** held, or with *System Settings → Keyboard → "Use F1, F2, etc. keys as
      standard function keys"* on. When the hotkey is a bare F-key, Settings shows this hint.
- [ ] A press before the thread is Ready → status "Still preparing…" and a short system beep;
      nothing is sent or queued.
- [ ] Settings field: **Record shortcut** — captures the next key combination with a local key
      monitor, validates it with the shared parser, writes the canonical string to
      `interview.hotkey`, re-registers. **Reset to F8** button.

## What gets sent

A pure Kit function, shared with Windows by vectors (`testdata/ask/`):

```swift
AskSelection.select(
    segments: [Segment],        // committed finals: text, tStartMs, tEndMs
    interim: String,            // the live provisional line at press time ("" if none)
    mode: SendMode,             // .sinceLastAsk | .lastSentences(n)
    mark: AskMark?,             // where the previous ask ended (nil = first ask)
    pressAudioMs: Int,          // recorded audio time at the press (pause-aware)
    maxWords: Int
) -> (text: String, newMark: AskMark)

struct AskMark { let audioMs: Int; let interimWasSent: Bool }
```

**`since_last_ask` (default):**
- Take committed segments with `tStartMs ≥ mark.audioMs`.
- A segment that **straddles** the mark (`tStartMs < mark.audioMs < tEndMs`) is included **only
  if** `mark.interimWasSent == false`. If the interim was sent last time, that utterance counts
  as already asked.
- Append the current `interim` (if any).
- First ask (`mark == nil`): everything so far.

**`last_sentences(n)`:** `Sentences.lastN(committedText, appending: interim, n)` — the existing,
already-vectored function (`testdata/sentences/`).

**Both modes:**
- Keep only the **last** `max_words` words (the question is at the end).
- `newMark = AskMark(audioMs: pressAudioMs, interimWasSent: !interim.isEmpty)` — updated in both
  modes, so switching mode mid-interview behaves.
- Including the interim is what makes the key fast: the final decode lags ~2 s behind speech,
  and the user presses right as the interviewer stops.
- **Known limitation:** words spoken *after* the press in the same utterance are counted as
  already asked. Acceptable — the user presses after the question ends.

**Empty result and no images** → nothing is sent; status "Nothing new since your last ask".

## Clipboard screenshots (opt-in)

Only when `interview.include_clipboard_images` is on. This is the **only** clipboard read in the
app, and only in Interview mode (SPEC-11 §Privacy).

- [ ] On Ask, read `NSPasteboard.general` items. Take an item if it has image data (`public.png`,
      `public.tiff`, `public.jpeg`, `public.heic`) or is a file URL whose type conforms to
      `public.image`. **Never read text** from the clipboard.
- [ ] At most **4** images. Each re-encoded to PNG with the longest side ≤ 2048 px; skip any over
      20 MB source size (with a note).
- [ ] Model doesn't accept images (`acceptsImages == false`) → send the text only, status "This
      model can't read images".
- [ ] Save as `attachments/<turn>-<n>.png` in the interview folder; send as `localImage` with the
      absolute path. (The attachments folder is outside Codex's `cwd` — the workspace stays empty.)
- [ ] **Removal after send** (`clear_clipboard_images_after_send`, default on): once `turn/start`
      is **accepted** (not before — a failed send must not lose the screenshot), and only if
      the pasteboard `changeCount` is unchanged since the read (the user hasn't copied anything
      new):
  - if every item was an image → clear the clipboard;
  - otherwise → rewrite the clipboard with the non-image items' data copied back **verbatim**
    (never inspected, logged or sent).
- [ ] Image-only ask (no new speech, ≥ 1 image) is allowed; see the message template.
- [ ] While the setting is on, the Ask button shows a badge with the image count, from a 0.5 s
      poll of `changeCount` and item **types** only.
- Screenshot-to-clipboard: macOS ⌘⌃⇧4; Windows Win+Shift+S — both land on the clipboard, so the
  flow is the same on both platforms.

## Message templates (Kit `InterviewPrompt`, golden-tested)

| Kind | Turn text |
|---|---|
| `ask` | `INTERVIEWER SAID:\n"""\n{text}\n"""` and, with images, a final line `({n} screenshot(s) attached.)` |
| `ask`, image-only | `INTERVIEWER SAID:\n(no new speech — see the attached screenshot(s).)` |
| `regenerate` | `Give me a different answer to that last question.` (latest card only) |
| `typed` | the typed text, unchanged |
| `quick` | the quick prompt's `text`, unchanged |

## Busy policy

One turn at a time per thread (SPEC-12). When a new Ask/typed/quick arrives while an answer is
streaming:

- **`interrupt` (default):** compute the selection now (mark advances), `turn/interrupt`, wait for
  the `interrupted` event (≤ 2 s, then proceed anyway), send the new turn. The cut-off card stays
  in the list marked *Interrupted*.
- **`queue`:** compute the selection now, hold it as the single queued turn; a further Ask
  **merges** its text into the queued one. Sent when the current turn completes.

## Answers panel

Right side of the caption area in Interview mode (SPEC-10's Key points slot). When
`show_key_points` is on, the right pane splits vertically: Answers (top ⅔) and Key points (⅓).

- [ ] **Cards**, oldest at the top, autoscrolled to the newest. The **latest card is expanded**;
      older cards collapse to their `**Q:**` line and expand on click.
- [ ] Card content: the answer as rendered Markdown (inline + lists), streaming. States:
      *Thinking…* (before first text), streaming, done, *Interrupted*, *Failed — reason*
      (`usageLimit` shows the reset time).
- [ ] Card actions: **Copy answer**, **Regenerate** (latest only), **Sent text** (a popover with
      exactly what was sent, plus image thumbnails — so the user can see what the model heard).
- [ ] A **Stop** button while streaming (interrupt).
- [ ] Bottom bar: **Ask** button (tooltip shows the hotkey; disabled until Ready), the
      quick-prompt buttons from `interview.quick_prompts`, and a text field *"Type to the
      coach…"* (Return sends, Shift-Return = newline).
- [ ] Text uses `caption.font_size` and follows the font ± controls; respects window opacity.
- [ ] Min width 280, ideal 380 (compact behaviour in SPEC-15).

## Recording

- [ ] Each turn is appended to `interview.json` when sent (`status: streaming`) and updated on
      completion/interrupt/failure, with `question` = the sent text, `audio_from_ms` /
      `audio_to_ms` (mark span), `images`, `answer`, `ttft_ms`, `total_ms`.

## Latency budget

| Step | Budget |
|---|---|
| Hotkey → `turn/start` written (text only) | ≤ 150 ms |
| + image encode (per image) | ≤ 300 ms |
| `turn/start` → first answer text | S0 result (target p50 ≤ 3 s) |

## Acceptance

- `testdata/ask/` vectors pass: first ask; since-last-ask with and without a straddling segment
  and with/without `interimWasSent`; `max_words` truncation keeps the end; last-sentences mode;
  empty result.
- During a live call, with another app focused, F8 sends the interviewer's last question and an
  answer starts streaming in the panel.
- Pressing F8 twice quickly with `interrupt` → the first card shows *Interrupted*, the second
  answers the newer text; with `queue` → both complete in order.
- With images enabled: a ⌘⌃⇧4 screenshot is sent with the next Ask, saved under `attachments/`,
  and removed from the clipboard; if the user copied something else in between, the clipboard
  is left untouched.
- With images disabled, the app performs **no** pasteboard reads (verified by code review: one
  call site, guarded by the setting and the mode).
- Typed messages and quick prompts go to the same thread and appear as cards.
- Hotkey conflict shows the header warning; the on-screen Ask still works.
