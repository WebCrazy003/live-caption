#!/usr/bin/env python3
"""
Interactive SPEC-10 summary tester (1B by default).

Paste any block of English (a call transcript, an email, anything), press Enter
on a BLANK line, and it prints the "Key points" card the way SPEC-10 would show
it. Type  q  (or Ctrl-D) on an empty prompt to quit.

  python try.py            # Llama-3.2-1B-Instruct-4bit (local cache)
  python try.py 3b         # Llama-3.2-3B-Instruct-4bit (downloads ~1.8GB first run)
  python try.py <path|hf-id>
"""
import sys, time, re
import mlx.core as mx
from mlx_lm import load, generate

ARG = sys.argv[1] if len(sys.argv) > 1 else "1b"
MODEL = {
    "1b": "/Users/minimac/Library/Caches/models/mlx-community/Llama-3.2-1B-Instruct-4bit",
    "3b": "mlx-community/Llama-3.2-3B-Instruct-4bit",
}.get(ARG.lower(), ARG)

SYSTEM = (
    "You help a non-native English speaker follow a live call. "
    "Read the transcript part and say, in very simple English (short words, short lines), "
    "what the other person wants. Lead with the main ask. Do NOT invent anything that is "
    "not in the text. If nothing important, say so.\n\n"
    "Answer ONLY in this exact format:\n"
    "MAIN: <one short line: the single most important ask/point>\n"
    "- <short point in simple words>\n"
    "- <short point in simple words>\n"
    'WANT: <what they want you to do, or "-" if none>'
)

def parse_card(text):
    """SPEC-10's strict normalizer: pull MAIN / up-to-4 bullets / WANT, dedupe, drop junk."""
    main, want, bullets = "", "", []
    for raw in text.splitlines():
        line = raw.strip().lstrip("-").strip()
        if not line:
            continue
        up = line.upper()
        if up.startswith("MAIN:") and not main:
            main = line[5:].strip()
        elif up.startswith("WANT:") and not want:
            want = line[5:].strip()
        elif raw.strip().startswith("-"):
            b = line
            if b and b not in bullets and not b.upper().startswith(("MAIN:", "WANT:")):
                bullets.append(b)
    # dedupe near-identical bullets (the 1B repetition failure mode)
    seen, uniq = set(), []
    for b in bullets:
        key = re.sub(r"[^a-z ]", "", b.lower())
        if key not in seen:
            seen.add(key); uniq.append(b)
    return main, uniq[:4], (want if want and want != "-" else "")

def render(main, bullets, want):
    print("\n\033[1m┌─ Key points ─────────────────────\033[0m")
    print(f"  \033[1m{main or '(nothing important)'}\033[0m")
    for b in bullets:
        print(f"    • {b}")
    if want:
        print(f"  \033[2mThey want you to:\033[0m {want}")
    print("\033[1m└──────────────────────────────────\033[0m")

print(f"loading {MODEL.split('/')[-1]} …")
model, tokenizer = load(MODEL)
mx.eval(mx.zeros(1))
print(f"ready — {mx.get_active_memory()/1e9:.2f} GB resident.\n")
print("Paste text, then a BLANK line to summarize.  'q' or Ctrl-D to quit.")

while True:
    print("\n\033[2m── paste transcript, blank line to run ──\033[0m")
    lines = []
    while True:
        try:
            line = input()
        except EOFError:
            print("\nbye."); sys.exit(0)
        if line.strip().lower() in ("q", "quit", "exit") and not lines:
            print("bye."); sys.exit(0)
        if line.strip() == "":
            break
        lines.append(line)
    chunk = " ".join(lines).strip()
    if not chunk:
        continue

    msgs = [{"role": "system", "content": SYSTEM},
            {"role": "user", "content": f'Transcript part:\n"{chunk}"\n\nNow write the card.'}]
    prompt = tokenizer.apply_chat_template(msgs, add_generation_prompt=True)
    t = time.time()
    out = generate(model, tokenizer, prompt=prompt, max_tokens=120, verbose=False)
    dt = time.time() - t

    main, bullets, want = parse_card(out)
    render(main, bullets, want)
    print(f"\033[2m  ({len(chunk.split())} words in · {dt*1000:.0f} ms · cleaned from raw model output)\033[0m")
