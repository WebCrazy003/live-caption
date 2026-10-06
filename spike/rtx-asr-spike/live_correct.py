"""SPEC-18 S0.1: live correction through one long-lived `codex app-server` thread.

Replays a session's utterances at real-time pace, as Accent mode would see them: each final's
caption appears `endpoint + primary` after its speech ends, its secondary text a little later.
Whenever finals are waiting and no turn is in flight, all of them go to Codex as one turn.
Reports, per line, the delay from caption shown to correction received.

  python live_correct.py <base> --audio <file> [--model gpt-6-luna] [--effort low] [--vocab "..."]

Reads <base>.parakeet.txt / <base>.whisper-large-v3.txt (one line per utterance, from the same
VAD cut this script redoes), writes <base>.appserver-live.txt and .json. Uses the app's CODEX_HOME
so it signs in as the app does. Sends transcript text (not audio) to OpenAI.
"""
import argparse, json, os, queue, re, statistics, subprocess, sys, tempfile, threading, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fix_codex import BOTH, RULES, read

# The app's lockdown (LocalCaptionKit/CodexRPC.swift), with web search off: correction needs none.
DISABLED = ["shell_tool", "unified_exec", "apps", "browser_use", "browser_use_external", "computer_use",
            "image_generation", "multi_agent", "plugins", "tool_suggest", "skill_search", "sleep_tool",
            "in_app_browser", "goals", "hooks"]
OVERRIDES = ['web_search="disabled"', "mcp_servers={}", "project_doc_max_bytes=0"]
CODEX_HOME = os.path.expanduser("~/Library/Application Support/LocalCaption/interview/codex-home")
RESET_EVERY = 40


