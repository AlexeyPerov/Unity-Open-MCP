# M1 Plan 2 — Discovery, launch, Projects tab

**Spec:** [M1-hub-launcher.md](./M1-hub-launcher.md) §Projects tab  
**Index:** [execution-plan.md](./execution-plan.md)  
**Prerequisite:** [execution-plan-1-foundation.md](./execution-plan-1-foundation.md) complete

How to use this plan: each task lists **Required context** — read only those docs for that task.

## Task Breakdown

#### Task 1: Unity installation discovery service (M1-6) [Score:8] [Agent:heavy] [DONE]

**Required context**

1. [hub-data.md](../../hub/hub-data.md) §Unity version discovery
2. [hub-ui.md](../../hub/hub-ui.md) §Unity Versions (discovery feeds this tab later)
3. Plan 1 Task 3 — settings schema (`unityDiscovery.parentFolders`)
4. Tauri filesystem APIs

- Implement discovery scan with precedence:
  1. `settings.unityDiscovery.parentFolders`
  2. OS default Unity Hub Editor paths
  3. `UNITY_HUB` env override as additional root
- Within each parent, enumerate versioned Editor installs (folder layout per OS).
- Build in-memory model: version string, install path, source label (`Hub` / `Manual` / `Env`), optional install date.
- Cache scan results; expose refresh hook for global Refresh and Settings-driven rescan.
- Do **not** parse arbitrary single-file `Unity.exe` / `.app` paths (deferred).

**Acceptance checklist**

- Discovery finds standard Hub installs on developer OS.
- User-added parent folder in settings is included on next refresh.
- `UNITY_HUB` env adds/install roots when set.
- Scan errors are non-fatal; partial results returned with error detail for UI.

Dependencies: Plan 1 complete.

---

#### Task 2: Launch resolver + Unity process spawn (M1-7) [Score:9] [Agent:heavy] [DONE]

**Required context**

1. [hub-data.md](../../hub/hub-data.md) §Platform intent, Kill Unity (`lastLaunchPid`)
2. [hub-requirements.md](../../hub/hub-requirements.md) §Version handling, Stability
3. Task 1 discovery service
4. Plan 1 Task 3 — projects persistence

- Resolve Unity executable for a project:
  - match project `unityVersion` to discovered install
  - surface typed errors: version missing, install not found, path invalid
- Build launch command:
  - open project at `path`
  - append per-project `launchArgs`
  - append `-buildTarget <platformIntent>` when `platformIntent` set
  - respect settings launch mode (open project vs empty editor) where applicable
- Spawn via Tauri; on success record `lastLaunchPid`, `lastLaunchAt` on project entry and persist.
- Refresh project `unityVersion` from `ProjectSettings/ProjectVersion.txt` when path exists.

**Acceptance checklist**

- Launch opens correct Unity version for a test project.
- `-buildTarget` applied only when `platformIntent` stored.
- Failed launch shows actionable error (missing install, bad path) without corrupting config.
- Successful launch persists PID fields for Kill Unity (Plan 3).

Dependencies: Task 1; Plan 1 Task 3.

---

#### Task 3: Projects tab — table, columns, selection (M1-8) [Score:7] [Agent:medium] [DONE]

**Required context**

1. [hub-ui.md](../../hub/hub-ui.md) §Projects tab layout schema
2. [hub-data.md](../../hub/hub-data.md) §projects.json schema
3. Plan 1 Task 2 — shell tab slot
4. Plan 1 Task 3 — projects store

- Implement Projects tab per hub-ui wireframe:
  - toolbar row (search, filter, Add Project, Refresh stubs wired later)
  - virtualized or performant table: name, path, version, modified, status chips
  - selection detail strip with action bar
- Column visibility driven by `settings.projectList` toggles (Settings tab in Plan 4; use defaults until then).
- Status chips: `ok`, `warn` (version missing), `missing path`, `launchable` composite rules.
- Keyboard: ↑↓ navigate, Enter launch (stub until Task 5), context menu key.

**Acceptance checklist**

- Project list renders from `projects.json` with responsive scroll for large lists.
- Single-click selects; selection strip mirrors row state.
- Missing-path rows show chip and disable Launch in UI.
- Double-click triggers launch handler (wire to Task 2).

Dependencies: Plan 1 Tasks 2–3; Task 2 for launch wiring.

---

#### Task 4: Search, filter, Add Project folder picker (M1-9) [Score:7] [Agent:medium]

**Required context**

1. [hub-ui.md](../../hub/hub-ui.md) §Projects toolbar behavior
2. [hub-data.md](../../hub/hub-data.md) §Add project (folder picker only)
3. [hub-requirements.md](../../hub/hub-requirements.md) §Project list
4. Task 3 Projects tab

- Search filters name + path when `settings.projectList.searchIncludesPath` true.
- Filter presets: all / launchable / missing version / missing path.
- Add Project: native folder picker → validate Unity project root (`Assets/`, `ProjectSettings/`) → append to `projects.json` with new uuid.
- Refresh: rescan project metadata (version, modified) + trigger discovery refresh (Task 1).
- Reserve AI Setup button slot disabled/hidden (Plan 4).

**Acceptance checklist**

- Search and filters compose correctly (intersection).
- Add Project rejects non-Unity folders with inline error.
- Duplicate path not added twice.
- Refresh updates version/modified columns without data loss.

Dependencies: Task 3; Task 1 for refresh.

---

#### Task 5: Project row actions — launch, folder, reveal, remove (M1-10) [Score:8] [Agent:medium]

**Required context**

1. [hub-ui.md](../../hub/hub-ui.md) §Projects context menu, selection strip
2. [hub-data.md](../../hub/hub-data.md) §Missing paths, Remove project
3. [hub-requirements.md](../../hub/hub-requirements.md) §Safety toggles
4. Tasks 2–4

- Wire Launch to Task 2 resolver (disabled for missing path).
- Open folder / reveal in explorer (platform-native).
- Remove from list: confirmation when `settings.safety.confirmRemoveProject`; update `projects.json` only.
- Context menu: copy path, reveal, remove, etc.
- Missing-path rows: remove-only; no relink UX.
- Errors inline first; status/log drawer opens on launch failure (shell from Plan 1).

**Acceptance checklist**

- Full launch → open → reveal flow works on Windows and macOS.
- Remove does not delete project folder or Unity Hub registry.
- Confirmation modals respect safety settings.
- Kill Unity button present but may delegate to Plan 3 (stub disabled until then).

Dependencies: Tasks 2–4.

---

## Dependency graph

```text
Task 1 → Task 2 → Task 5
Task 1 → Task 4
Plan 1 → Task 3 → Task 4 → Task 5
```

## Plan 2 exit criteria

- [ ] Unity installs discovered with documented precedence.
- [ ] Projects launch with correct version, args, and optional `-buildTarget`.
- [ ] Projects tab supports list, search, filter, add, and core row actions.
- [ ] Missing-path and missing-version states visible and enforced in UI.

**Next:** [execution-plan-3-versions-tools.md](./execution-plan-3-versions-tools.md)
