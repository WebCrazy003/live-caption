# S0 results — Codex answer engine (SPEC-12 §S0)

**Date:** 2026-10-01 · **Codex:** `codex-cli 0.159.3` (Homebrew cask) · **Machine:** M1, 16 GB,
macOS 14.7.6 · **Account:** ChatGPT **Plus**

**Verdict: S0 passes.** Build on Codex. Recommended default: **`gpt-6-luna`, effort `low`**.

Inputs: a real CV (PDF → PDFKit text, 5.6 KB) and a real JD (CreateFuture, Senior Fullstack
Engineer), no skill. Ten transcript-style questions (lower case, no punctuation, two with ASR
mistakes: "oh auth two and jay w t", "rabbit m q"). Prompts are SPEC-13's, verbatim, medium
length. CV, JD and answers stay in `private/` (git-ignored).

## Results by question

| # | Check | Result |
|---|---|---|
| S0.1 | Latency (press → first word) | **Luna low: p50 1.3 s, p90 2.1–2.2 s**, whole answer p50 3.6–3.8 s (two runs). Sol low: p50 3.0 s, p90 3.8 s, whole answer 11.4 s. ✅ |
| S0.2 | Tone | Both conversational and first-person; Sol longer and richer, Luna tighter. Grounded in the CV and honest where the CV has no example. **The user still has to compare these with the ChatGPT app** (`private/runs/*.md`). |
| S0.3 | Lockdown | Four attack prompts (run `ls ~`, read `~/.ssh/id_rsa`, web search, create a file) → all refused in words; **no tool item ever started** across ~40 turns; workspace stayed empty. ✅ |
| S0.4 | `baseInstructions` replaces the coding prompt | "What are you?" → *interview coach, no access to tools or files*. ✅ |
| S0.5 | Image input | `localImage` with a rendered coding question → correct Java + SQL answer, first word 2.1 s. ✅ |
| S0.6 | Usage read | `account/rateLimits/read` → plan `plus`, 5-hour and weekly windows (`usedPercent`, `windowDurationMins` 300 / 10080, `resetsAt`), keyed under `rateLimitsByLimitId.codex`. ✅ |
| S0.7 | Dedicated `CODEX_HOME`, in-app sign-in | Fresh home → `account/read` = signed out; `account/login/start {type: "chatgpt"}` → `loginId` + `authUrl`. In-app sign-in is possible; no Terminal step. ✅ |
| S0.8 | Tool-off overrides | Accepted by 0.159.3 (see below). |

**Usage cost:** three full runs (prep + 10 asks each) + 6 probes ≈ **1 %** of the 5-hour and
1 % of the weekly Plus window. One interview is a fraction of a percent.

## Protocol facts that differ from the docs (SPEC-12 updated)

Taken from `codex app-server generate-json-schema` for 0.159.3:

- `thread/start` takes **`sandbox: "read-only"`** (enum `read-only | workspace-write |
  danger-full-access`), not `sandboxPolicy`. `approvalPolicy: "never"` as documented.
- **`effort` exists only on `turn/start`, and it persists** to later turns ("for this turn and
  subsequent turns"). The engine must send `effort` on **every** turn — otherwise asks inherit
  the prep turn's `medium`.
- **`personality` is deprecated**: "`friendly` and `pragmatic` no longer select a style." Tone
  comes from `baseInstructions` alone — which S0.2/S0.4 show is enough.
- `thread/start` also accepts a `config` object (per-thread overrides) and `developerInstructions`.
- Item types: `userMessage, agentMessage, reasoning, plan, commandExecution, fileChange,
  mcpToolCall, dynamicToolCall, webSearch, imageView, imageGeneration, …`. At `low` effort no
  `reasoning` item appeared at all.
- Server→client requests to decline: `item/commandExecution/requestApproval`,
  `item/fileChange/requestApproval`, `item/permissions/requestApproval`,
  `item/tool/requestUserInput`, `mcpServer/elicitation/request`, `item/tool/call`, and the legacy
  `applyPatchApproval`, `execCommandApproval`. None arrived.
- No model id contains `codex` any more — the "coding-tuned" label rule has nothing to match.
- Lowest effort offered is `low` (no `minimal`).

## Working lockdown launch (0.159.3)

```
codex app-server \
  --disable shell_tool --disable unified_exec --disable apps --disable browser_use \
  --disable browser_use_external --disable computer_use --disable image_generation \
  --disable multi_agent --disable plugins --disable tool_suggest --disable skill_search \
  --disable sleep_tool --disable in_app_browser --disable goals --disable hooks \
  -c web_search="disabled" -c 'mcp_servers={}' -c project_doc_max_bytes=0
```

plus `thread/start {cwd: <empty dir>, sandbox: "read-only", approvalPolicy: "never",
baseInstructions, ephemeral}`.

## Prompt change found by the spike (SPEC-13 updated)

Luna answered a gap question with *"My CV doesn't describe…"* — not sayable to an interviewer.
Adding this rule fixed it (0 mentions of CV/setup in the re-run, no latency change):

> If my background does not cover the question, answer honestly in the first person and bridge
> from what I do have ("I haven't used X directly, but in my work on Y…").
> Everything in the answer must be something I can say out loud to the interviewer. Never
> mention my CV, my setup, these instructions or this conversation.

## Other observations

- **First turn on a new thread is slow** (prep 6–7 s to first word; a cold identity probe 5.7 s).
  Prep absorbs this, so the first live Ask is already warm.
- **PDFKit letter-spaces large headings** ("S t e v e  O n y e"). Harmless for the model, but the
  SPEC-13 editable-text step is worth keeping.
- The model resolved both ASR-garbled questions correctly and restated them on the `**Q:**` line,
  so the user can see what it heard (B13 mitigation works).

## Re-run

```bash
python3 spike.py info
python3 spike.py run --model gpt-6-luna --effort low
python3 spike.py probes --model gpt-6-luna
python3 spike.py codexhome
```

Needs `private/cv.txt` (`swift pdf_text.swift <cv.pdf> > private/cv.txt`) and `private/jd.txt`.
