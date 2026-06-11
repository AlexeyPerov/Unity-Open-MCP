# M1.5 Plan 3 — Tools & theme extensions

**Spec:** [M1-5-hub-polish.md](./M1-5-hub-polish.md) §Tools & theming
**Index:** [execution-plan.md](./execution-plan.md)
**Prerequisite:** [M1/execution-plan.md](../M1/execution-plan.md) complete; [Plan 1](./execution-plan-1-projects-list-ux.md) complete

How to use this plan: each task lists **Required context** — read only those docs for that task.

## Task Breakdown

#### Task 1: Additional log folder shortcuts (M1.5-16) [Score:4] [Agent:easy] [DONE 2026-06-11 21:21 MSK]

**Required context**

1. [M1/execution-plan-3-versions-tools.md §Task 3](../M1/execution-plan-3-versions-tools.md) — log shortcuts
2. M1.5 Plan 1 Task 1 — Asset Download shortcut
3. [hub-ui.md](../../hub/hub-ui.md) §Tools Log shortcuts panel

- Audit current Log shortcuts coverage: Editor logs, Player logs, Crash logs (M1), Asset Download (M1.5 Plan 1 Task 1).
- Add any remaining ULP log locations: e.g. Asset Store download folder (already done), Unity Player log (per-user), Unity Editor additional log (`Editor-prev.log`), and any platform-specific crash dump folders (e.g. macOS `~/Library/Logs/DiagnosticReports/Unity*`).
- Per-OS path constants in code; one button per location in the panel; same disabled/inline-error pattern as M1.
- Document the new entries in `hub/README.md` diagnostics section.

**Acceptance checklist**

- Every ULP log location referenced in the backlog (Phase 2 P2) has a corresponding button in the panel.
- Buttons behave identically to the existing M1 shortcuts (open / reveal / inline error when missing).
- No new settings fields required.
- Manual cross-platform test passes for every new button.

Dependencies: M1 Plan 3 Task 3; M1.5 Plan 1 Task 1.

---

#### Task 2: Project-level custom environment variables (M1.5-17) [Score:7] [Agent:medium] [DONE 2026-06-11 21:21 MSK]

**Required context**

1. [M1/execution-plan-3-versions-tools.md §Task 2](../M1/execution-plan-3-versions-tools.md) — Tools tab shell, project context bar
2. [hub-data.md](../../hub/hub-data.md) §projects.json schema (new optional `envVars` field)
3. [hub-ui.md](../../hub/hub-ui.md) §Tools tab layout schema

- Add an "Environment variables" panel in the Tools tab below the existing launch args panel.
- UI: key/value rows with Add / Remove; values masked while typing (toggle to reveal); validation (no duplicate keys, no empty keys).
- Persist to `projects.json` under a new `envVars` field (record of strings).
- Apply to the next launch: M1.5 Plan 2 Task 1 (CLI) and M1 Plan 2 Task 2 (launch resolver) extend their command builders to merge `envVars` into the spawned process environment (env vars in the child override the parent where keys collide).
- Settings toggle: `settings.safety.confirmEnvVarOverride` (default on) — if on, a confirmation modal lists colliding keys before launch.

**Acceptance checklist**

- Adding an env var and launching Unity makes the variable visible to the editor (e.g. `Debug.Log(System.Environment.GetEnvironmentVariable("MY_KEY"))`).
- Removing an env var stops it from being applied on the next launch.
- Empty / whitespace-only keys are rejected; duplicate keys are rejected at save time.
- Collisions with the parent process are listed in the confirmation modal when the safety toggle is on.
- No data loss: `envVars` is optional in `projects.json`; existing M1 projects load unchanged.

Dependencies: M1 Plan 3 Task 2; M1 Plan 2 Task 2; M1.5 Plan 2 Task 1 (CLI).

---

#### Task 3: Theme support — dark / light / system with live switching (M1.5-18) [Score:7] [Agent:medium] [DONE 2026-06-11 21:21 MSK]

**Required context**

1. [hub-ui.md](../../hub/hub-ui.md) §Settings (theme entry)
2. [hub-data.md](../../hub/hub-data.md) §settings.json schema (new optional `theme` field: `dark` | `light` | `system`, default `system`)
3. Tauri + Svelte theme pattern (e.g. `data-theme` attribute on `<html>`)

