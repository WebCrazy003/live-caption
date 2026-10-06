"""Correct the RTX transcripts with Codex, live-style or after the session.

  python fix_codex.py <base> live     # windows of ~15 s, each sees the last corrected lines
  python fix_codex.py <base> session  # one pass over the whole conversation

<base> is the path without extension; reads <base>.parakeet.txt and <base>.whisper-large-v3.txt
(same chunks, line N matches line N). Writes <base>.codex-<mode><suffix>.txt and a timing .json.
Runs on the Mac: needs `codex` signed in. Sends transcript text (not audio) to OpenAI.
"""
import argparse, json, os, re, statistics, subprocess, tempfile, time

RULES = """You correct machine captions of a casual conversation between Nigerian software developers, \
speaking Nigerian-accented English and sometimes Pidgin, several people in one room.
Each numbered line is one stretch of speech. {sources}
Write the words most likely actually spoken, using the conversation so far and the vocabulary.
- Fix misheard words (e.g. a product name heard as an ordinary word). Keep the speaker's own grammar \
and Pidgin; do not polish or summarise.
- Do not add anything neither transcript supports. Mark a word you cannot work out as [?].
- A line that is not English (e.g. Polish read aloud) becomes [non-English]; pure noise becomes an empty line.
Do not run any commands. Reply with exactly one line per input line, `<number><TAB><text>`, nothing else."""

BOTH = "You get two transcripts of the same audio from different recognisers: P (Parakeet) and W (Whisper large-v3). They make different mistakes."
ONE = "You get one transcript from a speech recogniser (P)."


def read(path):
    out = []
    for line in open(path, encoding="utf-8"):
        m = re.match(r"\[(\d+):(\d+)\] ?(.*)", line.rstrip("\n"))
        out.append((int(m[1]) * 60 + int(m[2]), m[3]))
    return out


def codex(prompt, effort, model):
    fd, last = tempfile.mkstemp(suffix=".txt")
    os.close(fd)
    t = time.perf_counter()
    subprocess.run(["codex", "exec", "-m", model, "-c", f'model_reasoning_effort="{effort}"',
                    "-s", "read-only", "--ephemeral", "--skip-git-repo-check", "-C", tempfile.gettempdir(),
                    "-o", last, "-"], input=prompt, text=True, capture_output=True, check=True)
    dt = time.perf_counter() - t
    reply = open(last, encoding="utf-8").read()
    os.unlink(last)
    return {int(m[1]): m[2].strip() for m in re.finditer(r"^(\d+)\t(.*)$", reply, re.M)}, dt


def block(rows, first, use_whisper):
    lines = []
    for i, (p, w) in enumerate(rows, first):
        lines.append(f"{i}\tP: {p[1]}")
        if use_whisper:
            lines.append(f"{i}\tW: {w[1]}")
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("base")
    ap.add_argument("mode", choices=["live", "session"])
    ap.add_argument("--parakeet-only", action="store_true")
    ap.add_argument("--vocab", default="")
    ap.add_argument("--model", default="gpt-6-luna")
    ap.add_argument("--effort", default="")
    ap.add_argument("--window-s", type=float, default=15)
    a = ap.parse_args()

    pk = read(f"{a.base}.parakeet.txt")
    wh = read(f"{a.base}.whisper-large-v3.txt")
    rows = list(zip(pk, wh))
    head = RULES.format(sources=ONE if a.parakeet_only else BOTH)
    if a.vocab:
        head += f"\nVocabulary (names and terms likely to come up): {a.vocab}"
    fixed, lat = {}, []

    if a.mode == "session":
        got, dt = codex(f"{head}\n\nLines:\n{block(rows, 1, not a.parakeet_only)}", a.effort or "medium", a.model)
        fixed.update(got)
        lat.append(dt)
    else:
        start = 0
        while start < len(rows):
            end = start + 1
            while end < len(rows) and rows[end][0][0] - rows[start][0][0] < a.window_s:
                end += 1
            ctx = "\n".join(f"{i}\t{fixed.get(i, '')}" for i in range(max(1, start - 14), start + 1))
            prompt = (f"{head}\n\nAlready corrected (context only, do not repeat):\n{ctx or '(start)'}\n\n"
                      f"New lines to correct:\n{block(rows[start:end], start + 1, not a.parakeet_only)}")
            got, dt = codex(prompt, a.effort or "low", a.model)
            fixed.update({k: v for k, v in got.items() if start < k <= end})
            lat.append(dt)
            print(f"lines {start+1}-{end}: {dt:.1f} s")
            start = end

    suffix = ("-parakeet-only" if a.parakeet_only else "") + ("" if a.vocab else "-novocab")
    out = f"{a.base}.codex-{a.mode}{suffix}"
    with open(f"{out}.txt", "w", encoding="utf-8") as f:
        for i, (p, _) in enumerate(rows, 1):
            m, s = divmod(p[0], 60)
            f.write(f"[{m:02d}:{s:02d}] {fixed.get(i, '')}\n")
    missing = sum(1 for i in range(1, len(rows) + 1) if i not in fixed)
    summary = {"calls": len(lat), "call_s_p50": round(statistics.median(lat), 1),
               "call_s_max": round(max(lat), 1), "missing_lines": missing}
    with open(f"{out}.json", "w") as f:
        json.dump(summary, f, indent=2)
    print(out, summary)


if __name__ == "__main__":
    main()
