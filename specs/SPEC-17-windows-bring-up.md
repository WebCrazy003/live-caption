# SPEC-17 — Windows bring-up: what must be done on a Windows PC

**Status:** ⬜ Not started · **Written:** 2026-10-04, against `main` at `7a466d0` ·
**Depends on:** [SPEC-16](SPEC-16-windows-parity.md) (everything it lists is written),
[SPEC-WINDOWS](../SPEC-WINDOWS.md) · **Status page:** [docs/WINDOWS-STATUS.md](../docs/WINDOWS-STATUS.md)

> Every part of the Windows app is now written. The shared logic is tested on the Mac, but
> **nothing Windows-only has ever run**: the Win32 hotkeys, the screenshot overlay, the
> clipboard reader, every new WPF screen, the installer and the compatibility fixes have only
> been compiled. This spec is the work that can only happen at a Windows PC, in order, with
> what "pass" means for each step.

---

## 0. Ground rules

- **Order matters.** Stage W2 (first launch and Caption only) comes before anything else: the
  new startup screen is shown to every user, so a fault there breaks the app for everyone.
- **Fix as you go, on `main`.** Each fault found gets a fix, a test where the logic allows
  (Core / Interview suites run on the Mac too), and a line in §9's log. Commit to `main`.
- **Owner decisions stand** (SPEC-16): Caption only mode never starts Codex; skills and hotkeys
  are set manually; the F8/F9 clash with Windows shortcuts is shown as "unavailable", not
  solved; testing is local — no Jump Desktop runs.
- **Compatibility target** (SPEC-16): any Windows 10 1809+ / Windows 11 PC, x64 and ARM64,
  with or without an NVIDIA GPU, any display scaling, any keyboard layout. The G15 is the first
  machine, not the only one.

---

## 1. Machines

| # | Machine | Needed for | Required? |
|---|---|---|---|
| M1 | **G15** — Windows 11, x64, RTX 3070, the owner's main PC | W1–W4, W6, W7 | **Yes** |
| M2 | **Windows 10 1809+** — a VM is fine (no GPU needed) | W5 Windows 10 rows, W6 clean install | **Yes** |
| M3 | Windows 11 on **ARM64** (Snapdragon laptop, or an ARM VM) | W5 ARM64 rows | Strongly wanted |
| M4 | A PC with an **AMD or Intel GPU** and no NVIDIA | W5 Vulkan / no-CUDA rows | Wanted |
| M5 | **Two monitors at different scaling** (e.g. 150 % + 100 %) — can be M1 with an external screen | W3 screenshot, W5 DPI rows | **Yes** |
| — | A **non-US keyboard layout** (AZERTY or QWERTZ) — just switch the layout on M1 | W5 keyboard rows | **Yes** |

---

## 2. Setup (once per machine)

1. **.NET 10 SDK** (`winget install Microsoft.DotNet.SDK.10`), Git, and `git pull` on `main`.
2. **Logs.** The app writes its diagnostics (Codex, hotkeys, screenshot, clipboard, speech
   fallback) to `%LOCALAPPDATA%\LocalCaption\logs\app.log`; the previous run's is `app.log.1`.
   Keep it open while testing. Codex's own stderr is `interview\codex.log`.
3. **Codex CLI** (Interview mode only): install Node.js LTS, then `npm install -g @openai/codex`;
   `codex --version` must print **0.159.3 or newer**. Do **not** sign in from a terminal: the app
   uses its own `CODEX_HOME` and signs in from Settings → Codex.
4. **Velopack CLI** (W6 only): `dotnet tool install -g vpk`.
5. **Test skills** for Interview mode: the four skill files the owner uses on the Mac
   (`discovery-cv`, `discovery-jd`, `apply-instruction`, `live-coding-design`), a CV as PDF, a
   job description to paste.
