# B0 results — Local Caption for Windows

Stage B0 of [`../SPEC-WINDOWS.md`](../SPEC-WINDOWS.md): measure the machine before writing
app code against assumptions about it. The macOS equivalent is [`../spike/RESULTS.md`](../spike/RESULTS.md),
which killed that build's original CPU plan outright. This one is less lucky.

**Run:** 2026-09-20, on the target machine itself.
**Machine:** ASUS ROG Zephyrus G15 GA503QR · Ryzen 9 5900HS (8C/16T, AVX2) · RTX 3070
Laptop 8 GB, **driver 616.92 (CUDA 13)** · 32 GB · Windows 11 Pro 26200 · .NET SDK 10.0.401.
(The driver was 555.97 for most of this work; §1 records why it had to move.)
**Clip:** first 6.00 s of whisper.cpp's `samples/jfk.wav` — 16 kHz mono, public domain.
Six seconds is one interim window (§5.4 question 2), so every decode number below is the
cost of one interim decode.
**Remote session:** a Jump Desktop session was live throughout, which is what made the W9
probe possible (§4.7.3 asks for exactly that).

---

## What B0 answered

| | Question | Answer |
|---|---|---|
| **W9** | Loopback on the Jump Desktop Virtual Speaker | ✅ **Yes — with one condition.** Initialises, correct mix format, monotonic device position. Delivers nothing unless a render stream exists |
| **§4.3** | The silence gap | ✅ **Real, and worse than specified on the virtual endpoint.** It also corrects the spec's mitigation gate — see below |
| **§4.4** | Format normalisation | ✅ Both endpoints: 48 kHz · 2 ch · 32-bit float, integer resample ratio |
| **W7** | Hybrid vs turbo-only | ✅ **Keep the hybrid.** Turbo alone clears the interim budget in isolation (420 ms) and **fails it under contention (690 ms)** |
| **§5.3** | Is `large-v3-turbo` the right final default | ✅ **Yes** — 416/420 ms against a ~2000 ms budget, and the transcripts are visibly better |
| **W2** | Are DTW word timings accurate enough | ✅ **Yes — keep the word-timed merge.** Every overlapping window pair offered an anchor inside 350 ms |

B0 is complete. The audio half produced two spec corrections; the ASR half confirmed one
default, killed one tempting simplification, and cost a driver update to get at.

---

## 1. The blocker (resolved 2026-09-20): CUDA needed a newer driver

§0.5 recorded "driver 555.97, **CUDA 12.5**" and §5.2 concluded "CUDA is the backend,
bundle the runtime in the installer". Both halves of that turn out to be unbuildable today,
for two independent reasons.

**Whisper.net's current CUDA runtime is built against CUDA 13.** The native
`ggml-cuda-whisper.dll` in `Whisper.net.Runtime.Cuda.Windows` **1.9.0 and later** imports
`cublas64_13.dll` and `nvcudart_hybrid64.dll`. CUDA 13 needs a **r580-series driver**; this
machine has 555.97. The library therefore cannot load at all — and Whisper.net's response
to a library that will not load is to fall back to the CPU one **without throwing or
logging anything**. The first run of the benchmark reported `cuda` in its header and
produced tiny.en numbers identical to CPU. That silence is now fixed (see §4 below).

**The last CUDA-12 build aborts the process when it does load.** `1.8.1` — the newest
release built against CUDA 12 (`cublas64_12.dll`, `cudart64_12.dll`) — loads correctly once
the CUDA 12.5 redistributable libraries are on the machine, and then dies inside ggml's
static initialiser before decoding anything:

```
D:\a\whisper.net\whisper.net\whisper.cpp\ggml\src\ggml.cpp:22:
GGML_ASSERT(prev != ggml_uncaught_exception) failed
```

That is the **same upstream defect class** A2 documented on macOS: a second ggml static
initialiser running in one process. Isolated by experiment:

| Configuration | Result |
|---|---|
| 1.9.1, CPU + CUDA packages, no CUDA libs present | runs, silently on CPU — the CUDA library never loads |
| 1.8.1, CPU + CUDA packages, CUDA 12.5 libs present | **abort** in `ggml.cpp:22` |
| 1.8.1, **CUDA package only**, CUDA 12.5 libs present | **abort** — so it is not caused by bundling both |
| 1.8.1, CUDA package only, CUDA libs removed | clean failure: "Native Library not found" |
| 1.8.0, same | **abort** |
| 1.7.4 | does not compile — `SegmentData.NoSpeechProbability` does not exist yet, and §5.6's metadata filter needs it |

