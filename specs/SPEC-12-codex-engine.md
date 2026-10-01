# SPEC-12 — Codex answer engine

**Status:** ⬜ Not started · **Step:** 0 (spike) and 2 of [SPEC-11](SPEC-11-interview-assist.md) ·
**Depends on:** SPEC-11 (config, prompts, records)

> Talks to a long-lived **`codex app-server`** child process over JSON-RPC (stdio) and turns it
> into a plain question-and-answer engine: one locked-down thread per interview, streamed
> answers, interrupt, model list, usage limits and sign-in state. No UI here.

---

## S0 — spike gate

**Nothing in steps 2–5 is built until S0 passes.** It answers the questions this spec can't
settle on paper. Lives in `spike/codex-answer-spike/` (a script, like the other spikes), results
in its `RESULTS.md`.

**Prerequisite (user):** `brew install codex` (or npm), then `codex login` with the ChatGPT Plus
account.

| # | Question | How | Pass |
|---|---|---|---|
| S0.1 | Time to first answer text, and to completion | 10 real interview questions, after a prep turn with a sample CV + JD, for each candidate model × effort (`minimal`/`low`/`medium` where offered) | **p50 first text ≤ 3 s, p90 ≤ 5 s** for at least one model at low effort |
| S0.2 | Tone | Same 10 answers, also pasted into the ChatGPT app; user compares side by side | User judges them "as good as ChatGPT" |
| S0.3 | Lockdown holds | Prompts that *try* to make it run `ls`, read `~/.ssh`, search the web, edit a file | No `commandExecution` / `fileChange` / `webSearch` / `mcpToolCall` item ever starts |
| S0.4 | `baseInstructions` really replaces the coding prompt | Ask "what are you / what tools do you have?" | Answers as the interview coach, lists no tools |
| S0.5 | Image input | `localImage` with a screenshot of a coding question | Answer uses the image |
| S0.6 | Usage read | `account/rateLimits/read` | Returns 5-hour and weekly windows |
| S0.7 | Dedicated `CODEX_HOME` + in-app sign-in | Start with `CODEX_HOME=<app>/interview/codex-home`; try the app-server login flow | Sign-in works without the Terminal, **or** record the fallback |
| S0.8 | Exact config keys that disable tools | Try the overrides below; read back with `config/read` if available | Record the working set for the pinned version |

Record the **Codex version** tested; it becomes `minimumCodexVersion`. Record the recommended
default `model` → the `""` default in config resolves to it.

**If S0.1 or S0.2 fails:** stop and ask the user — either accept the speed, or build an
`OpenAIChatEngine` behind the same `AnswerEngine` interface (D2) and skip Codex.

---

## The interface

```swift
protocol AnswerEngine: AnyObject, Sendable {
    func status() async -> EngineStatus            // installed? version ok? signed in?
    func models() async throws -> [EngineModel]    // id, displayName, efforts, images?, personality?
    func usage() async throws -> EngineUsage       // windows: usedPercent, windowMinutes, resetsAt; plan
    func startThread(_ cfg: ThreadConfig) async throws -> String        // → thread id
    func resumeThread(id: String, _ cfg: ThreadConfig) async throws
    func send(threadId: String, _ input: [TurnInput], effort: String?) -> AsyncThrowingStream<AnswerEvent, Error>
    func interrupt(threadId: String) async
    func shutdown() async
}

enum TurnInput   { case text(String), image(URL) }
enum AnswerEvent { case started(turnId: String), delta(String), thinking,
                   completed(fullText: String), interrupted, failed(EngineError) }
```

`ThreadConfig` = model, effort, personality, base instructions (from `InterviewPrompt`), cwd.
Events hop to `@MainActor` in the caller, as `SummaryEngine` results do today.

Windows implements the same shape in C# (`IAnswerEngine`); the JSON on the wire is identical.

---

## Finding `codex`

GUI apps don't inherit the shell `PATH`, so search explicitly, first hit wins:

1. `interview.codex_path` if set.
2. macOS: `/opt/homebrew/bin/codex`, `/usr/local/bin/codex`, `~/.npm-global/bin/codex`,
   `~/.local/bin/codex`, then `/usr/bin/env codex` via a login shell (`zsh -lc 'command -v codex'`).
3. Windows: `%APPDATA%\npm\codex.cmd`, `%LOCALAPPDATA%\Programs\…`, then `where codex`.

Run `codex --version`; below `minimumCodexVersion` → status `tooOld`. npm installs need `node`
on the child's `PATH`: prepend the directory of the found binary and, on macOS,
`/opt/homebrew/bin:/usr/local/bin`.

## Process & transport

- [ ] Spawn `codex app-server` (stdio transport) with `cwd = interview/workspace/`, env
      `CODEX_HOME = interview/codex-home/` (per S0.7), the config overrides below, and stderr to
      `interview/codex.log` (rotated at 5 MB).
- [ ] **Newline-delimited JSON-RPC 2.0.** A reader task splits stdout on `\n`, decodes each line,
      routes responses by `id` and notifications by `method`. Requests time out after 15 s.
- [ ] Handshake: `initialize` with `clientInfo {name: "localcaption", title: "LocalCaption",
      version}` and `capabilities.experimentalApi: true`; then the `initialized` notification.
      Opt out of notifications we never use (`optOutNotificationMethods`) to cut traffic.
- [ ] **Server-initiated requests** (e.g. approval requests): always answer *decline*. With
      approvals `never` none should arrive; if one does, log it as a lockdown breach.
- [ ] One process per app run, started on **Prepare**, kept until the interview's summary is
      done or the app quits. On unexpected exit: restart once, `thread/resume` the thread, mark
      any in-flight turn `failed` ("Codex restarted — press Ask again"). A second crash →
      engine `failed`, the UI says so, captions are unaffected.
- [ ] `shutdown()`: close stdin, wait 2 s, then terminate.
- [ ] Transport is a protocol (`LineTransport`) so tests drive the engine with scripted lines,
      no real process.

## Lockdown

Codex must behave as a chat model only. Defence in depth — any single layer failing is still
safe:

| Layer | Setting |
|---|---|
| Working folder | `cwd` = `interview/workspace/`. App-owned and **always empty**: verified (and emptied) before every `thread/start`; attachments and records live elsewhere. |
| Sandbox | `sandboxPolicy: readOnly`, with read access restricted to `cwd` where the pinned version supports restricted roots. |
| Approvals | `approvalPolicy: "never"` — nothing ever pauses waiting for a click. |
| Tools off (config) | Launch overrides, exact keys confirmed in S0.8: `-c features.shell_tool=false`, `-c web_search="disabled"`, `-c features.apply_patch_tool=false` (if the version has it), `-c mcp_servers={}`, `-c project_doc_max_bytes=0` (no `AGENTS.md`). |
| Isolation | Dedicated `CODEX_HOME`, so the user's own `~/.codex` config, `AGENTS.md`, skills and MCP servers never load (S0.7). Fallback if sign-in can't be done there: default `CODEX_HOME` + all overrides above. |
| Instructions | `baseInstructions` = the interview-coach prompt (SPEC-13), which states it has no tools and must only answer. |
| **Tool-call guard** | If an `item/started` arrives with any type other than `userMessage`, `agentMessage` or `reasoning`, immediately `turn/interrupt`, mark the turn `failed` ("Blocked: the model tried to use a tool"), and log the item type. |

## Threads & turns

- [ ] `thread/start` params: `model`, `cwd`, `approvalPolicy`, `sandboxPolicy`,
      `baseInstructions`, `personality` (only if `supportsPersonality`), `effort`,
      `ephemeral: false` (the thread must survive restarts), `serviceName: "localcaption"`.
- [ ] `turn/start` params: `threadId`, `input` (`text` items and `localImage` items with
      **absolute** paths), optional per-turn `effort` (prep/summary use
      `prep_reasoning_effort`, asks use `reasoning_effort`).
