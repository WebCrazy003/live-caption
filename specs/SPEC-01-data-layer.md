# SPEC-01 — Data Layer (Config + SQLite)

**Status:** Not started · **Depends on:** SPEC-00 · **Full detail:** SPEC.md §12.2, §12.3

## Goal
Two durable, versioned stores: JSON app config and a SQLite session-metadata database.
No transcript text is ever stored in SQLite.

## What should be done
### Config store
- [ ] `Config` model matching SPEC.md §12.2 (with `schema_version: 2`).
- [ ] Load → validate → **merge defaults** for missing keys.
- [ ] Corrupt/unparseable file → back up to `config.json.bak-<ts>`, write defaults, warn.
- [ ] **Atomic writes** (temp file + rename).
- [ ] Migration hook keyed on `schema_version`.

### SQLite (GRDB)
- [ ] `sessions` table exactly per SPEC.md §12.3 DDL (id, session_name, created_at, ended_at, duration_seconds, transcript_file).
- [ ] WAL mode; index on `created_at`.
- [ ] CRUD: insert (on stop), update (rename), delete, list (with sort/search support for SPEC-06).
- [ ] Schema migrations keyed on `PRAGMA user_version`.

## What is done
- Nothing built. Config JSON schema and SQLite DDL are fully specified in SPEC.md §12. (Mic-related config keys already removed for the system-audio-only design.)

## Blockers
- **B1** — Xcode (Swift build).
- **B5** — minor: whether to add a `.json` transcript sidecar (SPEC.md §22.4) may add one column later; not blocking.

## Acceptance
- Round-trip: write config → reload → identical values; deleting a key restores its default.
- Feeding a corrupt config produces a backup + working defaults, no crash.
- Insert/list/rename/delete sessions works; survives app restart; migrations run cleanly on an old DB.