The abort appears **only when `ggml-cuda-whisper.dll` genuinely loads**, which is why
removing `cudart`/`cublas` makes it go away. Nothing about the failure is specific to this
code: it happens before any model is opened.

### What this means

The port's central premise — §0.5's "CUDA is comfortably the strongest target this app has
ever run on" — is not disproved, it is **unmeasured**. The CPU numbers below show the
fallback cannot carry the design, so this has to be resolved before B2 rather than worked
around.

**Recommended: update the NVIDIA driver to the r580 series or newer, then re-run B0.**
That moves the machine onto Whisper.net 1.9.1 + CUDA 13, which is the combination upstream
currently ships and tests; the aborting binaries are a year-old build line nobody is fixing.
The RTX 3070 (Ampere, compute 8.6) is supported by CUDA 13. It is an owner decision —
§5.8 already flags that this laptop's GPU stack is managed through Armoury Crate and the
MUX switch — and it is the only step that unblocks W7, W2 and §5.3 together.

### The no-driver route was tried, and it does not work

Before accepting the driver as a dependency: whisper.cpp publishes **prebuilt Windows
`whisper-cublas-12.4.0-bin-x64` binaries**, which this driver supports, and they bundle their
own `cublas64_12` / `cudart64_12`. Dropping them into Whisper.net's runtime folder was the
obvious way to get CUDA today.

It does not work, for a structural reason. **Whisper.net builds its own copy of whisper.cpp
with renamed outputs** — `whisper.dll`, `ggml-whisper.dll`, `ggml-base-whisper.dll`,
`ggml-cuda-whisper.dll` — and those names are baked into each library's import table.
whisper.cpp's stock build ships `ggml.dll`, `ggml-base.dll`, `ggml-cuda.dll`. Measured both
ways:

| Attempt | Result |
|---|---|
| stock DLLs added alongside Whisper.net's | `cuda→cpu` — the loader uses its own names, whose CUDA variant still wants cuBLAS 13 |
| only stock DLLs present | `cuda→cpu` — the cuda folder no longer matches, so it falls back to the CPU runtime |

Renaming the files cannot fix it: the imports inside them would still point at the old names.

**So the fallbacks, in order of preference:**

1. **Update the driver** to r580+ and use Whisper.net 1.9.1 + CUDA 13 — what upstream ships
   and tests. One owner decision, then half a day.
2. **Drop Whisper.net and P/Invoke whisper.cpp's stock DLLs directly.** Entirely within this
   project's control, needs no driver change and no admin, and it would also remove the
   upstream defect class that has now bitten on *both* platforms (the ggml double-load abort
   on macOS in A2, and the CUDA-12 abort here). The cost is real: reimplementing the factory,
   processor, segment and token-probability surface, and the DTW word timings W2 depends on
   — several hundred lines of interop, replacing a dependency that otherwise works.
3. **Build Whisper.net's own fork with CUDA 12** from source. Needs the CUDA toolkit and
   MSVC, and the toolkit install wants admin — so it is not actually the cheap option.
4. **Accept CPU-only**, i.e. finals-only captions and a rewrite of §5.3 and §13.

Option 2 is the one to reach for if the driver cannot move.

### Resolved

**The driver was updated to 616.92 and CUDA works.** What it took, and the one thing that was
guesswork before:

| Piece | Where it came from |
|---|---|
| `nvcuda.dll` | the driver, already present |
| **`nvcudart_hybrid64.dll`** | **the driver** — `System32\DriverStore\FileRepository
vami.inf_*`. In no CUDA redistributable archive, and not on the loader's search path, so it has to be copied out |
| `cublas64_13.dll`, `cublasLt64_13.dll`, `cudart64_13.dll` | NVIDIA's CUDA 13.4 redistributable archives |

The open question below — whether `nvcudart_hybrid64.dll` was driver-supplied — is answered:
**yes**, but it ships somewhere the loader will not look. `build/package.ps1` now takes it
from the driver store automatically.

First run after that: `cuda→cuda`, tiny.en at 98/114 ms against 597/700 on the CPU.

### The checklist, as it was followed

**[DECISION, owner, 2026-09-20] Update the driver.** The steps, in order:

1. **Update the NVIDIA driver to r580 or newer** — NVIDIA App, or a manual Game Ready /
   Studio download for the RTX 3070 Laptop. On this laptop, expect Armoury Crate to have
   opinions: check afterwards that the MUX/Optimus mode is unchanged and that
   `nvidia-smi` still lists the dGPU.
2. **Confirm:** `nvidia-smi --query-gpu=driver_version --format=csv` reports ≥ 580.
3. **Add the CUDA 13 runtime libraries.** The driver does not ship cuBLAS, and
   `ggml-cuda-whisper.dll` needs `cublas64_13.dll`, `cublasLt64_13.dll` and a CUDA 13
   runtime. No toolkit install is needed — NVIDIA publishes the redistributables as plain
   archives at `developer.download.nvidia.com/compute/cuda/redist/`; drop the DLLs beside
   the binary (`BackendProbe` already probes `AppContext.BaseDirectory`). §11 will have to
   bundle them in the installer, which is what §5.2 already assumed.
   > One unknown: the 1.9.x CUDA backend also imports **`nvcudart_hybrid64.dll`**, which is
   > in no `cuda_cudart` redistributable archive (13.0 through 13.4 all ship only
   > `cudart64_13.dll`). It is most likely a driver-supplied component of CUDA 13's hybrid
   > runtime, in which case the r580+ install provides it. **Verify this first** — if it is
   > missing after the update, the CUDA library will fail to load exactly as it does now,
   > and the next thing to try is building whisper.cpp's backend locally.
4. **Re-run the bench** (`--backends cuda,cpu --models tiny.en,small.en,large-v3-turbo`) and
   replace §2's table. That answers W7, §5.3 and — with a word-timing spot check — W2.

---

## 2. ASR latency — CUDA on the RTX 3070

`LocalCaption.Bench`, 5 decodes per cell, one 6-second window (one interim window, §5.4).
**Budgets: interim < 500 ms p90, final < ~2 s p90.**

| backend | model | load ms | p50 ms | p90 ms | RTF | verdict |
|---|---|---:|---:|---:|---:|---|
| **cuda** | `tiny.en` | 2 887 | **94** | **109** | 0.02 | interim-capable |
| **cuda** | `small.en` | 2 234 | **246** | **262** | 0.04 | interim-capable |
| **cuda** | `large-v3-turbo` | 5 899 | **416** | **420** | 0.07 | interim-capable *in isolation* |
| cpu | `tiny.en` | 1 574 | 597 | 700 | 0.10 | misses the interim budget |
| cpu | `small.en` | 9 157 | 4 167 | 4 178 | 0.69 | 2× over the final budget |
| cpu | `large-v3-turbo` | 381 947 | 17 257 | 17 424 | 2.88 | slower than real time |

The GPU is **6×** faster than the CPU on tiny.en and **41×** on turbo. Resident VRAM for the
shipping pair (tiny.en + large-v3-turbo) is **1 765 MiB** of 8 192 — §5.3 guessed "well under
3 GB" and was right.

### §5.3 — `large-v3-turbo` is the right final default

420 ms p90 against a ~2 s budget, and the accuracy difference is not subtle. Same clip, same
pipeline, CPU `tiny.en` against GPU `tiny.en` + `large-v3-turbo`:

```
tiny.en    So am I fellow Americans. / Ask not! / What your country can do for you.
turbo      And so, my fellow Americans. / Ask not! / what your country can do for you.
           / Ask what you can do for your country.
```

§0.5's claim that Windows should ship **more accurate** transcripts than the Mac is confirmed.

### W7 — keep the dual-model hybrid

This is the question B0 existed to answer, and the isolated number is a trap. Turbo alone
decodes in 420 ms p90, which is inside the interim budget — so turbo-only *looks* viable.

§5.4 says to measure under concurrent decoding, because "isolated numbers will flatter the
design". Measured, with **two resident turbo models**, one per lane (the §5.1 rule — the lanes
never share a factory even when they choose the same model):

| Configuration | interim p50/p90 | final p50/p90 | verdict |
|---|---:|---:|---|
| **tiny.en + large-v3-turbo** (hybrid) | **335 / 339 ms** | **415 / 426 ms** | ✅ both inside budget |
| large-v3-turbo × 2 (turbo-only) | **680 / 690 ms** | 684 / 697 ms | ❌ interim **38% over budget** |

