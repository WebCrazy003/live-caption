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

    def transcribe(self, audio: np.ndarray) -> str:
        raise NotImplementedError


class NemoBackend(Backend):
    def __init__(self, entry, path, cuda=True):
        import onnxruntime as ort
        if cuda and hasattr(ort, "preload_dlls"):
            ort.preload_dlls()
        import onnx_asr
        providers = ["CUDAExecutionProvider", "CPUExecutionProvider"] if cuda else ["CPUExecutionProvider"]
        self.model = onnx_asr.load_model(entry["onnx_name"], path, providers=providers)

    def transcribe(self, audio):
        return self.model.recognize(audio, sample_rate=SR).strip()


class WhisperBackend(Backend):
    def __init__(self, entry, path, cuda=True):
        from faster_whisper import WhisperModel
        self.model = WhisperModel(path, device="cuda" if cuda else "cpu",
                                  compute_type="float16" if cuda else "int8")

    def transcribe(self, audio):
        segments, _ = self.model.transcribe(audio, language="en", beam_size=5,
                                            condition_on_previous_text=False, vad_filter=False)
        return " ".join(s.text.strip() for s in segments).strip()


class FakeBackend(Backend):
    """Stands in for a real model in tests (`agent.py --fake`)."""

    def __init__(self, entry, path, cuda=True):
        self.id = entry["id"]
        time.sleep(0.05)

    def transcribe(self, audio):
        return f"{self.id} heard {len(audio)} samples"


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


def _folder_bytes(path):
    # Counts the in-progress `.incomplete` files under .cache too, so a single 3 GB file moves.
    return sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(path) for f in fs)
