# Compile and Safe Mode

## Compile and reload evidence

`read_compile_errors` prefers a bounded live CompilationPipeline snapshot.
`currently_compiling` means wait; `assembly_stale` means source changed after
the last completed compile; `compile_failed` reports current diagnostics
(`errorSource: "Editor.log"` when the failing compile ran before the bridge
loaded, e.g. at Editor startup). A confirmed `no_errors_found` with
`sourceMatches:true` outranks `historicalLogErrors`. Read `determinacy` first:
`current` is backed by the live Editor (a compile generation, or — while it
compiles, reloads or is busy — its instance lock, `compileSource:
"instance_lock"`); `log_evidence` and `indeterminate` come from `Editor.log`
alone, read only when no live Editor answers. An offline clean read is
`indeterminate`, never "clean": confirm with the live Editor or `compile_check`.
Log errors superseded by a later successful build, older than the newest built
assembly, or citing a file/line that no longer exists are `historicalLogErrors`
(`historicalReason`) — never fix them.

**Recompiling.** After writing, adding or deleting C# on disk, activate
`typed-editor` and call `recompile_scripts {}` (`paths_hint` is optional). It
imports scripts Unity has not seen yet (`mutation.output.scriptImport`),
compiles, waits through the domain reload, and answers with the compile:
`status: "compiled"` + `compile.generation`, or `status: "compile_failed"` —
a failed call (`isError`, `mutation.error.code: "compile_failed"`) carrying the
error count and the first errors; fix them and call it again. Past ~40 s it
answers `status: "compiling"` with a `job_id`: `jobs {"action":"wait"}` on it.
`assets_refresh` answers the same way when it started a compile.
`capabilities` and `bridge_status` accept `project_path` for a different project
(also via `manage_tools` invoke); omit `_meta.port` on that call.

`editor_status` always answers: during a compile, reload or play-mode change it reports from the instance lock (`source: "lock"`, `reloading: true`, `transition`, `compileGeneration`). To wait, use `editor_status {"wait_until":"idle"}` — it holds until a requested compile has started and finished (the bridge publishes `compile.pending`; pass `after_generation` from a call's `compile.generationAtStart`) and returns `compile: {generation, status, errorCount}`, the same count `read_compile_errors` gives. A call sent mid-reload waits up to ~30 s on its own; past that it answers `editor_reloading` with `retryAfterMs` and `dispatched` (`false`: nothing ran, retry after the wait; `true` / `null`: a reload cut its response — verify a mutation's effect first). Do not start another Unity.
Disk-backed reads (`list_assets`, `read_asset`, `search_assets`,
`find_references`, `dependencies`, `read_compile_errors`) keep answering during
a compile or reload.
`compile_check` is headless-only. It captures its own log and classifies compiler,
project-load, and spawn failures. `compile_indeterminate` means exit 0 lacked
sufficient completion evidence; never infer success from exit 0 alone. A
`batch_in_progress` response means wait for the current child. On response
correlation/JSON errors, inspect post-state before repeating a mutation.

## Unity state triage (before edits/tests, and on `bridge_offline`)

The single most common agent mistake is misclassifying Unity's state. Follow this **in order** before running tests, before launching Unity, and whenever a tool returns `bridge_offline` or `bridge_compile_failed`.

> **Editing C#?** An offline bridge after a C# edit is frequently a *symptom* of a failed compile, not "Unity isn't running." Skip to [Compile failure recovery](#compile-failure-recovery) and call `read_compile_errors` — it reads `Editor.log` offline and survives a dead bridge.

### Step 1 — Is Unity running at all?

**Preferred — let the server tell you.** Call `unity_open_mcp_bridge_status` (or any live tool / `ping`). It reports `dead_bridge` (with an `evidence` block and a `recoveryHint` → `read_compile_errors`) only when `Editor.log` shows the bridge cannot come back: Unity in **Safe Mode**, a domain reload that **finished without the bridge**, or — no instance lock yet — the **startup compile's errors** (cold Safe Mode). Live tools and `ping` return `bridge_compile_failed` with the same `evidence` then. If you see either, **go directly to Step 4** — no manual `ps` needed. `unreachable` with `heartbeatStalledMs` (or `editor_reloading`) is a reload that runs long or an Editor still starting: wait with `editor_status {"wait_until":"idle"}`, do not hunt compile errors.

