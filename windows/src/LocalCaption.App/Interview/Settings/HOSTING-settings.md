# Interview settings pages — wired (SPEC-16 §5.7)

**Status: wired** into `SettingsWindow` (compile-checked on the Mac; not yet seen on screen).
The four pages — **Interview**, **Asking**, **Prompts**, **Codex** — sit between Shortcuts and
System, share one `InterviewSettingsModel` built on a copy of `config.interview`, and
`ApplyTo(config.Interview)` runs in `OnSave` before `_env.Update`, so they follow the window's
edit-a-copy / Save / Cancel model. `StartCodexOnOpen` is true only in Interview mode; the
Asking page shows the `GlobalHotkeys` registration errors captured when Settings opened (the
main window then lets both hotkeys go while Settings is open, so the recorder can hear F8/F9).
Settings reopens on the last page used (`ui.settings_page`); the preparation's "Open Settings
→ Interview → Skills" opens it on the Interview page. After Settings closes — saved or not,
since skills load at once — the main window re-applies the layout, hotkeys, labels and the
preparation.

Files in this folder (namespace `LocalCaption.App.Interview.Settings`):

| File | What |
|---|---|
| `InterviewSettingsModel.cs` | One view model shared by the four pages; edits a **copy** of `config.interview`. Also `SkillSlotModel`, `UsageWindowModel`. |
| `InterviewSettingsPage.xaml(.cs)` | **Interview**: Skills (4 slots), Model & answers, Layout, Privacy. |
| `AskingSettingsPage.xaml(.cs)` | **Asking**: Ask / Screenshot hotkey recorders, What to send, Screenshots. |
| `PromptsSettingsPage.xaml(.cs)` | **Prompts**: Custom instructions. |
| `CodexSettingsPage.xaml(.cs)` | **Codex**: status row + sign-in, Codex path, Check again, Sign out…, Plus usage. |
| `HotkeyRecorder.cs` | The recorder control (Record…, Esc cancels, Reset to F8/F9, duplicate / typing-key refusal, registration error, Fn-lock hint). |
| `SettingsPageResources.xaml` | Field / group / multi-line / usage-bar styles matching `SettingsWindow.xaml`. |
| `SettingsMvvm.cs` | Local `SettingsObservable`, `SettingsChoice`, converters. |

`codex_path` takes effect at Codex's next launch (`CodexAppServerEngine` reads it at each
launch); the coach picks up model, efforts, answer length and custom instructions at the next
thread start.

## 2. What is *not* Save/Cancel

* **Skills** load and remove at once (library files on disk, as on the Mac). The message under
  the slots says what happened, including files left out ("Left out (Codex can't use them): …").
* **Codex** sign-in, Cancel, Check again, Sign out… (confirmed; only shown when signed in) and
  usage Refresh act at once on the shared `CodexService`.

## 3. Codex start rule (SPEC-16 §9.1)

Opening Settings never starts `codex` by itself. The Codex page, when shown, refreshes usage if
Codex is already signed in; if Codex has never been checked it shows "Not checked yet. Check
again to look for Codex." unless `StartCodexOnOpen` is true. The Interview page's model list
shows only "Recommended (gpt-6-luna)" (and the configured model) until Codex has been checked.

## 4. Hotkey recorder

* Captures the next key-down by **virtual key** (`Key`), so layouts don't matter; Esc cancels;
  a bare modifier keeps listening; focus leaving the control cancels. Enter and Esc are
  swallowed while recording, so they don't hit Save / Cancel.
* Validation and the stored text come from `Core.Interview.Hotkey.Parse` / `ToString()` —
  canonical `Ctrl+Alt+Shift+Cmd+Key`. It is **displayed** with `Win` for `Cmd`, and the
  messages say "Ctrl, Alt or Win" (a key-name difference, allowed by §5.7's acceptance note).
* Messages (Mac): "That key can't be used." · "X is already the Screenshot hotkey." ·
  "X needs Ctrl, Alt or Win so it doesn't block typing." · "“X” isn't a valid shortcut." ·
  "Can't use it: <reason>. The on-screen Ask button still works." The Fn hint is PC wording.
* The recorder does **not** check the Windows `shortcuts` group (SPEC-16 W-I6: not handled).

## 5. Texts that differ from the Mac on purpose

| Where | Mac | Windows | Why |
|---|---|---|---|
| Prompts caption | "Prefilled into each new interview's setup." | "Appended to the coach's instructions when an interview's coach starts." | §5.7 |
| Privacy text | omits hotkey screenshots | mentions "screenshots you take with the Screenshot hotkey" | §5.1, §9.3 |
| Layout footer | "from 820 pt wide … Applied live." | "from 820 px wide … Applied when you save." | units; Save/Cancel window |
| Screenshot footer | ⌘⌃⇧4; "Space switches to picking a window" | Win+Shift+S; "Space captures the window under the pointer" | §4.3 |
| Fn hint | Apple keyboard setting | laptop Fn / Fn Lock | W-I2 |
| Codex status before any check | spinner ("Checking Codex…") | "Not checked yet. Check again to look for Codex." | §9.1 |