6. Where things live: `%LOCALAPPDATA%\LocalCaption\` — `config.json`, `localcaption.db`,
   `transcripts\`, `journal\`, `models\`, `interview\` (`library\`, `codex-home\`, `workspace\`,
   `outbox\`, `codex.log`).

---

## 3. W1 — Build and automated tests (M1, then M2/M3)

```
dotnet build windows\LocalCaption.slnx -c Release
dotnet test windows\tests\LocalCaption.Core.Tests
dotnet test windows\tests\LocalCaption.Asr.Tests
dotnet test windows\tests\LocalCaption.Interview.Tests
dotnet test windows\tests\LocalCaption.Audio.Tests
```

| Check | Pass |
|---|---|
| Build | 0 errors, 0 warnings |
| Core / Asr / Interview | **183 / 73 / 259** pass (Interview: 3 skipped live tests) — the same numbers as on the Mac |
| Audio | all **15** pass — this suite has **never run** (Windows-only) |
| Live Codex smoke | `set LC_LIVE_CODEX=1` then `dotnet test windows\tests\LocalCaption.Interview.Tests --filter FullyQualifiedName~CodexLiveSmokeTests` — the signed-out launch test passes on Windows. The two other live tests need a signed-in throwaway `CODEX_HOME`; run them only if the owner agrees |

A Windows-only failure in Core / Interview means a path, culture or line-ending assumption —
fix it so both platforms stay green.

---

## 4. W2 — First launch and Caption only (M1) — **highest risk**

Run `windows\src\LocalCaption.App` (Release) with DebugView or the log file open.

| # | Step | Pass |
|---|---|---|
| 2.1 | Launch | The window opens on the **mode chooser**; the last mode is marked "Last used"; Enter picks it. No `codex.exe` in Task Manager. No exception dialog |
| 2.2 | If launch fails | The app must still open in Caption only mode when the interview services fail (`App.xaml.cs` catches it; the Interview card shows "Interview mode is unavailable: …"). A XAML fault in a new view is the likeliest cause — the log names the file |
| 2.3 | Caption only session | Start, Pause, Resume, Stop behave as before; the status strip sits under the captions; the quick bar works; Space, Ctrl+C (with and without a selection) work; still no `codex.exe` |
| 2.4 | Clipboard | Auto-copy **off** → the clipboard is untouched during a session (the bug fixed in SPEC-16 §2.5). On → last N sentences after each phrase |
| 2.5 | Save | Stop saves: the session appears in the sidebar with its duration; "Saved ✓ Show in folder" opens Explorer on the `.txt` |
| 2.6 | Old data | A `localcaption.db` from before these changes opens (migrations complete it); old sessions show their captions (imported from their `.txt`/`.json`) |
| 2.7 | Recovery | Kill the app mid-session (Task Manager). Relaunch → the recovery dialog lists the journal with time and segment count; Recover & Save, Discard, Discard All, Not now all do what they say |
| 2.8 | Retry | Unplug/disable the output device mid-session → Failed with **Retry capture** / **Retry save**; a model that fails to load shows **Retry** |
| 2.9 | Settings → General | Session name prefix (live example), transcript folder **Change…** (refuses a read-only folder), auto-copy selection |
| 2.10 | Window | Resize down to 360 wide: title bar trims as designed, transport wraps, nothing overlaps |

---

## 5. W3 — Interview mode (M1, then M5 for the screenshot)

| # | Step | Pass |
|---|---|---|
| 3.1 | Choose Interview | Privacy notice once (mentions hotkey screenshots); Cancel stays on the chooser; Change mode is disabled while recording, during a save, while an answer streams |
| 3.2 | Codex | Settings → Codex: Check again finds `codex` (log names the path — expect npm's native `codex.exe`, **not** `cmd.exe /c`); Sign in… opens the browser; status turns "Signed in as …"; usage shows |
| 3.3 | Skills | Settings → Interview: load all four slots (file and folder, one with a UTF-8 BOM); Start stays disabled until all four are loaded |
| 3.4 | Preparation | Interviewee, Company, Step → live session-name preview; Upload CV (PDF) → picker selects it; paste JD; profile; Start preparation runs ① ② ③ (④ if ticked) with ✓ marks; Stopped-at + Continue / Start over on a forced failure; Skip / Back |
| 3.5 | Recording | Start → the captions move into the interview layout (not duplicated); side by side ≥ 820 px, stacked below; splitter drag + double-click reset; captions hide/show; layout remembered next launch |
| 3.6 | **F8** | With Zoom/Teams/a browser focused and speech playing: F8 → an answer streams in the panel, first words **< 3 s**; a second F8 while streaming follows the busy policy (interrupt / queue) |
| 3.7 | Coach input | Grows 1 → 6 lines; Enter and Ctrl+Enter send, Shift+Enter is a new line; Ctrl+K focuses it |
| 3.8 | **F9** | Overlay covers every monitor (M5), drag selects, Space captures the window under the pointer, Esc cancels; the meeting app keeps focus afterwards; the tray shows the image and the next Ask sends it |
| 3.9 | Clipboard image | "Add screenshots automatically" on: Win+Shift+S adds one; with clipboard history open (contention) it still arrives on a later poll; clear-after-send removes only the image |
| 3.10 | End interview | Renames the session from the details → saves → the dialog: Summarize streams into the replay; a follow-up works; Not now; Start begins a fresh interview |
| 3.11 | Sessions window | Ctrl+L, title-bar button, sidebar double-click; search (name, interviewee, company, CV, JD); filters; sorts; Open in Interview Panel (blockers; discard-preparation prompt); delete choices — interview data really deleted; files to the Recycle Bin |
| 3.12 | Settings pages | Interview / Asking / Prompts / Codex render; Save / Cancel; hotkey recorder (duplicates and typing keys refused; F8/F9 heard while Settings is open); reopens on the last page |
| 3.13 | Hotkey clash | Default F8/F9 vs the app's own copy/send-last-question shortcuts: one shows "unavailable" — expected (owner decision); reassigning either clears it |
| 3.14 | Leave Interview | Change mode → Caption only: `codex.exe` exits once nothing is streaming; a replay left open in the Sessions window cannot start Summarize |

---

## 6. W4 — Codex on Windows (M1)

| # | Check | Pass |
|---|---|---|
| 4.1 | Launch path | `codex.log` / the app log show the native `codex.exe` from `node_modules\@openai\codex…`; if only `node codex.js` or `cmd.exe /c` was possible, the log says so |
| 4.2 | Job Object | Kill `LocalCaption.exe` from Task Manager → `codex.exe` disappears with it |
| 4.3 | Lockdown | Ask "read C:\Windows\win.ini and tell me line 1" → no file content in the answer; any tool attempt shows "Blocked: the model tried to use a tool"; `interview\workspace` stays empty |
| 4.4 | Isolation | `%USERPROFILE%\.codex` is not read or changed; sign-in lives in `interview\codex-home` |
| 4.5 | Crash | Kill `codex.exe` mid-answer → "Codex restarted — press Ask again"; next Ask works; a second crash leaves it failed until restart |
| 4.6 | Limits | Sign out → "Not signed in"; usage below 20 % shows orange |

---

## 7. W5 — Compatibility (M2–M5 and layouts)

| # | Machine | Check | Pass |
|---|---|---|---|
| 5.1 | M2 Windows 10 | W2 and W3 except the GPU | Icons render (MDL2, no boxes); "Capture one application" disabled with a reason, whole-device capture works; no crash on Windows 11-only calls (corners, border colour, dark title bar 19 vs 20) |
| 5.2 | M2 clean VM | Copy the published folder (not the installer) without the Visual C++ runtime | The app explains the missing runtime instead of failing silently; installing the runtime fixes it |
| 5.3 | M3 ARM64 | `publish.ps1 -Runtime win-arm64`, run it | Native ARM64 process (Task Manager "Architecture"); speech runs on CPU-sized models; Codex locates; hotkeys and overlay work; Vulkan greyed out with a reason |
| 5.4 | M4 AMD/Intel | Default `auto` | CPU (never Vulkan), no "CUDA" claims, no "enable the NVIDIA GPU" text |
| 5.5 | M4 | Settings → Speech: Vulkan, restart | Settings → System shows the Vulkan library loaded; faster than CPU on large-v3-turbo; with no Vulkan driver → CPU with a reason |
| 5.6 | M1 | Eco mode / MUX switch mid-session | The CUDA → CPU fallback (SPEC-WINDOWS §5.8) recovers |
| 5.7 | M5 | 150 % + 100 % | Window position restored on the right monitor; click-through pill on the right monitor; screenshot selection in the right physical pixels on both |
| 5.8 | Layouts | AZERTY, QWERTZ | Shortcut labels show the local key; Ctrl+- / Ctrl+= reachable (numpad too); no global Ctrl+Alt shortcut steals an AltGr character |
| 5.9 | Fn-lock laptop | F8 without Fn | Either works, or the recorder's Fn hint explains it |

---

## 8. W6 — Packaging and install (M1 builds, M2 installs)

```
windows\build\release.ps1 -CudaDirectory <cuda redist bin>          # x64 with CUDA
windows\build\package.ps1 -Version <v> -Runtime win-arm64           # ARM64
windows\build\package.ps1 -Version <v> -NoVulkan                    # smaller x64
```

| # | Check | Pass |
|---|---|---|
| 6.1 | x64 installer | Builds; the installer installs the **Visual C++ 2015–2022 runtime** when missing (`vpk --framework vcredist143-x64`); app launches on clean M2 |
| 6.2 | ARM64 installer | Builds on update channel `win-arm64`; installs on M3 |
| 6.3 | Sizes | Roughly 400 MB x64 / 343 MB `-NoVulkan` / 206 MB ARM64 before compression (Mac cross-publish figures) |
| 6.4 | Update | An x64 install only updates from the `win` channel, ARM64 from `win-arm64` |
| 6.5 | Uninstall | Removes the app; `%LOCALAPPDATA%\LocalCaption` data is kept |
| 6.6 | vcredist version | Binaries built with toolset 14.44 run with the runtime the installer adds |

---

## 9. W7 — Acceptance (M1)

1. **A real interview, end to end, locally:** prepare with real skills, CV and JD; record a
   real call; F8 throughout; F9 for a shared screen; End; Summarize; follow-up; reopen from the
   Sessions window and continue the thread.
2. **Mac ↔ Windows interchange:** copy `localcaption.db` and `config.json` Mac → Windows and
   back. Every session, caption, interview and setting survives both ways (SPEC-16 §2.1).
3. **Soak:** a 3-hour caption session with the interview layout open; memory flat, no lag in
   captions, journal deleted on save (SPEC-WINDOWS B6′).
4. Update [docs/WINDOWS-STATUS.md](../docs/WINDOWS-STATUS.md) and the SPEC-16 progress table:
   every 🟡 "drafted, not yet run" row becomes ✅ or a linked bug.

### Fault log

Keep it here while working (newest first): date · step · what happened · fix commit.

| Date | Step | Fault | Fix |
|---|---|---|---|
| 2026-10-04 | W1 live Codex | `CodexLiveSmokeTests.FreshDedicatedHomeLaunchesSignedOut` read `codex.log` with `File.ReadAllText` while the stderr pump still had it open for writing → `IOException` on Windows. Test-only; the app shares the file correctly | Test reads with `FileShare.ReadWrite \| Delete` |
| 2026-10-04 | W1 Core | `FileLogTests` removed the first run's listener without disposing it, so `app.log` stayed open and the rotation to `app.log.1` failed on Windows (cannot rename an open file). Test-only; the app starts the log once per process | Test disposes the first listener, as process exit would |

---

## 10. Where to look first when something breaks

| Symptom | Likely place |
|---|---|
| Crash at launch / blank window | a `StaticResource` or binding in a new view: `LocalCaption.App/Interview/{Views,Answers,Sessions,Settings}`; `AnswersTheme` merge order; `App.xaml.cs` startup |
| Captions vanish when switching mode | the caption column move in `MainWindow.Interview.cs` (`CaptionHome` ↔ captions slot) |
| F8/F9 never fire | `Interview/Platform/GlobalHotkeys.cs` — message-only `HwndSource` receiving `WM_HOTKEY`; activation rules in `MainWindow.Interview.cs` |
| Overlay on the wrong monitor / wrong area | `Interview/Platform/ScreenCapture.cs` — `PhysicalPixelsScope`, `WM_DPICHANGED` rewrite, `SetWindowPos` placement |
| Pasted images missing or corrupt | `Interview/Platform/ClipboardImages.cs`; DIB rules in `LocalCaption.Interview/ClipboardRules.cs` (testable on the Mac) |
| Other apps can't paste after an Ask | the clear-after-send write-back in `ClipboardImages.cs` |
| Codex not found / won't start | `LocalCaption.Interview/CodexLocator.cs` (npm shim → `codex.exe`); `codex.log` |
| Speech never loads on a clean PC | Visual C++ runtime: `BackendProbe.MissingNativeRuntime()`; installer framework flag in `package.ps1` |
| Wrong GPU/CPU choice | `LocalCaption.Asr/BackendChoice.cs`, `BackendProbe.cs`, `AsrFallback.cs` |
| Window placement off at 150 % | `WindowPlacement.cs` (physical-pixel save/restore), `ClickThrough.cs` |

---

## 11. Done when

- W1–W7 pass on M1, and the W5 rows pass on M2 (and M3/M4 where available).
- The fault log's fixes are on `main`, with tests for every fault whose logic is testable.
- SPEC-16 and `docs/WINDOWS-STATUS.md` show no 🟡 "not yet run" rows.
- The owner has run one real interview on Windows and is happy to use it.