**Manual fallback** (Linux, or Unity opened without `-projectPath` — both are known blind spots of the auto-scan):

```bash
cat ~/.unity-open-mcp/instances/*.json 2>/dev/null
ps aux | grep -i "Unity.app/Contents/MacOS/Unity" | grep -v grep     # macOS
# Windows (PowerShell): Get-Process Unity -ErrorAction SilentlyContinue
```

- **No instance files AND no Unity process** → Unity isn't running. Open the project.
- **No instance files BUT a Unity process exists** → booting or in **Safe Mode** (bridge never started). Go to Step 4.
- **Instance files exist** → go to Step 2.

### Step 2 — Read the instance lock, classify state

The lock at `~/.unity-open-mcp/instances/<sha256(projectPath)>.json` (lowercase hex sha256, forward slashes, no trailing slash) carries `pid`, `port`, `state`, `heartbeatAt` (refreshed every 0.5s), `isCompiling`, `unityVersion`. Check `pid` liveness with `kill -0 <pid>` (exit 0 = alive) and heartbeat freshness (<10s = fresh):


| State                                                                 | Meaning                                             | Action                                                                                      |
| --------------------------------------------------------------------- | --------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| Lock missing / `pid` dead                                             | Unity not running                                   | Open Unity or use batch fallback                                                            |
| `pid` alive, heartbeat fresh, `state: idle`                           | Healthy                                             | **Proceed**                                                                                 |
| `pid` alive, `state: compiling` / `isCompiling: true` / `compile.pending: true` | Unity compiling (or about to)                       | **Wait** with `editor_status {"wait_until":"idle"}` (or CLI `wait-for-ready`). Don't edit/test |
| `pid` alive, `state: reloading` / `entering_playmode`, heartbeat **stale** (>10s) | A reload that runs long (large project, play-mode reload, a reload after a test run) — **or**, if `Editor.log` shows Safe Mode / a reload that finished without the bridge, a dead bridge | Wait with `editor_status {"wait_until":"idle"}`; the server reports `bridge_compile_failed` with `evidence` only in the second case → Step 4 |
| `pid` alive, heartbeat fresh, but every tool returns `bridge_offline` | **Port mismatch**                                   | Go to Step 3                                                                                |


### Step 3 — Port mismatch (`UNITY_OPEN_MCP_BRIDGE_PORT` trap)

The lock's `port` is authoritative. An MCP-client config pinned `UNITY_OPEN_MCP_BRIDGE_PORT` to a stale value (the env var always wins on both sides). The agent cannot edit the MCP client config at runtime, so **tell the user** the bridge's actual port and that the `UNITY_OPEN_MCP_BRIDGE_PORT` value in their MCP client config (e.g. `.mcp.json` for a Claude Code project, `.cursor/mcp.json`, `.zcode/config.json`) is wrong — removing it lets both sides derive the per-project hash and agree.

### Step 4 — Safe Mode / `bridge_compile_failed` recovery

A C# edit broke the bridge assembly (or a dependency); Unity is stuck mid-reload showing the "Enter Safe Mode?" dialog.

