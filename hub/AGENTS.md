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

## MCP client catalog (single source of truth)

The MCP client auto-config surface (wizard Step 4 writer, Clear AI Setup,
detection heuristic, and the bridge window configure panel) is driven by a
shared catalog. **Adding a client is a manifest + catalog change, not a
per-file constant edit.** To add a client:

1. **Skill target** — add the client's project-relative skill path to
   `skills/client-paths.json` (`clients` map) and map it under
   `mcpClientMapping` so the skill copy + `generate_skill` pick it up. Also
   update the `BUNDLED_MANIFEST` in `mcp-server/src/skill/client-paths.ts`
   (kept in sync by a unit test).
2. **Rust writer** — add a variant to `McpClientId`
   (`hub/src-tauri/src/config/mcp_config.rs`) and cover it in every `match`:
   `client_format`, `client_is_global`, `resolve_target_path`,
   `merge_key_path`, `build_entry_json`, `mcp_client_wire_key`. TOML clients
   also need a branch in `build_codex_toml` / `read_existing_config` skip.
3. **Rust clear + detect** — add the client to
   `clear.rs::FILE_BACKED_CLIENTS` + `resolve_clear_path` and to
   `wizard.rs::read_mcp_heuristic` (+ `any_skill_installed` if it ships a
   skill folder).
4. **TS preview** — extend `McpClientId` in
   `hub/src/lib/services/ai_toolkit.ts`, `mcpClientConfigTarget`, the
   `McpClientIdWire` / `McpClientWire` unions in `config.ts`, and
   `clientToWire` + `MCP_CLIENT_OPTIONS` in
   `hub/src/lib/components/wizard/constants.ts` (the Step 4 picker catalog
   consumed by the wizard modules).
5. **Bridge window** — add a row to
   `packages/bridge/Editor/Config/McpClientCatalog.cs` and (if a new envelope)
   a branch in `BuildEntryFields`.

The Rust + TS sides must agree byte-for-byte (the wizard preview is asserted
to match the writer in `mcp_config.rs` tests). Run `cargo test --lib` +
`npm test` + `npm run check` after a catalog change.

## UI conventions

- Follow the existing component-per-zone pattern (tabs, popups, drawer). Do not create monolithic single-file UI shells.
- No internal references in UI strings — labels, tooltips, and help text must never contain `specs/` paths, milestone IDs, or task numbers (root `AGENTS.md` §No internal references).

## Verification

- Run `npm run check` and `npm test` after changes.
- Tauri command changes: update the corresponding frontend service wrapper in `src/lib/services/` in the same task.
