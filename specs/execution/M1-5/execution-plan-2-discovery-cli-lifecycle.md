# M1.5 Plan 2 — Discovery, CLI, lifecycle

**Spec:** [M1-5-hub-polish.md](./M1-5-hub-polish.md) §Discovery & lifecycle
**Index:** [execution-plan.md](./execution-plan.md)
**Prerequisite:** [M1/execution-plan.md](../M1/execution-plan.md) complete; [Plan 1](./execution-plan-1-projects-list-ux.md) complete (CLI may start in parallel)

How to use this plan: each task lists **Required context** — read only those docs for that task.

## Task Breakdown

#### Task 1: CLI mode (auto-launch matching Unity) (M1.5-9) [Score:7] [Agent:medium] [DONE]

**Status:** DONE 2026-06-11 17:50 MSK. See changelog entry.

**Required context**

1. [M1/execution-plan-2-projects-launch.md §Task 2](../M1/execution-plan-2-projects-launch.md) — launch resolver
2. `tauri-plugin-cli` documentation
3. [hub/README.md](../../../hub/README.md) — distribution / install path notes

- Integrate `tauri-plugin-cli` and parse `unity-agent-hub -projectPath "<path>"` at startup.
- If `projectPath` is provided and the path is a valid Unity project root:
  - resolve the matching Unity version using the M1 discovery service
  - spawn Unity with the same command builder as a normal launch
  - record `lastLaunchPid` / `lastLaunchAt` and exit (do not open the Hub window unless no project is given)
- If `projectPath` is provided but invalid, log a clear error to stdout/stderr and exit non-zero.
- If no `projectPath` is provided, the app starts normally (Hub window opens).
- Document the CLI flag in `hub/README.md` and in the Settings tab Diagnostics section (Read-only "CLI help" copy button).

**Acceptance checklist**