1. Call `**unity_open_mcp_read_compile_errors`** — reads `Editor.log` tail offline, returns structured CSxxxx errors (`file`/`line`/`code`). The **only** diagnostic that survives a dead bridge. (`compile_check` does **not** work here — its batch entry point lives in the same broken assembly, and the per-project lock blocks a second instance.)
2. Read `errors[].file` / `line` / `code` and **fix the CS error in source first.** Do not retry tests, relaunch Unity, or call `compile_check`.
3. Trigger a recompile (see [Local package source recompile caveat](#local-package-source-recompile-caveat) if you develop against `packages/` source; otherwise a normal recompile from Unity is enough); the bridge reloads itself once the assembly compiles. The MCP server auto-dismisses the Safe Mode dialog under the default `UNITY_OPEN_MCP_DIALOG_POLICY=ignore`; set `manual` (or `UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1`) to opt out entirely. Full policy matrix: [Dialog policy](https://github.com/AlexeyPerov/Unity-Open-MCP/blob/main/docs/dialog-policy.md).
4. Only when `read_compile_errors` reports no errors AND the lock shows fresh heartbeat + `state: idle` is the bridge back.

### Step 5 — Never conclude "Unity not running" while a process is alive

Safe Mode still owns the per-project lock and may show a window, but the bridge is dead — looks identical to "not running" from the MCP server. Always confirm with `kill -0 <pid>`; if alive, you are in Step 4.

## Compile failure recovery

Compile failures surface as **machine-readable error codes** — branch on the code, not free-text. The four you will hit:

- `**bridge_compile_failed`** — the bridge cannot come back on its own, and `evidence` says why (`safe_mode`, `bridge_not_loaded`: the last reload finished without it, `startup_compile_errors`). **Recovery:** `read_compile_errors` (reads `Editor.log` offline; survives the broken bridge) → fix the CS error → trigger a recompile. Never reported for a frozen heartbeat alone.
- **`editor_reloading`** / **`response_lost`** — retryable: a compile, reload or play-mode change (possibly one that runs past the 10 s heartbeat threshold — `heartbeatStalledMs`) or a busy Editor swallowed the call or its answer. Each live call waits at most its budget (~30 s) for the Editor, then answers. Wait with `editor_status {"wait_until":"idle"}`, then: `dispatched: false` → send it again; `dispatched: true` / `null` → re-send a read, but check a mutation's effect first.
- **`reloadRequested: true`** (not an error) — your snippet called `EditorUtility.RequestScriptReload()`; its result is complete and the reload runs right after the answer (`reloadTiming: "after_play_mode"`: when play mode ends). Wait with `editor_status {"wait_until":"idle"}` before the next call.
- **`bridge_offline`** — Unity may not be running, or not yet recompiled. If Unity **is** open it has already written the latest CSxxxx diagnostics to `Editor.log`; `read_compile_errors` retrieves them.
- **`main_thread_blocked`** — the Unity main thread did not pick up the dispatch within the timeout. A Unity modal dialog (unsaved changes, scene modified externally, safe mode) or a long editor operation is almost certainly blocking it. The dispatch **never started**, so do NOT raise `timeout_ms`. **Recovery:** if a scene is dirty call `scene_save` then retry; check `editor_status` / `bridge_status`; the dismiss loop (`UNITY_OPEN_MCP_DIALOG_POLICY`) handles safe-mode / scene-modified-externally modals automatically, and `UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS=1` opts in to the destructive unsaved-changes modal; if unreachable, the editor must be restarted. Distinct from a plain timeout (the work started but ran long — that one CAN warrant a higher `timeout_ms`).
- **`execute_menu "Assets/Refresh"`** is the menu form of `assets_refresh`: a gated mutation (scope defaults to `Assets`) whose result carries the `compile` block — wait the same way.
- **`open_scene_changed_on_disk`** — a refresh (`assets_refresh`, `execute_menu "Assets/Refresh"`, the refresh `recompile_scripts` runs for a deleted script, an `execute_csharp` snippet that calls `AssetDatabase.Refresh` / `ImportAsset` — run `assets_refresh` first) was refused before it ran: an open scene changed on disk (a `git checkout`, a generator) and has unsaved edits, its file is gone, play mode is on, or a prefab stage with unsaved changes has its prefab changed. Refreshing would raise Unity's native "changed on disk" dialog and wedge the Editor (`bridge_status` `wedged` / `main_thread_wedged`) until a human clicks it. **Recovery:** pick from `agentNextSteps` — `scene_save` keeps the in-memory edits, `scene_open` + `ignore_scene_dirty: true` takes the disk version; stop play mode; `prefab_save` / `prefab_close {"save":false}` — then retry. A clean changed scene is not refused: it is reloaded in place and listed in `reloadedScenes`. After changing an open scene's file on disk, run `assets_refresh` instead of leaving it to Unity's own refresh (Auto Refresh on focus), which still asks in the dialog.
- **`timeout` on `execute_menu`** — the menu may still be **running**; a timeout here is a wait that elapsed, not a failure (`Assets/Refresh` after an importer version bump reimports the whole content tree and routinely outlasts the default 30s). `execute_menu` accepts `timeout_ms` (same semantics + ~55s host-safe cap as `execute_csharp`) — raise it rather than retrying. Confirm the outcome with `editor_status` / an asset probe: a blind retry of an authoring menu can **double-write** assets.
- **A tool failing in a way this file says is fixed** — check `bridge_status` → `wireContract` before concluding it regressed. `stale: true` means the installed Unity bridge package predates this MCP server's contract (reinstall/update it and retry); `stale: false` means it really is a regression worth reporting. `bridgeVersion` alone cannot tell you: the package semver does not move for a wire-contract fix, so a stale install and a current one report the same string.
- **`status: "wedged"`** (`bridge_status`) — the Editor process, heartbeat and `/ping` all look fine but the Editor cannot do the work. `wedged.reason = "editor_fd_exhaustion"`: the build driver is dead, `Library/ScriptAssemblies` has stopped updating, C# edits never take effect — only a restart recovers (`execute_csharp` fails with `editor_build_wedged` in this state rather than returning stale output). `wedged.reason = "main_thread_wedged"`: a modal dialog is blocking Unity's message pump — the heartbeat is stale but `/ping` still answers, which a failed bridge assembly could never do, so this is **not** Safe Mode and there is no compile error to chase. No tool can dismiss a modal; an operator must close it in the Unity UI. Prefer non-interactive APIs over `execute_menu`/`ExecuteMenuItem` for converter-style menus.
- **`editor_instance_locked`** — a batch/`compile_check` spawn cannot run while an Editor holds the project lock. The error names the tool you actually called and its message + `agentNextSteps[]` split the diagnosis three ways: a **fresh instance lock** (the live bridge should be reachable — retry the live route / check `bridge_status`), a **live process with the listener not up yet** (a booting Editor or Safe Mode — wait and retry; the call takes the live route once the listener binds), or **no matching process** (a stale `Temp/UnityLockfile` from a crashed session). Do not read this code as "close the Editor" until you have read which variant it reports.
- **`package_install_failed`** (an `issues[]` kind of `read_compile_errors`) — the Package Manager could not install a package (`reason`: often `ENOSPC` on a full disk, or a network error). The CS0234/CS0246 errors in packages that depend on it (e.g. Collections missing `Unity.Burst`) are a consequence — do not edit package code. **Recovery:** follow the issue's `hint` (free disk space / fix the network), then reopen the project or run `compile_check` headless so UPM re-resolves.
- **`unity_spawn_refused`** — the Unity binary could not be executed (spawn `ENOENT`/`EACCES`, or exit code `127`). Do **not** retry `compile_check` blindly. Verify `UNITY_PATH`, then fall back to `read_compile_errors` / `bridge_status` to check compile state without headless spawn.
- **`_compileVerify: { code: "compile_noop" | "dll_stale" }`** — a recompile *reported success* but the compiled state did not advance. Surfaced as an additive annotation on a **successful** result (not an error), so check for it after any `compile-reload` tool except `recompile_scripts` (whose answer is the compile result itself). `compile_noop` = tool registry count + DLL mtime unchanged (incremental no-op); `dll_stale` = `Library/ScriptAssemblies/*.dll` older than your source edit. Both mean: do **not** trust the success — force a rebuild (no-op `package_add`/`package_remove`, or operator refocus of the Editor), then verify DLL mtime > edit mtime before tests.
- **`compilePending: true`** (additive, on the gate envelope next to `settleMs`) — the editor was **still compiling** when the gate validated after the post-mutation settle wait. The gate's `passed` / `delta.newErrors:0` reflects the **PRE-compile** state, not the new code, so a clean-looking gate does **not** mean the new code is verified healthy. `agentNextSteps[]` carries the same advisory. Wait with `editor_status {"wait_until":"idle"}` (it reports the compile status and error count), then `read_compile_errors` for diagnostics. (Root cause: `RequestScriptCompilation` can no-op silently — the compile may be queued but not yet started, or the settle cap elapsed mid-compile.)
- **`_route.fallbackReason: "editor_busy"`** (with `_route.editorPid`) — a tool fell back to the batch route because the live bridge was unreachable, and the batch reported `editor_instance_locked` with a **live** editor PID. The actionable truth is "the editor is busy (a TestRunner run or long import is holding the main thread); retry shortly", **not** a lock conflict. Retry the call after a short wait; if it persists, check `editor_status` / `bridge_status`. Contrast `fallbackReason: "live_unavailable"` (no live PID, or a genuine lock conflict / offline bridge), which is not transient.
- **`status: "triggered_reload"`** (not an error) — a compile-reload tool (`reimport_package`, `asmdef_create`/`asmdef_modify`, `package_add`/`package_remove`, `execute_csharp`, …) ran, and the domain reload it triggered closed the response (`recompile_scripts` and `assets_refresh` wait past it themselves and answer with the compile; `triggeredReload: true` stays on that answer). The mutation almost certainly committed; do **not** retry blindly (that would re-run the mutation). Wait with the `editor_status` call its `agentNextSteps[]` names (keyed to `compile.generationAtStart`), which returns once the compile and reload are done with the compile status and error count.

For `bridge_compile_failed` / `bridge_offline`: call `**unity_open_mcp_read_compile_errors`** → fix `errors[].file`/`line`/`code` → trigger recompile. (If no Editor is open, `Editor.log` is stale from the previous session and won't reflect your latest edits — recompile verification needs Unity running.)

**Stale-log guard.** When the response carries `staleLogSuspected: true`, the cited source files were edited more recently than `Editor.log` — the error block may reference on-disk code you have **already fixed** (Unity's incremental compiler no-op'd the recompile of the broken assembly, so the log's most-recent error block never got rewritten). Before chasing the errors, call `**unity_open_mcp_recompile_scripts`** (in the `typed-editor` group — activate it first) and re-read; with the Editor closed or unreachable, `compile_check` spawns a fresh headless recompile instead. The response also carries `staleLogHint` + `staleLogNewerFiles[]` (the offending files).

**Errors-predate-edits guard.** `staleLogSuspected` compares against `Editor.log`'s **file** mtime, which unrelated asset imports keep refreshing — so the file can look fresh while the error BLOCK inside it is old. The response therefore also carries `errorsMayPredateEdits: true` + `errorsMayPredateEditsFiles[]` + a headline caveat when a file the errors **cite** is newer than the newest `Library/ScriptAssemblies/*.dll`: no compile has completed since you edited it, so the block is necessarily from an earlier compile. Do not re-read or re-fix that code — force a recompile and read again.

**`status: "stale_log"` downgrade.** When the log is stale (`staleLogSuspected`) **or** authored by a different Unity (`logAuthorshipMismatch`), the top-level `status` is downgraded from `compile_failed` to `"stale_log"` and `unhealthy` is suppressed — the `errors[]` are still attached as evidence, but the headline makes clear they **may not apply** to the running editor and must be confirmed by a genuine recompile before acting on them. The raw log verdict is preserved in `logVerdict`. Branch on `unhealthy`/`status === "stale_log"` rather than `errorCount > 0` so you do not "fix" a burst of phantom errors that were already resolved on disk.

**Stale-assembly guard.** The response carries `staleAssembly: true` when at least one `Assets/**/*.cs` source is newer than the newest `Library/ScriptAssemblies/*.dll` — the running assembly predates the latest source (Unity's incremental compiler no-op'd a recompile), so a clean log says nothing about the current code until the assembly is rebuilt. This runs even on a clean log, and when it coincides with errors the **headline itself** carries the may-predate caveat (the error block can come from an earlier compile — do not act on it as fact). Recovery: call `**unity_open_mcp_recompile_scripts`** — it imports new scripts, recompiles deterministically and answers with the compile result (status, generation, error count, first errors). Prefer it over `assets_refresh` / `execute_csharp` when you need a guaranteed recompile of edited C#.

**`status: "indeterminate"`.** An offline read with no current errors (including one from the ROTATED `Editor-prev.log`, `logSource: prev_log_*`), or a live Editor that did not report a determinate compile state, is `indeterminate`, **not** `no_errors_found` — nothing backs it with a compile generation. Follow its `agentNextSteps` (the `editor_status` wait with a live Editor; open the project or `compile_check` without one); the raw log verdict is preserved in `logVerdict`.

**Stale-domain guard (`_staleDomain`).** `execute_csharp` / `invoke_method` responses carry `_staleDomain: { hint, newerSources, dllMtimeMs }` (additive, on a successful non-error body) when an `Assets/**/*.cs` source is newer than the newest built DLL — the snippet ran against the **pre-edit** assembly, so its results reflect code that has not been loaded. This is the runtime-facing twin of the `staleAssembly` read guard: same mtime check, attached to the two tools where a stale domain silently produces wrong answers. The signal is promoted to a top-level `staleAssembly: true` + `warning` on the same body so it cannot be skimmed past next to a `mutation.success: true`. Treat any such result as suspect: call `**unity_open_mcp_recompile_scripts`**, then re-invoke. The signal is cached short-TTL and refreshed on dead-bridge/compile-settle, so it tracks a genuine reload without re-scanning on every call.

**`editor_build_wedged`.** When the assembly is stale **and** the freshest `Editor.log` carries the `editor_fd_exhaustion` signature, `execute_csharp` / `invoke_method` **fail** with `error.code = "editor_build_wedged"` instead of returning a successful-looking result. The build driver is dead: your edits are on disk but were never compiled, and the snippet ran against the previous assembly. A recompile will **not** clear this — save scene work and restart the Editor, then discard any conclusion drawn from results returned since it wedged.

**Log-authorship guard.** The response carries `logUnityVersion`, `logBatchMode`, and `liveUnityVersion` (from the instance lock) when the log header was parseable. On `logAuthorshipMismatch: true`, a **different Unity** (often a `-batchmode` run of a newer version) wrote the log — its errors (e.g. an API deprecation that is only an error on that version) **cannot all occur** in the live editor. Do **not** "fix" those files or refuse to continue on a `compile_failed` from a mismatched log; the inverse of the stale-log trap is just as dangerous. Confirm live health via `bridge_status` (`unityVersion`) instead.

## Editor fd-exhaustion recovery (Bee build-driver hang)

Long Editor sessions with many domain reloads leak file-descriptor numbers until one crosses Mono's internal ~1024 ceiling, and Mono's IOSelector refuses to register it — `System.NotSupportedException: Could not register to wait for file descriptor N` thrown from the Bee build driver. The Editor **hangs mid-build and never recovers on its own**; there is no C# error to fix. The bridge dies with it. Three always-visible local tools cover this failure mode:

- **Diagnose (reactive):** `unity_open_mcp_read_compile_errors` surfaces an `editor_fd_exhaustion` issue kind (with restart hint) from the same `Editor.log` tail that carries CSxxxx errors.
- **Health (reactive):** `unity_open_mcp_bridge_status` folds the same log scan in and reports `status: "wedged"` with `wedged.reason = "editor_fd_exhaustion"` + a non-null `recoveryHint`. A `running` / `healthy` result means *the listener answers*, **not** *the Editor can compile* — process, heartbeat and `/ping` all survive a dead build driver, which is why the scan exists.
- **Kill (reactive):** `unity_open_mcp_restart_editor` with `confirm: true` terminates the hung Unity (SIGTERM → SIGKILL on macOS/Linux; `taskkill /T /F` on Windows). It refuses when the `editor_fd_exhaustion` signature is absent — never restart on a fixable compile failure. When the resolved `Editor.log` does not carry the signature **but the bridge is still reachable**, it falls back to scanning the live Unity console (`unity_senses_read_console`) — the Bee driver exception is raised into the console independently of whichever log file the resolver picked; the response reports `signatureSource: "editor_log"` or `"live_console"` so you can tell which confirmed it. **Relaunch is NOT automatic** (the Hub owns the interactive-Editor launch recipe); the response tells the operator to relaunch via the Hub, then poll `bridge_status` until `running`.
- **Predict (proactive):** `unity_open_mcp_resource_pressure` samples the live Unity process's fd usage and reports headroom against the Mono fd ceiling (default ~1024, NOT the OS soft limit, which is misleading for a GUI-launched Unity on macOS). Returns `state` (`ok` / `warn` at ≥80% / `critical` at ≥90% / `over_ceiling` / `unknown`), a `trend` (`stable`/`rising`/`leaking` — a monotonic climb across successive samples is the leak signature), `ceiling` + `ceilingSource` (`"default"` vs `"config"`), and the session-scoped sample ring. Sample after heavy automation to catch fd growth before the Editor hangs. **`over_ceiling`:** with a real fd count (`fdMethod` lsof/proc — only numbered descriptors are counted, never mmap/txt rows) this IS an alarm: at/past the ceiling every NEW descriptor number lands above it and the next Mono IOSelector registration can hang the Editor, so the response carries a critical-level `warning` (a fresh healthy Unity 6 editor sits at ~160 fds). The one soft case is a Windows `HandleCount` probe (handles are far broader than fds — an over-ceiling handle count carries an informational `pressureNote`, and only a `leaking` trend alarms there); a timed-out `lsof` (`partial: true`) is *not* soft — its count is a lower bound on real fds, so the normal thresholds apply. `execute_csharp` responses also carry an in-band fd-pressure advisory in `agentNextSteps` once the Editor passes 80% of the ceiling (same configured ceiling, so the two surfaces never contradict). Override the ceiling for a runtime whose internal limit differs from Mono's 1024 (e.g. Unity 6 / CoreCLR) via `.unity-open-mcp/settings.json` (`resourcePressure.fdCeiling`).

**Workflow.** After heavy automation, call `resource_pressure`. When the response carries a `warning` (`state` warn/critical, a real-fd `over_ceiling`, or a `leaking` trend), surface the risk to the operator (recommend saving scene work + planning a restart) — do NOT call `restart_editor` yet (the Editor is still healthy). A domain reload (e.g. `recompile_scripts`) releases leaked descriptors, so a recompile is a valid pressure-relief step while work continues. Only a Windows HandleCount over the proxy ceiling is soft (a `pressureNote`) — treat it as a concern when the trend becomes `leaking`. Once `read_compile_errors` reports an actual `editor_fd_exhaustion` issue (the Editor is hung), call `restart_editor` with `confirm: true`, then tell the operator to relaunch via the Hub. Both tools act on the OS process — neither depends on the bridge being reachable. `headless_run_in_progress` from either tool (or a `headlessRun` block on a `stopped` `bridge_status`) means the only Unity for the project is this server's own headless run: wait for that pending call to return (it exits by itself when done or at `timeoutMs`), and do not relaunch Unity via the Hub meanwhile.

Use `**unity_open_mcp_compile_check**` only for a deliberate "does this build clean from scratch?" check — it spawns a fresh headless Unity and **always** routes to batch. It is not first-line recovery for a broken bridge assembly. When a live Editor already has the project open, the headless spawn cannot acquire Unity's one-Editor-per-project lock and returns `editor_instance_locked` — either close the live Editor and retry, or verify compile state via the live bridge instead (`execute_csharp` + `Library/ScriptAssemblies/*.dll` mtime check, or `read_compile_errors`).

## Batch fallback / Unity discovery

`compile_check` is always batch. Other batch-capable tools fall back to batch only when the live bridge is down. The MCP server auto-discovers Unity from OS-default Hub install paths (macOS `/Applications/Unity/Hub/Editor`, Windows `C:\Program Files\Unity\Hub\Editor`, Linux `~/Unity/Hub/Editor`) plus the `UNITY_HUB` override; picks the newest unless the lock records a `unityVersion`, then matches that minor line. Explicit env vars:

- `UNITY_PATH` — **optional**, override auto-discovered Unity executable (highest priority).
- `UNITY_PROJECT_PATH` — Unity project root (absolute, or relative to the client's working directory). A committed config uses the server args `--project-from-cwd [--unity-subpath <rel>]` instead; the env var wins when both are set.
- `UNITY_HUB` — **optional**, override Hub install root.

If Unity can't be found, batch tools return `unity_not_discovered`; only offline reads + `read_compile_errors` still work.

A headless failure before its report (`compile_failed`, `project_load_failed`, `compile_indeterminate`, `markers_missing`, `batch_aborted`) ends its message with `Last output: …` — the run's own Unity log. Read the diagnosis there (a package-resolution failure prints the Package Manager notice as its last lines); `read_compile_errors` cannot see a batch run.

> **In-Editor progress.** The Unity Open MCP bridge window's **Activity** tab has a **Batch runs** section — a read-only view of in-Editor batch runs (live progress: pending / running / done / failed; per-entry tool name, args summary, pass/fail, error text). It observes batch state; it does not start batches. Useful when an operator wants to watch a batch run from inside Unity.

---

### Local package source recompile caveat

Edits under `packages/` live **outside** Unity's `Assets/` watch root — Unity does not auto-detect them, and neither `assets_refresh` nor `RequestScriptCompilation()` reliably picks them up. If you skip this, you'll run tests against the stale DLL and conclude your fix failed when it was never compiled in.

After editing `packages/` source, before tests:

1. Confirm the new source has no CS errors (only meaningful once Unity has seen it once — see [Safe Mode recovery](#step-4--safe-mode--bridge_compile_failed-recovery) if the bridge is already dead).
2. Trigger the recompile via one of:
  - **(a, recommended)** `reimport_package` with `package_id` — force-reimports the local package's source and reports `dllMtimeBefore`/`After` + a `recompiled` boolean, so a no-op recompile is detectable (on a no-op the response's `agentNextSteps` points at a standalone Roslyn-compile fallback). The package id is the scope; `paths_hint` is optional.
  - **(b)** `package_add` / `package_remove` a no-op entry to force UPM resolution + domain reload, then revert it.
  - **(c)** Ask the user to focus the Unity window after you touch a tracked `Assets/` file.
  - **(d, if bridge up)** `execute_csharp` calling `AssetDatabase.ImportAsset("Packages/<pkg>/...", ImportAssetOptions.ForceUpdate)` then `CompilationPipeline.RequestScriptCompilation()`.
3. **Verify the DLL actually rebuilt before tests:**
  ```bash
   stat -f "%Sm %N" Library/ScriptAssemblies/<assembly>.dll   # macOS
  ```
   Compare mtime to your last edit; do not run tests until DLL mtime > edit mtime.

> **Stale-DLL trap:** if tests fail identically to before your fix, suspect the DLL never recompiled before concluding the fix is wrong. Two further pitfalls when the DLL mtime refuses to advance:
> - **`read_compile_errors` never shows a `compile_check` / batch run.** The headless run logs only to its captured output (returned as the error's `Last output` tail), not to `Editor.log` — so a subsequent `read_compile_errors` reads the live editor's or a previous session's log, and a "no errors" result there proves nothing about the batch run. Diagnose a batch failure from that tail.
> - **Stale DLL + `isCompiling:false` can be a crash, not a no-op.** If `RequestScriptCompilation` returns success but the `Library/ScriptAssemblies/*.dll` mtime never advances *and* `read_compile_errors` is clean, do **not** conclude "incremental no-op" — grep the live editor log for `Unhandled exception during build` / `Bee.BeeDriver` / `NotSupportedException` / `file descriptor`. A Bee build-backend crash (fd/socket exhaustion) aborts before emitting the DLL and leaves the editor idle with a stale DLL; it needs an editor restart to clear the broken state. `editor_status` does not distinguish "completed-ok" from "crashed."
