# SPEC-10 — Live AI Summary (on-device)

**Status:** ✅ Implemented (engine via **local mlx-lm server**; native MLX-Swift deferred — see
Implementation notes) · **Depends on:** SPEC-04 (finals via `SessionController.ingestFinal`),
SPEC-05 (Active Session layout), SPEC-01 (config) · **Extends:** SPEC.md §9 (UI) & §12 (config)
— this is a **new feature**, not in SPEC.md v2.0.

> **Progress:** built and running. A second, on-device model — a small **local LLM (1B, MLX)** —
> watches the live transcript and, **every ~50 words**, emits a short "what the other person is
> actually asking, in easy English" card in a panel to the **right** of the live captions.

## Implementation notes (what actually shipped)

- **Engine = local `mlx_lm.server` over `http://127.0.0.1` (localhost only).** The spec's native
  in-app MLX-Swift plan is **blocked on this toolchain**: `mlx-swift` can't build its Metal lib
  from a bare `swift build` (needs Xcode's build system), and the fixed `mlx-swift-lm` needs
  Swift 6.1 (machine has 6.0.3). So the 1B LLM runs in a sibling on-device process; the app posts
  to it. Still 100% on-device — nothing leaves the machine. Swap in native MLX / Apple Foundation
  Models later behind the same `SummaryEngine` protocol. Start it: `app/scripts/summary-server.sh`
  (also auto-started by `run.sh`).
- **Default model = 1B** (`mlx-community/Llama-3.2-1B-Instruct-4bit`) — spike measured B6 ~1 GB /
  B7 ~1.2 s (both green). B8 quality is the known risk (inconsistent, format-unreliable) —
  mitigated by the strict `SummaryCard.parse` normalizer.
- **Files:** `LocalCaptionKit/SummaryPrompt.swift`, `SummaryCard.swift` (+ `SummaryTests`, 10
  tests); `LocalCaption/Summary/SummaryEngine.swift`; `LocalCaption/UI/SummaryView.swift`; wiring
  in `SessionController`, `ActiveSessionView`, `SettingsView`, `Config` (summary group, no schema
  bump), `Info.plist` (localhost ATS), `run.sh` + `scripts/summary-server.sh`.
- **Verified:** `swift build` clean; **45 unit tests** pass; real prompt → server → parser
  produces clean cards; app builds/signs/launches. Live in-session visual is user-verified.

---

## Why (the user need)

The user's listening/understanding of fast English is limited. On calls, a client often gives a
long, winding block of context and **the point gets lost**. The captions alone don't help —
they're just as long and as fast as the speech. The user needs the captions **distilled, live,
into a few short lines of simple English** that say *what the client wants*.

So this is an **accessibility / comprehension aid**, not a note-taking feature. The bar is: a
non-native speaker can glance right and, in one read, get the point.

**Design consequences of that bar:**
- **Simple words, short lines.** Target CEFR A2–B1 vocabulary. No jargon, no long clauses.
- **The point first.** Lead with the ask/decision, not a chronological recap.
- **Content-paced, not clock-paced.** A new summary fires each time ~50 words of speech
  accumulate — so it keeps up during a dense monologue (the user's worst case) and stays quiet
  when little is said. It does **not** flicker on every final.
- **Easy English only** (confirmed with the user — no translation to a native language).

---

## Decisions (settled with the user)

- **Engine = local LLM via MLX**, fully on-device. Chosen to preserve the product's core
  principle — *nothing leaves the Mac* (STATUS.md "On-device only"). Cloud APIs were rejected:
  they'd send the call transcript off-device.
- **Trigger = every 50 words** of committed transcript (not a time cadence). When the running
  word count of finals reaches ≥ `words_per_summary` (default **50**), summarize that block, emit
  a card, **reset the counter, and start counting again**. Each block is summarized **on its own**
  (no prior-summary feed) — so summaries never drift or inherit an earlier mistake.
- **Runs on the user's current macOS 14 (Sonoma).** Apple's built-in Foundation Models framework
  needs macOS 26 and is therefore **out** for now (documented as a future path in Open decisions).
- **Language:** easy English only.

---

## Goal

While recording, keep a scrolling "Key points" panel to the right of the captions. Every ~50
words of speech, an on-device LLM adds one short card — in simple English — saying what the other
person is asking for. Fully on-device; never blocks or slows captioning.

---

## What should be done

### Engine (app target — like WhisperKit, not unit-tested)
- [ ] Add **MLX-Swift** dependency (`mlx-swift` + `mlx-swift-examples`'s `MLXLLM`) to `Package.swift`,
      as a dependency of the `LocalCaption` target only (keep `LocalCaptionKit` pure).
- [ ] `Summary/SummaryEngine.swift` — a **background `actor`** (MLX inference must not run on the
      main actor) that:
  - [ ] Loads **one resident instruct model** (default **`Llama-3.2-1B-Instruct-4bit`**; opt-in
        **`Qwen2.5-3B-Instruct-4bit`** for higher quality — see Open decisions/B6/B7). Download-once
        with progress (mirror WhisperKit's first-run download UX), offline after. This is the
        **only** new network use — same exception already granted to model download (STATUS.md).
  - [ ] `summarize(chunk:) async -> SummaryResult` — one generation per ~50-word block, greedy
        decode, `max_tokens ≈ 96`, temperature ≈ 0. Results hop back to `@MainActor`.
  - [ ] Serialize calls (never run two generations at once); expose `isGenerating`.

### Prompt + parsing (LocalCaptionKit — **pure, unit-tested**)
- [ ] `SummaryPrompt.swift` — build the system+user prompt from a **single ~50-word chunk** (no
      prior summary):
  - [ ] Instruct: *"You help a non-native English speaker follow a live call. In very simple
        English (short words, short lines), say what the other person wants in this part. Lead with
        the main ask. Do not invent anything not in the text."* Output a fixed shape (see **Output
        format**).
  - [ ] Prefer **extractive** phrasing — quote/echo the actual request line — over interpretation,
        to limit hallucination (the user can't verify against the audio; see B8).
- [ ] `SummaryResult.swift` + parser — parse the model's output into a struct; tolerate malformed
      output (fall back to showing raw lines, never crash).

### Trigger + integration (`SessionController`)
- [ ] Maintain `summaryWordBudget` and a `pendingSummaryText` buffer. In `ingestFinal`
      (`SessionController.swift:102`), append the final's text to the buffer and add
      `Filters.wordCount(text)` to a running count (reuse the same word counter already used for
      paragraph breaks at `SessionController.swift:136`).
- [ ] When the running count **reaches ≥ `words_per_summary`** (default 50): capture the buffer,
      **reset buffer + counter to zero**, and dispatch `SummaryEngine.summarize(chunk:)`. Do **not**
      split a final mid-way — the block is "≥50 words" (the final that crosses the line is
      included whole).
- [ ] On completion, append the parsed `SummaryResult` to `@Published var summaries: [SummaryCard]`
      (a growing list) and autoscroll the panel to the newest.
- [ ] **Overlap safety:** if a block completes while the previous generation is still running,
      **queue at most one** pending block; if a second arrives, merge it into the queued one (never
      build an unbounded backlog). With ~50 words ≈ ~20 s of speech vs a ~3–4 s generation, this is
      rare, but must be handled.
- [ ] Freeze with the clock: no new summaries while `paused`; the counter resumes on `resume()`.
- [ ] On `pause()`/`stop()`: **flush** — summarize the remaining `<50`-word buffer as a final card
      (so the tail of the call isn't dropped). Do **not** block the Stop→save path on it: `stop()`
      already awaits finalize + synchronous save (`SessionController.swift:92`); run the flush
      without gating the save, and mark the card as it lands.
- [ ] The summary is **display-only** — **not** written to the `.txt`/`.json` transcript or the
      journal (transcript stays a faithful verbatim record). *(Persisting the summary cards into
      the `.json` sidecar is an Open decision, default: no.)*

### UI (`ActiveSessionView` + new `SummaryView`)
- [ ] Change the caption area from a single `CaptionView` to an **`HStack`**: captions left,
      **`SummaryView` right** (`ActiveSessionView.swift:20`), with a `Divider()` between;
      header/transport bar unchanged.
- [ ] `UI/SummaryView.swift` — titled **"Key points"**; a **scrolling list of cards**, newest at
      the bottom, autoscrolled. Each card renders one `SummaryResult`:
  - [ ] A one-line **main ask** (bold), then up to `max_bullets` short bullets, then optional
        **"They want you to: …"** line.
  - [ ] States: *waiting for first card* (subtle "Listening…"), *generating next* (small spinner at
        the bottom; existing cards stay visible), *model off/failed* (quiet inline note, captions
        unaffected).
  - [ ] Honor `caption.font_size`; respect `window.opacity`.
- [ ] Toggle to **show/hide** the panel (Settings and/or a header button). When hidden, no LLM runs
      and captions get full width.
- [ ] Widen the default window when the panel is on — current default is **480×640**
      (`Config.Window`, `Config.swift:152`); a two-pane layout wants ~**820** wide. Preserve
      window-memory behavior (SPEC-08).

### Config (`LocalCaptionKit/Config.swift`)
- [ ] Add a `summary` group (merge-defaults on load like every other group — a new group needs **no
      schema bump**; existing schema-2 files load with these defaults via `decodeIfPresent`):
  - `enabled: Bool = true`
  - `words_per_summary: Int = 50`
  - `model: String = "llama-3.2-1b-instruct-4bit"`
  - `max_bullets: Int = 4`
- [ ] Settings UI: new **"Summary"** section (enable, words-per-summary, model picker, max bullets)
      — mirror the existing Settings sections in `SettingsView.swift`.

---

## Output format (what the LLM must return, per card)

Fixed, easy-to-parse, easy to read:

```
MAIN: <one short line — the single most important ask/point in this block>
- <short point in simple words>
- <short point in simple words>
WANT: <what they want you to do, or "-" if none>
```

- ≤ ~7 words per line where possible. No filler. If the block has nothing meaningful,
  `MAIN: (nothing important)`. The parser maps `MAIN`/`-`/`WANT`; unknown lines are ignored.

---

## Data flow

```
ingestFinal(text) ─► buffer += text ; words += wordCount(text)   (SessionController, @MainActor)
        │
   words ≥ 50 ? ─► capture block ; reset buffer + counter to 0
        │ yes
        ├─► SummaryPrompt.build(block)          (LocalCaptionKit, pure)
        ├─► SummaryEngine.summarize(block)      (MLX, background actor)
        ├─► SummaryResult (parsed)              (LocalCaptionKit, pure)
        └─► append to summaries[]  ─►  SummaryView list (autoscroll)   (@MainActor)

pause()/stop() ─► flush remaining <50-word buffer as a final card
```

Captions and summary are separate tracks in code — **but they share the Metal GPU** with
WhisperKit's final decode (see B_GPU); the engine runs off the main actor so the UI never blocks.

## New files

| File | Target | Tested |
|------|--------|--------|
| `LocalCaptionKit/SummaryPrompt.swift` | Kit (pure) | ✅ unit |
| `LocalCaptionKit/SummaryResult.swift` (+ parser) | Kit (pure) | ✅ unit |
| `LocalCaption/Summary/SummaryEngine.swift` | App (MLX) | manual/live |
| `LocalCaption/UI/SummaryView.swift` | App (SwiftUI) | manual/live |
| Edits: `Config.swift`, `SessionController.swift`, `ActiveSessionView.swift`, `SettingsView.swift`, `Package.swift` | — | — |

## Non-goals (this spec)

- **No cloud / API summarization** — on-device only.
- **No translation** to a native language (easy English only).
- **No diarization** ("who said what"); summarize the conversation as one stream.
- **No editing/export of summaries**; the card list is not persisted to disk.
- **No change to transcript fidelity** — captions/`.txt`/`.json` stay verbatim.
- **No cross-block memory** — each ~50-word card is independent (a pronoun referring back >50
  words is out of scope; accepted trade for zero drift).

## Blockers / risks

- **B6 — Memory pressure (measured against the real use case).** The LLM runs **beside a live
  video call** (Zoom/Teams 1–2 GB) *and* two WhisperKit models. On a 16 GB Mac a 3B‑4bit model
  (~2.5–3.5 GB resident) risks swap → audio glitches that degrade *ASR itself*. → **default to the
  1B model**; make 3B opt-in. Consider loading the LLM only while recording and unloading on stop.
- **B_GPU — GPU contention.** MLX generation and WhisperKit's final decode both use the Metal GPU.
  A sustained generation can delay a final decode and add caption latency — so "never blocks
  captions" is a claim to **measure**, not assume. 1B (shorter generations) reduces the window;
  validate p90 final-caption latency with the panel on.
- **B8 — Hallucinated asks you can't verify.** The user cannot check the summary against audio they
  don't understand, so a confident-but-wrong "what they want" is worse than nothing. Mitigate with
  extractive prompting, `temperature 0`, "do not invent," and a `(nothing important)` escape — but
  residual risk remains and must be validated on **real** call audio (ties to SPEC.md §19).
- **B7 — Latency vs. trigger (downgraded).** ~50 words ≈ ~20 s of speech ≫ ~3–4 s generation, so
  backlog is unlikely; the overlap queue-of-one is the safety net. Confirm real tok/s on-device.
- **"Easy English" is only prompt-deep.** Small models drift back to normal/technical vocabulary
  (echoing the very words the user doesn't know). No hard enforcement exists; assess on real calls.
- **Model download size** (~0.8 GB for 1B / ~1.8 GB for 3B) — one-time, offline after; same posture
  as WhisperKit, but a **second** download path (HF Hub via `swift-transformers`) the "no runtime
  network" audit must cover.

## Acceptance

- With a session recording, each **~50 words** of speech adds one **≤5-line, simple-English** card
  to the "Key points" list; the counter resets and counts the next 50. Existing cards never flicker.
- During a long **monologue** (few pauses), cards still appear (finals land at ≥ the 20 s
  `max_utterance_s` cap, so words accumulate) — the feature works in its intended worst case.
- Summarization **does not degrade captions**: p90 final-caption latency (SPEC-03) with the panel on
  is within tolerance of panel-off (**measured**, per B_GPU).
- **No new network calls** after the one-time model download (verify offline).
- Pausing/stopping flushes a final card for the remaining `<50`-word tail; no summaries while paused;
  Stop→save is not blocked by the flush.
- Toggling the panel off stops all LLM work and returns captions to full width.
- `LocalCaptionKit` stays pure: prompt-build + result-parse are unit-tested; `swift test` still green.
- Old `config.json` (no `summary` key) loads with summary defaults — no data loss, no schema bump needed.

## Open decisions

1. **Default model:** **1B** (recommended default — RAM/latency/GPU headroom) vs **3B** opt-in
   (better instruction-following, higher hallucination cost paid in RAM). Decide after measuring B6/B_GPU.
2. **Persist the cards** into the `.json` sidecar as a saved "call summary"? → default **no**.
3. **Future engine swap:** on macOS 26, allow **Apple Foundation Models** (zero-download, lower-RAM)
   behind the same `SummaryEngine` interface.
4. **Words-per-summary default (50).** 50 words ≈ ~20 s of normal speech → a card every ~20 s in
   calm conversation. If the user wants faster feedback, lower it (e.g. 60); if too noisy, raise it.
   Exposed in Settings so it's tunable without a rebuild.
5. **Real-time gap (unresolved by this trigger).** Cards still trail the live audio by a block +
   generation time, so this remains a *catch-up* aid, not a *respond-this-instant* one. If real-time
   turn-taking help is needed, add an on-demand "explain the last 30 s **now**" hotkey later.
6. **Split-attention.** Two live English panels may overload the user. Consider, after first use,
   making the summary the **primary** view with captions collapsible.
