## 2026-06-10 14:30 MSK

- Completed M1 Plan 3 Tasks 3 & 4 — Tools tab log shortcuts + PID-scoped Kill Unity:
  - Added `hub/src-tauri/src/config/logs.rs` — platform-aware log path resolution. macOS uses `~/Library/Logs/Unity` (editor) and `~/Library/Logs/DiagnosticReports` (crash); Windows uses `%LOCALAPPDATA%\Unity\Editor` and `%LOCALAPPDATA%\CrashDumps`; Linux falls back to `~/.config/unity3d` for both. Player logs are always the project's own `Logs/` subfolder. New `LogPaths` struct + `log_paths(project_path)` Tauri command. 6 new unit tests covering player-dir subpath, absolute editor/crash dirs, editor-file-in-editor-dir, command/resolver parity, and empty defaults.
  - Added `hub/src-tauri/src/config/process.rs` — PID-scoped terminate with typed result. `KillUnityStatus` enum (`Killed` | `NotFound` | `AccessDenied`) and `KillUnityResult { pid, status, message }`. macOS/Linux: probes with `kill -0`, sends `SIGTERM` and falls back to `SIGKILL` if needed, then re-probes to distinguish vanished vs. denied. Windows: probes with `tasklist /FI "PID eq <pid>"` and terminates with `taskkill /F /PID <pid>`. PID 0 is rejected outright before any shell-out (would otherwise mean "process group" on Unix and is unused on Windows). New `kill_unity(pid)` Tauri command. 6 new unit tests (unused PID, real running process, second-kill after wait/reap, camelCase serialization for both result and status enum, PID 0 safety).
  - Registered `log_paths` and `kill_unity` in `hub/src-tauri/src/lib.rs` invoke handler.
  - Extended `hub/src/lib/services/config.ts` with `LogPaths`, `KillUnityStatus`, `KillUnityResult` types and `getLogPaths(projectPath)` / `killUnity(pid)` invokes.
  - Extended `ToolsTab.svelte` per `hub-ui.md` §Tools:
    - **Log shortcuts panel** with three folder buttons (Editor / Player / Crash) plus an Editor.log file link. Each button is disabled with an explanatory title when no project is selected or the path is missing. Opening is gated by a single `openingLog` state to prevent duplicate dialogs, with inline error feedback. The Editor.log file link supports middle-click (`onauxclick` with `button === 1`) per the spec. Per-OS path semantics are documented in the panel hint and the Rust comments.
    - **Utilities panel** with destructive `Kill Unity` button (PID-scoped, honouring `settings.safety.confirmKillUnity`), plus `Open project folder` and `Copy project path`. A hint line below the buttons shows the last-recorded `lastLaunchPid` and `lastLaunchAt` (or a "no PID recorded yet" message) so the user always knows what the Kill action will target. Successful and stale-`notFound` results both clear the recorded PID so subsequent Kill actions show the right state.
    - The `$effect` that watches `selected` now also refreshes log paths on project switch; selecting no project (or a project with no path) clears log paths, log errors, and per-field drafts.
  - Wired real `Kill Unity` in `ProjectsTab.svelte`:
    - Replaced the Plan 2 stub in the selection detail strip with a real handler. Button is enabled only when `lastLaunchPid` is present; label switches to `Killing…` while in flight; tooltip explains the missing-PID case.
    - Added a `Kill Unity` item to the right-click context menu between `Copy path` and the destructive separator, matching the new Tasks/Projects/Tools wiring and the spec's "context menu where specified" clause. Same enabled/disabled/tooltip rules as the selection strip.
    - Shared `performKill` / `handleKillUnity` helpers format `KillUnityResult` into a one-line status drawer message and clear `lastLaunchPid` from the project entry on `killed` or `notFound` outcomes, keeping the UI consistent with the recorded state.
  - `cargo test`: 74/74 pass (12 new). `npm run check`: 0 errors, 0 warnings. `npm run build` and `cargo build`: clean.
  - Marked Tasks 3 and 4 as DONE in [execution/M1/execution-plan-3-versions-tools.md](execution/M1/execution-plan-3-versions-tools.md); the last two Plan 3 exit criteria are now DONE.