- [ ] Streaming → `AnswerEvent`:
  - `turn/started` → `.started`
  - `item/agentMessage/delta` → `.delta(deltaText)` (append in order)
  - `item/started` of type `reasoning` → `.thinking` (UI shows "thinking…" until the first delta)
  - `item/completed` of type `agentMessage` → its text replaces the streamed buffer (authoritative)
  - `turn/completed` → `.completed` / `.interrupted` / `.failed` by `status`
- [ ] `interrupt` → `turn/interrupt`; the stream ends with `.interrupted`.
- [ ] **Timeouts:** no first delta in 30 s → keep waiting but surface "slow"; nothing in 120 s →
      interrupt, `.failed(.timeout)`.
- [ ] One turn at a time per thread. The engine rejects a second `send` while one is active;
      queue/interrupt policy is the caller's (SPEC-14).
- [ ] **Delete an interview** → `thread/archive` (if available) so the CV doesn't linger in the
      Codex session store; otherwise delete its rollout under the dedicated `CODEX_HOME`.

## Models

- [ ] `model/list` (`includeHidden: false`) → `EngineModel { id, displayName, isDefault,
      defaultEffort, efforts[], acceptsImages (inputModalities ∋ "image"), supportsPersonality }`.
- [ ] Models whose id contains `codex` are labelled **"coding-tuned"** in pickers; the
      recommended default (S0) is a general model.
- [ ] If the configured model disappears from the list → fall back to the recommended default
      and tell the user once.

## Usage (Plus limits)

- [ ] `account/rateLimits/read` → windows with `usedPercent`, `windowDurationMins`, `resetsAt`
      (Unix seconds, UTC) and the plan type. **Classify by duration** (300 ≈ 5-hour, 10080 =
      weekly), not by the `primary`/`secondary` slot name. Remaining = `100 − usedPercent`.
- [ ] Subscribe to `account/rateLimits/updated` while the process is running.
- [ ] Usage-limit failures on a turn map to `.failed(.usageLimit(resetsAt))` so the UI can say
      *"Plus limit reached — resets at 14:20"* rather than a generic error.

## Sign-in

- [ ] `status()` reports `notInstalled` | `tooOld(version)` | `signedOut` | `ready(account)`.
- [ ] Sign-in from Settings via the app-server's ChatGPT login method (exact method names from
      S0.7): open the returned URL in the default browser, wait for completion, refresh status.
- [ ] Fallback (if in-app sign-in isn't available): Settings shows the exact command to run, with
      a Copy button: `CODEX_HOME="<path>" codex login`.

## Kit vs app split

| Piece | Where | Tested |
|---|---|---|
| `CodexRPC` — request builders, response/notification decoders, `AnswerEvent` mapping, rate-limit window classification | Kit (pure) | ✅ unit + `testdata/codex/` vectors |
| `CodexLocator`, `LineTransport` over `Process`, `CodexAppServerEngine` | App | ✅ with a scripted transport; live smoke run |

## Acceptance

- S0 `RESULTS.md` exists with the numbers, the pinned version and the working override set.
- With a scripted transport: handshake, thread start, a streamed turn, interrupt, a tool-item
  breach (→ interrupted + failed), a crash + resume, and a usage-limit failure all produce the
  specified events.
- Live: on a real thread, ten turns produce answers and the event log contains **no** item types
  other than `userMessage`, `agentMessage`, `reasoning`.
- `interview/workspace/` is empty after every run.
- Missing `codex`, too-old `codex`, and signed-out states each show a specific, actionable
  message — never a hang.
- `swift test` green, including the new `testdata/codex/` vectors.

## Risks

- **B10 — protocol drift.** Some methods/params are marked experimental. Pin a tested
  version; on a newer *major* version, run and warn ("untested Codex version").
- **B12 — lockdown completeness** — see the Lockdown table; S0.3 is the proof.
- The model list and effort names change over time — never hard-code them in UI; always read
  `model/list`.
