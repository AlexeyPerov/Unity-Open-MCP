# M1 Plan 1 — Scaffold, shell port, persistence

**Spec:** [M1-hub-launcher.md](./M1-hub-launcher.md) §Scaffold & persistence  
**Index:** [execution-plan.md](./execution-plan.md)  
**Prerequisite:** [questions/questions-1.md](../../questions/questions-1.md) resolved

How to use this plan: each task lists **Required context** — read only those docs for that task.

## Task Breakdown

#### Task 1: Greenfield `hub/` Tauri 2 + Svelte 5 scaffold (M1-1) [Score:7] [Agent:medium] [DONE]

**Required context**

1. [M1-hub-launcher.md](./M1-hub-launcher.md) — Scaffold & persistence
2. [monorepo-layout.md](../../architecture/monorepo-layout.md) §`hub/` scaffold
3. [hub-data.md](../../hub/hub-data.md) §Shell port and stack versions
4. `vibe-launcher` `package.json` — dependency version pins (local clone at path in hub-requirements)

- Create `hub/` at repo root with Tauri 2 + SvelteKit + Svelte 5 + Vite 6 matching `vibe-launcher` versions.
- Wire minimal app entry: empty main window, dev/build scripts, Tauri permissions baseline for later file/process ops.
- Add `hub/README.md` with local dev commands (`npm install`, `npm run dev`, `npm run tauri dev`).
- Ensure `hub/` is listed in root docs/index if a monorepo dev section exists.

**Acceptance checklist**

- `npm run tauri dev` from `hub/` opens a blank Hub window on the developer's OS.
- Dependency versions documented in `hub/README.md` match `vibe-launcher` pins from hub-data.
- No domain logic or config I/O yet — scaffold only.

Dependencies: none.

---

#### Task 2: Copy/adapt vibe-launcher shell components (M1-2) [Score:8] [Agent:medium] [DONE]

**Required context**

1. [hub-ui.md](../../hub/hub-ui.md) — Design intent, Main window structure, Shell layout schema
2. [hub-requirements.md](../../hub/hub-requirements.md) §Locked implementation decisions (UI reuse)
3. `vibe-launcher` source — tab strip, buttons, layout tokens, modal overlay pattern
4. Task 1 scaffold output in `hub/`

- Copy/adapt (not submodule) shell components into `hub/src/lib/components/shell/`:
  - top bar + pill-tab strip (Projects, Unity Versions, Tools, Settings)
  - tab panel flex column layout
  - button variants (primary/secondary/destructive)
  - confirmation modal overlay skeleton
  - status/log drawer shell (collapsible, empty state)
- Apply Hub branding ("Unity Agent Hub") and semantic status color tokens per hub-ui.
- Wire tab routing/state so each tab renders a placeholder panel.
- Global Refresh button in top bar (no-op handler stub).

**Acceptance checklist**

- Main window matches hub-ui shell wireframe zones (top bar, tab panel, status drawer).
- All four tabs switch without full page reload; placeholders visible.
- Modal and drawer components render in isolation/story or via dev trigger.
- No imports from `vibe-launcher` package path — copied Svelte components only.

Dependencies: Task 1.

---

#### Task 3: Config directory + split JSON persistence (M1-3) [Score:8] [Agent:heavy]

**Required context**

1. [hub-data.md](../../hub/hub-data.md) — Config directory, schemas, idempotent saves
2. [hub-requirements.md](../../hub/hub-requirements.md) §Non-functional requirements (reliability)
3. Task 1 Tauri scaffold — filesystem API permissions

- Implement config path resolver:
  - macOS/Linux: `~/.config/unity-agent-hub/`
  - Windows: `%APPDATA%\unity-agent-hub\`
- Define typed schemas for `settings.json` and `projects.json` (version field, safe defaults).
- Implement read/write layer with:
  - atomic write (temp file + rename) or equivalent safe pattern
  - idempotent saves (no duplicate entries on repeated write)
  - corrupt/missing file recovery → defaults + optional backup suffix
- Expose Tauri commands or frontend service API: `loadSettings`, `saveSettings`, `loadProjects`, `saveProjects`.
- Create config dir on first access if missing.

**Acceptance checklist**

- Fresh install creates both JSON files with documented default shapes.
- Repeated save of unchanged data does not corrupt or duplicate content.
- Truncated/invalid JSON falls back to defaults with logged warning (no crash).
- Paths resolve correctly on Windows and macOS (document test steps in hub README).

Dependencies: Task 1.

---

#### Task 4: First-run Unity Hub seed import (M1-4) [Score:9] [Agent:heavy]

**Required context**

1. [hub-data.md](../../hub/hub-data.md) §Project list source, Unity Hub seed paths
2. [questions/questions-1.md](../../questions/questions-1.md) — Q1 seed + owned store decision
3. Task 3 persistence layer

- Detect first run (empty/missing `projects.json` or explicit flag).
- Read Unity Hub editor registry and recent-projects data (OS-specific paths; `Editor.json` where present).
- Map Hub entries to `projects.json` project records:
  - generate stable `id` (uuid)
  - copy name, path, last-known unity version where available
  - set timestamps from Hub data when present
- Skip seed when `projects.json` already has projects (non-destructive re-launch).
- Document discovered Hub paths in `hub/` dev comments or README subsection.

**Acceptance checklist**

- First launch on a machine with Unity Hub projects populates `projects.json`.
- Second launch does not duplicate or overwrite user edits in `projects.json`.
- Seed failure (Hub not installed) yields empty project list + inline-friendly error for UI later.
- Imported paths validated for existence; missing paths kept with note for missing-path chip (Plan 2).

Dependencies: Task 3.

---

#### Task 5: Config persistence unit tests (M1-5 optional) [Score:5] [Agent:easy]

**Required context**

1. [hub-data.md](../../hub/hub-data.md) §Testing (M1)
2. Tasks 3–4 outputs

- Add cheap unit tests for schema defaults, round-trip serialize/deserialize, and corrupt-file recovery.
- Run from `hub/`: project test command + `npm run check` if configured.

**Acceptance checklist**

- Tests pass locally without Unity or Hub installed.
- Core persistence contracts locked (defaults, version field, project list shape).

Dependencies: Tasks 3–4.

---

## Dependency graph

```text
Task 1 → Task 2
Task 1 → Task 3 → Task 4 → Task 5
```

## Plan 1 exit criteria

- [ ] `hub/` scaffold runs on dev machine.
- [ ] Shell UI with four tab placeholders matches hub-ui structure.
- [ ] Split config read/write works with safe defaults.
- [ ] First-run seed imports Unity Hub projects when available.

**Next:** [execution-plan-2-projects-launch.md](./execution-plan-2-projects-launch.md)
