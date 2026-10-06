# RTX ASR spike — Parakeet + Whisper large-v3 on the RTX desktop

Goal: the raw transcripts and per-chunk speed of the two models we would run on the RTX
desktop, on the Nigerian room recording (`2026-10-06_17-10-11.m4a`). The output is scored on
the Mac against the reference transcript, with and without the Codex correction pass.

`transcribe_rtx.py` cleans the audio (80 Hz–7.5 kHz band-pass, equal loudness per chunk),
splits it on silence into chunks of at most 20 s, and runs **both** models on the **same**
chunks. Checked on the Mac CPU (first 60 s, `tiny.en`); not yet run on CUDA.

## Run it (PowerShell on the RTX desktop)

Put this folder and the `.m4a` on the RTX machine, then from this folder:

```powershell
py -3.12 -m venv .venv
.venv\Scripts\Activate.ps1
pip install faster-whisper "av<16" "onnx-asr[gpu,hub]" nvidia-cublas-cu12 "nvidia-cudnn-cu12==9.*" nvidia-cuda-runtime-cu12 nvidia-cufft-cu12 nvidia-curand-cu12
python transcribe_rtx.py C:\path\to\2026-10-06_17-10-11.m4a
```

The first run downloads about 3 GB of model weights (Whisper large-v3 and Parakeet).

## What comes back

Next to the `.m4a`:

| File | Contents |
|---|---|
| `…parakeet.txt` | Parakeet output, one line per chunk, `[mm:ss] text` |
| `…whisper-large-v3.txt` | Whisper large-v3 output on the same chunks |
| `…timing.json` | Total time and per-chunk decode time (p50, max) for each model |

Bring all three back to the Mac.

## If it looks wrong

- **`av` error about `metadata_errors`** — `av` 16+ is installed; keep the `"av<16"` pin.
- **Parakeet `chunk_ms_p50` above ~100 ms** — onnxruntime fell back to the CPU. Its log
  names the missing CUDA DLL; the installed `onnxruntime-gpu` may want CUDA 13 rather than 12,
  in which case install the `-cu13` versions of the `nvidia-*` packages instead.
- **`External data path escapes model directory`** — the script already loads Parakeet from
  `models\` beside it for this reason; delete `models\` and rerun.