## 2026-06-10 14:15 MSK

- Completed M1 Plan 3 Task 2 — Tools tab: launch args + platform intent:
  - Lifted the project selection out of `ProjectsTab.svelte` into the shared `projectsStore` (`projects.svelte.ts`) as `selectedProjectId` + `select(id)`. `add()` auto-selects new projects; `remove()` clears the selection when the removed project was selected. `ProjectsTab` now reads/writes through the store, so the Tools tab inherits the user's Projects selection on first open (or falls back to the first project when the user opens Tools directly).
  - Built out `ToolsTab.svelte` per `hub-ui.md` §Tools and [execution-plan-3 §Task 2](execution/M1/execution-plan-3-versions-tools.md):
    - **Project context bar** — labelled dropdown listing every project (`name · version`) plus the full path; changing it updates the store-wide selection. Disabled (with explanatory copy) when no projects exist.
    - **Launch args panel** — multi-line text input bound to the selected project's `launchArgs` draft, Save and Reset buttons. Save is disabled when the draft is empty (Reset is the way to clear) and when the input contains unsafe characters (regex blocks `\n`, `\r`, `\0`, `` ` ``, `$`, `|`, `&`, `;`, `<`, `>`); an inline error names the offending character. Persists via the existing `projectsStore.update()` round-trip. The hint line shows the currently saved value and reminds the user the args are appended on the **next** launch (matches the existing `launch_project` Tauri command, which whitespace-splits and appends after `-projectPath`).
    - **Platform intent panel** — labelled `<select>` of common `BuildTarget` names (StandaloneWindows64 / StandaloneWindows / StandaloneOSX / StandaloneLinux64 / iOS / Android / WebGL / WSAPlayer / tvOS / VisionOS) plus a "None" option, with a current-value display and Save button. Saving persists the chosen string to `platformIntent` via the same store round-trip; the existing `launch.rs` already appends `-buildTarget <name>` on the next launch, so no backend change was required. The hint copy explicitly states the intent is applied on the next launch only and is not a live switch while the editor is open.
    - Changing the project in the context bar resets both drafts to the new project's stored values via a `$effect` (and clears both the dirty flag and any pending validation error).
    - A dismissible inline error banner covers persist failures from the Tauri side, and the status drawer logs every save / reset / clear.
  - `cargo test`: 62/62 pass (no Rust changes). `npm run check`: 0 errors, 0 warnings. `npm run build` and `cargo check`: clean.
  - Marked Task 2 as DONE in [execution/M1/execution-plan-3-versions-tools.md](execution/M1/execution-plan-3-versions-tools.md); Plan 3 exit criterion "Tools tab persists launch args and platform intent per project" is now DONE.

## 2026-06-10 13:45 MSK

