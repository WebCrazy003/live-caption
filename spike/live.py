#!/usr/bin/env python3
"""
Live streaming captioner — the hybrid architecture, running for real.

  interim partials : faster-whisper tiny.en (CPU)   ~200ms
  final captions   : mlx large-v3-turbo (Apple GPU) ~1.5s + endpoint wait

Captures 16kHz mono from an input device, does energy VAD + endpointing,
prints INTERIM (provisional) and FINAL (committed) lines with latency.

Usage:  python live.py [device_index] [max_seconds]
Ctrl-C to stop.
"""
import sys, time, queue, threading
import numpy as np
import sounddevice as sd

DEVICE = int(sys.argv[1]) if len(sys.argv) > 1 else None
MAX_S = float(sys.argv[2]) if len(sys.argv) > 2 else 180.0
SR = 16000
HOP = int(0.1 * SR)              # 100 ms
INTERIM_HOPS = 5                 # 500 ms
ENDPOINT_MS = 600
MAX_WIN_S = 15.0

print("[init] loading models (cached)...", flush=True)
from faster_whisper import WhisperModel
import mlx_whisper
interim_m = WhisperModel("tiny.en", device="cpu", compute_type="int8")
MLX_REPO = "mlx-community/whisper-large-v3-turbo"

# C7 silence-hallucination filter
BLOCK = {"thank you", "thanks for watching", "thank you very much", "you", "bye",
         "thank you for watching", "thanks", "please subscribe", "so", "the",
         "thank you so much", "okay", "bye bye"}
def _stem(t):
    return t.lower().strip().strip(".!?,").strip()

def norm(a):
    """Peak-normalize quiet audio so Whisper sees a healthy level."""
    p = float(np.max(np.abs(a))) if len(a) else 0.0
    return (a / p) * 0.6 if p > 1e-4 else a

def decode_interim(a):
    segs, _ = interim_m.transcribe(norm(a), language="en", beam_size=1, temperature=0.0,
                                   vad_filter=False, condition_on_previous_text=False)
    txt = "".join(s.text for s in segs).strip()
    return "" if _stem(txt) in BLOCK else txt

def decode_final(a):
    r = mlx_whisper.transcribe(norm(a), path_or_hf_repo=MLX_REPO, language="en", fp16=True)
    return r["text"].strip(), r

def keep_final(text, audio, r):
    """Return text, or '' if it looks like a silence hallucination."""
    if not text:
        return ""
    if _stem(text) in BLOCK:
        return ""
    segs = r.get("segments", []) if isinstance(r, dict) else []
    ns = [sg.get("no_speech_prob", 0.0) for sg in segs]
    if ns and (sum(ns) / len(ns)) > 0.6:
        return ""
    cr = [sg.get("compression_ratio", 0.0) for sg in segs]
    if cr and max(cr) > 2.4:          # repetition loop ("come come come")
        return ""
    if rms(audio) < 0.008:           # only drop genuinely-silent windows
        return ""
    return text

# warm both engines so first real utterance isn't slow
_ = decode_interim(np.zeros(SR, np.float32))
_ = decode_final(np.zeros(SR, np.float32))
print("[init] models ready.", flush=True)

q = queue.Queue()
def audio_cb(indata, frames, t, status):
    if status:
        print(f"[audio] {status}", flush=True)
    q.put(indata[:, 0].copy())   # first channel, float32

def rms(x):
    return float(np.sqrt(np.mean(x * x))) if len(x) else 0.0

print(f"[start] device={DEVICE} sr={SR} — calibrating noise floor for 1s...", flush=True)
stream = sd.InputStream(device=DEVICE, channels=1, samplerate=SR,
                        blocksize=HOP, dtype="float32", callback=audio_cb)
stream.start()

# --- noise calibration ---
cal = []
t_cal = time.time()
while time.time() - t_cal < 1.0:
    try:
        cal.append(q.get(timeout=0.5))
    except queue.Empty:
        pass
noise = rms(np.concatenate(cal)) if cal else 0.0
# cap the threshold so a noisy mic can't push it above real speech level
THRESH = float(sys.argv[3]) if len(sys.argv) > 3 else min(max(0.012, noise * 1.8), 0.05)
print(f"[start] noise floor={noise:.4f}  speech threshold={THRESH:.4f}", flush=True)
print("[start] SPEAK / PLAY NOW. Ctrl-C to stop.\n", flush=True)

buf = np.zeros(0, np.float32)
utter = np.zeros(0, np.float32)
has_speech = False
silence_ms = 0
since_interim = 0
t0 = time.time()
meter_max = 0.0
meter_hops = 0

try:
    while time.time() - t0 < MAX_S:
        try:
            block = q.get(timeout=0.5)
        except queue.Empty:
            continue
        buf = np.concatenate([buf, block])
        while len(buf) >= HOP:
            hop = buf[:HOP]; buf = buf[HOP:]
            utter = np.concatenate([utter, hop])
            level = rms(hop)
            since_interim += 1
            meter_max = max(meter_max, level); meter_hops += 1
            if meter_hops >= 20:   # ~2s level meter
                print(f"[level] max RMS last 2s = {meter_max:.4f}  (threshold {THRESH:.4f})", flush=True)
                meter_max = 0.0; meter_hops = 0
            if level >= THRESH:
                has_speech = True; silence_ms = 0
            elif has_speech:
                silence_ms += 100

            if has_speech and since_interim >= INTERIM_HOPS and silence_ms == 0:
                since_interim = 0
                t = time.time(); txt = decode_interim(utter); dt = (time.time()-t)*1000
                if txt:
                    print(f"  ... interim ({dt:4.0f}ms): {txt}", flush=True)

            if (has_speech and silence_ms >= ENDPOINT_MS) or len(utter)/SR >= MAX_WIN_S:
                t = time.time(); txt, r = decode_final(utter); dt = (time.time()-t)*1000
                txt = keep_final(txt, utter, r)
                if txt:
                    print(f"FINAL ({dt:4.0f}ms decode, ~{dt+ENDPOINT_MS:.0f}ms perceived): {txt}", flush=True)
                utter = np.zeros(0, np.float32)
                has_speech = False; silence_ms = 0; since_interim = 0
except KeyboardInterrupt:
    pass
finally:
    if has_speech and len(utter) > SR*0.3:
        txt, r = decode_final(utter)
        txt = keep_final(txt, utter, r)
        if txt:
            print(f"FINAL (flush): {txt}", flush=True)
    stream.stop(); stream.close()
    print("\n[stop] done.", flush=True)
