# M1 Plan 3 — Unity Versions tab, Tools tab, Kill Unity

**Spec:** [M1-hub-launcher.md](./M1-hub-launcher.md) §Unity Versions tab, §Tools tab  
**Index:** [execution-plan.md](./execution-plan.md)  
**Prerequisite:** [execution-plan-2-projects-launch.md](./execution-plan-2-projects-launch.md) Tasks 1–2 complete

How to use this plan: each task lists **Required context** — read only those docs for that task.

## Task Breakdown

#### Task 1: Unity Versions tab (M1-11) [Score:7] [Agent:medium] [DONE]

**Status:** DONE 2026-06-10 13:45 MSK.

**Required context**

1. [hub-ui.md](../../hub/hub-ui.md) §Unity Versions tab layout schema
2. [hub-data.md](../../hub/hub-data.md) §Unity version discovery
3. Plan 2 Task 1 — discovery service
4. Plan 2 Task 3 — Projects tab (for warnings cross-link)

- Implement Unity Versions tab per hub-ui wireframe:
  - toolbar: search + Refresh
  - conditional warnings banner when projects reference undiscovered versions
  - installations table: version, source, path, project count, installed date
  - action bar: open install folder, release notes link, run selected Unity
- Version cell color: `ok` / `warn` / `missing` mapping health.
- "Show projects" link on banner switches to Projects tab with missing-version filter.
- Release notes URL opens in system browser.

**Acceptance checklist**

- Table lists all discovered installs from Plan 2 Task 1.
- Project count per version matches `projects.json` data.
- Warnings banner appears when a project version has no install.
- Open folder and Run Unity work for selected row.

Dependencies: Plan 2 Tasks 1–3.

---

#### Task 2: Tools tab — launch args + platform intent (M1-12) [Score:7] [Agent:medium] [DONE]

**Required context**

1. [hub-ui.md](../../hub/hub-ui.md) §Tools tab layout schema
2. [hub-data.md](../../hub/hub-data.md) §Platform intent, launchArgs field
3. Plan 2 Task 2 — launch command builder
4. Plan 2 Task 3 — project selection state

- Implement Tools tab shell with project context bar (defaults to Projects tab selection).
- Launch args panel: load/save/reset per-project `launchArgs` in `projects.json`.
- Inline validation for unsafe characters or empty save.
- Platform intent selector: persist Unity `BuildTarget` name to `platformIntent`; show current value.
- Hint copy: intent applied on **next launch** only (not live switch).
- Wire Save/Reset to idempotent persistence from Plan 1.

**Acceptance checklist**

- Changing launch args persists and appears on next launch command.
- Platform intent persists and `-buildTarget` appears on next launch (verify via log or dev trace).
- Reset clears to empty args without removing project entry.
- Controls disabled when no project selected.

Dependencies: Plan 2 Tasks 2–3.

---

#### Task 3: Tools tab — log shortcuts (M1-13) [Score:6] [Agent:medium] [DONE]

**Status:** DONE 2026-06-10 14:30 MSK.

**Required context**

1. [hub-ui.md](../../hub/hub-ui.md) §Tools Log shortcuts panel
2. [hub-requirements.md](../../hub/hub-requirements.md) §Log shortcuts
3. [hub-data.md](../../hub/hub-data.md) — project path as anchor
4. Task 2 project context bar

- Resolve platform-aware log paths for selected project:
  - Editor logs folder
  - Player logs folder (where applicable)
  - Crash logs folder (where available per OS)
- Buttons open folder in native file manager; Editor.log link opens file when present.
- Disabled state when no project selected or path missing.
- Middle-click opens file where OS supports it.

**Acceptance checklist**

- Editor log folder opens on Windows and macOS for a typical project.
- Missing log paths show inline message, not silent no-op.
- Paths documented in code comments per OS.

Dependencies: Task 2.

---

#### Task 4: Kill Unity — PID-scoped terminate + confirmation (M1-14) [Score:8] [Agent:medium] [DONE]

**Status:** DONE 2026-06-10 14:30 MSK.

**Required context**

1. [hub-data.md](../../hub/hub-data.md) §Kill Unity
2. [hub-ui.md](../../hub/hub-ui.md) §Confirmation modal, Tools/Projects Kill actions
3. Plan 2 Task 2 — `lastLaunchPid` recording
4. Plan 1 Task 2 — modal component

- Tauri command: terminate process by PID with typed result (success, not found, access denied).
- Kill Unity action on Projects selection strip, Tools utilities panel, and context menu where specified.
- Confirmation modal when `settings.safety.confirmKillUnity` (default on).
- If PID missing or stale: inline message; **do not** kill all Unity processes.
- Clear or mark stale PID after successful kill.

**Acceptance checklist**

- Kill terminates only the PID recorded for selected project.
- Stale PID shows clear feedback without side effects on other Unity instances.
- Confirmation bypass works when safety toggle off.
- Kill available from Projects and Tools surfaces consistently.

Dependencies: Plan 2 Task 2; Tasks 2–3 of this plan for Tools wiring.

---

## Dependency graph

```text
Plan 2 Task 1 → Task 1
Plan 2 Tasks 2–3 → Task 2 → Task 3
Plan 2 Task 2 + Plan 1 Task 2 → Task 4
Task 2 → Task 4 (Tools surface)
```

## Plan 3 exit criteria

- [x] Unity Versions tab shows installs, warnings, and row actions.
- [x] Tools tab persists launch args and platform intent per project.
- [x] Log shortcuts work platform-aware for selected project.
- [x] Kill Unity is PID-scoped with confirmation and stale-PID handling.

**Next:** [execution-plan-4-settings-validation.md](./execution-plan-4-settings-validation.md)