- Completed M1 Plan 3 Task 1 — Unity Versions tab:
  - Added `hub/src-tauri/src/config/launch.rs::run_unity_install` Tauri command — spawns the resolved Unity executable for a version with no project (opens the editor). Typed errors: `VersionMissing`, `InstallNotFound`, `ExecutableMissing`, `LaunchFailed` with camelCase serialization. Made `get_unity_executable_path` and `resolve_install_for_version` `pub(crate)` for reuse. Registered the new command in `hub/src-tauri/src/lib.rs`.
  - 7 new unit tests: 4 `RunUnityError` variants serialize correctly, `RunUnityResult` camelCase serialization, `get_unity_executable_path` finds macOS bundle, returns `None` for missing directory.
  - Added `opener:allow-open-url` to `hub/src-tauri/capabilities/default.json` (required for release notes link → system browser).
  - Extended `hub/src/lib/services/config.ts` with `RunUnityError` / `RunUnityResult` types and `runUnityInstall(version)` invoke.
  - Added `hub/src/lib/state/discovery.svelte.ts` — shared Svelte 5 `$state` discovery store with `load()` / `refresh()` and helpers for per-version project counts, missing-version buckets, and `ok` / `warn` / `missing` health mapping.
  - Extended `hub/src/lib/state.svelte.ts` with a `pendingProjectsFilter` signal + `requestProjectsFilter()` / `consumeProjectsFilter()` helpers so the Versions tab can deep-link into Projects with the missing-version filter pre-applied.
  - Built out `UnityVersionsTab.svelte` per `hub-ui.md` §Unity Versions:
    - Toolbar: search input (filters version + path) and Refresh button (re-runs discovery via `refreshDiscovery`).
    - Conditional warnings banner listing every `unityVersion` referenced by `projects.json` that has no install, with a "Show projects →" link that switches to the Projects tab and applies the missing-version filter.
    - Installations table (virtualized): colored health dot + status chip on the version cell (`ok` / `warn` / `missing`), source label, install path, project count, installed date.
    - Action bar: **Open Install Folder** (`openPath`), **Reveal** (`revealItemInDir`), **Release Notes ↗** (`openUrl` to `https://unity.com/releases/editor/whats-new/<dashes>`), **Run Unity** (new `runUnityInstall` command). All actions disabled with explanatory titles when no row is selected; Run Unity shows "Running…" while in flight.
    - Keyboard: ↑/↓/Home/End navigate, Enter runs the selected Unity, double-click also runs.
  - `ProjectsTab.svelte` consumes the pending filter on mount (covers the deep-link from the warnings banner). No change to its existing filter preset semantics.
  - Wired the global TopBar Refresh button to refresh both `discoveryStore` and `projectsStore` (logs summary to the status drawer).
  - `cargo test`: 62/62 pass (7 new). `npm run check`: 0 errors, 0 warnings. `cargo check`: clean.
  - Marked Task 1 as DONE in [execution/M1/execution-plan-3-versions-tools.md](execution/M1/execution-plan-3-versions-tools.md); Plan 3 exit criterion "Unity Versions tab shows installs, warnings, and row actions" is now DONE.

## 2026-06-10 13:20 MSK

- Completed M1 Plan 2 Task 5 — Project row actions (launch, folder, reveal, remove):
  - Added `hub/src-tauri/src/config/projects.rs::remove_project` Tauri command — removes a project entry by id from `projects.json` only (does not touch the project folder or Unity Hub registry). Typed errors: `ProjectNotFound`, `PersistFailed` with camelCase serialization. 3 new unit tests (result serialization, both error variants).
  - Registered `remove_project` in `hub/src-tauri/src/lib.rs` invoke handler.
  - Added `opener:allow-open-path` to `hub/src-tauri/capabilities/default.json` (the `opener:default` set does not include `openPath` — required for "Open Folder" via system default app).
  - Extended `hub/src/lib/services/config.ts` with `RemoveProjectError` / `RemoveProjectResult` types and `removeProject(projectId)` invoke.
  - Extended `hub/src/lib/state/projects.svelte.ts` with `remove(id)` store helper that updates in-memory list and persists.
  - Wired `ProjectsTab.svelte` row actions:
    - **Open Folder** uses `openPath()` from `@tauri-apps/plugin-opener`; disabled for missing-path rows with explanatory title; logs success / failure to status drawer and surfaces a dismissible inline error.
    - **Reveal in file manager** uses `revealItemInDir()` from the same plugin; same disabled/visible-tooltip treatment for missing paths. Mirrored on both the More ▾ menu and the right-click context menu (where Launch is now also disabled when not launchable).
    - **Remove from list** invokes the new Tauri command. Honors `settings.safety.confirmRemoveProject` via the existing `S.confirm(...)` modal; if disabled, removes immediately. Disabled while a remove is in flight ("Removing…" label).
    - Missing-path rows are remove-only: Launch, Open Folder, and Reveal are all disabled; only Copy path and Remove from list remain enabled.
    - Selection is cleared automatically when the selected project is removed.
  - `cargo test`: 55/55 pass (3 new). `npm run check`: 0 errors, 0 warnings. `cargo check`: clean.
  - Marked Task 5 as DONE in [execution/M1/execution-plan-2-projects-launch.md](execution/M1/execution-plan-2-projects-launch.md); Plan 2 exit criterion "Projects tab supports list, search, filter, add, and core row actions" is now DONE.

