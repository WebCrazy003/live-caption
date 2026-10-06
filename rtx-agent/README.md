# LocalCaption RTX agent

Runs **Accent mode**'s speech models ([SPEC-18](../specs/SPEC-18-accent-pipeline.md)) on a
Windows PC with an NVIDIA GPU, for the LocalCaption Mac app on the same network. It holds no
models until the Mac asks, loads the pair the Mac picked, transcribes each utterance with both,
and unloads them after 15 idle minutes.

## Install (PowerShell on the RTX desktop)

From this folder, with Python 3.12:

```powershell
py -3.12 -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
.venv\Scripts\python -m pip uninstall -y onnxruntime
.venv\Scripts\python -m pip install --force-reinstall --no-deps "onnxruntime-gpu==1.30.0"
```

The last two lines matter: faster-whisper pulls in the CPU build of onnxruntime, which hides the
GPU build. Then, **as Administrator**:

```powershell
.venv\Scripts\python install.py --start
```

This registers a scheduled task that starts the agent at logon (and restarts it if it stops),
and opens TCP 8765 on **Private** networks only. Check that your LAN is Private:
`Get-NetConnectionProfile`. Undo with `install.py --uninstall`.

## Pair with the Mac

On first start the agent shows a **6-digit pairing code** (also in `state\pairing-code.txt`).
In LocalCaption on the Mac: Settings → Accent mode → pick this PC (or type its address) → enter
the code. Each Mac gets its own token, kept in `state\tokens.json`; delete an entry there, or
press *Unpair* on the Mac, to revoke it. A new code is made each time the agent starts.

## Models

`catalog.json` lists what the Mac can choose. Defaults: **Parakeet TDT 0.6B v2** (primary) and
**Whisper large-v3** (secondary) — the measured pair. The first load downloads a model into
`models\` (2–4 GB each). If downloads fail on your network, copy the model folder from another
machine and add an empty `.complete` file inside it.

## API

| Call | Auth | |
|---|---|---|
| `GET /hello` | – | name, version, GPU, total VRAM, paired? |
| `POST /pair` `{code, name}` | – | → `{token}`; 5 wrong codes lock pairing for 10 min |
| `DELETE /pair` | token | revoke this token |
| `GET /models` | token | catalog with `downloaded` / `loaded` |
| `POST /load` `{primary, secondary}` | token | starts download/load/warm in the background (202); 409 if busy or not enough VRAM |
| `GET /status` | token | `state` (idle · downloading · loading · warming · ready · error), `model`, `progress`, `loaded`, `vram_used_mb`; also keeps the models loaded |
| `POST /unload` | token | frees the GPU |
| `POST /transcribe?roles=primary[,secondary]&id=N` | token | body: 16 kHz mono PCM16 LE, ≤ 30 s → `{id, primary: {text, ms}, secondary?: {...}}` |

## Develop

```bash
python -m unittest discover -s tests          # fake models, no GPU needed
python agent.py --fake --no-advertise         # run locally with fake models
```

Logs: `state\agent.log`.
