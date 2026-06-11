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

The Tools tab exposes the four platform-aware log paths per the hub-ui spec:

| Shortcut | macOS | Windows |
|---|---|---|
| Editor logs | `~/Library/Logs/Unity` | `%LOCALAPPDATA%\Unity\Editor` |
| Player logs | `<project>/Logs` (per-project) | `<project>/Logs` (per-project) |
| Crash logs | `~/Library/Logs/DiagnosticReports` | `%LOCALAPPDATA%\CrashDumps` |
| **Asset Store downloads** | `~/Library/Application Support/Unity/Asset Store-5.x` | `%LOCALAPPDATA%\Unity\Asset Store-5.x` |

The Asset Store shortcut resolves the newest `Asset Store-5.*` subfolder; if no versioned subfolder exists yet, it falls back to the parent `Unity` folder and shows an inline message so the user knows what to do.