## 2026-06-10 13:10 MSK

- Completed M1 Plan 2 Task 4 — Search, filter, Add Project folder picker:
  - Added `tauri-plugin-dialog` (Rust crate + `@tauri-apps/plugin-dialog` JS package); registered the plugin in `hub/src-tauri/src/lib.rs` and added `dialog:default` to `hub/src-tauri/capabilities/default.json`.
  - Added `hub/src-tauri/src/config/projects.rs` — project add/refresh logic:
    - `add_project(path)` Tauri command: validates the path is a directory and contains `Assets/` and `ProjectSettings/` (returns typed `AddProjectError::{NotADirectory, NotAUnityProject, Duplicate, PersistFailed}`); rejects duplicates by canonical path; reads `ProjectVersion.txt` and filesystem mtime; generates a new uuid, appends to `projects.json`, and updates in-memory state.
    - `refresh_all_projects()` Tauri command: walks every entry, re-reads `ProjectVersion.txt` and directory mtime for paths that still exist (missing paths are reported in `skipped`); persists updates; re-runs Unity discovery and refreshes the cache.
    - 7 new unit tests (valid root, missing `Assets`, missing `ProjectSettings`, file-not-dir, version parsing, missing version file, name derivation).
  - Extended `hub/src/lib/services/config.ts` with `AddProjectError`, `AddProjectResult`, `RefreshAllResult` types and `addProject()` / `refreshAllProjects()` invokes.
  - Extended `hub/src/lib/state/projects.svelte.ts` with `add()` and `replaceAll()` store helpers.
  - Wired `ProjectsTab.svelte`:
    - Add Project opens a native folder picker via `@tauri-apps/plugin-dialog`, calls `add_project`, inserts the returned entry into the store, re-checks path existence, and selects the new row. Inline error banner appears for non-Unity / duplicate / persist errors and the status drawer logs success and failures.
    - Refresh button calls `refresh_all_projects`, swaps the store list, re-runs path-existence checks, and logs how many entries were updated/skipped.
    - AI Setup button remains disabled/reserved.
  - `cargo test`: 52/52 pass (7 new). `npm run check`: 0 errors, 0 warnings. Frontend and backend build cleanly.
  - Marked Task 4 as DONE in [execution/M1/execution-plan-2-projects-launch.md](execution/M1/execution-plan-2-projects-launch.md); marked Plan 2 exit criteria 1, 2, and 4 as DONE.

## 2026-06-10 12:30 MSK

- Completed M1 Plan 2 Task 3 — Projects tab (table, columns, selection):
  - Added `hub/src-tauri/src/config/commands.rs::check_paths_exists` Tauri command — returns `HashMap<path, bool>` so the UI can derive missing-path status without per-row IPC chatter. Registered in `hub/src-tauri/src/lib.rs`. 4 new unit tests (existing, missing, empty, mixed).
  - Added `hub/src/lib/state/projects.svelte.ts` — shared Svelte 5 `$state` projects store with `load()`, `find()`, `update()`, `persist()`.
  - Added shared UI primitives in `hub/src/lib/components/`:
    - `StatusChip.svelte` — ok / warn / missing / running / info / muted tones.
    - `RelativeTime.svelte` — compact relative date with full timestamp in `title`.
    - `VirtualList.svelte` — generic typed windowed list with overscan, page-keyboard nav (PageUp/PageDown/Home/End), and graceful fallback to direct rendering below 60 items.
  - Updated `Button.svelte` shell component to forward `title` and arbitrary rest props (e.g. `aria-haspopup`).
  - Built out `ProjectsTab.svelte` per `hub-ui.md` §Projects:
    - Toolbar: search input (respects `settings.projectList.searchIncludesPath`), filter pill group (all / launchable / missing version / missing path), reserved `AI Setup` disabled button, `Add Project` and `Refresh` stubs that log to the status drawer.
    - Table: virtualized list of `ProjectEntry` rows. Columns: Name, Path (`showPathColumn`), Version, Modified (`showModifiedColumn`), Status. Status chip rules: path existence + version presence → `ok` + `launchable` / `warn` (version missing) / `missing path` (Launch disabled).
    - Selection: single-click selects; selection strip mirrors row context with name · version · path and action bar (Launch / Open Folder / Kill Unity / More ▾). More menu and right-click context menu share: Launch, Open folder, Reveal in file manager, Copy path, Remove from list (last two stub-logged for Plan 2 Task 5).
    - Keyboard: ↑/↓ navigate, Home/End jump, Enter launches when launchable, ContextMenu key opens context menu, double-click launches. Esc closes menus.
    - Empty states: separate copy for "no projects yet" vs "no projects match filter".
  - Added `checkPathsExists(paths: string[])` to `hub/src/lib/services/config.ts`.
  - `npm run check`: 0 errors, 0 warnings. `cargo test`: 44/44 pass (4 new). Frontend and backend build cleanly.
  - Marked Task 3 as DONE in [execution/M1/execution-plan-2-projects-launch.md](execution/M1/execution-plan-2-projects-launch.md).

