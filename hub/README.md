# Unity Agent Hub (`hub/`)

Greenfield desktop scaffold for Hub v1 using Tauri 2 + SvelteKit + Svelte 5.

## Local development

From the `hub/` directory:

```bash
npm install
npm run dev
npm run tauri dev
```

## Stack and pinned versions

Versions match `vibe-launcher` as required by M1 scaffold specs:

- Tauri: `^2` (`tauri`, `tauri-build`, `@tauri-apps/api`, `@tauri-apps/cli`)
- Svelte: `^5.0.0`
- SvelteKit: `^2.9.0`
- Vite: `^6.0.3`
- `@sveltejs/vite-plugin-svelte`: `^5.0.0`
- `@sveltejs/adapter-static`: `^3.0.6`

## Current scope

- Empty main window with branded app metadata (`Unity Agent Hub`).
- Baseline Tauri capabilities for upcoming M1 work:
  - `core:default`
  - `fs:default`
  - `shell:default`
  - `opener:default`
- Split JSON config persistence (`settings.json` + `projects.json`).

## Config directory

All Hub state lives in a platform-specific config directory:

- **macOS:** `~/.config/unity-agent-hub/`
- **Windows:** `%APPDATA%\unity-agent-hub\`
- **Linux:** `~/.config/unity-agent-hub/`

Both JSON files are created with safe defaults on first access. Corrupt or missing files are restored to defaults with a `.json.corrupt` backup of the original.

### Config files

| File | Purpose |
|---|---|
| `settings.json` | App preferences: launch defaults, column visibility, safety toggles, Unity discovery parent folders |
| `projects.json` | Project inventory: paths, ordering, per-project launch args, platform intent, last-known Unity version, launch PID |

### Frontend API

The TypeScript service at `src/lib/services/config.ts` exposes:

- `loadSettings()` / `saveSettings(settings)`
- `loadProjects()` / `saveProjects(projects)`

These call Tauri commands that handle atomic writes (temp file + rename) and corrupt-file recovery.

### Verifying config persistence

1. `npm run tauri dev` to launch the app.
2. Check `~/.config/unity-agent-hub/` (macOS) — both `settings.json` and `projects.json` should exist with default content.
3. Call `saveSettings` with modified data, then reload — changes should persist.
4. Manually truncate `settings.json` and restart — app should recover with defaults and a `.json.corrupt` backup.

## First-run Unity Hub seed import

On first launch (when `projects.json` has no projects yet), the Hub reads the local Unity Hub project registry and imports all projects into `projects.json`. This is a one-time seed — subsequent launches never overwrite user edits.

### Unity Hub data paths (read-only)

| Platform | Path |
|---|---|
| **macOS** | `~/Library/Application Support/UnityHub/` |
| **Windows** | `%APPDATA%\UnityHub\` |
| **Linux** | `~/.config/UnityHub/` |

The seed reads `projects-v1.json` from the Unity Hub data directory. This file has the format:

```json
{
  "schema_version": "v1",
  "data": {
    "<project_path>": {
      "title": "MyProject",
      "lastModified": 1779392390300,
      "path": "/path/to/project",
      "version": "2022.3.62f2",
      ...
    }
  }
}
```

### Seed behavior

- Skipped silently if Unity Hub is not installed or has no projects.
- Imported paths are validated for existence; missing paths are kept with a `skippedPaths` note for the missing-path chip (Plan 2).
- Each imported project gets a new stable UUID `id`.
- Projects are sorted by `lastModified` (most recent first).
- `lastModified` (Unix epoch ms) is converted to ISO 8601 `lastModifiedAt`.

### Frontend API

- `seedFromUnityHub()` — returns `SeedResult { projects, seededCount, skippedPaths, error? }`

## Diagnostics

The Hub writes a per-launch record (one JSON object per line) to:

```
~/.config/unity-agent-hub/logs/launches.log
```

On Windows the same file lives under `%LOCALAPPDATA%\unity-agent-hub\logs\launches.log`.

### Record fields

| Field | Description |
|---|---|
| `timestamp` | RFC 3339 UTC timestamp written when the launch was attempted. |
| `projectId`, `projectName`, `projectPath` | Identifying info for the project that was launched. |
| `unityVersion` | Resolved Unity version (from `ProjectSettings/ProjectVersion.txt` when fresh, else from the stored record). |
| `installPath` | Resolved Unity install path. `null` when the version could not be resolved. |
| `pid` | PID returned by the OS for the spawned process. `null` on failure. |
| `launchArgs` | Argument list handed to the Unity executable. |
| `buildTarget` | `BuildTarget` name appended via `-buildTarget` (only set when `platformIntent` is configured). |
| `outcome` | Tagged enum — `{ result: "ok", pid, unityVersion, executablePath }` on success, `{ result: "error", code, message }` on failure. `code` matches one of the typed `LaunchError` variants: `projectNotFound`, `pathInvalid`, `versionMissing`, `installNotFound`, `launchFailed`. |

### Rotation

The file is rotated on size, not on time, so the on-disk record is always bounded. Policy:

- Default cap: **5 MB**.
- On rotation, the current `launches.log` is renamed to `launches.log.1` and a fresh `launches.log` is started. The previous `launches.log.1` (if any) is discarded before the rename, so Hub keeps at most the current file plus one rotated copy.
- Rotation happens synchronously inside the background writer, never on the launch command path. The writer itself runs on a dedicated `hub-launch-log` OS thread so spawning Unity is never blocked on disk I/O.
- The frontend can ask for a tail at any time via the `get_launch_log_tail` Tauri command (clamped to the last 2 000 lines).

### Settings

A new `diagnostics` block in `settings.json` controls crash-failure UX:

```json
"diagnostics": {
  "autoOpenDrawerOnLaunchFailure": true
}
```

When `true` (the default), a failed Unity launch expands the Status / Log drawer, appends the typed error line, and tails the last 200 lines from the persistent launch log. When `false`, the same data is appended to the drawer log but the drawer is not auto-expanded. Legacy `settings.json` files written before this feature load fine — the default value kicks in via the `serde(default)` attribute.

If the failure is specifically a `launchFailed` (i.e. Unity could not be spawned — typical of a hard crash on launch), the drawer also shows a `Reveal crash logs` quick-action that opens the per-OS crash folder (`~/Library/Logs/DiagnosticReports` on macOS, `%LOCALAPPDATA%\CrashDumps` on Windows).

## Tools tab — log shortcuts

The Tools tab exposes the platform-aware log paths per the hub-ui spec (M1.5-16 extended the M1 set with `Editor-prev.log`, the per-project `Player.log`, and the per-user global `Player.log` for standalone builds):

| Shortcut | macOS | Windows | Linux |
|---|---|---|---|
| Editor logs folder | `~/Library/Logs/Unity` | `%LOCALAPPDATA%\Unity\Editor` | `~/.config/unity3d` |
| `Editor.log` | `<editor logs>/Editor.log` | `<editor logs>/Editor.log` | `<editor logs>/Editor.log` |
| `Editor-prev.log` (M1.5-16) | `<editor logs>/Editor-prev.log` | `<editor logs>/Editor-prev.log` | `<editor logs>/Editor-prev.log` |
| `Player.log` (editor preview, M1.5-16) | `<editor logs>/Player.log` | `<editor logs>/Player.log` | `<editor logs>/Player.log` |
| Player logs folder (per-project) | `<project>/Logs` | `<project>/Logs` | `<project>/Logs` |
| Standalone `Player.log` (M1.5-16) | `~/Library/Logs/Unity/Player.log` | `%LOCALAPPDATA%\Unity\Player.log` | `~/.config/unity3d/Player.log` |
| Crash logs | `~/Library/Logs/DiagnosticReports` | `%LOCALAPPDATA%\CrashDumps` | `~/.config/unity3d` |
| Asset Store downloads | `~/Library/Application Support/Unity/Asset Store-5.x` | `%LOCALAPPDATA%\Unity\Asset Store-5.x` | not resolved yet |

The Asset Store shortcut resolves the newest `Asset Store-5.*` subfolder; if no versioned subfolder exists yet, it falls back to the parent `Unity` folder and shows an inline message so the user knows what to do.

The standalone `Player.log` button is disabled (with the inline message "no standalone player log on disk yet") until a standalone Unity Player build has been run on the machine — the file does not exist on a clean dev box.

## Tools tab — env variables (M1.5-17)

The Tools tab exposes a per-project environment-variables editor
("Environment variables" panel, below the log shortcuts). Each row
is a `KEY=value` pair that is merged into the spawned Unity
process's environment on launch (the child overrides the parent on
key collision).

- Add / remove rows; values are typed in `password` mode and a
  "Show" button toggles reveal (so secrets are not displayed in
  plain text while typing).
- Validation on save: empty keys are rejected, keys with `=` are
  rejected, duplicate keys are rejected.
- Persisted to `projects.json` under a new optional `envVars`
  field (`Record<string, string>`); legacy projects load with an
  empty map.
- Applied to **every launch** — both the GUI launch button and the
  CLI mode read the same per-project `envVars` and layer them on
  top of the inherited parent env.
- Safety toggle: `Settings → Safety → "Confirm env-var overrides
  before launch"` (default on). When the project has any env vars
  and the toggle is on, the Launch button shows a confirmation
  modal listing the keys that would override a parent-process
  variable; the user can Cancel or Save anyway. The collision
  lookup is non-mutating and a backend `#[tauri::command]`
  (`env_var_collisions`) so the modal can be shown without
  spawning Unity.

