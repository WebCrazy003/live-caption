#!/usr/bin/env python3
"""
SPEC-10 live-summary spike (Python / mlx-lm).

The Swift/MLX path is blocked from a bare `swift build` CLI (mlx-swift ships no
metallib build step, so `default.metallib` is never produced — it needs Xcode's
build system). So — exactly as the ASR architecture was proven in Python
(spike/bench.py) before the Swift app — we measure the SAME model on the SAME
MLX runtime here:

  B6  memory footprint of the resident 1B LLM (mx peak GPU memory)
  B7  generation latency + tokens/sec for a ~100-word block
  B8  summary quality on messy, ASR-style transcript text (eyeball output)

Usage: python spike.py [model_dir_or_hf_id]
Default loads the already-downloaded local Llama-3.2-1B-Instruct-4bit.
"""
import sys, time, resource
import mlx.core as mx
from mlx_lm import load, generate

MODEL = sys.argv[1] if len(sys.argv) > 1 else \
    "/Users/minimac/Library/Caches/models/mlx-community/Llama-3.2-1B-Instruct-4bit"

# ---- Prompt (mirrors SPEC-10 §"Output format") ----------------------------
SYSTEM = (
    "You help a non-native English speaker follow a live call. "
    "Read the transcript part and say, in very simple English (short words, short lines), "
    "what the other person wants. Lead with the main ask. Do NOT invent anything that is "
    "not in the text. If nothing important, say so.\n\n"
    "Answer ONLY in this exact format:\n"
    "MAIN: <one short line: the single most important ask/point>\n"
    "- <short point in simple words>\n"
    "- <short point in simple words>\n"
    "WANT: <what they want you to do, or \"-\" if none>"
)

def user(chunk):
    return f'Transcript part:\n"{chunk}"\n\nNow write the card.'

# ---- Realistic ~100-word ASR-style chunks (one speaker: the client) --------
CHUNKS = [
    ("project scope (rambling -> ask)",
     "so yeah basically the thing is we launched the new site back in march and it was fine "
     "for a while but lately the checkout has been really slow especially on mobile and a few "
     "customers have complained on twitter which is not great for us um and we also want to add "
     "that new payment method the one everyone keeps asking about and honestly the whole thing "
     "feels a bit dated so what i really need from you is a proper estimate on how long it would "
     "take to fix the checkout speed first and then maybe we talk about the redesign after that"),
    ("billing complaint",
     "i've been charged twice this month and i really don't understand why because i only have "
     "the one subscription the basic plan and i checked my bank and there are two charges on the "
     "fourth and the ninth both for the same amount and i already emailed support last week but "
     "nobody got back to me and i'm getting a little frustrated to be honest so i need someone to "
     "actually look at my account today refund the extra charge and tell me it won't happen again "
     "next month because otherwise i'm going to cancel"),
    ("jargon-heavy technical (easy-English + hallucination test)",
     "right so our current stack is a monolith on kubernetes and the p99 latency on the api "
     "gateway has been creeping up and we think it's the n plus one queries in the orm plus we're "
     "not caching the auth tokens so every request hits the identity provider and the on call "
     "rotation is getting paged constantly so what we'd like is for you to come in do an audit of "
     "the hot paths maybe introduce a read replica and a redis layer and help us set some slos so "
     "the team stops firefighting every single night"),
]

def rss_mb():
    return resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / (1024 * 1024)  # macOS: bytes

print("=" * 56)
print(f"SPEC-10 summary spike (Python/mlx-lm) — {MODEL.split('/')[-1]}")
print("=" * 56)

t0 = time.time()
model, tokenizer = load(MODEL)
load_s = time.time() - t0
mx.eval(mx.zeros(1))  # force metal init
model_mem = mx.get_active_memory() / 1e9
print(f"load: {load_s:.1f}s  |  mx active memory after load: {model_mem:.2f} GB  |  proc RSS: {rss_mb():.0f} MB\n")

lat, tps = [], []
for i, (label, text) in enumerate(CHUNKS, 1):
    words = len(text.split())
    print(f"---- block {i}/{len(CHUNKS)}: {label}  ({words} words) ----")
    msgs = [{"role": "system", "content": SYSTEM}, {"role": "user", "content": user(text)}]
    prompt = tokenizer.apply_chat_template(msgs, add_generation_prompt=True)

    t = time.time()
    out = generate(model, tokenizer, prompt=prompt, max_tokens=120, verbose=False)
    dt = time.time() - t
    ntok = len(tokenizer.encode(out))
    lat.append(dt * 1000); tps.append(ntok / dt if dt else 0)
    print(out.strip())
    print(f"  ⏱  {dt*1000:.0f} ms  ·  ~{ntok/dt:.1f} tok/s  ·  {ntok} out tokens\n")

peak = mx.get_peak_memory() / 1e9
avg = lambda xs: sum(xs) / len(xs) if xs else 0
print("=" * 56)
print("SUMMARY")
print(f"  model load        : {load_s:.1f} s")
print(f"  mx active memory  : {model_mem:.2f} GB (weights resident)")
print(f"  mx PEAK memory    : {peak:.2f} GB (during generation)")
print(f"  process peak RSS  : {rss_mb():.0f} MB")
print(f"  gen latency (avg) : {avg(lat):.0f} ms per ~100-word block")
print(f"  throughput  (avg) : {avg(tps):.1f} tok/s")
print("  context: 100 words ~= ~40s of speech, so a ~{:.0f}ms card fits easily in the gap".format(avg(lat)))
print("=" * 56)