## 2026-06-10 04:00 MSK

- Completed M1 Plan 2 Task 2 — Launch resolver + Unity process spawn:
  - Added `hub/src-tauri/src/config/launch.rs` — resolves Unity executable for a project and spawns the editor process.
  - Resolve flow: match project `unityVersion` to discovered install, find platform-specific executable (macOS: `Unity.app/Contents/MacOS/Unity`, Windows: `Editor/Unity.exe`).
  - Typed errors: `ProjectNotFound`, `PathInvalid`, `VersionMissing`, `InstallNotFound`, `LaunchFailed` — all with camelCase serialization for frontend.
  - Launch command: `-projectPath <path>` (respects `settings.launch.mode`), per-project `launchArgs` (whitespace-split), `-buildTarget <platformIntent>` when set.
  - Spawns via `std::process::Command`; on success records `lastLaunchPid` and `lastLaunchAt` on project entry, persists to `projects.json`.
  - Refreshes project `unityVersion` from `ProjectSettings/ProjectVersion.txt` before launch.
  - Exposed `launch_project` and `refresh_project_version` Tauri commands.
  - Added 12 unit tests covering: version file parsing (normal, missing, empty, no newline, non-version lines), error variant serialization, result camelCase serialization.
  - All 40 tests pass; builds cleanly.
  - Marked Task 2 as DONE in execution-plan-2-projects-launch.md.

## 2026-06-10 03:00 MSK

- Completed M1 Plan 2 Task 1 — Unity installation discovery service:
  - Added `hub/src-tauri/src/config/discovery.rs` — scans parent folders for versioned Unity Editor installs with three-source precedence: user-configured `settings.unityDiscovery.parentFolders` (source: Manual), OS default Hub Editor paths (source: Hub), `UNITY_HUB` env override (source: Env).
  - Platform-aware detection: macOS checks `Unity.app` bundle, Windows checks `Editor/Unity.exe`, Linux checks `Editor/Unity`.
  - Builds in-memory model: version string, install path, source label, optional install date (from filesystem metadata).
  - Deduplicates installs found through multiple scan roots; sorted by version descending.
  - Scan errors are non-fatal — partial results returned with per-parent error detail for UI.
  - Discovery results cached in `AppState.discovery_cache`; `discover_installations` returns cache, `refresh_discovery` forces rescan.
  - Added `UnityInstallation`, `DiscoveryError`, `DiscoveryResult` types and `discoverInstallations()`, `refreshDiscovery()` to frontend config service.
  - Added 9 unit tests covering: find installs, sort order, skip non-editor dirs, nonexistent parents, unreadable dirs, source labels, deduplication, install dates, empty settings.
  - All 28 tests pass; builds cleanly.

## 2026-06-10 02:00 MSK

