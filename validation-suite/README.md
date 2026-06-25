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
  src/lib/state/               Svelte 5 runes app state (+ per-step action log)
  src/lib/services/            Tauri IPC wrappers (+ action backend adapter)
  src/lib/components/          UI components (project bar, test nav, step renderer, action log)
  src-tauri/                   Rust shell (sandboxed fs ops, MCP CLI runner, manifests, persistence)
  packages/core/               scenario DTOs, loader, state, action runner, patch transform (engine-neutral TS)
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

## Setup actions and reset (Phase 2)

Scenario `setup` steps run **declarative actions** through an engine-neutral runner (`packages/core/src/actions.ts`) that delegates to a Rust backend. Every fs action is **sandboxed to the project root** — traversal outside the project is rejected.

| Action | Executor | Behavior |
|---|---|---|
| `fs_copy` | Rust | Copies a file or directory tree; auto-tracks companion `.meta` when the source companion exists. |
| `fs_patch` | Rust | Applies the pinned patch-op vocabulary (`replace_line_contains`, `insert_after_line_contains`, `insert_before_line_contains`, `trim_trailing_whitespace`); snapshots the pre-patch file for reset. |
| `fs_delete` | Rust | Deletes manifest-listed paths (used by reset; no heuristic deletes). |
| `mcp_tool` | Rust subprocess | Runs an MCP tool via `unity-open-mcp run-tool --json`; surfaces `isError` and the tool body in the action log. |
| `manual` | UI gate | Records an info log; the operator confirms the action. |

Patch ops are validated at scenario-load time, so an unknown op never reaches the executor. Each mutating step records a **manifest** (created/modified artifacts + snapshots) under `UserSettings/ValidationSuite/manifests/`; the state file keeps only the blob id per step.

**Reset** walks a step's manifest in reverse order: modified files restore from their snapshot, created artifacts are deleted. Missing/incomplete manifest metadata warns and continues (best-effort) rather than crashing. Run setup from a step's **Run setup** button; re-run or reset with **Re-run setup** / the test-level **Reset test**.

## Where data lives

Per active project + Unity profile:

- **State file:** `UserSettings/ValidationSuite/.state.json` — atomic read/write; survives app restart.
- **Manifests:** `UserSettings/ValidationSuite/manifests/` — per-step artifact manifests for reset.
- **Actuals:** `UserSettings/ValidationSuite/actuals/` (wired in Phase 2).
- **Exports:** `UserSettings/ValidationSuite/exports/` — run-summary markdown (Phase 5).
- **Fixtures:** `Assets/_ValidationSuite/<test-id>/`.

State is **not migrated** between versions: a version mismatch produces a warning with reset guidance.

### Demo project hygiene

The suite stages disposable fixtures under `Assets/_ValidationSuite/` and writes local state under `UserSettings/ValidationSuite/`. The bundled `demo/` project's `.gitignore` ignores both so they never get committed:

- `Assets/_ValidationSuite/` — staged and reverted per scenario by the suite.
- `UserSettings/ValidationSuite/` — operator state, manifests, actuals, exports.

## Requirement tiers and optional scenarios

Every scenario declares a `requirementLevel` (idea.md → Coverage policy):

- **required-core** — the milestone closeout gate. Use the **Required · core** filter to isolate these.
- **required-extended** — a recommended confidence pass.
- **optional** — runnable; usually shows automated coverage. Collapsed into a default-closed "Optional" subsection within each milestone group, with an **Auto** badge when `automatedCoverage` references exist.

Optional scenarios carry an `automatedCoverage` array naming the tests that already cover the behavior; they stay runnable so an operator can do a live confidence pass even when automation exists.

## Export (run summary)

Use **Export…** in the top bar to produce a sign-off markdown summary of the current run:

- **Copy summary to clipboard** — markdown ready to paste into a milestone checklist or changelog.
- **Save summary as file…** — writes a timestamped `.md` under `UserSettings/ValidationSuite/exports/`.

The summary includes the project path, engine profile id, timestamp, a requirement-tier breakdown, one status table per milestone (required grouped, optional folded under an "Optional" subheading), and the closeout-gate verdict (passes only when every `required-core` scenario is `done`). The builder is engine-neutral (`packages/core/src/export.ts`) and unit-tested.

## Status

Phases 0–1: design contracts + app foundation — Tauri + SvelteKit scaffold, core package, project + profile selection, scenario loader with validation, state persistence, baseline UI.

Phase 2: action executor (`fs_*`, `mcp_tool`, `manual`) with project-root sandboxing, manifest-based reset, the MCP CLI subprocess runner, and a per-step action log panel.

Phase 3: operator-only bridge admin (`bridge_status` MCP tool) wired into the TopBar chip and offline scenarios.

Phase 4: the M9 `required-core` + `required-extended` scenario set, runnable end-to-end.

Phase 5 (this directory): optional automated-covered scenarios (extension matrix, error paths, compact-read quantitative checks, serializer behavior), the Export run summary, and milestone process wiring — the manual checklist convention now supports a Validation Suite index model and the demo `.gitignore` documents suite data paths.
