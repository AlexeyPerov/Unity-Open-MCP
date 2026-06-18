# Hub rules

## Scope

Rules for `hub/` — the Tauri + SvelteKit desktop application. Inherits root `AGENTS.md`; deeper rules win on overlap.

## Package shape

- SvelteKit (Svelte 5, runes mode) frontend in `src/`, Tauri (Rust) shell in `src-tauri/`.
- Frontend: `$state` proxies for reactivity (`src/lib/state.svelte.ts`), component-per-zone pattern. Backend commands invoked via `@tauri-apps/api`.
- Do not add UI framework dependencies (React, Vue, etc.) — Svelte 5 is the only frontend framework.
- TypeScript strict. Run `npm run check` (svelte-check) after changes.

## State and data

- Project and settings data lives in `projects.json` and `settings.json` at the OS config dir, read/written via Tauri commands. Do not introduce a database.
- No migrations. The app is pre-release; prefer simplifying storage/codecs over backward compatibility (root `AGENTS.md` §Migrations).
- Platform-neutral storage only — no Windows-only config formats.
- New `ProjectEntry` fields that are derived from disk (SRP label, default build target, …) must be `#[serde(default, skip_serializing_if = "Option::is_none")]` so legacy `projects.json` files keep loading and the on-disk shape stays compact until the value is computed. Populate them in every entry-construction site (`add_project`, `refresh_all_projects`, `walk_up_scan`, `seed_from_unity_hub`, `create_new_project`) and recompute them in `refresh_all_projects` alongside the other disk-derived fields.

## Tauri commands

- New backend commands go in `src-tauri/src/`. Commands must be `#[tauri::command]` and registered in the invoke handler.
- Commands that touch the filesystem must validate paths and reject traversal outside expected roots.

## UI conventions

- Follow the existing component-per-zone pattern (tabs, popups, drawer). Do not create monolithic single-file UI shells.
- No internal references in UI strings — labels, tooltips, and help text must never contain `specs/` paths, milestone IDs, or task numbers (root `AGENTS.md` §No internal references).

## Verification

- Run `npm run check` and `npm test` after changes.
- Tauri command changes: update the corresponding frontend service wrapper in `src/lib/services/` in the same task.
