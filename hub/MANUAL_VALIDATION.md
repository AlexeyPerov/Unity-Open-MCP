# Unity Hub Pro — M1 Manual Validation Checklist

**Scope:** [specs/hub/hub-requirements.md](../../specs/hub/hub-requirements.md) §V1 done criteria, executed on Windows + macOS.
**Plan:** [specs/execution/M1/execution-plan-4-settings-validation.md](../../specs/execution/M1/execution-plan-4-settings-validation.md) Task 4.
**Version under test:** `0.1.0` (see [hub/src/lib/version.ts](src/lib/version.ts)).

Use a copy of this checklist per platform. Record pass/fail notes inline. Critical failures must be filed against [execution-plan-4 Task 5](../../specs/execution/M1/execution-plan-4-settings-validation.md#task-5-deferrals-audit-vs-backlog-m1-19) follow-up or fixed before M1 sign-off.

## 0. Automated pre-flight (run on every platform)

| # | Check | Command | Expected | Result |
|---|-------|---------|----------|--------|
| 0.1 | Rust unit tests | `cargo test --manifest-path hub/src-tauri/Cargo.toml` | all tests pass (see the latest `specs/changelog.md` entry for the current count) | ☐ |
| 0.2 | Frontend typecheck | `npm --prefix hub run check` | `0 errors, 0 warnings` | ☐ |
| 0.3 | Frontend build | `npm --prefix hub run build` | clean exit, no errors | ☐ |
| 0.4 | Tauri typecheck | `cargo check --manifest-path hub/src-tauri/Cargo.toml` | clean exit | ☐ |

Notes:

## 1. First-run seed and config creation

> Covers: Plan 1 Task 3 (config dir), Task 4 (Hub seed), M1-hub-launcher §Scaffold & persistence.

| # | Step | Expected | Result |
|---|------|----------|--------|
| 1.1 | Launch Hub with no prior config (delete or rename `~/.config/unity-hub-pro/` on macOS/Linux; `%APPDATA%\unity-hub-pro\` on Windows) | App opens with default Settings; Projects tab shows imported Unity Hub projects (or empty state if Hub not installed) | ☐ |
| 1.2 | Open Settings → Diagnostics → Reveal `settings.json` | OS file manager opens at the right path with both files visible | ☐ |
| 1.3 | Open Settings → Diagnostics → Reveal `projects.json` | OS file manager opens at the right path; if Unity Hub was installed, file contains seeded entries with stable `id` UUIDs and ISO 8601 timestamps | ☐ |
| 1.4 | Quit and relaunch | Projects list is unchanged; no duplicate entries appear | ☐ |
| 1.5 | Corrupt `settings.json` (e.g. write `not-json`) and relaunch | Hub recovers to defaults; the corrupt file is moved aside as `settings.json.corrupt` (verify via Reveal) | ☐ |
| 1.6 | Corrupt `projects.json` similarly | Hub recovers to an empty project list; `projects.json.corrupt` left for inspection | ☐ |

Notes:

## 2. Add / remove project, missing path behavior

> Covers: Plan 2 Tasks 4 + 5, M1-hub-launcher §Projects tab.

| # | Step | Expected | Result |
|---|------|----------|--------|
| 2.1 | Click **Add Project** in toolbar, pick a valid Unity project folder (`Assets/` + `ProjectSettings/` present) | Row appears, shows detected Unity version and an `ok` + `launchable` chip; new entry is selected | ☐ |
| 2.2 | Repeat step 2.1 with the same folder | Inline error `already in list — <path>`; no duplicate row | ☐ |
| 2.3 | Click **Add Project** on a non-Unity folder | Inline error `not a Unity project (<reason>) — <path>`; no row | ☐ |
| 2.4 | Click **Add Project** on a file | Inline error `not a directory — <path>`; no row | ☐ |
| 2.5 | Rename a project folder on disk (e.g. `mv MyGame MyGame.bak`); click **Refresh** | Row gets `missing path` chip; status `pathExists === false`; Launch button disabled | ☐ |
| 2.6 | On a `missing path` row, right-click → **Open folder** | Disabled with title `Path missing — cannot open folder` | ☐ |
| 2.7 | On a `missing path` row, click **Remove from list** (confirmation disabled in Settings → Safety for the test) | Row is removed; `projects.json` no longer contains the entry; folder on disk is intact | ☐ |
| 2.8 | Re-enable *Confirm before removing project from list*, attempt a remove | Confirmation modal appears with project name + path; cancel keeps the row | ☐ |
| 2.9 | Restore the folder name and click **Refresh** | The `missing path` chip clears, `ok` chip returns | ☐ |

Notes:

## 3. Launch — version match, launch args, `-buildTarget`

> Covers: Plan 2 Task 2, Plan 3 Task 2, M1-hub-launcher §Projects / §Tools.

| # | Step | Expected | Result |
|---|------|----------|--------|
| 3.1 | Settings → Launch → pick **Open project scene on launch**; pick a project whose `ProjectVersion.txt` matches an installed Unity | Hub spawns Unity with `-projectPath <path>`; status drawer logs `launched <name> (pid <pid>, <version>)`; row's `lastLaunchPid` is set | ☐ |
| 3.2 | On the same project, switch Settings → Launch to **Open empty editor only**, launch again | Hub spawns Unity *without* `-projectPath`; drawer log reflects the mode | ☐ |
| 3.3 | Tools tab → Launch args → enter `-batchmode -nographics` → Save | `projects.json` shows `launchArgs` persisted; next Launch includes those args (visible in the launched Unity's process arguments via OS activity monitor / `Get-Process` with command-line) | ☐ |
| 3.4 | Tools tab → Platform intent → pick `StandaloneOSX` (or `StandaloneWindows64` on Windows) → Save | `projects.json` shows `platformIntent` persisted; next Launch appends `-buildTarget StandaloneOSX` | ☐ |
| 3.5 | Tools → Launch args → enter `evil\` `; rm -rf /` (unsafe chars) | Inline validation error appears naming the offending char; Save is disabled | ☐ |
| 3.6 | Click **Reset** on Launch args | Input clears, `projects.json` `launchArgs` is removed; next Launch no longer appends custom args | ☐ |
| 3.7 | Try to launch a project whose Unity version is **not** installed | Inline-style error in drawer: `launch failed: Unity <version> is not installed`; `lastLaunchPid` is **not** set | ☐ |
| 3.8 | Try to launch a project whose path is missing (per step 2.5) | Drawer log: `cannot launch: path missing — <path>`; no process spawned | ☐ |
| 3.9 | Restart Hub; if *Remember last selected project on startup* is enabled, the same project is selected | Selection restored from `settings.json`; with the toggle off, no project is selected on first paint | ☐ |

Notes:

## 4. Unity Versions discovery and warnings banner

> Covers: Plan 2 Task 1, Plan 3 Task 1, M1-hub-launcher §Unity Versions tab.

| # | Step | Expected | Result |
|---|------|----------|--------|
| 4.1 | Open **Unity Versions** tab with default discovery parents | Installations table shows Hub-detected installs on macOS (`/Applications/Unity/Hub/Editor/...`) and Windows (`%ProgramFiles%\Unity\Hub\Editor\...`) labelled `Source: Hub`; sorted descending by version | ☐ |
| 4.2 | If a project references a Unity version that is **not** installed, the warnings banner appears with the count and a **Show projects** link | Clicking the link switches to Projects tab and applies the `Missing version` filter | ☐ |
| 4.3 | Settings → Additional parent folders → **Add Folder**, pick a directory that contains a Unity install not in the Hub default | After the rescan, the new install appears with `Source: Manual` | ☐ |
| 4.4 | **Remove Folder** on a previously added entry | The install disappears from the table; the Hub default installs remain | ☐ |
| 4.5 | Set `UNITY_HUB=/path/to/alt/hub` and relaunch | Installations under that path appear with `Source: Env` | ☐ |
| 4.6 | Click **Refresh** with a transient typo in a parent folder (e.g. add `/does/not/exist`) | Hub continues to return the working installs; per-parent error is logged to the drawer | ☐ |
| 4.7 | Select an install row → **Open Install Folder** | OS file manager opens the install's parent directory | ☐ |
| 4.8 | Select an install row → **Release Notes ↗** | System default browser opens `https://unity.com/releases/editor/whats-new/<version-with-dashes>` | ☐ |
| 4.9 | Select an install row → **Run Unity** | Hub spawns the editor without `-projectPath`; drawer logs the resulting PID; row gets a `running` chip | ☐ |

Notes:

## 5. Tools: log shortcuts, Kill Unity (live + stale PID)

> Covers: Plan 3 Tasks 3 + 4, M1-hub-launcher §Tools tab.

| # | Step | Expected | Result |
|---|------|----------|--------|
| 5.1 | With a project selected in the **Tools** tab, click **Editor Logs** / **Player Logs** / **Crash Logs** | OS file manager opens the resolved directory (macOS `~/Library/Logs/Unity`, `~/Library/Logs/DiagnosticReports`; Windows `%LOCALAPPDATA%\Unity\Editor`, `%LOCALAPPDATA%\CrashDumps`; player = `<project>/Logs`) | ☐ |
| 5.2 | Click **Editor.log ↗** with a project selected | Default OS app opens the editor log file; middle-click also opens it | ☐ |
| 5.3 | Buttons are disabled with explanatory title when no project is selected or path missing | Tooltip matches the disabled reason | ☐ |
| 5.4 | From a live Unity (per step 3.1), go to the project's **Kill Unity** button in Projects tab | Confirmation modal appears (if *Confirm before Kill Unity* is enabled); confirm → drawer logs `kill: terminated pid <pid>`; `lastLaunchPid` is cleared from the project row | ☐ |
| 5.5 | Click **Kill Unity** again on the same project (PID was just cleared) | Button is disabled with title `No recorded Unity PID — launch Unity once to enable Kill` | ☐ |
| 5.6 | Relaunch Unity (step 3.1) → wait for the editor to exit on its own → click **Kill Unity** | Drawer logs `kill: pid <pid> is not running (<reason>)`; `lastLaunchPid` is cleared so the next launch re-arms the button | ☐ |
| 5.7 | Re-run the same project (PID re-recorded) → Tools tab → **Kill Unity for project** | Same behaviour as 5.4, exercised from the Tools tab (PID is per project, not per tab) | ☐ |
| 5.8 | Disable *Confirm before Kill Unity* in Settings → Safety | Kill Unity now runs without the modal | ☐ |
| 5.9 | Try to Kill a PID that the OS won't let you touch (e.g. PID 1 / a system-owned PID injected via a unit test) | Drawer logs `kill: access denied for pid <pid> — <reason>`; UI stays responsive | ☐ |

Notes:

## 6. Settings toggles and diagnostics export

> Covers: Plan 4 Tasks 1 + 2, M1-hub-launcher §Settings tab.

| # | Step | Expected | Result |
|---|------|----------|--------|
| 6.1 | Projects tab → toggle off **Show path column** in Settings → Project list | Path column disappears immediately without restart | ☐ |
| 6.2 | Toggle off **Show modified column** | Modified column disappears immediately | ☐ |
| 6.3 | Toggle off **Search path in addition to name**, type a path substring that wouldn't match a name | Search returns no rows; only names match | ☐ |
| 6.4 | Re-enable all three toggles | Columns / search scope come back live | ☐ |
| 6.5 | Settings → Safety → uncheck *Confirm before Kill Unity*; Projects → Kill Unity | No modal; kill proceeds | ☐ |
| 6.6 | Uncheck *Confirm before removing project from list*; remove a project | No modal; row removed | ☐ |
| 6.7 | Settings → Additional parent folders → **Add Folder** with the same path that's already in the list | The entry is **not** duplicated; no discovery rescan is triggered (drawer stays quiet) | ☐ |
| 6.8 | Tweak any toggle and watch the sticky footer | Footer shows `Saving…` → `Saved ✓`; on a forced error (corrupt disk / read-only) footer shows `Save failed` and drawer logs the reason | ☐ |
| 6.9 | Settings → Diagnostics → **Export diagnostics bundle…** | Native save dialog opens with default name `unity-hub-pro-diagnostics-YYYY-MM-DD_HH-MM-SS`; after confirming, the folder is created and contains `settings.json`, `projects.json`, `version.txt`, and (if the drawer has logs) `log.txt` | ☐ |
| 6.10 | Open the exported `version.txt` | Contains app name, version (`0.1.0`), target arch, build profile, ISO 8601 timestamp | ☐ |
| 6.11 | Force an unwritable target (e.g. choose a read-only location) | Inline error appears; drawer logs `export failed: <reason>`; no partial bundle left behind | ☐ |
| 6.12 | Set the version label in the footer | Reads `Unity Hub Pro v0.1.0 · build` (matches [hub/src/lib/version.ts](src/lib/version.ts) and `tauri.conf.json`) | ☐ |

Notes:

## 7. AI Setup reserved slot

> Covers: Plan 4 Task 3, M1-hub-launcher §Validation & deferrals.

| # | Step | Expected | Result |
|---|------|----------|--------|
| 7.1 | Projects tab toolbar shows the `AI Setup` button in a disabled state with title `AI Setup — coming in a later milestone (reserved slot)` | Visible, disabled, no click action | ☐ |
| 7.2 | Click the disabled `AI Setup` button | Nothing happens (the stub `handleAiSetupStub` may log a hint line to the drawer, but no navigation occurs) | ☐ |
| 7.3 | `grep -ri "wizard\|mcp\|bridge" hub/src` shows **no** wizard/MCP/bridge detect logic; only the AI Setup stub in Projects toolbar | Matches the M1 deferral rule | ☐ |

Notes:

## 8. Deferrals audit

> Covers: Plan 4 Task 5, hub-requirements §Explicitly not required for v1, hub/backlog.md.

| # | Check | Expected | Result |
|---|-------|----------|--------|
| 8.1 | `drag-and-drop add project` is implemented in the Hub UI (M1.5-7) | Drop a valid Unity project folder on the Projects tab — it is added with a new uuid | ☐ |
| 8.2 | `relink missing path` action is available for missing-path rows (M1.5-8) | Right-click a row with the `missing path` chip — a `Relink…` action appears in the context menu and the popup's More menu | ☐ |
| 8.3 | No arbitrary Unity.exe path parser — discovery is limited to configured parent folders | Confirmed via the Unity Versions tab and `discovery.rs` | ☐ |
| 8.4 | No Linux-only code paths exposed; Linux is allowed to render but the manual test matrix is Windows + macOS only | Confirmed by [hub-requirements.md](../../specs/hub/hub-requirements.md) §V1 done criteria + [backlog.md](../../specs/hub/backlog.md) | ☐ |
| 8.5 | No `template-based new project creation` flow | Confirmed by the Add Project button opening a folder picker only | ☐ |
| 8.6 | No advanced ecosystem adapters (UCP, unity-cli, Unity official MCP) wired into the Hub UI | Confirmed by `grep -ri` searches in the source | ☐ |
| 8.7 | `specs/hub/backlog.md` is consistent with the deferrals in [hub-requirements.md](../../specs/hub/hub-requirements.md) §Explicitly not required for v1 | Each deferred item appears in backlog with a priority bucket | ☐ |

Notes:

## 9. Repeat-launch / data-loss smoke

> Covers: hub-requirements §Non-functional requirements, §V1 done criteria #4.

| # | Step | Expected | Result |
|---|------|----------|--------|
| 9.1 | Run steps 1.1, 2.1, 3.1, 4.1 in sequence, then close and relaunch the app | All state preserved; no duplicates; no `*.corrupt` files left behind | ☐ |
| 9.2 | With Hub running, externally edit `projects.json` to add a new entry; click **Refresh** | New entry appears; external edit is not overwritten or lost | ☐ |
| 9.3 | Trigger a discovery rescan mid-launch (start a launch, then change discovery folders) | Both operations complete without corrupting `settings.json` or `projects.json`; the launched Unity process is unaffected | ☐ |
| 9.4 | Hard-quit the app during a settings save (kill the process while footer shows `Saving…`) | Next launch reads either the previous-good file or the new file (atomic write), never a half-written one; check the file's trailing bytes are valid JSON | ☐ |
| 9.5 | Review the status drawer for the whole session | No unexpected stack traces; all errors have actionable text and a path/link where applicable | ☐ |

### 9a. Double-launch guard (M1.5-10 follow-up)

> Covers: backend `LaunchError::AlreadyRunning` + frontend pre-check +
> status drawer "Terminate & relaunch" action.

| # | Step | Expected | Result |
|---|------|----------|--------|
| 9a.1 | Launch a project (per step 3.1) and wait for the `running` chip to appear | Row shows `running` chip; `lastLaunchPid` is set in `projects.json` | ☐ |
| 9a.2 | With that Unity still running, click the same row again (Launch) | No second Unity is spawned; status drawer shows `launch refused: Unity is already running for "<name>" (pid <pid>). Terminate it first, or click "Terminate & relaunch" in the status drawer.`; the failure banner shows a **Terminate & relaunch** action button | ☐ |
| 9a.3 | In the settings popup for the same running project | The **Launch** button is disabled, label reads `Running`, tooltip `Unity is already running for this project — terminate it first` | ☐ |
| 9a.4 | Open the status drawer, click **Terminate & relaunch** | Drawer logs `terminating pid <pid> for <name>…`; on success logs `terminated pid <pid> — <msg>`; the original Unity exits (verify in OS); a new Unity launches for the project; the `running` chip stays on the row | ☐ |
| 9a.5 | Repeat 9a.2, then in the settings popup **More** menu, click **Terminate Unity** (the existing kill flow), wait for Unity to exit, then click the row's Launch | No "already running" error; the spawn proceeds normally | ☐ |
| 9a.6 | Externally launch Unity on a different project via Unity Hub (or `open -a Unity --args -projectPath /p`), wait for it to register, then click that row in Hub | Frontend pre-check surfaces the same `launch refused` error; backend also returns `AlreadyRunning` if the snapshot is stale (verify by reloading the Hub and immediately clicking the row) | ☐ |
| 9a.7 | Kill -9 the running Unity process from a terminal (no `child.wait()` reap), then click the row in Hub | After the next running-Unity poll (≤ `scanIntervalSeconds`, default 5s) the `running` chip clears and Launch works again; the Hub's `lastLaunchPid` is still the dead PID, but the backend `AlreadyRunning` matcher is path-based first so the launch is allowed once the scan catches up | ☐ |

### 9b. Confirmation modal — Escape + focus (regression of "stuck popup")

> Covers: the bug where the env-var override confirm (and any other
> `S.confirm` consumer) had no Escape handler and no focus management,
> so a user who clicked a table row could not dismiss the modal with
> the keyboard.

| # | Step | Expected | Result |
|---|------|----------|--------|
| 9b.1 | Set a project env var that collides with a parent process var (`Settings → Safety → Confirm before env-var override` must be on). Click Launch on that project | The "Override environment variables?" modal opens | ☐ |
| 9b.2 | Without clicking inside the modal, press **Escape** | Modal closes; no Unity is spawned; drawer log shows no error | ☐ |
| 9b.3 | Reopen the modal (Launch again). Click outside the modal (on the overlay) | Modal closes; no Unity is spawned | ☐ |
| 9b.4 | Reopen the modal. Press **Tab** | Focus cycles through the close (X), Cancel, Confirm controls (focus was moved into the modal on open) | ☐ |
| 9b.5 | Reopen the modal. Press **Enter** while focus is on the Confirm button | Modal closes; Unity spawns as expected (env-var override accepted) | ☐ |
| 9b.6 | Trigger the same flow on the Platform intent save (M1.5-5: edit `platformIntent` while Unity is running for the project) — same `S.confirm` consumer, same modal | All of 9b.2–9b.5 pass for that prompt as well | ☐ |

Notes:

## 10. Sign-off

| Platform | Date | Tester | Critical issues filed | Result |
|----------|------|--------|------------------------|--------|
| macOS    |      |        |                        | ☐ pass / ☐ fail |
| Windows  |      |        |                        | ☐ pass / ☐ fail |

Critical issues found (block M1 sign-off):

- [ ]
- [ ]

Follow-ups filed against [execution-plan-4 Task 5](../../specs/execution/M1/execution-plan-4-settings-validation.md#task-5-deferrals-audit-vs-backlog-m1-19):

- [ ]
- [ ]

M1 sign-off recorded in [specs/changelog.md](../../specs/changelog.md) on the day all critical issues above are resolved.
