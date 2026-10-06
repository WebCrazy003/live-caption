# SPEC-18 — Accent mode: two models on the RTX, Codex correction live and at the end

**Status:** 🟡 step 1 (RTX agent) built and running on the RTX; S0.2 ✅ S0.3 ✅ (2026-10-07) · **Depends on:** SPEC-03 (ASR
lanes), SPEC-04 (finals, journal, save), SPEC-05 (caption view), SPEC-07 (Settings), SPEC-12
(Codex engine and lockdown) · **Extends:** SPEC.md §8 (ASR) and §17 (privacy) — changes a product
invariant, see [Privacy](#-privacy-this-mode-changes-a-product-invariant) · **Platform:** macOS
app + an agent on the user's RTX desktop. Windows app: out of scope (see [Windows](#windows))

> Captions for Nigerian-accented English are unusable today. A recording that a human could
> barely follow came out of `large-v3-turbo` roughly half wrong. What fixed it in the spike was
> three things together: **clean the audio and cut it at pauses**, **run two different
> recognisers**, and **correct the text with an AI that reads the conversation**. This spec adds
> an **Accent mode** — separate from today's Standard mode — that does all three **live**: the
> heavy models on the RTX desktop, everything else on the Mac, and a stronger correction pass
> over the whole transcript when the session ends.

---

## Why (the user need)

The user is in calls with Nigerian developers who speak fast, mix in Pidgin and talk over each
other, often in noisy rooms. Today's models drop words and turn names into ordinary words
("Claude" → "cloud", "Cowork" → "C cloud", "Paystack" → "pay stack"). The user needs:

- **Readable live captions** during such calls, a second or two behind speech.
- **A transcript afterwards that is close to what a careful human would write.**
- **Today's mode untouched** for standard-English calls — fully on the Mac, nothing leaves it.

## Evidence (spike, 2026-10-06 → 07)

One 12 min 40 s room recording (3+ speakers, Nigerian English + Pidgin + Polish read aloud),
scored against a careful reference transcript (`~/Downloads/transcript_2026-10-06_17-10-11.md`).
Scripts: [`../spike/rtx-asr-spike/`](../spike/rtx-asr-spike/) (`transcribe_rtx.py`, `fix_codex.py`).

| Version | Where | WER vs reference | Words right |
|---|---|---|---|
| `large-v3-turbo` (Standard mode's opt-in accuracy model) | Mac | 46.9% | — |
| Parakeet-TDT 0.6B v2, raw | Mac (MLX) | 41.2% | — |
| Parakeet + Whisper large-v3, raw, cut at pauses | RTX 4090 D | 55.6% / 58.5% | — |
| **Live:** both + Codex `gpt-6-luna` low, 15 s windows | RTX + Mac | **48.8%** | **71%** |
| Final: Parakeet only + Codex `gpt-6.1-sol` high | RTX + Mac | 42.0% | — |
| **Final:** both + Codex `gpt-6.1-sol` high, whole session | RTX + Mac | **39.9%** | **74%** |

Split by difficulty: on clear speech, live 39.8% vs final 36.4%; on the stretches the reference
itself marks unreliable, live 69.0% vs final 47.0%.

What the numbers say, and what they do not:

- **The correction pass does most of the work.** The same RTX transcripts go 55.6% → 39.9%.
  Raw models alone never get "Claude" right (1 of 15); the final pass gets 15 of 15.
- **The correction model matters:** `gpt-6.1-sol` high 39.9% vs `gpt-6-luna` medium 50.2%.
- **Two models beat one, modestly:** 39.9% vs 42.0%.
- **Cutting at pauses transcribes more of what was said** (including Polish and side talk the
  reference summarises), so raw WER *rises* although outputs read better. WER here ranks versions;
  it overstates the absolute error. The reference is edited, not verbatim.
- **Audio cleanup did not measurably help on this recording.** Parakeet on 300 ms pause cuts:
  band-pass + loudness levelling 55.6%, levelling only 56.6%, untouched 54.0%. Longer pieces did
  help: 1000 ms pause cuts 50.6% vs 300 ms 54.0%. Cleanup is still **on by default** (owner's
  decision — it targets noisy rooms, which this recording was not the worst case of); S0.4
  re-measures it on noisier recordings, and it can be switched off.
- **Speed on the RTX:** per piece (median 2.4 s of speech) Parakeet 123 ms p50 / 341 ms max,
  Whisper large-v3 311 ms p50 / 1.19 s max. Both fit live.
- **`codex exec` was too slow for live** (12.7 s per correction) because each call starts a new
  process. The app must use one long-lived app-server thread, as Interview mode does (1.3 s to
  first word in SPEC-12 S0). Unmeasured for correction — S0.1.
- **One recording.** The word list was written after hearing it. Everything above must be
  re-checked on more recordings — S0.5.

## Decisions (settled with the user)

| # | Decision |
|---|---|
| D1 | **A separate mode.** A switch **Standard ↔ Accent** chooses the speech pipeline. Standard is today's on-device WhisperKit path, unchanged. Accent is for non-standard English and noisy audio. |
| D2 | Accent mode's models run on the **RTX desktop** on the same LAN; the Mac captures, segments, shows captions, runs Codex and saves. |
| D3 | **Two recognisers per utterance**, both **selectable** from the RTX's model catalog. Defaults: Parakeet-TDT 0.6B v2 (primary) and Whisper large-v3 (secondary). |
| D4 | **Audio cleanup on by default** (band-pass + loudness levelling); switchable. |
| D5 | **Codex corrects**, through the engine Interview mode already uses (SPEC-12). **Model and reasoning effort are selectable** separately for live and final. Defaults: live `gpt-6-luna` / `low`, final `gpt-6.1-sol` / `high`. |
| D6 | **Live:** raw caption first, corrected text replaces it a few seconds later. **At Stop:** one pass over the whole session with the final model; its text is the saved default. |
| D7 | **No RTX, no Accent mode.** If the RTX cannot be reached, Accent mode cannot start; there is no on-Mac fallback. Standard mode is always available. |
| D8 | **RTX address is set in the UI**, with discovery on the LAN and a one-time pairing code, so connecting is a click and a code. |
| D9 | **Entering Accent mode starts the models on the RTX automatically**; they unload again when idle. |

### How the switch relates to Caption / Interview

The existing first screen chooses **Caption only** or **Interview** (`interview.mode`). The new
switch is a second, independent choice on the same screen and in the session top bar (while
nothing is recording): **Speech: Standard | Accent**. So Caption + Accent and Interview + Accent
both work — an interview with a Nigerian interviewer is exactly the case that needs it. The last
choice is remembered.

---

## ⚠ Privacy: this mode changes a product invariant

Standard mode keeps today's promise: "on-device only", nothing leaves the Mac. In **Accent mode**:

- **Audio leaves the Mac — to the user's own RTX desktop on the LAN**, never to the internet.
  Plain HTTP on the LAN with a paired token (see [RTX agent](#rtx-agent)); acceptable on a home
  network, documented as such.
- **Transcript text goes to OpenAI** through the user's Codex login, for live and final
  correction. Same disclosure model as Interview mode: a notice the first time Accent mode is
  chosen, and the mode's card says it in one line.

The mode chooser's Accent card reads: *"For accented or noisy speech. Audio goes to your RTX
desktop; transcript text goes to OpenAI for correction."* STATUS.md's "On-device only" decision
is updated to name Accent mode as the exception.

---

## Architecture

```
 Mac (LocalCaption, Accent mode)                          RTX desktop (rtx-agent)
 ─────────────────────────────────────────────            ───────────────────────────────
 enter Accent mode ───────────────── POST /load ────────▶ download (once) → load → warm
 ScreenCaptureKit → cleanup → SpeechSegmenter (VAD, endpoint)
   │ interim snapshot ─── POST /transcribe?roles=primary&lane=interim ─▶ primary    p90 ~0.3 s
   │ final utterance ──── POST /transcribe?roles=primary ─────────────▶ primary    p90 ~0.4 s
   │                 └─── POST /transcribe?roles=secondary (concurrent) ▶ secondary p90 ~1.2 s
   ▼
 CaptionPipeline lanes (unchanged)
   │ final: text = primary (caption shows now); alt = secondary (arrives later, feeds correction)
   ├──▶ Session: journal + transcript (raw)
   ├──▶ Caption view (raw, muted)
   └──▶ LiveCorrector ── Codex thread (live model/effort) ──▶ corrected lines ─▶ view + journal
 Stop ─▶ FinalPass ── Codex thread (final model/effort) ──▶ final text per segment ─▶ save
 idle 15 min ───────────────────────── (agent unloads models itself)
```

The seam already exists: `CaptionPipeline(session:interim:final:)` takes two decode closures
returning `SpeechOutcome`. Accent mode supplies closures backed by the RTX; lanes,
LocalAgreement, filters, journal and metrics are untouched. `SpeechOutcome.success` gains an
optional `alt` text (the secondary model) carried through `onFinal`. Standard mode keeps
`WhisperEngine`.

---

## RTX agent

New top-level folder `rtx-agent/` (Python, runs on Windows). A small always-on process that
holds **no models until asked**, so it costs no VRAM when Accent mode is not in use.

### Install and run

- README: create the venv, `pip install -r requirements.txt`, then `python install.py`, which
  registers a Task Scheduler entry (start at logon, restart on failure) and a Windows Firewall
  rule for TCP 8765 on the **Private** profile only.
- Pinned packages, including what the spike learned: `av<16` (faster-whisper 1.2.1 breaks on
  av 16+); onnxruntime-gpu's CUDA 13 runtime wheels alongside CUDA 12 / cuDNN 9 for CTranslate2;
  `HF_HUB_DISABLE_XET=1`; models stored in `rtx-agent/models/` as plain folders (onnxruntime ≥
  1.30 rejects the Hugging Face symlink cache).
- Advertises itself on the LAN with mDNS/Bonjour as `_localcaption._tcp` (name = PC name).

### Pairing

On first start (and whenever no Mac is paired) the agent shows a **6-digit pairing code** in a
small window and writes it to `rtx-agent/pairing-code.txt`. The Mac sends the code once; the agent
returns a random token, the Mac keeps it in the Keychain. Every other call needs
`Authorization: Bearer <token>`. *Unpair* on either side revokes it.

### API

| Call | Auth | Contract |
|---|---|---|
| `GET /hello` | none | `{name, version, gpu, vram_total_mb, paired}` — for discovery and the address field's live check |
| `POST /pair` | none | `{code}` → `{token}`; 5 wrong codes → locked for 10 min |
| `GET /models` | token | Catalog: `[{id, family, size_mb, vram_mb, downloaded, loaded}]` |
| `POST /load` | token | `{primary, secondary}` (`secondary` may be `null`). Returns at once; work runs in the background. Refuses (409) if the pair would not fit in free VRAM. Loading the already-loaded pair is a no-op. |
| `GET /status` | token | `{state: idle·downloading·loading·warming·ready·error, model, progress, message, loaded: {primary, secondary}, vram_used_mb}` |
| `POST /unload` | token | Frees both models |
| `POST /transcribe` | token | Body: 16 kHz mono PCM16 LE, ≤ 30 s (413 otherwise). Query: `roles=` any of `primary`, `secondary`; `lane=interim·final` (default final); `id=<utterance>`. Reply: `{id, primary?: {text, ms}, secondary?: {text, ms}}`. 409 if not `ready`. |

Each (lane, role) handles one utterance at a time, so interim never queues behind a final, and the
Mac sends a final's primary and secondary as **two concurrent requests**: the caption needs only
the primary (S0.3: Whisper large-v3 takes ~0.65 s p50 / 1.2 s p90 on 1000 ms-endpoint pieces,
too slow to hold the caption for). Requests waiting > 5 s get 503. The agent keeps
no audio and writes none to disk. After **15 minutes** with no `/transcribe`, it unloads itself.

### Model catalog (v1)

Any catalog model can be primary or secondary. Only the defaults were measured in the spike;
the others are offered because the same libraries run them (onnx-asr for NeMo, faster-whisper
for Whisper).

| id | Library | Note |
|---|---|---|
| `parakeet-tdt-0.6b-v2` | onnx-asr | **default primary**; English; measured |
| `parakeet-tdt-0.6b-v3` | onnx-asr | multilingual (25 European languages) |
| `canary-1b-v2` | onnx-asr | larger NeMo model |
| `whisper-large-v3` | faster-whisper | **default secondary**; measured |
| `whisper-large-v3-turbo` | faster-whisper | faster, weaker on accents (spike: Mac turbo 46.9%) |
| `distil-whisper-large-v3` | faster-whisper | fastest Whisper |
| `whisper-medium.en` | faster-whisper | English-only, smaller |

The catalog is a JSON file in `rtx-agent/`, so adding a model is a data change plus a test.

## Mac: connecting and starting

### Settings → Accent mode → RTX desktop

- **Found on your network:** list of `_localcaption._tcp` agents (name, GPU), or **Enter address**
  (`host[:port]`, e.g. `172.20.101.43`). The field checks `/hello` as you type and shows the GPU.
- **Pair:** asks for the code shown on the RTX; on success shows *Paired with DESKTOP-931J6H0 —
  RTX 4090 D, 24 GB*. **Unpair** and **Test connection** buttons.
- Status line while open: agent state, loaded models, VRAM used.

### Entering Accent mode (D7, D9)

1. Switch to Accent → the app calls `/hello`. Not reachable, not paired, or Codex not signed in
   (when correction is on) → the switch shows why, with a button to the right Settings page, and
   **Start stays disabled**. Standard mode is one click away.
2. Reachable → `POST /load` with the configured pair, then poll `/status` once a second. The
   session screen shows *Starting models on RTX…* with the agent's step and progress
   (*Downloading whisper-large-v3 · 45%*, *Loading*, *Warming*). **Start enables at `ready`.**
3. While Accent mode stays selected, the app pings `/status` every 30 s so the idle unload does
   not fire under it; leaving Accent mode sends nothing — the agent unloads after 15 min itself.

### If the RTX drops mid-session

Pending decodes retry with backoff for up to **30 s** while the caption view shows *RTX
unreachable — retrying*. Audio keeps queuing in the pipeline; nothing is dropped. After 30 s the
session **pauses** through the existing pause path, with the reason shown. **Resume** is enabled
once `/hello` answers and `/status` is `ready` (re-loading if the agent restarted); the queued
utterances are then decoded in order.

### Engine and cleanup

- **`RTXEngine`** — `interim` → `roles=primary&lane=interim`; `final` → `roles=primary` and, in
  parallel, `roles=secondary`. Keep-alive `URLSession`. Applies the existing `Filters`
  (hallucination, non-English, quality gate) to each text. The final returns as soon as the
  primary answers: `.success(text: primary)`; the secondary text is delivered to the segment (and
  the corrector) when it arrives. If the primary is empty and the secondary is not, the secondary
  becomes the caption when it arrives.
- **Audio cleanup** (on by default) — per utterance before sending: 80 Hz–7.5 kHz band-pass
  (vDSP biquad) and loudness levelling to a fixed RMS. Two toggles. Standard mode never applies it.

## Live correction

`LiveCorrector` (app target) owns one Codex thread per session through the existing `AnswerEngine`
(SPEC-12). Separate thread and base instructions from Interview mode; same lockdown (dedicated
`CODEX_HOME`, read-only sandbox, tool-call guard).

- **Instructions** — the rules proven in `fix_codex.py`: Nigerian English and Pidgin, two
  sources per line (P = primary, W = secondary), keep the speaker's grammar and Pidgin, never add
  content neither source supports, `[?]` for an unresolvable word, `[non-English]` for another
  language, empty for noise, reply `<number>\t<text>` one line per input line.
- **Vocabulary** — global list in Settings plus an optional per-session list at Start (names of
  people, products, stack). Sent once in the first turn.
- **Turns** — when ≥ 1 final is waiting and no turn is in flight, send **all** waiting finals as
  one turn with the **live model and effort**. The thread's history gives context. Every 40
  turns, start a fresh thread seeded with instructions, vocabulary and the last 15 corrected lines
  (bounds token growth — S0.6 confirms the number).
- **Applying a reply** — for each returned number, replace that segment's displayed text. A reply
  line is **rejected** (raw kept) if it is missing, or if fewer than half its words occur in P, W
  or the vocabulary (guards against invented content). Rejections are logged with counts.
- **Journal** — each accepted correction is journalled (`kind: "correction"`, segment id, text)
  so crash recovery keeps it.
- **Usage** — reads the Codex usage windows (SPEC-12); below 10% left, live correction pauses
  with a notice and the session continues raw.

## Final pass (at Stop)

- Runs after the transcript is saved raw, so Stop is never blocked. Status on the saved session:
  `Improving transcript…` → `Improved` / `Couldn't improve (reason)` with **Retry**.
- One fresh thread with the **final model and effort**, the same instructions, the whole session
  as numbered P/W lines (live corrections are **not** sent — the final model starts from both raw
  sources). Sessions over ~15 min go in 15-minute blocks; each block's turn includes the previous
  block's last 15 final lines as context.
- Same rejection rule as live. Output becomes each segment's `final_text`.
- Measured: ~126 s for a 12.7 min session with `gpt-6.1-sol` high. A 60 min session ≈ 4 blocks
  ≈ 8–10 min (S0.6).

## Data

- **`TranscriptSegment`** gains `altText` (`alt_text`), `liveText` (`live_text`), `finalText`
  (`final_text`) — all optional. `text` stays the raw primary result, unchanged in meaning.
- **SQLite** migration `v6_correction`: three nullable columns on the segments table; on sessions,
  `speech_mode` (`standard` · `accent`), `models` (e.g. `parakeet-tdt-0.6b-v2+whisper-large-v3`)
  and `correction_status` (`none` · `running` · `done` · `failed`).
- **`.json` sidecar** carries all four texts and the models used. **`.txt` export** writes the
  best available: `final_text` → `live_text` → `text`; re-exported when the final pass completes.
- **Session detail** (SPEC-06) gets a *Raw / Corrected* switch; default Corrected when present.
  The session list shows an *Accent* badge.

## Config — new `accent` group

Merge-default (missing → defaults), no schema bump — same treatment as `summary` and `interview`.

| Key | Default | Meaning |
|---|---|---|
| `accent.enabled` | `false` | the Standard ↔ Accent switch, remembered |
| `accent.rtx_address` | `""` | `host[:port]`; port defaults to 8765 |
| `accent.rtx_name` | `""` | paired agent's PC name (display only) |
| — token — | | Keychain item `LocalCaption RTX token`, never in `config.json` |
| `accent.primary_model` | `"parakeet-tdt-0.6b-v2"` | catalog id |
| `accent.secondary_model` | `"whisper-large-v3"` | catalog id, or `""` for none |
| `accent.audio_bandpass` | `true` | 80 Hz–7.5 kHz before sending |
| `accent.audio_level` | `true` | per-utterance loudness levelling |
| `accent.endpoint_silence_ms` | `1000` | Accent mode's own endpoint (spike: longer pieces did better); S0.4 tunes it |
| `accent.live_correction` | `true` | live Codex correction |
| `accent.live_model` / `accent.live_effort` | `""` / `"low"` | `""` → `gpt-6-luna` |
| `accent.final_pass` | `true` | final pass at Stop |
| `accent.final_model` / `accent.final_effort` | `""` / `"high"` | `""` → `gpt-6.1-sol` |
| `accent.vocabulary` | `""` | comma-separated global word list |
| `accent.notice_accepted` | `false` | privacy notice shown once |

## Settings → Accent mode (UI)

| Section | Controls |
|---|---|
| RTX desktop | discovered list / address field, Pair, Unpair, Test connection, status line ([above](#settings--accent-mode--rtx-desktop)) |
| Speech models | **Primary** and **Secondary** pickers from `/models` (size, VRAM, *downloaded* tick; Secondary has *None*). Changing them while in Accent mode re-sends `/load`. *Reset to defaults*. |
| Audio | *Clean up audio* (band-pass) and *Even out loudness* toggles; *Pause before a caption ends* slider (endpoint, 400–1500 ms) |
| Correction | **Live:** on/off, **Model** picker and **Reasoning effort** picker. **Final pass:** same three. Lists come from Codex (`model/list`, each model's efforts) — never hard-coded (SPEC-12). Shows the Codex usage line. |
| Vocabulary | multi-line field; one-line hint: names of people, products, your stack |

## Windows

The Windows app keeps its current engine. So that both builds keep sharing files: the Windows
config loader must **round-trip** the `accent` group untouched, and its DB migration list gains
`v6_correction` (columns only) so a shared database opens on both — tracked in SPEC-16. The RTX
agent runs on Windows but is a separate program, not part of the Windows app.

---

## Build steps

| Step | What | Where |
|---|---|---|
| **S0** | Spike gate — below. Nothing else starts until it passes or the user accepts a miss. | Mac + RTX |
| 1 | `rtx-agent/`: API, catalog, load/unload/idle-unload, pairing, mDNS, `install.py`, tests | RTX |
| 2 | Replay CLI in `Benchmark`: feed a `.wav`/`.m4a` through cleanup → segmenter → RTX → corrector → final pass, write P/W/live/final text files and a WER report. All acceptance numbers come from it. | Mac |
| 3 | `RTXClient` (discovery, pairing, Keychain token, status polling) + `RTXEngine` + `alt` in `SpeechOutcome` + audio cleanup + drop/retry/pause handling | Mac |
| 4 | Mode switch (chooser + top bar), entering-mode flow (`/load`, progress, Start gating), privacy notice | Mac |
| 5 | Data: segment fields, `v6_correction`, sidecar, `.txt` best-text export, journal `correction` kind | Kit |
| 6 | `LiveCorrector` + caption view replacement (raw muted → corrected normal, `[?]` highlighted) | Mac |
| 7 | Final pass + session status + Raw/Corrected switch + Retry + list badge | Mac |
| 8 | Settings → Accent mode (all sections above) | Mac |
| 9 | Acceptance on the reference clip and S0.5 recordings; STATUS.md, README, SPEC.md §17 updates | Mac + RTX |

## S0 — spike gate

| # | Question | How | Pass |
|---|---|---|---|
| S0.1 | Live correction latency with a persistent thread | App-server thread, `gpt-6-luna` low, replay the clip's finals at real-time pace | p50 ≤ 3 s, p90 ≤ 5 s from final to corrected text |
| S0.2 | Model start time | Cold `/load` of the default pair (already downloaded) on the RTX | `ready` ≤ 60 s — ✅ **23.3 s** (Parakeet 11 s, Whisper 11 s); +7.6 GB VRAM |
| S0.3 | LAN round trip | Mac → RTX, the clip's pieces, one kept-alive connection per lane | p90 ≤ 800 ms for the caption, ≤ 400 ms for interim — ✅ endpoint 1000 ms: caption **423 ms**, interim 319 ms, secondary 1,184 ms; endpoint 600 ms: caption 293 ms, interim 275 ms, secondary 898 ms |
| S0.4 | Cleanup and endpoint | Full pipeline: cleanup on/off × endpoint 600 / 1000 / 1500 ms, on this clip and a noisier one | Confirm or change the defaults; live caption delay ≤ 1.5 s p90 |
| S0.5 | Generalisation | ≥ 2 more Nigerian or noisy recordings, each with a 3-minute verbatim reference | Accent final pass beats Standard mode's best on every clip |
| S0.6 | Long sessions and Plus limits | Replay a 60 min recording | Final pass ≤ 12 min; live + final together use ≤ 25% of a 5-hour usage window |

If S0.1 misses, live correction ships as **Corrected every ~30 s** (larger batches) instead of per
utterance. If S0.2 misses, entering Accent mode still works — Start just waits longer.

## Acceptance (feature-level)

1. **Reference clip, replayed:** final-pass WER ≤ 41% and live WER ≤ 50% vs the reference
   (spike: 39.9% / 48.8%); "Claude" correct ≥ 14 of 15 in the final text.
2. **Live delay:** raw caption ≤ 1.0 s p90 after endpoint; corrected text ≤ 5 s p90 after its raw
   caption (S0.1's number if lower).
3. **Starting:** choosing Accent with the RTX on and paired loads the configured models with no
   other action; Start enables at `ready`. With the RTX off, Start stays disabled and says why.
4. **Connecting:** a fresh Mac finds the agent on the LAN (or by typed address) and pairs with
   the code in under a minute; no file editing on either machine.
5. **Mid-session loss:** stopping the agent mid-session shows *retrying*, pauses after 30 s, and
   after the agent returns, **Resume** decodes every queued utterance — no journalled final
   without text.
6. **Model choice:** changing primary/secondary in Settings reloads on the RTX and the session's
   saved record names the models used.
7. **No invention:** across the S0.5 set, < 1% of accepted correction lines contain a content word
   found in neither source nor the vocabulary (the replay CLI reports it).
8. **Crash:** killing the app mid-session recovers raw text **and** every live correction made.
9. **Standard mode unchanged:** with Standard selected, a network monitor shows no traffic during
   a session, and its tests and behaviour are as before.
10. **Idle:** 15 minutes after the last session, the RTX's VRAM use returns to its pre-load level.

## Non-goals (v1)

On-Mac fallback for Accent mode · speaker labels (diarization) · translating Pidgin or Polish ·
encrypting LAN traffic (TLS) · Wake-on-LAN · an agent for macOS/Linux · Accent mode in the Windows
app · a second correction provider.

## Blockers / risks

| ID | Risk | Mitigation |
|---|---|---|
| B15 | Codex Plus limits under continuous correction | Usage read every 5 min; pause live correction at 10%; S0.6 measures |
| B16 | RTX off, asleep or network down | Accent mode refuses to start (D7); mid-session retry → pause → resume |
| B17 | Audio in plain HTTP on the LAN | Pairing token, Private-profile firewall rule, documented; TLS is a non-goal for v1 |
| B18 | Correction invents plausible text | Word-overlap rejection rule; acceptance 7 |
| B19 | WER against an edited reference misleads | Rank versions with it; S0.5 adds verbatim references |
| B20 | Hugging Face downloads fail on the RTX network (Xet) | `HF_HUB_DISABLE_XET=1`; manual model copy from the Mac documented (done in the spike) |
| B21 | Other GPU work on the RTX competes for VRAM | `/load` checks free VRAM (409 with the numbers); idle unload |
| B22 | Unmeasured catalog models misbehave | Defaults are the measured pair; others labelled *not tested* until the replay CLI has run them |

## Open decisions

- Does Interview mode use corrected text for F8 questions (better wording, +1–3 s), or raw?
  Proposed: raw for F8, corrected for the end-of-interview summary.
- Should the per-session vocabulary be pre-filled from Interview prep (CV/JD names and stack)?