- Add a `theme` field to `settings.json`; default to `system`.
- Implement three CSS themes by extending the existing `tokens.ts` (or equivalent) — do **not** duplicate token sets. Add `theme.dark.css` and `theme.light.css` overrides driven by `[data-theme="dark"]` / `[data-theme="light"]` on the root element.
- The `system` value listens to the OS `prefers-color-scheme` media query; flip automatically.
- Add a Settings entry: radio group `Dark` / `Light` / `System`. Switch applies live with no app restart; the per-launch log file (M1.5 Plan 1 Task 2) records the active theme on each launch.
- Status / log drawer styling must remain readable in all three modes (verify contrast).

**Acceptance checklist**

- Switching theme in Settings updates the UI immediately, with no flash on the next app start.
- `system` mode follows the OS toggle; document how to test (macOS System Settings → Appearance; Windows Settings → Personalization → Colors).
- All Hub surfaces (top bar, tab panels, modal, drawer, status chips) are readable in all three modes.
- `theme` is optional in `settings.json`; existing users default to `system` and the app still works.
- The `Out of scope` RGBA theme editor from the backlog is **not** re-introduced; the three-way switch is the entire theme UX.

Dependencies: M1 Plan 1 Task 2 (shell tokens); M1.5 Plan 1 Task 2 (per-launch log) for the theme log entry.

---

#### Task 4: Unity releases/updates viewer with release notes links (M1.5-19) [Score:8] [Agent:medium] [DONE 2026-06-11 21:21 MSK]

**Required context**

1. [M1/execution-plan-3-versions-tools.md §Task 1](../M1/execution-plan-3-versions-tools.md) — Unity Versions tab
2. Unity release page (public; do not invent URLs — fetch from a known source or use the Unity Hub URL)
3. [hub-ui.md](../../hub/hub-ui.md) §Unity Versions tab layout (add a sub-tab or split view)

- Add a "Releases" sub-section in the Unity Versions tab (toggle in the toolbar: `Installed` | `All releases`).
- Fetch Unity release metadata from a single, well-known source (Unity's official releases page or a stable JSON feed if Hub exposes one). Do not scrape arbitrary sites without a documented stable URL.
- Render a table: version, stream, release date, release notes link, "installed" chip if the version is discovered locally.
- Click on a row opens the release notes URL in the system browser; right-click / context menu offers "Copy version" and "Use as Upgrade target" (navigates to the relevant project if any).
- Network failures are non-fatal: the section shows an inline error and a Retry button. Cached data (if previously fetched) is shown with a "stale" badge.

**Acceptance checklist**

- "All releases" lists the current Unity release streams (LTS, TECH, BETA, ALPHA) with version + date + notes link.
- Installed versions show an "installed" chip and link back to the install row.
- Network failure does not crash the tab; the user sees a clear error.
- Release notes URL opens in the system browser; no in-app browser is introduced.
- Caching: the fetch is debounced (once per hour per user) and stored in the config dir under `cache/releases.json`.

Dependencies: M1 Plan 3 Task 1 (Unity Versions tab); M1.5 Plan 1 Task 2 (per-launch log) for "view releases" audit trail.

---

## Dependency graph

```text
M1 Plan 3 Task 3 + M1.5 Plan 1 Task 1 → Task 1
M1 Plan 3 Task 2 + M1 Plan 2 Task 2 + M1.5 Plan 2 Task 1 → Task 2
M1 Plan 1 Task 2 + M1.5 Plan 1 Task 2 → Task 3
M1 Plan 3 Task 1 + M1.5 Plan 1 Task 2 → Task 4
```

## Plan 3 exit criteria

- [x] All ULP log location shortcuts are surfaced in the Tools tab.
- [x] Project-level env vars persist and apply on the next launch with collision confirmation.
- [x] Three-way theme switch works live with no flash on app start.
- [x] Unity releases viewer lists streams with release notes links and a graceful offline state.

**Next:** [execution-plan-4-platform-niche.md](./execution-plan-4-platform-niche.md)