**The dual-model split stays.** §18.1's default was right, and the hybrid has comfortable
headroom on both lanes — 339 ms against 500, and 426 ms against 2000.

---

## 2a. ASR latency — CPU, for the §5.8 fallback

`LocalCaption.Bench`, 5 decodes per cell, threads = 8 (physical cores). **These are the
fallback path (§5.8), not the shipping path.** Budgets: interim < 500 ms p90, final < ~2 s p90.

| backend | model | load ms | p50 ms | p90 ms | RTF | verdict |
|---|---|---:|---:|---:|---:|---|
| cpu | `tiny.en` | 1 574 | 597 | 700 | 0.10 | ❌ misses the 500 ms interim budget |
| cpu | `small.en` | 9 157 | 4 167 | 4 178 | 0.69 | ❌ 2× over the final budget |
| cpu | `large-v3-turbo` | 381 947 | 17 257 | 17 424 | 2.88 | ❌ slower than real time |

Every model transcribed the clip correctly, so this is purely a latency result.

Two things are worth keeping even though the GPU numbers are missing:

- **`large-v3-turbo` on this CPU is 17 s per 6-second window** — 2.9× slower than real time,
  and its first load cost **6.4 minutes** including the warm-up decode. §5.8's CPU-fallback
  default of `small.en` is confirmed as right, and turbo must never be the CPU fallback.
- **Even `tiny.en` misses the interim budget on CPU** (700 ms p90 against 500 ms). The Mac
  measured 222 ms for the same model under faster-whisper, so this is whisper.cpp's
  30-second padded mel encoder on Zen 3, not a tuning error. **If the dGPU disappears
  mid-session (§5.8), interim captions cannot keep their cadence at all** — the fallback
  has to drop to finals-only, which is a design consequence §5.5 should state outright.

### Under contention

§5.4 specifies the interim budget *under concurrent final decoding*, because isolated
numbers flatter the design. Both lanes decoding the same 6 s window at once, CPU, 5 rounds —
the §5.8 fallback pairing:

| lane | model | p50 ms | p90 ms | isolated p90 | cost of contention |
|---|---|---:|---:|---:|---|
| interim | `tiny.en` | 1 103 | 1 118 | 700 | **1.6×** |
| final | `small.en` | 4 390 | 4 428 | 4 178 | 1.06× |

The interim lane pays for all of it, which is the wrong way round: the lane with the budget
is the one that degrades. On CPU the two lanes are competing for the same 8 cores, so this
is expected — but it is the number to compare against once the GPU path works, since both
lanes will then be contending for one device (§5.4 question 3) and this is the shape of the
answer that matters.

### W2 — DTW word timings hold still enough

§5.7 makes `RollingCaption` depend on word timings, and §18.1 left open whether whisper.cpp's
DTW is *accurate* enough for its **350 ms anchor**. The question is stability rather than
accuracy: each interim decode is a different 6-second tail with no shared prefix, so the merge
aligns two hypotheses by finding a word that matches lexically and lands within 350 ms of
where it was. Drift beyond that does not degrade the caption — it scrambles it.

Measured by decoding overlapping windows exactly as the interim lane does (6 s tail every
500 ms) and comparing each word's midpoint in session coordinates against the previous
window (`--word-timings`):

| | |
|---|---|
| Window pairs | 10 |
| Matched word pairs | 105 |
| Drift p50 / p90 / worst | **20 ms** / **290 ms** / 790 ms |
| Words within the 350 ms anchor | 93.3% |
| **Merges offering at least one anchor** | **10 / 10** |

**The per-merge number is the operative one.** `RollingCaption` anchors on the *first* word
that matches within 350 ms; it does not need every word to agree, and one that drifts is
simply not chosen. Every merge found an anchor, with p50 drift an order of magnitude inside
the tolerance.

**W2 closes on its §18.1 default**: keep the word-timed merge. §5.7.1's fixed-origin +
`LocalAgreement` fallback stays written and unused — and the `ICaptionMerger` seam that was
never built (known gap 2) is now a hedge against a risk that has been measured away.

---

## 3. Audio — W9 and the silence gap

`LocalCaption.Probe`, five runs. Both active render endpoints report **48 kHz · 2 ch ·
32-bit IEEE float (extensible)** — §4.4's common case, and an integer 48 k → 16 k ratio.

