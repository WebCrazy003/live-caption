# SPEC-06 — Session List & Management

**Status:** Not started · **Depends on:** SPEC-01, SPEC-04 · **Full detail:** SPEC.md §10

## Goal
The default screen: browse, create, open, rename, delete, search, and sort past sessions.

## What should be done
- [ ] **List (SPEC.md §10):** columns Session Name · Created · Duration · Transcript file (reveal in Finder). Backed by SQLite (SPEC-01).
- [ ] **Create:** optional name; empty → `<prefix><YYYY-MM-DD_HH-mm-ss>` (prefix from config). Transitions to Active Session (SPEC-05).
- [ ] **Open (read-only):** load saved `.txt` into a non-editable viewer (reuse caption area in read-only mode). No re-transcription.
- [ ] **Rename:** edits metadata only (`session_name`); does **not** rename the timestamped file.
- [ ] **Delete:** remove DB row; ask **"Also delete the transcript file?"** (default keep); destructive file delete requires explicit confirm.
- [ ] **Search:** by name/date. **Sort:** name/created/duration, asc/desc.

## What is done
- Nothing built. Behavior + file-vs-metadata semantics fully specified in SPEC.md §10 (resolves the draft's ambiguities C9).

## Blockers
- **B1** — Xcode. Depends on SPEC-01 (DB) and SPEC-04 (sessions exist to list).

## Acceptance
- Create/open/rename/delete/search/sort all work and persist across restarts.
- Rename leaves the transcript filename unchanged; Delete prompts before removing the file.
- Open shows a read-only transcript; no accidental edits.
