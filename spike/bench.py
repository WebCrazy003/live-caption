#!/usr/bin/env python3
"""
Real-time captioning spike for Local Caption.

Measures the numbers that decide the ASR architecture on THIS Mac:
  1. Accuracy     - transcript vs known ground truth
  2. Decode speed - real-time factor (RTF = decode_time / audio_duration)
  3. Interim cadence - sliding-window partial decode time
  4. Final latency  - decode time for a full utterance window

Engines:
  faster-whisper  -> CPU (CTranslate2, the original draft plan)
  mlx             -> Apple-Silicon GPU (proxy for WhisperKit's acceleration)

Usage:
  python bench.py <engine> <model> <audio.wav>
    engine = fw | mlx
    fw  model e.g.  large-v3-turbo | small.en | distil-small.en
    mlx model e.g.  mlx-community/whisper-large-v3-turbo
"""
import sys, time, wave
import numpy as np

def load_wav_16k_mono(path):
    with wave.open(path, "rb") as w:
        assert w.getframerate() == 16000, f"expected 16k, got {w.getframerate()}"
        assert w.getnchannels() == 1, "expected mono"
        sw = w.getsampwidth()
        raw = w.readframes(w.getnframes())
    if sw == 2:  # int16 PCM
        return (np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0)
    if sw == 4:  # float32 PCM
        return np.frombuffer(raw, dtype=np.float32).copy()
    raise ValueError(f"unsupported sample width {sw}")

def rms(x):
    if len(x) == 0:
        return 0.0
    return float(np.sqrt(np.mean(x * x)))

# ---- engine wrappers -------------------------------------------------------
class FWEngine:
    name = "faster-whisper (CPU int8)"
    def __init__(self, model):
        from faster_whisper import WhisperModel
        self.m = WhisperModel(model, device="cpu", compute_type="int8")
    def transcribe(self, audio):
        # temperature=0 (scalar) disables the fallback ladder -> single decode pass,
        # which is what a real streaming impl would use.
        segs, _ = self.m.transcribe(audio, language="en", beam_size=1, temperature=0.0,
                                    vad_filter=False, condition_on_previous_text=False)
        return "".join(s.text for s in segs).strip()

class MLXEngine:
    name = "mlx-whisper (Apple GPU)"
    def __init__(self, model):
        import mlx_whisper
        self.mlx_whisper = mlx_whisper
        self.repo = model
        # warm the model into memory
    def transcribe(self, audio):
        r = self.mlx_whisper.transcribe(audio, path_or_hf_repo=self.repo,
                                        language="en", fp16=True)
        return r["text"].strip()

def make_engine(engine, model):
    return FWEngine(model) if engine == "fw" else MLXEngine(model)

# ---- main ------------------------------------------------------------------
def main():
    if len(sys.argv) < 4:
        print(__doc__); sys.exit(2)
    engine, model, path = sys.argv[1], sys.argv[2], sys.argv[3]
    SR = 16000

    print("=" * 60)
    print(f"engine : {engine}   model: {model}")
    print(f"audio  : {path}")
    print("=" * 60)

    t = time.time()
    eng = make_engine(engine, model)
    print(f"[load] engine constructed in {time.time()-t:.1f}s ({eng.name})")

    audio = load_wav_16k_mono(path)
    dur = len(audio) / SR
    print(f"[audio] {len(audio)} samples = {dur:.2f}s\n")

    # warm-up (first decode pays model download + graph build)
    print("[warmup] first decode (includes model download on first run)...")
    tw = time.time()
    _ = eng.transcribe(audio[: SR * 2] if len(audio) > SR * 2 else audio)
    print(f"[warmup] done in {time.time()-tw:.1f}s\n")

    # ---- Phase 1: full-file accuracy + RTF --------------------------------
    print("---- PHASE 1: full-file decode (accuracy + RTF) ----")
    t = time.time()
    text = eng.transcribe(audio)
    d = time.time() - t
    print(f"  transcript : {text}")
    print(f"  decode     : {d:.2f}s for {dur:.2f}s audio")
    print(f"  RTF        : {d/dur:.2f}x  (<1.0 = faster than real time)\n")

    # ---- Phase 2: streaming simulation ------------------------------------
    print("---- PHASE 2: streaming sim (interim cadence + final latency) ----")
    hop = int(0.1 * SR)          # 100 ms
    interim_hops = 5             # 500 ms interim cadence
    endpoint_ms = 600
    silence_rms = 0.006
    max_win_s = 28.0

    fed = 0; utter_start = 0; has_speech = False; silence_ms = 0; since_interim = 0
    interim_times = []; final_times = []
    while fed < len(audio):
        nxt = min(fed + hop, len(audio))
        level = rms(audio[fed:nxt]); fed = nxt; since_interim += 1
        if level >= silence_rms:
            has_speech = True; silence_ms = 0
        elif has_speech:
            silence_ms += int((nxt - (nxt - hop)) / SR * 1000)
        clock = fed / SR
        win_s = (fed - utter_start) / SR

        if has_speech and since_interim >= interim_hops and silence_ms == 0:
            since_interim = 0
            t = time.time(); txt = eng.transcribe(audio[utter_start:fed]); dt = (time.time()-t)*1000
            interim_times.append(dt)
            print(f"  [{clock:6.2f}s] INTERIM ({dt:4.0f}ms): {txt}")

        if (has_speech and silence_ms >= endpoint_ms) or win_s >= max_win_s:
            t = time.time(); txt = eng.transcribe(audio[utter_start:fed]); dt = (time.time()-t)*1000
            final_times.append(dt)
            print(f"  [{clock:6.2f}s] >>> FINAL ({dt:4.0f}ms decode, ~{dt+endpoint_ms:.0f}ms perceived): {txt}")
            utter_start = fed; has_speech = False; silence_ms = 0; since_interim = 0

    if has_speech and fed > utter_start:
        t = time.time(); txt = eng.transcribe(audio[utter_start:fed]); dt = (time.time()-t)*1000
        final_times.append(dt)
        print(f"  [{fed/SR:6.2f}s] >>> FINAL (flush {dt:4.0f}ms): {txt}")

    print("\n---- SUMMARY ----")
    if interim_times:
        print(f"  interim decode: n={len(interim_times)} avg={np.mean(interim_times):.0f}ms max={np.max(interim_times):.0f}ms  (cadence budget=500ms)")
    if final_times:
        print(f"  final  decode : n={len(final_times)} avg={np.mean(final_times):.0f}ms  -> ~{np.mean(final_times)+endpoint_ms:.0f}ms perceived latency")
    print()

if __name__ == "__main__":
    main()
