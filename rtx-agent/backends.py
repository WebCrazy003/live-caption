"""Speech models the agent can load: download, load, warm and transcribe.

Everything here runs on the agent's worker threads; nothing is HTTP-aware.
"""
import fnmatch, glob, os, sys, threading, time

# Hugging Face's Xet transfer fails on the RTX's network (SPEC-18 B20); plain HTTP works.
os.environ.setdefault("HF_HUB_DISABLE_XET", "1")

# Windows: make the CUDA DLLs from the nvidia-* pip packages findable. CTranslate2 loads them
# by name through PATH; onnxruntime's preload_dlls() covers its own set.
if sys.platform == "win32":
    import site
    for sp in site.getsitepackages() + [site.getusersitepackages()]:
        for d in glob.glob(os.path.join(sp, "nvidia", "*", "bin")):
            os.add_dll_directory(d)
            os.environ["PATH"] = d + os.pathsep + os.environ["PATH"]

import numpy as np

SR = 16000
# onnx-asr loads the full-precision files; the repos also carry int8 copies we never use.
NEMO_IGNORE = ["*int8*"]


class Backend:
    """No close(): unloading just drops the agent's reference, so a transcription already
    running keeps its model alive until it finishes, and the memory goes with the last reference."""

    def transcribe(self, audio: np.ndarray, words: bool = False):
        """Return (text, words). `words` is [{word, start, end}] in seconds from the start of
        `audio` when asked for (interim captions align on them), else None."""
        raise NotImplementedError


class NemoBackend(Backend):
    def __init__(self, entry, path, cuda=True):
        import onnxruntime as ort
        if cuda and hasattr(ort, "preload_dlls"):
            ort.preload_dlls()
        import onnx_asr
        providers = ["CUDAExecutionProvider", "CPUExecutionProvider"] if cuda else ["CPUExecutionProvider"]
        self.model = onnx_asr.load_model(entry["onnx_name"], path, providers=providers)
        self.timed = self.model.with_timestamps()

    def transcribe(self, audio, words=False):
        if not words:
            return self.model.recognize(audio, sample_rate=SR).strip(), None
        r = self.timed.recognize(audio, sample_rate=SR)
        return r.text.strip(), _join_tokens(r.tokens, r.timestamps, len(audio) / SR)


class WhisperBackend(Backend):
    def __init__(self, entry, path, cuda=True):
        from faster_whisper import WhisperModel
        self.model = WhisperModel(path, device="cuda" if cuda else "cpu",
                                  compute_type="float16" if cuda else "int8")

    def transcribe(self, audio, words=False):
        segments, _ = self.model.transcribe(audio, language="en", beam_size=5, word_timestamps=words,
                                            condition_on_previous_text=False, vad_filter=False)
        segments = list(segments)
        text = " ".join(s.text.strip() for s in segments).strip()
        if not words:
            return text, None
        return text, [{"word": w.word.strip(), "start": round(w.start, 3), "end": round(w.end, 3)}
                      for s in segments for w in (s.words or []) if w.word.strip()]


class FakeBackend(Backend):
    """Stands in for a real model in tests (`agent.py --fake`)."""

    def __init__(self, entry, path, cuda=True):
        self.id = entry["id"]
        time.sleep(0.05)

    def transcribe(self, audio, words=False):
        text = f"{self.id} heard {len(audio)} samples"
        if not words:
            return text, None
        step = len(audio) / SR / 4
        return text, [{"word": w, "start": round(i * step, 3), "end": round((i + 1) * step, 3)}
                      for i, w in enumerate(text.split())]


BACKENDS = {"nemo": NemoBackend, "whisper": WhisperBackend}


def is_downloaded(path):
    return os.path.exists(os.path.join(path, ".complete"))


def download(entry, path, on_progress):
    """Download a catalog model into `path` (a plain folder, not the symlinked HF cache, which
    onnxruntime >= 1.30 refuses). Calls on_progress(fraction) about twice a second."""
    from huggingface_hub import HfApi, snapshot_download
    ignore = NEMO_IGNORE if entry["family"] == "nemo" else None
    info = HfApi().model_info(entry["repo"], files_metadata=True)
    total = sum(s.size or 0 for s in info.siblings
                if not ignore or not any(fnmatch.fnmatch(s.rfilename, p) for p in ignore)) or 1
    done = threading.Event()

    def watch():
        while not done.wait(0.5):
            on_progress(min(0.99, _folder_bytes(path) / total))

    threading.Thread(target=watch, daemon=True).start()
    try:
        snapshot_download(entry["repo"], local_dir=path, ignore_patterns=ignore)
    finally:
        done.set()
    open(os.path.join(path, ".complete"), "w").close()
    on_progress(1.0)


def load(entry, path, fake=False):
    return (FakeBackend if fake else BACKENDS[entry["family"]])(entry, path)


def warm(backend):
    """The first CUDA run compiles kernels; do it before a caption has to wait for it."""
    backend.transcribe(np.zeros(SR, dtype=np.float32))


def _join_tokens(tokens, starts, duration):
    """NeMo subword tokens with start times -> words. A token starting with a space opens a word;
    a word ends where the next one starts (the last one 80 ms after its last token, clipped)."""
    out = []
    for tok, t in zip(tokens, starts):
        if tok.startswith(" ") or not out:
            out.append({"word": tok.strip(), "start": round(float(t), 3)})
        else:
            out[-1]["word"] += tok
    for w, nxt in zip(out, out[1:] + [None]):
        w["end"] = round(nxt["start"] if nxt else min(duration, float(starts[-1]) + 0.08), 3)
    return [w for w in out if w["word"]]


def _folder_bytes(path):
    # Counts the in-progress `.incomplete` files under .cache too, so a single 3 GB file moves.
    return sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(path) for f in fs)