class AppServer:
    def __init__(self):
        args = ["codex", "app-server"] + [a for f in DISABLED for a in ("--disable", f)] + \
               [a for o in OVERRIDES for a in ("-c", o)]
        self.p = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                  text=True, bufsize=1, env={**os.environ, "CODEX_HOME": CODEX_HOME})
        self.next_id, self.responses, self.events = 0, {}, queue.Queue()
        self.cv = threading.Condition()
        threading.Thread(target=self._read, daemon=True).start()
        self.call("initialize", {"clientInfo": {"name": "localcaption", "title": "S0.1", "version": "0"},
                                 "capabilities": {"experimentalApi": True}})
        self.send({"jsonrpc": "2.0", "method": "initialized", "params": {}})

    def send(self, msg):
        self.p.stdin.write(json.dumps(msg) + "\n")
        self.p.stdin.flush()

    def _read(self):
        for line in self.p.stdout:
            m = json.loads(line)
            if "id" in m and "method" in m:          # server -> client request: always decline
                self.send({"jsonrpc": "2.0", "id": m["id"], "error": {"code": -32601, "message": "no"}})
            elif "id" in m:
                with self.cv:
                    self.responses[m["id"]] = m
                    self.cv.notify_all()
            else:
                self.events.put(m)

    def call(self, method, params):
        self.next_id += 1
        rid = self.next_id
        self.send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        with self.cv:
            self.cv.wait_for(lambda: rid in self.responses, timeout=120)
        m = self.responses.pop(rid)
        if "error" in m:
            raise RuntimeError(f"{method}: {m['error']}")
        return m["result"]

    def start_thread(self, model, instructions):
        return self.call("thread/start", {"model": model, "cwd": tempfile.gettempdir(), "approvalPolicy": "never",
                                          "sandbox": "read-only", "baseInstructions": instructions,
                                          "ephemeral": True, "serviceName": "localcaption"})["thread"]["id"]

    def turn(self, thread, text, effort):
        """Run one turn; return (agent message text, seconds to first delta, seconds to completion)."""
        t = time.monotonic()
        self.call("turn/start", {"threadId": thread, "effort": effort,
                                 "input": [{"type": "text", "text": text}]})
        first, reply = None, ""
        while True:
            m = self.events.get(timeout=180)
            p, method = m.get("params", {}), m.get("method")
            if p.get("threadId") not in (None, thread):
                continue
            if method == "item/agentMessage/delta" and first is None:
                first = time.monotonic() - t
            elif method == "item/completed" and p.get("item", {}).get("type") == "agentMessage":
                reply = p["item"].get("text", "")
            elif method == "item/started" and p.get("item", {}).get("type") not in ("userMessage", "agentMessage", "reasoning"):
                raise RuntimeError(f"lockdown: tool item {p['item'].get('type')}")
            elif method == "turn/completed":
                return reply, first, time.monotonic() - t


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("base")
    ap.add_argument("--audio", required=True)
    ap.add_argument("--model", default="gpt-6-luna")
    ap.add_argument("--effort", default="low")
    ap.add_argument("--vocab", default="")
    ap.add_argument("--endpoint-ms", type=int, default=1000)
    ap.add_argument("--primary-ms", type=int, default=220)       # S0.3 p50, Parakeet round trip
    ap.add_argument("--secondary-ms", type=int, default=665)     # S0.3 p50, Whisper round trip
    a = ap.parse_args()

    from faster_whisper import decode_audio
    from faster_whisper.vad import VadOptions, get_speech_timestamps
    from transcribe_rtx import SR, clean
    audio = clean(decode_audio(a.audio, sampling_rate=SR))
    chunks = get_speech_timestamps(audio, VadOptions(max_speech_duration_s=20, min_silence_duration_ms=a.endpoint_ms,
                                                     speech_pad_ms=200))
    pk, wh = read(f"{a.base}.parakeet.txt"), read(f"{a.base}.whisper-large-v3.txt")
    assert len(chunks) == len(pk) == len(wh), (len(chunks), len(pk), len(wh))
    ends = [c["end"] / SR + a.endpoint_ms / 1000 for c in chunks]
    shown = [e + a.primary_ms / 1000 for e in ends]              # raw caption on screen
    ready = [e + a.secondary_ms / 1000 for e in ends]            # both texts in hand

    instructions = RULES.format(sources=BOTH) + (f"\nVocabulary (names and terms likely to come up): {a.vocab}"
                                                 if a.vocab else "")
    srv = AppServer()
    thread, turns = srv.start_thread(a.model, instructions), 0
    fixed, delay, firsts, totals, sent = {}, {}, [], [], 0
    t0 = time.monotonic()
    while sent < len(chunks):
        now = time.monotonic() - t0
        waiting = [i for i in range(sent, len(chunks)) if ready[i] <= now]
        if not waiting:
            time.sleep(min(0.05, max(0.0, ready[sent] - now)))
            continue
        batch = waiting
        if turns and turns % RESET_EVERY == 0:
            thread = srv.start_thread(a.model, instructions)
            ctx = "\n".join(f"{i + 1}\t{fixed.get(i + 1, '')}" for i in range(max(0, sent - 15), sent))
            prefix = f"Already corrected (context only, do not repeat):\n{ctx}\n\n"
        else:
            prefix = ""
        lines = "\n".join(f"{i + 1}\tP: {pk[i][1]}\n{i + 1}\tW: {wh[i][1]}" for i in batch)
        reply, first, total = srv.turn(thread, f"{prefix}New lines to correct:\n{lines}", a.effort)
        turns += 1
        done = time.monotonic() - t0
        got = {int(m[1]): m[2].strip() for m in re.finditer(r"^(\d+)\t(.*)$", reply, re.M)}
        for i in batch:
            if i + 1 in got:
                fixed[i + 1] = got[i + 1]
            delay[i] = done - shown[i]
        firsts.append(first or total)
        totals.append(total)
        sent = batch[-1] + 1
        print(f"turn {turns}: lines {batch[0] + 1}-{batch[-1] + 1} total {total:.1f} s, "
              f"worst caption waited {max(delay[i] for i in batch):.1f} s", flush=True)

    with open(f"{a.base}.appserver-live.txt", "w", encoding="utf-8") as f:
        for i, (secs, _) in enumerate(pk, 1):
            m, s = divmod(secs, 60)
            f.write(f"[{m:02d}:{s:02d}] {fixed.get(i, '')}\n")
    d = sorted(delay.values())
    q = lambda v, p: round(v[min(len(v) - 1, int(p * len(v)))], 2)
    summary = {"model": a.model, "effort": a.effort, "turns": turns, "lines": len(chunks),
               "missing_lines": len(chunks) - len(fixed),
               "caption_to_correction_s": {"p50": q(d, .5), "p90": q(d, .9), "max": round(d[-1], 2)},
               "turn_total_s": {"p50": round(statistics.median(totals), 2), "max": round(max(totals), 2)},
               "turn_first_text_s_p50": round(statistics.median(firsts), 2)}
    with open(f"{a.base}.appserver-live.json", "w") as f:
        json.dump(summary, f, indent=2)
    print(json.dumps(summary, indent=2))
    srv.p.terminate()


if __name__ == "__main__":
    main()
