# M1.5 Plan 3.5 — Unity install, releases catalog, shell chrome, select styling

**Spec:** [M1-5-hub-polish.md](./M1-5-hub-polish.md) §Tools & theming / shell polish (follow-on)
**Index:** [execution-plan.md](./execution-plan.md)
**Prerequisite:** [Plan 3](./execution-plan-3-tools-theme.md) complete (Unity Versions tab, theme tokens, releases viewer baseline)

How to use this plan: each task lists **Required context** — read only those docs for that task.

## Task Breakdown

#### Task 1: Unity Editor install from the Hub (M1.5-20) [Score:8] [Agent:medium] — DONE

**Required context**

1. [execution-plan-3-tools-theme.md §Task 4](./execution-plan-3-tools-theme.md) — Unity releases viewer (M1.5-19)
2. [M1/execution-plan-3-versions-tools.md §Task 1](../M1/execution-plan-3-versions-tools.md) — Unity Versions tab, discovery refresh
3. Unity Hub CLI reference — `install` command (`--version` / `-v`, optional `--changeset`, `-m` modules): [Unity Hub CLI](https://docs.unity.com/en-us/hub/hub-cli)
4. Standalone Unity CLI (optional fallback): [Unity CLI](https://docs.unity.com/en-us/hub/unity-cli) — `unity install [version]`
5. [hub-ui.md](../../hub/hub-ui.md) §Unity Versions tab layout (action bar + All releases rows)

- Add a Rust Tauri command (e.g. `install_unity_version`) that spawns the Unity Hub CLI in headless mode:
  - macOS: `/Applications/Unity Hub.app/Contents/MacOS/Unity Hub -- --headless install -v <version>` (+ optional `--changeset`, `-m` modules, `-a` architecture on Apple Silicon).
  - Windows: `"C:\Program Files\Unity Hub\Unity Hub.exe" -- --headless install -v <version>` (same flags).
  - Linux: AppImage path from settings or a documented default when Hub is present.
- Resolve the Hub executable the same way `new_project.rs` already probes for Hub templates; return a typed error when Hub is not installed (surface copy: "Install Unity Hub or add the editor manually").
- Stream install stdout/stderr into the status drawer (append-only log lines); mark the command as in-progress in the frontend so the Install button shows a spinner.
- On success (process exit 0), call `refresh_discovery` automatically so the new version appears under **Installed** without a manual Refresh.
- UI — **Unity Versions → All releases**:
  - Add an **Install** primary action on rows where the version is not installed (action bar when a not-installed release is selected, or a per-row Install affordance — pick one pattern and stay consistent).
  - Disable Install while an install is running; show inline error text from the typed error on failure (licensing, network, missing changeset).
- Optional advanced (defer if heavy): module picker modal (Android, WebGL, …) before install; document as follow-up in task notes if skipped.

**Acceptance checklist**

- Selecting a not-installed release and clicking **Install** kicks off a headless Hub CLI install when Unity Hub is present.
- Progress / log lines appear in the status drawer during the install; the tab does not freeze.
- After a successful install, the version shows under **Installed** with `Source: Hub` (or `Manual` if the path is outside the default Hub root) without restarting the app.
- When Unity Hub is missing, the user sees a clear inline error — no silent failure.
- Install does not block other tabs; only one install runs at a time (second attempt is disabled with a message).
- Manual test on macOS and Windows with at least one small TECH/LTS version.

Dependencies: Plan 3 Task 4 (releases list + installed chip); M1 Plan 3 Task 1 (discovery refresh).

---

#### Task 2: Full installable catalog with archived releases toggle (M1.5-21) [Score:8] [Agent:medium] — DONE

**Required context**

1. Task 1 above (install command needs a resolved version + changeset)
2. Unity Hub CLI reference — `editors` command (`-r` releases, `--all` for archive): [Unity Hub CLI](https://docs.unity.com/en-us/hub/hub-cli)
3. [hub/src-tauri/src/config/releases.rs](../../../hub/src-tauri/src/config/releases.rs) — current static snapshot + cache (`cache/releases.json`)
4. [execution-plan-3-tools-theme.md §Task 4](./execution-plan-3-tools-theme.md) — All releases sub-section UX

- Replace (or augment) the static `snapshot_entries()` list with live data from the Hub CLI:
  - Default fetch: `editors -r` (current release streams) — parse CLI output into the existing `ReleaseEntry` shape (`version`, `stream`, `releaseDate` when available, `releaseNotesUrl` built from the documented Unity site pattern).
  - When **Include archived** is enabled in the toolbar: re-fetch with `--all` (or the documented archive flag) so older Editor versions appear in the table.
- Persist fetched payloads in `cache/releases.json` with the existing 1-hour TTL + `stale` badge + Retry button (reuse M1.5-19 infrastructure; extend the schema only if the CLI adds fields we need, e.g. `changeset` for archive installs).
- Toolbar toggle: **Include archived** (off by default). When off, the table shows current streams only; when on, show every version the CLI returns (may be large — keep search filter; consider virtualizing if row count exceeds ~200).
- Wire **Install** (Task 1) to pass `--changeset` when the catalog entry includes one (required for some archived builds).
- Non-fatal failures: if the CLI is unavailable, fall back to the static snapshot with `stale: true` and an inline message ("Unity Hub CLI unavailable — showing cached / bundled list").

**Acceptance checklist**

- **All releases** lists versions from the Hub CLI when Hub is installed; static snapshot is only a fallback.
- **Include archived** off → no archive-only versions in the table; on → archived versions appear and are searchable.
- Cached data respects the 1-hour TTL; Refresh forces a new CLI fetch.
- Rows still show stream chip, release notes link, and installed chip (side-by-side with discovery).
- Install on an archived row succeeds when `--changeset` is supplied from the catalog entry.
- CLI / Hub missing does not crash the tab; user sees stale badge + fallback list.

Dependencies: Plan 3 Task 4; Task 1 (install uses catalog metadata).

---

#### Task 3: macOS integrated title bar with native window controls (M1.5-22) [Score:6] [Agent:medium]

**Required context**

1. [hub-ui.md](../../hub/hub-ui.md) §Global shell layout
2. Tauri 2 window customization — `titleBarStyle: Overlay`, `hiddenTitle`, `trafficLightPosition`, `data-tauri-drag-region`: [Window customization](https://v2.tauri.app/learn/window-customization/)
3. [hub/src/routes/+page.svelte](../../../hub/src/routes/+page.svelte) — theme CSS variables (`--hub-*`)
4. [hub/src-tauri/tauri.conf.json](../../../hub/src-tauri/tauri.conf.json) — window config
5. [hub/src-tauri/capabilities/default.json](../../../hub/src-tauri/capabilities/default.json) — permissions

- macOS only in this task; Windows/Linux keep the current window chrome (document as follow-up if needed).
- Configure the main window for an overlay title bar with **native** traffic-light buttons (close, minimize, zoom) — do **not** draw custom window-control glyphs; use Apple's standard controls positioned via `trafficLightPosition`.
- Add a slim top chrome strip inside the webview:
  - Background uses `--hub-surface` (or `--hub-bg`) so the bar matches the active theme (dark / light / system).
  - Left inset clears space for the native traffic lights (~70–80px); the remainder is a `data-tauri-drag-region` drag handle (no custom widgets in the bar for this task — title / tabs stay in the existing sidebar layout below).
  - Height ~28–32px; the main `.app` content sits below the strip (adjust padding on `.shell` / `.app` so nothing is clipped under the overlay bar).
- Tauri config: `decorations: true`, `titleBarStyle: Overlay`, `hiddenTitle: true`, sensible `trafficLightPosition` (tune per theme/DPI).
- Capabilities: add `core:window:allow-start-dragging`.
- Verify theme live-switch (M1.5-18) updates the bar color immediately; verify traffic-light position after window resize (re-apply if Tauri resets position on title change — avoid unnecessary `setTitle` calls).

**Acceptance checklist**

- On macOS, the window shows native close / minimize / zoom buttons embedded in a theme-colored top strip (VS Code–style integration, not a separate gray system title bar).
- The strip is draggable; double-click on the drag region does not break window behavior.
- Dark, light, and system themes all produce a readable bar with correct contrast.
- Existing sidebar (`TopBar.svelte`) and tab content layout remain usable — no overlap with traffic lights.
- Windows and Linux builds are unaffected (no regression in window decorations).
- Manual sign-off on macOS (Intel + Apple Silicon if available).

Dependencies: Plan 3 Task 3 (theme tokens); M1 Plan 1 Task 2 (shell layout).

---

#### Task 4: Custom Select component matching Button styling (M1.5-23) [Score:5] [Agent:easy]

**Required context**

1. [hub/src/lib/components/shell/Button.svelte](../../../hub/src/lib/components/shell/Button.svelte) — visual baseline (`primary` / `secondary` variants, `--hub-*` tokens)
2. [hub/src/lib/tabs/ProjectsTab.svelte](../../../hub/src/lib/tabs/ProjectsTab.svelte) — `.filter-select`, `.newproj-select`, `.intent-select` (native `<select>` usages)
3. [hub/src/lib/tabs/UnityVersionsTab.svelte](../../../hub/src/lib/tabs/UnityVersionsTab.svelte) — `.view-toggle` and `.ctx-menu` patterns (popover vocabulary)
4. [hub-ui.md](../../hub/hub-ui.md) — shared component conventions

- Add `hub/src/lib/components/shell/Select.svelte` (name may vary) — a custom dropdown, not a styled native `<select>`:
  - Trigger button reuses `Button` secondary styles (border, radius, padding, hover, focus-visible, disabled).
  - Panel: popover list styled like existing context menus (`--hub-surface`, `--hub-border`, row hover).
  - Keyboard: ArrowUp/Down, Enter to select, Escape to close; `aria-expanded`, `role="listbox"` / `role="option"`.
  - Props: `options: { value: string; label: string; disabled?: boolean }[]`, `value` / `onchange`, optional `placeholder`, `disabled`, `title`.
- Migrate high-visibility native selects first:
  - Projects toolbar filter (`filter-select`).
  - New project modal: Unity version, render pipeline, Hub template picker (`newproj-select`).
  - Upgrade modal version picker (if it uses `<select>`).
- Remove dead CSS for migrated `.filter-select` / `.newproj-select` rules once unused.
- Do **not** migrate every `<select>` in Settings in this task unless trivial — document any remaining native selects in the task DONE note.

**Acceptance checklist**

- Filter dropdown on the Projects tab looks like a secondary `Button` when closed; open menu matches Hub surface styling (not the macOS native picker sheet).
- New project and upgrade modals use the same component; keyboard navigation works.
- Light and dark themes: trigger and panel remain readable.
- No regression in filter behavior or bound values.
- `svelte-autofixer` / `npm run check` clean for new component.

Dependencies: Plan 3 Task 3 (theme tokens on components); independent of Tasks 1–2.

---

## Dependency graph

```text
Plan 3 Task 4 (releases viewer) ──→ Task 1 (install)
Plan 3 Task 1 (discovery)      ──→ Task 1
Task 1 + Plan 3 Task 4         ──→ Task 2 (full catalog + archived)
Plan 3 Task 3 (theme) + M1 shell ─→ Task 3 (macOS title bar)
Plan 3 Task 3 (theme)          ──→ Task 4 (Select component)
```

Tasks 3 and 4 may run in parallel with Tasks 1–2. Task 2 should not start until Task 1's install command exists.

## Plan 3.5 exit criteria

- [ ] User can install a Unity Editor version from the Hub when Unity Hub is present; discovery refreshes automatically.
- [ ] **All releases** is backed by the Hub CLI with an **Include archived** toggle and graceful fallback.
- [ ] macOS shows a theme-integrated overlay title bar with native traffic-light controls; other platforms unchanged.
- [ ] Shared `Select` component replaces native `<select>` on Projects toolbar and new-project / upgrade modals.
- [ ] `hub/MANUAL_VALIDATION.md` extended with sections for install, archived catalog, title bar (macOS), and select styling.
- [ ] No M2/M4/M5/M6/M7 code paths introduced.

**Next:** [execution-plan-4-platform-niche.md](./execution-plan-4-platform-niche.md) (or ship Plan 3.5 items before Plan 4 niche work)