## Appearance (M1.5-18)

The Hub ships with a three-way theme switch — `Dark` / `Light` /
`System` — exposed in `Settings → Appearance` (M1.5-18). The
choice is persisted to `settings.json` under a new optional
`theme` field (`"dark" | "light" | "system"`, default `"system"`)
and is applied live with no app restart.

- `Dark` and `Light` pin the Hub to that palette regardless of the
  OS setting.
- `System` follows the OS via the `prefers-color-scheme` media
  query; the Hub auto-flips when the user toggles the OS setting
  (macOS System Settings → Appearance; Windows Settings →
  Personalization → Colors).
- The choice is mirrored to `localStorage["hub-theme"]` and the
  `app.html` inline boot script reads it on first paint, so the
  first frame after relaunch is already in the right palette
  (no flash of dark when the user picked `Light`).
- The per-launch log (`~/.config/unity-agent-hub/logs/launches.log`)
  records the active theme on every launch. The frontend resolves
  `system` to a concrete `dark` / `light` value before the spawn
  so the on-disk record is always a concrete palette.

## Unity Versions — All releases sub-section (M1.5-19)

The Unity Versions tab carries a two-way toggle in the toolbar —
`Installed` (the default; the existing discovered-installations
table) and `All releases` (the M1.5-19 additions). The `All
releases` view renders a table of recent Unity release streams
(LTS / TECH / BETA / ALPHA) with:

- `Version` — the version string (`6000.0.32f1`, …).
- `Stream` — chip with the release stream.
- `Released` — ISO `YYYY-MM-DD` date.
- `Notes` — the documented `unity.com/releases/editor/whats-new/<version>` URL.
- `Status` — `installed` chip when the version is present in the
  discovered installations list, `—` otherwise.

Clicking a row opens the release-notes URL in the system
browser. Right-click exposes `Open release notes`, `Copy version`,
and `Use as Upgrade target` (the latter selects a project on the
Projects tab so the user can drop into the upgrade flow).

### Data source and caching

Unity does not publish a public JSON feed of releases that the
Hub can rely on; the spec explicitly forbids scraping arbitrary
sites without a documented stable URL. The chosen documented URL
is the release-notes page, and the Hub ships a static snapshot of
recent LTS / TECH / BETA / ALPHA releases as the "fetched"
payload. The infrastructure (cache, stale badge, debounced
refresh) is fully implemented so the call sites can be swapped to
a real feed when Unity publishes one:

- Cache file: `<config_dir>/cache/releases.json`.
- Default TTL: **1 hour** (the spec's "once per hour per user"
  debounce).
- `fetch_releases` reads the cache; the response is annotated
  with `stale: true` when the file is older than the TTL.
- `refresh_releases` rewrites the cache unconditionally (the
  Refresh button on the toolbar).

Network / cache failures are non-fatal: the snapshot is always
served; `stale: true` is the user's signal to click Refresh.

## CLI mode

Hub ships a small CLI surface for launching Unity from a terminal. The
binary inspects its own argv at startup; if `-projectPath <path>` is
present, the same launch resolver the GUI uses resolves the matching
Unity install and spawns the editor. The Hub window is never opened.

### Usage

```text
unity-agent-hub -projectPath <path>
```

`-projectPath=<path>` and `--projectPath <path>` are accepted as
aliases. From a terminal in the project root:

```bash
# macOS / Linux
unity-agent-hub -projectPath "$PWD"

# Windows (PowerShell)
unity-agent-hub -projectPath "$PWD"

# Windows (cmd)
unity-agent-hub -projectPath "%CD%"
```

### Behavior

- The path must be a Unity project root (`Assets/` + `ProjectSettings/`).
  Invalid paths print `unity-agent-hub: <reason>` to stderr and the
  process exits with status `1`.
- The Unity version is read from `ProjectSettings/ProjectVersion.txt`,
  matched against the same discovery feed the Settings → Unity Versions
  tab uses (parent folders, OS defaults, `$UNITY_HUB`), and the matching
  `Unity` / `Unity.exe` is spawned with `-projectPath <path>`.
- On success a one-line confirmation is written to stdout and the
  matching `projects.json` entry is updated with `lastLaunchPid` /
  `lastLaunchAt` (no frecency bump — the GUI is not consulted, so the
  sort should not be skewed by a terminal script).
- The same flag is registered with `tauri-plugin-cli` so the JS side
  can read matches at runtime; the Schema is also available in
  `tauri.conf.json` under `plugins.cli`.
- The Settings tab → Diagnostics section carries a "Copy CLI help"
  button that puts the same usage block on the clipboard.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Unity was spawned successfully. |
| `1` | The path is missing, not a directory, not a Unity project root, the project's Unity version is not installed, or the spawn failed. A one-line `unity-agent-hub: <reason>` message is written to stderr. |

### macOS .app bundle

When launched from Finder, the macOS bundle does not receive the
`-projectPath` flag in argv. Use Terminal (`open -a "Unity AI Hub" --args -projectPath /path`)
or invoke the binary in the bundle directly
(`/Applications/Unity\ AI\ Hub.app/Contents/MacOS/hub -projectPath /path`).
The CLI mode does not require single-instance protection for v1: a
second Finder launch with the same flag would simply spawn Unity again
and exit.