| # | Endpoint | Condition | Packets | Audio vs wall clock | Position | Longest gap |
|---|---|---|---:|---:|---|---:|
| 1 | Jump Desktop Virtual Speaker | nothing rendering | **0** | 0.00 s / 12.01 s | — | **12 010 ms** |
| 2 | Jump Desktop Virtual Speaker | silent keepalive only | 1 199 | 11.99 s / 12.01 s (**−18 ms**) | monotonic | 0 ms |
| 3 | Jump Desktop Virtual Speaker | tone with an 8 s silent gap | 2 000 | 20.00 s / 20.01 s (**−14 ms**) | monotonic | 0 ms |
| 4 | Realtek (default) | nothing rendering | 1 123 | 11.98 s / 12.01 s (−27 ms) | monotonic | 0 ms |
| 5 | Realtek (default) | tone with an 8 s silent gap | 1 873 | 19.98 s / 20.00 s (−25 ms) | monotonic | 0 ms |

### W9 — answered

`IAudioClient.Initialize(SHARED | LOOPBACK)` **succeeds** on the Jump Desktop Virtual
Speaker, the mix format is ordinary, `u64DevicePosition` advances monotonically across 2 000
packets, and the clock holds to **−14 ms over 20 s**. Mode B is viable on the virtual
endpoint. §4.7.3's four questions are all answered yes — *provided* something is rendering.

One incidental finding: with the Jump Desktop session connected, the virtual speaker was
**not** the default endpoint — Realtek was. §4.7 assumes "the default render endpoint flips
twice per session". On this configuration it did not flip at all, which means mode B would
have captured the wrong device silently. It does not change the decision (mode A is already
the default for other reasons) but it does mean §4.6's picker cannot rely on "default"
meaning "the one the remote user hears".

### §4.3 — the silence gap is real, and the spec's gate is backwards

Run 1 is the trap exactly as §4.3 describes it, on the endpoint that matters: **zero packets
for twelve seconds.** Not degraded timing — no clock at all.

Run 2 is the same endpoint with §4.3's mitigation 1 (a second client rendering zeros) and
nothing else changed: **1 199 packets, −18 ms over 12 s.**

Runs 3 and 5 isolate why. Eight seconds of *digital silence inside an active render stream*
produced **no stall on either endpoint** — packets kept arriving, carrying zeros, and
`AUDCLNT_BUFFERFLAGS_SILENT` was never set. So what keeps loopback alive is **a stream
existing, not the samples being non-zero**.

Two corrections to §4.3 follow, and both matter in B1:

1. **The keepalive must NOT be skipped on the Jump Desktop endpoint.** §4.3 says to gate
   mitigation 1 off for remote-desktop virtual devices, "pushing keepalive silence into the
   Jump Desktop speaker streams it over the network for no benefit", and to rely on
   mitigation 2 there. Run 1 shows there is no benefit to rely on: with no keepalive that
   endpoint produces nothing. **Mitigation 1 is the only thing that works there; mitigation 2
   does nothing on its own.**
2. **Mitigation 2 is not the correctness guarantee §4.3 calls it.** Device-position padding
   corrects a gap *when the next packet arrives carrying a position*. If no packet ever
   arrives — run 1, or any silence that reaches the end of a session — there is nothing to
   correct against and the clock is simply short by the whole gap. `padding implied` was
   **0 frames in every run**, including the 12-second stall. B1 needs a third rule: when no
   packet has arrived for longer than one buffer period, advance the clock from the wall
   clock and reconcile on the next real packet.

Also worth noting for the §4.3 gate: the Jump Desktop endpoint reports its form factor as
**`Speakers` (1), not `RemoteNetworkDevice` (0)**, so the "gate it on the endpoint's form
factor" instruction cannot identify it. Gate on the device's friendly name or driver, or —
given finding 1 — simply always run the keepalive.

Run 4 carries a caveat: the Realtek endpoint delivered packets with nothing deliberately
playing, which means some other process on the machine was holding a render stream open.
That is a property of the moment, not of the driver, so **it must not be read as "the
default endpoint is safe without a keepalive"**.

---

## 3a. B1 measurements — the capture layer built on all this

Added after B1's mode A and mode B were written, using `LocalCaption.Probe --capture`, which
runs `LocalCaption.Audio` end to end and asks the only question that matters downstream:
**does one second of wall clock produce one second of 16 kHz mono audio?** §4.3's acceptance
is ±100 ms.