- Completed M1 Plan 1 Task 5 — config persistence unit tests:
  - Added 19 Rust unit tests in `hub/src-tauri/src/config/` covering schema defaults, round-trip serialize/deserialize, camelCase serialization, optional field exclusion, corrupt JSON rejection, partial JSON rejection, atomic write (create/overwrite/no leftover tmp), backup_corrupt rename, and corrupt-file recovery to defaults.
  - Added `tempfile` dev-dependency for clean test isolation.
  - All tests pass via `cargo test` without Unity or Hub installed.
  - Marked Task 5 as DONE in [execution/M1/execution-plan-1-foundation.md](execution/M1/execution-plan-1-foundation.md).

## 2026-06-10 01:30 MSK

- Completed M1 Plan 1 Task 4 — first-run Unity Hub seed import:
  - Added `hub/src-tauri/src/config/seed.rs` — reads Unity Hub's `projects-v1.json` (OS-specific paths), maps entries to Hub `ProjectEntry` records with stable UUIDs, ISO 8601 timestamps, and Unity version strings.
  - Seed is non-destructive: skipped when `projects.json` already has projects (second launch does not overwrite user edits).
  - Missing project paths are kept with `skippedPaths` note for missing-path chip (Plan 2).
  - Seed failure (Hub not installed / no projects file) yields empty project list + error message for UI.
  - Added `chrono` dependency for Unix epoch ms → ISO 8601 conversion.
  - Exposed `seed_from_unity_hub` Tauri command returning `SeedResult { projects, seededCount, skippedPaths, error? }`.
  - Added `seedFromUnityHub()` to frontend config service (`src/lib/services/config.ts`).
  - Updated `hub/README.md` with Unity Hub data paths, `projects-v1.json` format, and seed behavior docs.
  - Marked Task 4 as DONE in [execution/M1/execution-plan-1-foundation.md](execution/M1/execution-plan-1-foundation.md).

## 2026-06-10 01:00 MSK

