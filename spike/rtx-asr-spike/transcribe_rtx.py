"""Transcribe one recording with Parakeet-TDT 0.6B v2 and Whisper large-v3, on the same chunks.

Run on the RTX desktop (see README.md next to this file). Writes, next to the audio:
  <name>.parakeet.txt   <name>.whisper-large-v3.txt   <name>.timing.json
Both .txt files use the same chunk boundaries, so line N in one matches line N in the other.
"""
import argparse, glob, json, os, statistics, sys, time

# Windows: make the CUDA 12 / cuDNN 9 DLLs from the nvidia-* pip packages findable.
if sys.platform == "win32":
    import site
    for sp in site.getsitepackages() + [site.getusersitepackages()]:
        for d in glob.glob(os.path.join(sp, "nvidia", "*", "bin")):
            os.add_dll_directory(d)
            os.environ["PATH"] = d + os.pathsep + os.environ["PATH"]

import numpy as np
from faster_whisper import WhisperModel, decode_audio
from faster_whisper.vad import VadOptions, get_speech_timestamps

SR = 16000


def clean(audio):
    """Band-pass 80 Hz - 7.5 kHz: drops room rumble and hiss."""
    spec = np.fft.rfft(audio)
    freqs = np.fft.rfftfreq(len(audio), 1 / SR)
    spec[(freqs < 80) | (freqs > 7500)] = 0
    return np.fft.irfft(spec, n=len(audio)).astype(np.float32)


def level(chunk, target_rms=0.05):
    """Bring every chunk to the same loudness, so quiet speakers come up."""
    rms = float(np.sqrt(np.mean(chunk ** 2))) or 1e-9
    return np.clip(chunk * (target_rms / rms), -1, 1).astype(np.float32)


def stamp(sec):
    m, s = divmod(int(sec), 60)
    return f"[{m:02d}:{s:02d}]"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("audio")
    ap.add_argument("--device", default="cuda", choices=["cuda", "cpu"])
    ap.add_argument("--whisper-model", default="large-v3")
    ap.add_argument("--seconds", type=float, default=0, help="only the first N seconds (0 = all)")
    a = ap.parse_args()

    audio = decode_audio(a.audio, sampling_rate=SR)
    if a.seconds:
        audio = audio[: int(a.seconds * SR)]
    audio = clean(audio)
    chunks = get_speech_timestamps(audio, VadOptions(max_speech_duration_s=20, min_silence_duration_ms=300, speech_pad_ms=200))
    print(f"{len(audio)/SR:.0f} s audio -> {len(chunks)} speech chunks")
    if not chunks:
        sys.exit("no speech found")

    import onnxruntime as ort
    if a.device == "cuda":
        if hasattr(ort, "preload_dlls"):
            ort.preload_dlls()
        if "CUDAExecutionProvider" not in ort.get_available_providers():
            sys.exit("onnxruntime has no CUDA provider - install onnxruntime-gpu (see README.md)")
    import onnx_asr
    providers = ["CUDAExecutionProvider", "CPUExecutionProvider"] if a.device == "cuda" else ["CPUExecutionProvider"]
    # A plain folder, not the Hugging Face cache: newer onnxruntime refuses the cache's
    # symlinked weight files ("External data path escapes model directory").
    from huggingface_hub import snapshot_download
    pdir = snapshot_download("istupakov/parakeet-tdt-0.6b-v2-onnx",
                             local_dir=os.path.join(os.path.dirname(os.path.abspath(__file__)), "models", "parakeet-tdt-0.6b-v2"))
    parakeet = onnx_asr.load_model("nemo-parakeet-tdt-0.6b-v2", pdir, providers=providers)
    whisper = WhisperModel(a.whisper_model, device=a.device, compute_type="float16" if a.device == "cuda" else "int8")

    base = os.path.splitext(a.audio)[0]
    wname = a.whisper_model.replace("/", "_")
    lat = {"parakeet": [], "whisper": []}
    with open(f"{base}.parakeet.txt", "w", encoding="utf-8") as fp, \
         open(f"{base}.whisper-{wname}.txt", "w", encoding="utf-8") as fw:
        for i, c in enumerate(chunks):
            piece = level(audio[c["start"]: c["end"]])
            t = time.perf_counter()
            ptxt = parakeet.recognize(piece, sample_rate=SR).strip()
            lat["parakeet"].append(time.perf_counter() - t)
            t = time.perf_counter()
            segs, _ = whisper.transcribe(piece, language="en", beam_size=5,
                                         condition_on_previous_text=False, vad_filter=False)
            wtxt = " ".join(s.text.strip() for s in segs).strip()
            lat["whisper"].append(time.perf_counter() - t)
            ts = stamp(c["start"] / SR)
            fp.write(f"{ts} {ptxt}\n")
            fw.write(f"{ts} {wtxt}\n")
            print(f"{i+1}/{len(chunks)} {ts} P: {ptxt[:60]}")

    # Drop the first chunk from latency stats: it includes CUDA warm-up.
    summary = {
        "device": a.device, "audio_s": round(len(audio) / SR, 1), "chunks": len(chunks),
        "chunk_s_median": round(statistics.median((c["end"] - c["start"]) / SR for c in chunks), 1),
        **{f"{k}_total_s": round(sum(v), 1) for k, v in lat.items()},
        **{f"{k}_chunk_ms_p50": round(statistics.median(v[1:] or v) * 1000) for k, v in lat.items()},
        **{f"{k}_chunk_ms_max": round(max(v[1:] or v) * 1000) for k, v in lat.items()},
    }
    with open(f"{base}.timing.json", "w") as f:
        json.dump(summary, f, indent=2)
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
