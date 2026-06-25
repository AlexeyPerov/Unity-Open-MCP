# Validation Suite

Standalone desktop app that guides milestone manual validation as repeatable scenario runs across engine MCP toolkits. Initially targets Unity Open MCP against a Unity project (e.g. the bundled `demo/` project).

The app replaces long, copy-heavy manual checklists with a guided operator workflow: filesystem + MCP/CLI actions automate setup, MCP-client verification stays human-driven (copy a prompt → run in the agent → paste the result), and progress + evidence persist as project-local data.

This app is a **standalone Tauri app**, not a Hub tab. It ships separately from `hub/` and invokes the engine via the MCP CLI as a subprocess.

## Stack

Tauri 2 + SvelteKit + Svelte 5 (mirrors the Hub frontend). Engine-neutral orchestration is framework-free TypeScript in `packages/core/`.

## Repository layout

```
validation-suite/
  src/                         SvelteKit UI
  src/lib/state/               Svelte 5 runes app state
  src/lib/services/            Tauri IPC wrappers
  src/lib/components/          UI components (project bar, test nav, step renderer)
  src-tauri/                   Rust shell (fs ops, persistence, project detection)
  packages/core/               scenario DTOs, loader, state model (engine-neutral TS)
  engine-profiles/
    unity.json                 Unity v1 profile (paths, CLI, companions, markers)
  scenarios/
    unity/sample/*.json        shipped sample scenario definitions
```

## Running

```bash
cd validation-suite
npm install
npm run tauri dev
```

Then use **Open project…** to select a Unity project folder (e.g. `../demo`). The suite validates the folder against the engine profile's project markers and scopes all state + fixtures under that project.

## Tests

```bash
# Engine-neutral core (node:test, TS)
npm run test:core

# UI type-check
npm run check

# Rust backend
cd src-tauri && cargo test
```

## Where data lives

Per active project + Unity profile:

- **State file:** `UserSettings/ValidationSuite/.state.json` — atomic read/write; survives app restart.
- **Actuals:** `UserSettings/ValidationSuite/actuals/` (wired in Phase 2).
- **Exports:** `UserSettings/ValidationSuite/exports/` (Phase 5).
- **Fixtures:** `Assets/_ValidationSuite/<test-id>/` (Phase 2).

State is **not migrated** between versions: a version mismatch produces a warning with reset guidance.

## Status

Phase 1 (this directory): app foundation — Tauri + SvelteKit scaffold, core package, project + profile selection, scenario loader with validation, state persistence, baseline UI.

Later phases add the action executor (`fs_*`, `mcp_tool`, `manual`) and manifest-based reset (Phase 2), bridge admin tools (Phase 3), the M9 required scenarios (Phase 4), and optional/export/process integration (Phase 5).