- Completed M1 Plan 1 Task 3 — config directory + split JSON persistence:
  - Added `hub/src-tauri/src/config/` module: `paths.rs` (platform-specific config dir resolver), `schemas.rs` (typed `Settings` and `ProjectsFile` structs with safe defaults), `persistence.rs` (atomic write via temp+rename, idempotent saves, corrupt/missing file recovery with `.json.corrupt` backup), `commands.rs` (Tauri commands: `load_settings`, `save_settings`, `load_projects`, `save_projects`).
  - Added `hub/src/lib/services/config.ts` — frontend TypeScript types and `invoke` wrappers for all four commands.
  - Wired commands and `AppState` into Tauri builder in `lib.rs`.
  - Config dir: macOS/Linux `~/.config/unity-agent-hub/`, Windows `%APPDATA%\unity-agent-hub\`. Auto-created on first access.
  - Updated `hub/README.md` with config directory docs, file descriptions, and manual verification steps.
  - Marked Task 3 as DONE in [execution/M1/execution-plan-1-foundation.md](execution/M1/execution-plan-1-foundation.md).

## 2026-06-10 00:15 MSK

- Completed M1 Plan 1 Task 2 — shell components port:
  - Added `hub/src/lib/components/shell/` with TopBar (pill-tab strip + Refresh button), TabPanel, Button (primary/secondary/destructive), ConfirmationModal (overlay skeleton with promise-based confirm), StatusDrawer (collapsible, empty state, log tail).
  - Added `hub/src/lib/tabs/` with four placeholder panels: ProjectsTab, UnityVersionsTab, ToolsTab, SettingsTab.
  - Added `hub/src/lib/state.svelte.ts` (reactive tab state, modal confirm, drawer log store) and `hub/src/lib/tokens.ts` (brand name, status colors, brand color tokens).
  - Wired full shell layout in `+page.svelte` matching hub-ui wireframe zones (top bar, tab panel, status drawer).
  - Tab switching works without page reload; all four placeholders render.
  - Marked Task 2 as DONE in [execution/M1/execution-plan-1-foundation.md](execution/M1/execution-plan-1-foundation.md).

## 2026-06-09 23:15 MSK

- Completed M1 Plan 1 Task 1 scaffold work:
  - Added new `hub/` app scaffold at repo root (Tauri 2 + SvelteKit + Svelte 5 + Vite 6) with versions aligned to `vibe-launcher` pins.
  - Replaced template greet UI with a minimal blank Hub window and updated app metadata to "Unity Agent Hub".
  - Added baseline Tauri permissions/plugins for upcoming file/process operations (`fs`, `shell`, `opener`).
  - Added `hub/README.md` with local dev commands and documented pinned dependency versions.
  - Marked Task 1 as DONE in [execution/M1/execution-plan-1-foundation.md](execution/M1/execution-plan-1-foundation.md).

## 2026-06-09 23:30 MSK

- M1 execution planning:
  - Moved [execution/M1-hub-launcher.md](execution/M1/M1-hub-launcher.md) → [execution/M1/](execution/M1/).
  - Added [execution/M1/execution-plan.md](execution/M1/execution-plan.md) — index with assumptions, risks, dependency graph, exit criteria.
  - Added agent sub-plans: [execution-plan-1-foundation.md](execution/M1/execution-plan-1-foundation.md), [execution-plan-2-projects-launch.md](execution/M1/execution-plan-2-projects-launch.md), [execution-plan-3-versions-tools.md](execution/M1/execution-plan-3-versions-tools.md), [execution-plan-4-settings-validation.md](execution/M1/execution-plan-4-settings-validation.md) — 19 scored tasks with required context and acceptance checklists.
  - Updated [execution/README.md](execution/README.md), [README.md](README.md), [idea.md](idea.md), [questions/questions-0.md](questions/questions-0.md), [questions/questions-1.md](questions/questions-1.md) links.

## 2026-06-09 22:00 MSK

- Resolved [questions/questions-0.md](questions/questions-0.md) and [questions/questions-1.md](questions/questions-1.md):
  - M0: README-only status tracking; decisions folded into target specs; keep absolute `vibe-launcher` path (publish-time sanitization note).
  - M1: all ten recommended answers accepted — Hub seed + owned store, shell copy port, split config, PID Kill Unity, `BuildTarget` launch arg, folder-picker add, missing-path chip, discovery precedence, manual test matrix.
- Added [hub/hub-data.md](hub/hub-data.md) — config schemas, project list source, platform intent, Kill Unity, Unity discovery, stack versions.
- Updated [hub/hub-requirements.md](hub/hub-requirements.md), [hub/hub-ui.md](hub/hub-ui.md), [architecture/monorepo-layout.md](architecture/monorepo-layout.md), [execution/M1-hub-launcher.md](execution/M1-hub-launcher.md), [idea.md](idea.md), [README.md](README.md), [questions/README.md](questions/README.md).

## 2026-06-09 21:00 MSK

- Added `specs/questions/` — per-milestone pre-execution question files `questions-0.md` … `questions-7.md` with answer options and recommended defaults.
- Updated `specs/README.md` and `specs/idea.md` — link questions folder and milestone map column.

## 2026-06-09 20:00 MSK

- Resolved spec contradictions:
  - `specs/idea.md` — M0 marked DONE; wizard removed from M1/v1 baseline (M4 only); M2 tool count clarified (`ping` + 4 meta-tools).
  - `specs/hub/backlog.md` — purpose aligned to M1 launcher-only; removed per-project launch args (already in v1 Tools tab).
  - `specs/hub/hub-wizard.md` — bridge detect uses `com.alexeyperov.unity-agent-bridge`.
  - `specs/architecture/gate-policy.md` — empty `paths_hint` fallback labeled M2 stub (was M1).
- Restructured `specs/` layout:
  - `specs/architecture/` — `gate-policy.md`, `mcp-tools.md`, `monorepo-layout.md` (extracted from idea.md).
  - `specs/packages/` — `bridge.md` (new), `verify.md` (from `verify-package.md`), `mcp-server.md` (new).
  - `specs/agents/` — `agent-skill.md` (new).
  - `specs/execution/` — M0–M7 task plans; M0 marked DONE.
  - `specs/archive/README.md` — relocation map for moved paths.
- Updated `specs/README.md` — reading order, milestone map, manual setup path, full document index.

## 2026-06-09 18:00 MSK

- Added **OpenCode** as a first-class MCP client alongside Cursor and Claude:
  - `specs/idea.md` — architecture diagram, monorepo `skills/` / `templates/` notes, M3/M4 deliverable wording.
  - `specs/mcp-tools.md` — OpenCode `opencode.json` example, optional per-agent tool gating, client config path table.
  - `specs/hub/hub-wizard.md` — Step 1 detection heuristic, Step 4 OpenCode global/project options, Done screen links and actions.

## 2026-06-08 (specs tighten)

- Split roadmap **M6** / **M7** in `specs/idea.md`:
  - **M6** — bring-your-own-bridge (ecosystem docs only).
  - **M7** — MCP Resources and verify cache backing.
- Tightened `specs/mcp-tools.md`:
  - Added verify rule registry table (M3 / M3+ / M5; `regression_trend` not a rule).
  - Removed `unity_agent_scan_category` — use `scan_paths` with one `categories` entry.
  - Trimmed M7 Resources to three URIs; deferred references/categories/dependencies resources.
  - Renamed old combined M6 section into separate M6 (ecosystem) and M7 (resources).
- Updated `specs/verify-package.md` rule-ID milestones and M6/M7 mapping to match.

## 2026-06-09 15:25 MSK

- Updated `specs/hub-wizard.md` to reflect locked strategy decisions:
  - Greenfield Hub implementation (Tauri + Svelte), no LauncherPro fork.
  - Explicit v1 scope (core parity + selected power tools + minimal wizard).
  - Windows + macOS first-class for v1, Linux deferred.
  - Added references to backlog tracking for deferred scope.
- Updated `specs/idea.md` to align product/roadmap language with greenfield Hub:
  - Distribution pillar now describes Hub as Tauri + Svelte app.
  - Monorepo layout no longer describes `hub/` as a LauncherPro fork.
  - M3 deliverable wording updated to greenfield implementation.
  - Added v1 Hub scope baseline decisions and related backlog reference.
- Updated `specs/README.md`:
  - Added `specs/hub/backlog.md` to document index.
  - Updated Unity Agent Hub working-name description to greenfield Tauri + Svelte positioning.
- Added `specs/hub/backlog.md`:
  - Introduced structured deferred scope with priority buckets and milestone windows.
  - Captured UnityLauncherPro parity gaps not included in v1.
  - Added Linux support as explicit post-v1 platform backlog item.

## 2026-06-09 17:30 MSK

- Expanded `specs/hub/hub-ui.md` with full UI layout schemas:
  - Shell wireframe and zone table for the main window.
  - Per-tab wireframes, zone tables, and component hierarchy diagrams for Projects, Unity Versions, Tools, and Settings.

## 2026-06-09 16:53 MSK

- Restructured Hub specs under `specs/hub/` and split former wizard monolith into v1-focused documents:
  - Added `specs/hub/hub-requirements.md` as the main Hub v1 requirements/scope document.
  - Added `specs/hub/hub-ui.md` for tabbed UI architecture, layout schemas, and `vibe-launcher` reuse strategy.
  - Added `specs/hub/hub-wizard.md` as the dedicated wizard and MCP onboarding specification.
  - Removed legacy `specs/hub-wizard.md` after content migration.
- Updated `specs/README.md` document index to reference new Hub spec locations.
- Updated `specs/idea.md` roadmap:
  - Inserted new milestone after M0: **M1 — Hub v1 launcher** (without wizard and without new MCP integration).
  - Shifted subsequent milestones and renumbered sequencing through M6.
  - Updated milestone deliverable sections and related spec links to new Hub docs.
- Normalized milestone numbering in dependent specs to match the new roadmap:
  - Updated `specs/mcp-tools.md` milestone headings/references from M1-M5 to M2-M6 where applicable.
  - Updated `specs/verify-package.md` milestone mapping and references (wizard now M4, batch now M5, ecosystem now M6).