| Mode | Endpoint / target | Condition | Audio vs elapsed | Drift | Corrections needed |
|---|---|---|---:|---:|---|
| B | Jump Desktop Virtual Speaker | silent, keepalive on | 20.04 s / 20.03 s | **−5 ms** | none |
| B | Jump Desktop Virtual Speaker | silent, **keepalive off** | 19.94 s / 19.74 s | **−31 ms** | 957 111 frames synthesised |
| B | Realtek (default) | tone, 12 s silent gap | 29.89 s / 29.82 s | **+52 ms** | none |
| A | own process tree | tone, 8 s silent gap | 20.08 s / 20.03 s | **+18 ms** | none |

Every run: no samples lost to `CaptureBuffer`, worst callback gap 13 ms.

Three findings, two of which correct the spec further.

### The wall-clock rule works, and it is not optional

Row 2 is the safety net running alone: keepalive deliberately disabled on the endpoint that
B0 measured delivering *nothing*. The clock held to −31 ms across twenty seconds in which no
packet ever arrived, entirely on synthesised silence. That is §4.3's failure case turned into
a survivable one.

It also caught a real bug in the first implementation. The clock originally started on the
*first packet* — which on a silent endpoint never comes, so it never started, and fifteen
seconds of session evaporated while every indicator read healthy. **The clock has to start
when the stream opens, not when audio does.** Now covered by
`SampleClockTests.AStreamThatNeverDeliversAPacketStillKeepsTime`.

### ⚠ Process loopback does not report a device position at all

Mode A — **the v1 default capture mode** — returns `u64DevicePosition = 0` on *every* packet.
Measured directly, against mode B on the same machine in the same run:

```
mode A (process):   devicePosition=0          frames=512    ← every packet
mode B (endpoint):  devicePosition=249026048  frames=512
                    devicePosition=249026560  frames=512    ← +512, as expected
```

§4.5 says of mode A: *"The §4.3 device-position padding stays in place regardless."* **It
cannot.** There is no position to pad against. Mode A's entire clock protection is the
wall-clock rule — the mitigation the spec does not have. `SampleClock` detects this rather
than being told (a stream that answers zero eight times is not reporting a position) and
stops counting every packet as a backwards jump.

This does not weaken mode A: §4.5's other claim held up exactly as written — *"a process that
renders silence still renders"* — and row 4 shows an eight-second digital-silence gap costing
no correction at all. But it does mean the two modes have **different** clock guarantees, and
only mode B has two independent ones.

### The keepalive belongs to mode B only

There is no endpoint to keep alive in mode A, and none is needed. Row 4 needed no keepalive,
no padding and no synthesis.

---

## 3b. B1/B3 soak — what twenty minutes found that thirty seconds could not

A 20-minute recorded session (`--session`, speech on loop, CPU `tiny.en`) came back **+2323 ms
fast**. Not a gap, not a stall — a *rate*: 0.193%, steady.

Every earlier run had the same rate and passed anyway, because they were short:

| Run | Drift | Rate |
|---|---:|---:|
| 16 s | +30 ms | 0.19% |
| 20 s | +26 ms | 0.13% |
| 30 s | +52 ms | 0.17% |
| 180 s | +358 ms | 0.199% |
| **1200 s** | **+2323 ms** | **0.194%** |

§4.3's ±100 ms is a duration, so a rate error passes every short test and fails the only one
that matters. Ten seconds across a ninety-minute interview.

**It was ours, not the hardware.** The raw WASAPI layer over the same three minutes gave
**179.99 s of audio in 180.01 s of wall clock** — the device clock is fine. And the
normaliser's own unit test said 60 s in, 60 s out, exactly.

The arithmetic gave it away: this endpoint delivers **512-frame packets**, and 512 ÷ 3 =
170.667. Rounding up once per packet is 0.195% — the measured number. The unit tests had used
480-frame packets, which 3 divides exactly, so they could never have seen it.

`AudioNormalizer` now tracks input frames and output samples as integers and asks the
resampler for only what the input entitles it to. Re-measured over the same three minutes:

```
  before   drift  +358 ms      after   drift  +5 ms
```

The tests now run at 441, 480, 512 and 1024 frames, because the rate must not depend on how a
driver happens to chunk its audio.

**Re-soaked for an hour on the GPU** with the shipping pair, after the fix:

| | 20 min, before | 60 min, after |
|---|---:|---:|
| Drift | +2 323 ms | **−84 ms** |
| Rate | 0.193% | 0.002% |
| Peak working set | 513 MB | 737 MB → 267 MB at the end |
| Handles | — | 642 → 560 |
| Journal after Stop | clean | clean |

Extrapolated over a 90-minute interview: **10 seconds of error became about a fifth of a
second**. Memory and handle counts fell rather than grew, which is §17.12's question.

One caveat on both long runs: the harness's looping playback stops producing audio well
before the run ends (12 minutes into the hour), so these measure **clock and resource
stability, not sustained recognition**. A real interview is the only test of the latter.

---

## 4. Changes made during B0

| Change | Why |
|---|---|
| `Journal.Pending` reads with `FileShare.ReadWrite \| FileShare.Delete` | Windows-only bug, found by the first `dotnet test` on this machine: the default share mode denies the journal's own live write handle, the `IOException` was swallowed, and crash recovery reported nothing to recover. 66/66 now pass on Windows as well as macOS |
| `EngineInfo.Library` / `.FellBack`, printed by the bench as `requested→loaded` | A silent CPU fallback is how the first CUDA run passed itself off as a GPU run. §5.2 asked for the active backend to be visible; it now is |
| `Whisper.net.Runtime.Cuda.Windows` added to `LocalCaption.Asr` | §5.2 requires the CUDA runtime to be bundled. It was never referenced, so "cuda" could only ever have meant CPU |
| New project `LocalCaption.Probe` | §4.7.3's loopback probe. Not shipped. Its `--play --gap` mode is §4.3's acceptance test, self-contained, and `--capture` runs the same check through the B1 capture layer |

---

## Caveats

- **One clip, one speaker, clean audio.** These are latency numbers, not WER numbers —
  §14.7 and §20's position on accuracy is unchanged.
- **The CPU rows are not the shipping path** and should not be quoted as "Windows
  performance". They exist to bound §5.8's fallback.
- **The probe holds the render stream it measures.** Runs 2, 3 and 5, and every §3a row,
  prove the mechanism — not that a real meeting app keeps its stream open during silence.
  Zoom or Teams muting the far end may or may not tear its stream down. Worth one
  observation against a real call.
- **Mode A was tested against this probe's own process**, which is a faithful exercise of
  activation, the tree flag and the packet path, but not of a browser. §4.5's
  `INCLUDE_TARGET_PROCESS_TREE` reasoning — that Google Meet's audio lives in a Chrome
  audio-service child process — is implemented and unverified until someone captures a real
  browser call.
- **No soak.** The longest run here is thirty seconds. Clock drift over a ninety-minute
  interview is a B6 question, and it is the one that matters for §13's budgets.

## Reproducing

```bash
cd windows
PATH="$HOME/.dotnet:$PATH" dotnet build -c Release

# ASR latency (downloads weights on first run: tiny.en 75 MB, small.en 466 MB, turbo 1.6 GB)
dotnet run --project src/LocalCaption.Bench -c Release -- \
    --wav <6-second-16k-mono.wav> --models tiny.en,small.en --backends cpu --runs 5

# Audio, raw WASAPI: the three runs that matter, per endpoint
dotnet run --project src/LocalCaption.Probe -c Release -- --list
dotnet run --project src/LocalCaption.Probe -c Release -- --device 0 --seconds 12
dotnet run --project src/LocalCaption.Probe -c Release -- --device 0 --seconds 12 --keepalive
dotnet run --project src/LocalCaption.Probe -c Release -- --device 0 --seconds 20 --play --gap 8

# Audio, through the B1 capture layer (§3a)
dotnet run --project src/LocalCaption.Probe -c Release -- --device 0 --seconds 20 --capture
dotnet run --project src/LocalCaption.Probe -c Release -- --device 0 --seconds 20 --capture --no-keepalive
dotnet run --project src/LocalCaption.Probe -c Release -- --device 1 --seconds 20 --capture --play --gap 8
dotnet run --project src/LocalCaption.Probe -c Release -- --list-sources
dotnet run --project src/LocalCaption.Probe -c Release -- --device 1 --seconds 20 --capture --process self --play --gap 8
```

The clip was `samples/jfk.wav` from `ggerganov/whisper.cpp`, truncated to its first 6.00 s.
It is not committed here; `testdata/audio/` (§6.1) still waits on a working engine on both
platforms.