- `unity-agent-hub -projectPath "<path>"` from a terminal launches the matching Unity on Windows and macOS.
- Invalid path produces a non-zero exit code and a one-line error.
- The Hub window does not appear when `projectPath` is provided (verify on both platforms).
- The M1 launch resolver is reused as-is; no duplicated launch path.
- macOS `.app` bundle treats argv correctly even when launched from Finder (use `tauri-plugin-cli`'s single-instance support if multi-launch protection is needed).

Dependencies: M1 Plan 2 Task 2.

---

#### Task 2: Running Unity instance auto-detection (M1.5-10) [Score:8] [Agent:heavy] [DONE]

**Status:** DONE 2026-06-11 18:05 MSK. See changelog entry.

**Required context**

1. [M1/execution-plan-2-projects-launch.md §Task 2](../M1/execution-plan-2-projects-launch.md) — launch resolver, `lastLaunchPid`
2. OS process APIs (`ps`, `tasklist`, or `sysinfo` crate)
3. [hub-ui.md](../../hub/hub-ui.md) §Projects tab status chips

- Scan for all running `Unity` processes (per-OS names: `Unity.exe` on Windows, `Unity` on macOS, `unityhub://` excluded).
- Match by `-projectPath "<path>"` argument to the discovered `projects.json` paths; tag the matching row as `running` (new status chip).
- A row with `lastLaunchPid === scannedPid` is `running` even if the `-projectPath` argument cannot be parsed.
- Rate-limit the scan to once per N seconds (default 5s; settings-toggleable).
- Do **not** kill any process from this task; that is M1 Plan 3 Task 4 (Kill Unity) and is unchanged.

**Acceptance checklist**

- Projects launched from outside Hub (e.g. Unity Hub, double-click on `Assets/`) are correctly tagged `running` when Hub is open.
- A project launched from Hub retains its `running` chip until the process exits.
- Scan does not noticeably increase CPU usage while Hub is idle (document the chosen rate).
- Unit tests cover the `-projectPath` argument parser against fixture command lines on each OS.

Dependencies: M1 Plan 2 Task 2.

---

#### Task 3: Walk-up directory scan for projects (M1.5-11) [Score:8] [Agent:heavy] [DONE]

**Status:** DONE 2026-06-11 18:25 MSK. See changelog entry.

**Required context**

1. [M1/execution-plan-1-foundation.md §Task 4](../M1/execution-plan-1-foundation.md) — first-run Unity Hub seed
2. [hub-data.md](../../hub/hub-data.md) §Project list source
3. [hub-ui.md](../../hub/hub-ui.md) §Settings tab "Unity discovery" section

- Add an alternate discovery source: walk-up directory scan from one or more user-chosen roots.
- Detection rule: a folder is a Unity project root if it contains both `Assets/` and `ProjectSettings/`. (Consistent with M1's "Add Project" validation.)
- Configurable depth (default 4, max 8, exposed in Settings as `settings.discovery.walkUpMaxDepth`).
- Progress UI: a modal / inline status with current root, current depth, found-so-far count, and a Cancel button. Run on a background task.
- New entries are appended to `projects.json` with the same uuid + path scheme as the M1 Add Project path; duplicates are skipped.
- Source label `Walk-up` on each row (or a separate column / hover chip) to distinguish from Hub-seeded and manually added rows.

**Acceptance checklist**

- Choosing a root folder finds Unity project subfolders up to the configured depth and adds them to the list.
- Scan is cancelable; partial results are kept or discarded per a Settings toggle (`settings.discovery.walkUpKeepPartial`).
- Projects deeper than the configured depth are not added; non-Unity folders are not added.
- Scan does not follow symlinks (or follows them only with a toggle, default off).
- Settings expose: roots list, max depth, follow-symlinks toggle, keep-partial toggle.

Dependencies: M1 Plan 1 Task 4 (seed) for the deduplication shape.

---

#### Task 4: New project creation — basic scaffold (M1.5-12) [Score:8] [Agent:medium]

**Status:** DONE 2026-06-11 19:13 MSK. See changelog entry.

**Required context**

1. [hub-requirements.md](../../hub/hub-requirements.md) §Explicitly not required for v1 (template-based creation deferred)
2. [hub-ui.md](../../hub/hub-ui.md) §Projects toolbar (entry point for "New project…")
3. [hub-data.md](../../hub/hub-data.md) §projects.json schema

- New toolbar entry: "New project…" (next to Add Project).
- Modal flow: pick an installed Unity version, enter a name, pick a parent folder, optionally pick a render pipeline (None / URP / HDRP — list driven by the discovery service and a small static map of which versions support which pipelines).
- Scaffold the project on disk:
  - create `<parent>/<name>/` with `Assets/`, `ProjectSettings/`, `Packages/`, `ProjectVersion.txt` (matching the chosen Unity version)
  - write a minimal `ProjectSettings/ProjectManager.asset` with the chosen `bundleVersion` (default `0.1.0`)
  - add the new project to `projects.json` with the resolved Unity version and current timestamp
- Errors (parent not writable, name collision, version install missing) are surfaced inline; the modal stays open until success or Cancel.

**Acceptance checklist**

- Flow produces a project that opens cleanly in the chosen Unity version on Windows and macOS.
- Render pipeline selection writes a minimal `Packages/manifest.json` with the right package line for the chosen pipeline and version (or "no manifest" for `None`).
- A name collision does not silently overwrite; user must pick a new name or confirm overwrite.
- Cancel at any step leaves the parent folder untouched.
- The new project appears at the top of the Projects tab (frecency-aware sort).

Dependencies: M1 Plan 2 Task 1 (discovery) for the Unity version dropdown; M1.5 Plan 1 Task 4 (frecency) for sort.

---

#### Task 5: Template-based new project creation (M1.5-13) [Score:8] [Agent:medium]

**Status:** DONE 2026-06-11 19:13 MSK. See changelog entry.

**Required context**

1. Task 4 (basic scaffold)
2. [hub-data.md](../../hub/hub-data.md) §Custom templates (new optional settings field `customTemplateFolders`)
3. UPM-style template structure: a folder containing `Assets/`, `ProjectSettings/`, `Packages/`, `ProjectVersion.txt`

- Extend the Task 4 modal with a "Template" picker: `Hub default` | `Empty` | `Custom folder…`.
- Hub default = Unity Hub's standard templates (only when Hub is installed and the templates folder is discovered).
- Empty = current Task 4 behavior.
- Custom folder = a user-picked folder validated as a Unity project root; the contents are copied (with overwrite) into the new project folder, and `ProjectVersion.txt` is rewritten to the chosen Unity version.
- New Settings field: `settings.discovery.customTemplateFolders` (list of absolute paths, validated on save).

**Acceptance checklist**

- Hub default templates appear in the picker when Hub is installed; otherwise the option is disabled with an inline message.
- Custom folder picker only accepts valid Unity project roots.
- Generated project opens cleanly in the chosen Unity version on both platforms.
- Custom template paths persist in `settings.json` and survive restart.
- Cancel returns to the Projects tab without leaving the template folder modified.

Dependencies: Task 4; M1 Plan 2 Task 1 for the Hub templates discovery.

---

#### Task 6: Unity upgrade assistant (M1.5-14) [Score:8] [Agent:medium]

**Required context**

1. [M1/execution-plan-2-projects-launch.md §Task 1](../M1/execution-plan-2-projects-launch.md) — `unityVersion` resolution
2. [hub-data.md](../../hub/hub-data.md) §projects.json schema (`unityVersion`, `bundleVersion`)
3. Unity `ProjectVersion.txt` + `ProjectSettings/ProjectManager.asset` layout

- New project row action: "Upgrade Unity…" (only when the project's `unityVersion` differs from any installed version *and* a higher installed version is available).
- Modal: list candidate versions (installed, higher than current); show the project's current Unity version, current `bundleVersion`, and a "preview" of the change.
- On confirm:
  - rewrite `ProjectVersion.txt` to the chosen version
  - bump `ProjectManager.asset` `bundleVersion` per a user-chosen strategy (`none` | `patch` | `minor` | `major`, default `patch`)
  - refresh `lastModified` and `unityVersion` in `projects.json`
  - log the change to the per-launch log file (M1.5 Plan 1 Task 2) with a `upgrade` event code
- Errors (file unwritable, `ProjectManager.asset` malformed) roll back the change to both files; the modal stays open.

**Acceptance checklist**

- Upgrade changes the project's `unityVersion` and the editor opens at the new version.
- `bundleVersion` bumps according to the chosen strategy.
- File-rewrite failure does not leave the project in a mixed state.
- The action is hidden when no upgrade is available (no installed higher version).
- The action is hidden when the user has already upgraded the project's Unity version manually since Hub's last refresh (path check is sufficient).

Dependencies: M1 Plan 2 Task 1; M1.5 Plan 1 Task 2 (per-launch log).

---

#### Task 7: Missing project handling UX parity (M1.5-15) [Score:6] [Agent:medium]

**Required context**

1. [M1/execution-plan-2-projects-launch.md §Task 5](../M1/execution-plan-2-projects-launch.md) — missing-path chip
2. M1.5 Plan 1 Task 8 — relink
3. [hub-data.md](../../hub/hub-data.md) §projects.json schema

- Extend the missing-path chip behavior beyond "remove-only / relink" with three controls:
  - `Relink…` (M1.5 Plan 1 Task 8) — pick a new folder
  - `Hide` — soft-delete; the row is removed from the list view but the entry is kept in `projects.json` with `hidden: true`
  - `Mark stale` — keep the row visible but tag it with a `stale` chip (distinct from `missing`); the row is treated as a candidate for a future relink and is excluded from `running`/launch actions
- New filter preset: `missing or stale` (visible only when the user wants to clean up).
- New Settings toggle: `settings.projectList.hideMissingByDefault` (default off).

**Acceptance checklist**

- `Relink` / `Hide` / `Mark stale` are all reachable from the row context menu and the selection strip.
- `Hide` removes the row from the default view; a `Show hidden` toggle in the Projects toolbar reveals it.
- `Mark stale` keeps the row visible with a distinct chip; Launch is disabled and the relink action remains.
- No destructive file operations; only `projects.json` changes.
- The M1 missing-path chip and behavior remain unchanged when the new toggles are off.

Dependencies: M1.5 Plan 1 Task 8 (relink); M1 Plan 2 Task 5 (missing path chip).

---

## Dependency graph

```text
M1 Plan 2 Task 2 → Task 1
M1 Plan 2 Task 2 → Task 2
M1 Plan 1 Task 4 → Task 3
M1 Plan 2 Task 1 + M1.5 Plan 1 Task 4 → Task 4
Task 4 + M1 Plan 2 Task 1 → Task 5
M1 Plan 2 Task 1 + M1.5 Plan 1 Task 2 → Task 6
M1.5 Plan 1 Task 8 + M1 Plan 2 Task 5 → Task 7
```

## Plan 2 exit criteria

- [x] CLI mode launches Unity from a terminal and respects Hub's launch resolver.
- [x] Running-Unity detection tags rows correctly with rate-limited scan.
- [x] Walk-up directory scan finds projects up to the configured depth and respects user toggles.
- [x] New project creation scaffolds a working Unity project for the chosen version.
- [x] Template-based creation supports Hub default + Empty + Custom folder.
- [ ] Unity upgrade assistant bumps the version + `bundleVersion` with rollback on error.
- [ ] Missing project handling supports Relink + Hide + Mark stale with new filter preset.

**Next:** [execution-plan-3-tools-theme.md](./execution-plan-3-tools-theme.md)
