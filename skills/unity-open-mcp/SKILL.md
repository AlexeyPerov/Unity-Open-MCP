# Unity Open MCP

Practical skill for AI agents driving a Unity project through the `unity-open-mcp` MCP server.

> Tool prefixes: `unity_open_mcp_*` (bridge-routed) and `unity_senses_*` (standalone, live-only). The prefix signals routing.

## Preconditions

- Unity Editor is open with the target project when using live-only tools.
- `unity_open_mcp_ping` should report `connected: true` for live bridge flows.
- Offline reads (`list_assets`, `search_assets`, `find_references`, `read_asset`) and `read_compile_errors` work without a live bridge.

## Non-negotiable rules

1. **Discover first** — call `unity_open_mcp_capabilities` before assuming tool names, schemas, route policy, or fixes.
2. **No hardcoded bridge port** — per-project port is `20000 + (sha256(projectPath) % 10000)`. Use the instance lock `port` as authority.
3. **Always scope mutations** — mutating tools require non-empty `paths_hint` (empty fails with `paths_hint_required`).
4. **One test run at a time** — never start a second `unity_senses_run_tests` before the first resolves (no concurrency guard; results cross-contaminate).
5. **Mutation success ≠ project safe** — always read gate output (`gate.delta`, `agentNextSteps`).
6. **Never launch a second Unity instance** for the same project — Unity holds a per-project lock (`<project>/Temp/UnityLockfile`); diagnose the wedged instance instead.
7. **Start compact, expand on demand** — the heavy tools (`read_asset` / `search_assets` / `scene_get_data` / `find_references` / `validate_edit` / `scan_paths`) default to `profile: "compact"` + a `pagination` block. Drill down (`component` / `path` / `id` flags) or escalate to `balanced` / `full` only for the slice you need; set `page_size` to bound any profile. See `capabilities.costHints` for per-tool profile cost bands + recommended page sizes.

## Fast start sequence

1. `unity_open_mcp_capabilities` — discover surface (call first on a fresh project).
2. `unity_open_mcp_manage_tools(action="list_groups")` — see which tool groups exist and their compiled-state availability. Sessions start with only `core` enabled; activate the group you need before calling its tools (e.g. `activate` `navigation` before `navigation_surface_add`). State is per-session and ephemeral.
3. `unity_open_mcp_ping` — bridge health.
4. `unity_open_mcp_find_members` — before reflection-heavy calls.
5. Mutate with `gate: "enforce"` + scoped `paths_hint`.
6. On gate failure, prefer `unity_open_mcp_apply_fix` with `dry_run: true` first.
7. Re-run mutation; confirm `newErrors == 0` or `resolvedErrors > 0`.

## Tool groups and session visibility

Sessions start with several main groups visible in `ListTools`. Every other group is hidden until you activate it (or auto-activates when its Unity package is installed — see below) — this keeps the prompt small (255 tools in the full surface). Call `unity_open_mcp_manage_tools` to toggle:

- `list_groups` — every group with active flag, compiled-state availability, and tool roster.
- `activate` / `deactivate` — toggle one group for this session. When visibility actually changes, the MCP server emits `notifications/tools/list_changed`; clients that support `listChanged` refresh `ListTools` automatically (no reconnect required).
- `reset` — restore `core`-only.

Common groups: `gate-and-verify`, `asset-intelligence`, `typed-editor`, `diagnostics`, `gate-intelligence`, `build-settings`, `navigation`, `input-system`, `probuilder`, `particle-system`, `animation`, `splines`, `lighting`, `audio`, `ui`, `constraints`, `terrain`, `cinemachine`, `timeline`, `tilemap`, `shadergraph`, `vfx`, `memoryprofiler`, `agent-senses`, `unity-hub-control`. Compiled-state availability (`available: true/false/null`) reflects whether the Unity domain package compiled in (built-in modules like Lighting, Audio, UI, Constraints & LOD, and Terrain are always compiled — `available: true`; Cinemachine is reflection-gated — the assembly always compiles and per-call detection surfaces the install/upgrade error); the authoritative source is `unity_open_mcp_capabilities` → `toolGroups[].available`. **Auto-activation:** the `shadergraph`, `vfx`, and `memoryprofiler` groups activate automatically for the session when their Unity package (`com.unity.shadergraph`, `com.unity.visualeffectgraph`, `com.unity.memoryprofiler`) is installed (no manual `manage_tools` call) — `list_groups` reports them with `activationSource: "auto"`; all other groups stay manual-activation only. State resets to `core`-only on MCP-server restart.

`unity-hub-control` covers Unity Hub operations that need **no running Unity or bridge**: `hub_list_editors` (installed editors + build-target platforms), `hub_available_releases` (download feed, with changesets), `hub_install_editor` / `hub_install_modules` (fire the `unityhub://` deep link — install runs inside the Hub), and `hub_get/set_install_path`. Activate it when a task needs to install a Unity version or inspect what editors are available. Mutating members are system-level ops (paths_hint N/A, gate-free); there is no in-call completion detection for installs — poll `hub_list_editors` afterwards.

Each domain group also has a deeper playbook under `skills/extensions/<domain>/SKILL.md` (lighting, audio, ui, constraints, terrain, cinemachine, timeline, tilemap, shadergraph, vfx, memoryprofiler, + the M18 packs) — consult the matching one for domain-specific tool contracts, gate/lifecycle hints, and round-trip workflows.

## Unity state triage (before edits/tests, and on `bridge_offline`)

The single most common agent mistake is misclassifying Unity's state. Follow this **in order** before running tests, before launching Unity, and whenever a tool returns `bridge_offline` or `bridge_compile_failed`.

> **Editing C#?** An offline bridge after a C# edit is frequently a *symptom* of a failed compile, not "Unity isn't running." Skip to [Compile failure recovery](#compile-failure-recovery) and call `read_compile_errors` — it reads `Editor.log` offline and survives a dead bridge.

### Step 1 — Is Unity running at all?

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
| `pid` alive, `state: compiling` / `isCompiling: true`                 | Unity compiling                                     | **Wait** — poll `curl http://127.0.0.1:<port>/ping`, re-read `isCompiling`. Don't edit/test |
| `pid` alive, `state: reloading`, heartbeat **stale** (>10s)           | **Safe Mode** — bridge assembly failed to recompile | Go to Step 4                                                                                |
| `pid` alive, heartbeat fresh, but every tool returns `bridge_offline` | **Port mismatch**                                   | Go to Step 3                                                                                |


### Step 3 — Port mismatch (`UNITY_OPEN_MCP_BRIDGE_PORT` trap)

The lock's `port` is authoritative. An MCP-client config pinned `UNITY_OPEN_MCP_BRIDGE_PORT` to a stale value (the env var always wins on both sides). The agent cannot edit the MCP client config at runtime, so **tell the user** the bridge's actual port and that the `UNITY_OPEN_MCP_BRIDGE_PORT` value in their MCP client config (e.g. `.zcode/cli/config.json`, `.cursor/mcp.json`, `.claude.json`) is wrong — removing it lets both sides derive the per-project hash and agree.

### Step 4 — Safe Mode / `bridge_compile_failed` recovery

A C# edit broke the bridge assembly (or a dependency); Unity is stuck mid-reload showing the "Enter Safe Mode?" dialog.

1. Call `**unity_open_mcp_read_compile_errors`** — reads `Editor.log` tail offline, returns structured CSxxxx errors (`file`/`line`/`code`). The **only** diagnostic that survives a dead bridge. (`compile_check` does **not** work here — its batch entry point lives in the same broken assembly, and the per-project lock blocks a second instance.)
2. Read `errors[].file` / `line` / `code` and **fix the CS error in source first.** Do not retry tests, relaunch Unity, or call `compile_check`.
3. Trigger a recompile (see [Local package source recompile caveat](#local-package-source-recompile-caveat) if you develop against `packages/` source; otherwise a normal recompile from Unity is enough); the bridge reloads itself once the assembly compiles. The MCP server auto-dismisses the Safe Mode dialog under the default `UNITY_OPEN_MCP_DIALOG_POLICY=ignore`; set `manual` (or `UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1`) to opt out entirely.
4. Only when `read_compile_errors` reports no errors AND the lock shows fresh heartbeat + `state: idle` is the bridge back.

### Step 5 — Never conclude "Unity not running" while a process is alive

Safe Mode still owns the per-project lock and may show a window, but the bridge is dead — looks identical to "not running" from the MCP server. Always confirm with `kill -0 <pid>`; if alive, you are in Step 4.

## Compile failure recovery

Compile failures surface as **machine-readable error codes** — branch on the code, not free-text. The four you will hit:

- `**bridge_compile_failed`** — bridge assembly itself failed; live Editor stuck (Safe Mode), every live tool refuses. **Recovery:** `read_compile_errors` (reads `Editor.log` offline; survives the broken bridge) → fix the CS error → trigger a recompile.
- **`bridge_offline`** — Unity may not be running, or not yet recompiled. If Unity **is** open it has already written the latest CSxxxx diagnostics to `Editor.log`; `read_compile_errors` retrieves them.
- **`editor_instance_locked`** — `compile_check` cannot run while a live Editor holds the project lock. The response carries a structured `agentNextSteps[]`. Close the live Editor, or verify compile state without closing via `read_compile_errors`, or `reimport_package` (with `dllMtimeBefore`/`After`) to confirm a specific local package recompiled.
- **`_compileVerify: { code: "compile_noop" | "dll_stale" }`** — a recompile *reported success* but the compiled state did not advance. Surfaced as an additive annotation on a **successful** result (not an error), so check for it after any `compile-reload` tool. `compile_noop` = tool registry count + DLL mtime unchanged (incremental no-op); `dll_stale` = `Library/ScriptAssemblies/*.dll` older than your source edit. Both mean: do **not** trust the success — force a rebuild (no-op `package_add`/`package_remove`, or operator refocus of the Editor), then verify DLL mtime > edit mtime before tests.

For `bridge_compile_failed` / `bridge_offline`: call `**unity_open_mcp_read_compile_errors`** → fix `errors[].file`/`line`/`code` → trigger recompile. (If no Editor is open, `Editor.log` is stale from the previous session and won't reflect your latest edits — recompile verification needs Unity running.)

Use `**unity_open_mcp_compile_check**` only for a deliberate "does this build clean from scratch?" check — it spawns a fresh headless Unity and **always** routes to batch. It is not first-line recovery for a broken bridge assembly. When a live Editor already has the project open, the headless spawn cannot acquire Unity's one-Editor-per-project lock and returns `editor_instance_locked` — either close the live Editor and retry, or verify compile state via the live bridge instead (`execute_csharp` + `Library/ScriptAssemblies/*.dll` mtime check, or `read_compile_errors`).

## Batch fallback / Unity discovery

`compile_check` is always batch. Other batch-capable tools fall back to batch only when the live bridge is down. The MCP server auto-discovers Unity from OS-default Hub install paths (macOS `/Applications/Unity/Hub/Editor`, Windows `C:\Program Files\Unity\Hub\Editor`, Linux `~/Unity/Hub/Editor`) plus the `UNITY_HUB` override; picks the newest unless the lock records a `unityVersion`, then matches that minor line. Explicit env vars:

- `UNITY_PATH` — **optional**, override auto-discovered Unity executable (highest priority).
- `UNITY_PROJECT_PATH` — absolute project root (optional when a lock exists).
- `UNITY_HUB` — **optional**, override Hub install root.

If Unity can't be found, batch tools return `unity_not_discovered`; only offline reads + `read_compile_errors` still work.

> **In-Editor progress.** The Unity Open MCP bridge window has a **Batch** tab — a read-only view of in-Editor batch runs (live progress: pending / running / done / failed; per-entry tool name, args summary, pass/fail, error text). It observes batch state; it does not start batches. Useful when an operator wants to watch a batch run from inside Unity.

---

## Debug / contributor (skip if you use the released package)

> *Only relevant if you develop against the `packages/` source tree (e.g. a `file:../../packages/...` reference in `Packages/manifest.json`) rather than an installed release. End users on the published package can ignore this section.*

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
> - **Distrust `read_compile_errors` after a `compile_check` / batch attempt.** The headless batch spawn *overwrites* `~/Library/Logs/Unity/Editor.log`, so a subsequent `read_compile_errors` reads the batch process's log, not the live editor's — a "no errors" result there proves nothing about the live build. Cross-check by reading the log's content (does it mention the live PID / `BeeDriver`?) before trusting it.
> - **Stale DLL + `isCompiling:false` can be a crash, not a no-op.** If `RequestScriptCompilation` returns success but the `Library/ScriptAssemblies/*.dll` mtime never advances *and* `read_compile_errors` is clean, do **not** conclude "incremental no-op" — grep the live editor log for `Unhandled exception during build` / `Bee.BeeDriver` / `NotSupportedException` / `file descriptor`. A Bee build-backend crash (fd/socket exhaustion) aborts before emitting the DLL and leaves the editor idle with a stale DLL; it needs an editor restart to clear the broken state. `editor_status` does not distinguish "completed-ok" from "crashed."

## Core loop: mutate → gate → fix

1. **Discover** — `capabilities`, then `find_members` for reflection targets.
2. **Declare scope** — `paths_hint` for every asset path you intend to touch.
3. **Mutate** — typed tools preferred over `execute_csharp` / `invoke_method` / `execute_menu`; default `gate: "enforce"`.
4. **Read the gate** — on `isError: true`, inspect `gate.delta.newIssues` + `agentNextSteps`.
5. **Fix** — address the top error; `apply_fix` with `dry_run: true` first when a `fixId` is present. Each issue carries `rootCause` (machine-readable, branch on it) + `evidence` (the specific broken ref/value) + `fixCandidates` (every fix option with its `safe` flag) + `remediation` (the human-readable next step) — read these before choosing a fix.
6. **Retry** — confirm `gate.delta.resolvedErrors > 0` or `newErrors == 0`.

### Gate modes


| Mode                | When                                                    |
| ------------------- | ------------------------------------------------------- |
| `enforce` (default) | Normal edits — fail fast on new errors                  |
| `warn`              | Exploratory — read `gate.delta` but call does not error |
| `off`               | Trusted admin scripts only — no checkpoint/validate     |


### Gate failure (canonical shape)

```json
{
  "mutation": { "success": true, "output": "Player(Clone)", "error": null },
  "gate": {
    "mode": "enforce",
    "validation": {
      "passed": false,
      "issues": [{
        "severity": "Error", "code": "MISSING_SCRIPT",
        "assetPath": "Assets/Prefabs/Player.prefab",
        "fixId": "remove_missing_script", "fixSafe": true
      }]
    },
    "delta": {
      "newErrors": 1, "resolvedErrors": 0,
      "newIssues": ["missing_references|Error|Assets/Prefabs/Player.prefab|MISSING_SCRIPT"]
    }
  },
  "logs": [
    { "severity": "warning", "message": "...", "source": "unity" }
  ],
  "agentNextSteps": [
    "New error: missing_references MISSING_SCRIPT on Assets/Prefabs/Player.prefab",
    "Fix available: use unity_open_mcp_apply_fix with fix_id=\"remove_missing_script\""
  ]
}
```

Issue keys in `gate.delta.newIssues` are `ruleId|severity|assetPath|issueCode` (severity is `ERROR` / `WARN`). On success, `validation.passed: true`, empty `issues`, `agentNextSteps: []`. The `logs` array carries Unity console entries emitted *during this call* (always present, `[]` when none) — read it inline instead of polling `read_console` after a mutation. Stacks are omitted here; `read_console` stays the verbose path.

### Verify rules and issue codes

Authoritative via `capabilities` (call for the live list). Implemented:

- `**missing_references**` — per-PPtr-field view. Codes: `missing_guid` (Error, fix `relink_broken_guid` — unsafe, needs `target_guid`), `missing_fileid` (Error), `missing_script` (Error, fix `remove_missing_script`), `missing_local_fileid` / `empty_local_ref` (Warning), `missing_method` / `type_mismatch` / `duplicate_component` / `invalid_layer` (Warning, full-scan only).
- `**scene_prefab_health**` — structural health. Codes: `broken_reference` (Error), `high_risk_bootstrap` / `scene_object_count` / `component_hotspot` / `inactive_expensive` / `inactive_heavy` / `deep_nesting` / `override_explosion` (Warning).
- `**dependencies**` — forward dependency graph. Codes: `broken_dependency` (Error — asset-graph edge to a missing asset; fix `relink_broken_guid` — unsafe), `dependency_cycle` (Warning).
- `**asmdef_audit**` — assembly definition health. Codes: `broken_asmdef_reference` (Error — reference that does not resolve to a compiled assembly or known asmdef), `asmdef_missing_name` (Error — no `name` field), `malformed_asmdef` (Error — JSON failed to parse), `asmdef_duplicate_name` (Error, full-scan — name shared by 2+ asmdefs), `asmdef_circular_reference` (Error, full-scan — DFS cycle over the name-based reference graph), `asmdef_editor_in_runtime` (Warning — runtime assembly referencing an editor assembly), `asmdef_auto_referenced_orphan` (Warning, full-scan — `autoReferenced=false` and unreferenced), `asmdef_platform_filter_broad` (Warning — no platform filters), `asmdef_platform_filter_contradict` (Warning — simultaneous include + exclude platforms), `asmdef_version_define_invalid` (Warning — version define references a `com.*` package).
- `**project_health**` — whole-project integrity, full-scan only (does not fire on a scoped validate_edit). Codes: `orphan_meta` (Warning — `.meta` with no companion asset, fix `remove_orphan_meta` safe), `duplicate_guid` (Error — GUID shared by 2+ assets, fix `fix_duplicate_guid` unsafe), `missing_project_setting` (Error — required ProjectSettings file missing), `project_empty_folder` / `project_meta_only_folder` (Warning), `project_deep_nesting` (Warning — depth > 8), `project_large_folder` (Warning — > 200 files), `project_broken_asset` (Error — asset failed to load), `project_empty_scene` (Warning — zero root objects).
- `**materials**` — material reference + performance health. Per-asset codes: `missing_shader` (Error — null shader or InternalErrorShader, i.e. the original shader failed to compile/is missing), `missing_texture` (Warning — null texture at a shader property), `builtin_shader` (Warning — Standard/Legacy/Mobile builtin), `builtin_texture` (Warning — unity_builtin texture), `render_queue_override` (Warning), `unable_to_load` (Error). Full-scan-only codes: `duplicate_material` (Warning — SHA-256 fingerprint match), `unused_material` (Warning — unreferenced + not in Resources), `variant_parent_invalid` (Error), `variant_deep_chain` / `variant_heavy_overrides` (Warning), `gpu_instancing_off` / `srp_batcher_incompatible` (Warning), `null_material` / `null_material_slot` / `builtin_material` (Warning, renderer-side).
- `**animation_analysis**` — animator controller + clip health. Per-asset codes: `missing_clip` (Error — state with no motion assigned), `empty_clip` (Warning — `.anim` declaring no curves), `unreachable_state` (Warning — not reachable from entry/default/any-state via BFS), `complexity_over_threshold` (Warning — > 50 states), `anystate_overuse` (Warning — > 5 any-state transitions), `parameter_mismatch` (Warning — script references a param not in the controller), `expensive_curves_density` / `expensive_curves_count` (Warning). Full-scan-only: `duplicate_clip` (Warning — byte-size match).
- `**shader_analysis**` — shader compile + variant health. Per-asset codes: `shader_compile_error` (Error — InternalErrorShader), `missing_shader_asset` (Error — `.shader` failed to load), `variant_explosion` (Warning — `2^keywords × passes` over threshold), `pass_count_exceeded` (Warning), `fallback_shader` (Warning — has a `Fallback` directive), `expensive_feature_platform` (Warning — mobile-expensive keywords, mobile profile only), `platform_keyword_mismatch` (Warning — HDRP shader on mobile profile). Full-scan-only: `duplicate_keyword_profiles` (Warning — materials sharing a keyword set).

### Issue explainability

Every issue in a `scan_paths` / `validate_edit` response carries optional explainability fields (beyond `ruleId` / `severity` / `code` / `assetPath` / `description`):

- **`rootCause`** — a stable machine-readable code identifying *why* the issue class happens. Branch recovery on this, not on free-text. Values: `missing_guid_reference`, `missing_fileid_reference`, `missing_script_class`, `missing_dependency`, `orphaned_meta`, `duplicate_guid`, `structural_complexity`, `configuration_mismatch`, `resource_missing`, `build_blocker`. The same code is declared on the issue descriptor in `capabilities` / `list_rules` (`issues[].rootCause`).
- **`evidence`** — the per-instance payload that fired *this* issue (the broken reference's GUID / fileID / line, the duplicate group's paths, the count vs threshold, etc.). Keys are issue-class-specific, always string-valued. Absent when the rule has no per-instance detail.
- **`fixCandidates`** — every fix that can resolve the issue, each `{fixId, safe}`. Use this over the legacy single `fixId` / `fixSafe` pair (kept for backwards compat): it lists safe **and** unsafe options so you pick deliberately. Absent when no fix exists.
- **`remediation`** — a short, clean, user-visible playbook for the issue class (the human-readable next step). Pair with `rootCause`: branch on the code, surface the remediation text.

### Fixes

`apply_fix` defaults to `dry_run: true` (the dry-run short-circuits the gate entirely — returns description/candidates without checkpoint+validate):

- `**remove_missing_script**` (safe) — strips `MonoBehaviour` whose script GUID no longer resolves. Works on `.prefab` / `.unity`.
- `**remove_orphan_meta**` (safe) — deletes a `.meta` whose companion asset was deleted. Producer: `project_health` (live) / `offline_integrity` (offline). No asset data lost.
- `**relink_broken_guid**` (unsafe) — rewrites a broken external GUID reference. Dry-run advertises candidate targets; apply requires `target_guid`. Never auto-applied.
- `**fix_duplicate_guid**` (unsafe) — regenerates the GUID of one colliding asset. Re-GUIDing silently rewires the asset graph, so pick the less-referenced asset deliberately; apply on that asset's issue id. Producer: `project_health` / `offline_integrity`. Never auto-applied.
- `**reassign_missing_texture**` (unsafe) — assigns a texture to the material's null texture slot(s). Dry-run advertises candidate textures; apply requires `target_texture` (asset path or GUID). Producer: `materials`. Never auto-applied.
- `**reassign_missing_shader**` (unsafe) — assigns a shader to a material whose shader is null / the error shader. Dry-run advertises candidate shaders; apply requires `target_shader` (shader name e.g. `Standard`, or asset path). Producer: `materials`. Never auto-applied.

If `fix_id` omitted, the response lists every fix that can resolve the given `issue_id`.

**Safe auto-fix rollback.** A non-dry-run `apply_fix` runs checkpoint → apply → validate → delta, and if the fix fails to apply **or** introduces new errors under `enforce`, the touched files are restored to their pre-fix state and the response carries a top-level `rollback` block: `{rolledBack: true, reason, restoredPaths[]}`. Read `gate.delta.newErrors` + `rollback` together — a rolled-back fix left no project change, so inspect the issue manually before retrying. Rollback is **not** triggered by new warnings (informational) or under `warn`/`off` gate modes (report-only).

## Gate intelligence: plan before, explain after

Three read-only, gate-free tools compose gate foundations into agent-actionable shapes. They do **not** run a rule scan or mutate — treat outputs as guidance (every response carries a `heuristicNote`).

- **Before mutating** — `unity_open_mcp_impact_preview` (`paths_hint`): resolves the auto-selected rule set, classifies each path, reports coarse `risk.band` (`low`/`moderate`/`high`) with `confidence`. Size risk before paying for a checkpoint.
- **Before mutating** — `unity_open_mcp_gate_budget_estimate` (`paths_hint`, `mode: "cache"` | `"sample"`): forecasts `estimatedDurationMs` (lower bound) + `estimatedIssueBudget` (upper bound) with `basis` + `confidence`. `sample` runs a cheap checkpoint scan (grounded); `cache` inspects the latest VerifyCacheService snapshot (cheap, coarse).
- **After mutating** — `unity_open_mcp_mutation_explain` (`checkpoint_id?`, `tool_name?`): projects the most recent gate run into a `narrative` + structured `summary` (outcome, new/resolved counts, durations, `agentNextSteps`).

Typical sequence: `impact_preview` (size) → `gate_budget_estimate` `mode: "sample"` (cost) → mutate → `mutation_explain` (narrate).

## Lifecycle & scene safety


| Policy                | Meaning                                                                             | Tools                                                                                                                                                                                                                              |
| --------------------- | ----------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `none`                | Read-only, returns immediately                                                      | `ping`, `find_members`, `validate_edit`, `checkpoint_create`, `delta`, `find_references`, `dependencies`, `scan_paths`, `read_asset`, `search_assets`, `list_assets`, `editor_status`, `read_console`, `screenshot`, `profiler_*`, `spatial_query` |
| `editor_settle`       | Mutating; bridge waits for asset refresh/serialization to finish                    | `apply_fix`, `reserialize`                                                                                                                                                                                                         |
| `restart_then_settle` | Mutating; may trigger domain reload; bridge blocks until compile finishes (cap 60s) | `execute_csharp`, `invoke_method`, `execute_menu`, `compile_check`                                                                                                                                                                 |
| `custom_confirmation` | Async; returns immediately, result via external completion signal you poll          | `run_tests`                                                                                                                                                                                                                        |


> The table above is the bridge's settle-timing surface (how long it blocks before returning). For the **recovery-axis** taxonomy — `none` / `compile-reload` / `modal-dialog` / `scene-dirty` / `process-stale`, describing what to do when a call fails or stalls — read `capabilities.tools[].lifecycle` (+ `lifecycleNote`) per tool, and `capabilities.lifecycleBlock` for the full class table. See the docs §Lifecycle policy for the `compile-reload` agent limits (notably `compile_check` is batch-only and returns `editor_instance_locked` when a live Editor holds the project lock).

**Active-scene dirty guard.** Before any `restart_then_settle` op, the bridge preflights loaded scenes. If any scene has unsaved changes, the call refuses with `error.code = "scene_dirty"` + `dirtyScenes[]` + `agentNextSteps` so Unity's native save modal never interrupts. Recover by: saving first (`scene_save`), discarding (`EditorSceneManager.RestoreSavedSceneState()`), or passing `ignore_scene_dirty: true` on `execute_csharp` / `invoke_method` / `execute_menu` / `scene_open` / `editor_set_state` / `build_set_target` / `build_set_defines` / `settings_set_player`.

`apply_fix`, `reserialize`, and the non-`scene_open` scene mutators (`scene_create` / `scene_save` / `scene_unload` / `scene_set_active` / `scene_focus` / `sceneview_set_camera`) are **not** guarded.

**Power-tool deny heuristic.** `execute_csharp` / `execute_menu` are blocked from destructive patterns by default (`EditorApplication.Exit`, `Application.Quit`, `AssetDatabase.DeleteAsset`, `BuildPipeline.BuildPlayer`, `File/Quit`, `TestRunnerApi`). Refused calls return `error.code = "denied_by_policy"` (csharp) or `"menu_blocked"` (menu) with the matched pattern + alternative. If you genuinely need one, set **both** `gate: "off"` and `confirm_bypass: true` — the bypass is audited.

**`execute_csharp` runs on Unity's main thread — never block it.** The snippet executes synchronously inline on `EditorApplication.update`, so any blocking primitive wedges the editor **unrecoverably**: `WaitOne` / `.Result` / `Thread.Sleep` / `while(!done)` waiting for a callback, and driving `TestRunnerApi` (which delivers its callbacks on the same main thread), all deadlock. The HTTP timeout fires on the worker thread and **cannot self-heal a stuck main thread** — no further editor tick runs, so the editor must be killed externally. The timeout envelope's `agentNextSteps` will tell you this; heed it (check `editor_status` / `bridge_status` before retrying, **don't** just raise `timeout_ms`). For test execution use `unity_senses_run_tests` (async, does not block). For everything else, prefer a typed tool or write the snippet to be non-blocking (fire-and-forget + poll via a follow-up call).

## Routing rules

Treat `capabilities.routePolicy` + `batchCapable` as source of truth.

- **Live is the default** — when the bridge is connected, most tools route to `POST /tools/{name}` on the Editor.
- **Batch fallback** — spawns headless Unity (`-batchmode`) **only** for `batchCapable: true` tools when the live bridge is unavailable. Mutating meta-tools (`execute_csharp`, `invoke_method`, `execute_menu`) are blocked in batch — they need a live Editor.
- **Senses are live-only** — `run_tests`, screenshots, profiler, console, spatial queries have no batch form.
- **Offline reads** (`list_assets`, `find_references`, `read_asset`, `search_assets`) parse the project from disk and never need Unity. Coverage: text-serialized Unity YAML (`.prefab`/`.unity`/`.asset`/`.mat`/`.controller`/`.anim`/`.playable`/`.preset`/`.spriteatlas`/`.terrainlayer`/`.vfx`) **plus** JSON assets unity-scanner can't parse (`.asmdef`, `.shadergraph`/`.shadersubgraph`). `read_asset` also reconstructs the full hierarchy offline, parses prefab-variant overrides (matching `prefab_get_overrides`), and surfaces `integrity[]` signals (missing refs, missing scripts, malformed JSON, orphaned prefab instances) on the response — act on them before a `validate_edit` round-trip.
- `**dependencies` is live-only** — it reuses the verify `Dependencies.Scanner` (forward) + `ReferenceGraph` (reverse), both of which call AssetDatabase. No offline form.
- `**compile_check` is always batch** — spawns a fresh headless Unity that recompiles from scratch, even when the live bridge is up.

## Typed tool catalog

Prefer these over `execute_csharp` for routine workflows — explicit schemas, same gate envelope, structured results. All mutating tools accept `gate` (`enforce`/`warn`/`off`, default `enforce`) and require non-empty `paths_hint`.

**Asset CRUD** (`paths_hint` = touched asset paths) — `assets_create_folder` / `assets_copy` / `assets_move` / `assets_delete` / `assets_refresh` (`whole_project: true` binds whole-project scope).

**Materials** — `material_create` / `material_get_properties` / `material_set_property` (typed by `type: color|float|int|vector|texture`) / `material_get_keywords` / `material_set_keyword` / `material_set_shader`. Resolve by `asset_path` (.mat) or `instance_id` of a scene GameObject's Renderer.sharedMaterial. Use `shader_list_all` to discover valid shader names.

**Shaders (gate-free)** — `shader_list_all` / `shader_get_data` (folds compile errors into `errors[]`).

**GameObjects** (`paths_hint` = active scene path) — `gameobject_create` / `gameobject_destroy` / `gameobject_duplicate` / `gameobject_modify` (note: target name is `name_target` so `name` stays free for the new value; supports name/tag/layer/active + transform, plus a three-surface form: `gameObjectDiffs` for grouped root patches, `pathPatchesPerGameObject` for descendants, `jsonPatchesPerGameObject` for per-component reflection patches) / `gameobject_set_parent` (cycle-safe). Address by `instance_id` > `path` > `name`. Every mutator is undo-recorded.

**Components** (`paths_hint` = scene path containing host) — `component_add` (by `component_types[]`, full name preferred) / `component_destroy` / `component_modify` (per-path serialized patches via `fields: [{path, value, type?}]`; for enums `type: "name"` sets by enum name). Resolve by `component_instance_id` (specific) or `type_name` (full name preferred). Use `component_list_all` to discover attachable types before `add`; `component_get` to discover serialized paths before `modify`.

**Prefabs** — `prefab_instantiate` / `prefab_create` / `prefab_open` / `prefab_close` / `prefab_save` / `prefab_apply` / `prefab_revert` / `prefab_unpack`. Read-only: `prefab_get_overrides` / `prefab_status`. Address scene instances by `instance_id` > `path` > `name`.

**Scenes** (`paths_hint` = scene asset path) — `scene_create` (`setup: empty|default`, `mode: single|additive`) / `scene_open` (Single is `restart_then_settle` — dirty guard preflights; `ignore_scene_dirty: true` skips) / `scene_save` (active scene when `name` omitted; idempotent on clean) / `scene_unload` (refuses last opened scene) / `scene_set_active`. Read-only: `scene_list_opened` / `scene_get_data` (`profile: compact|balanced|full`, `depth`, `max_nodes` — reflects unsaved editor state, unlike `read_asset` on the `.unity`; `page_size`/`cursor` page the node stream) / `scene_get_dirty_summary`. SceneView tools: `scene_focus` frames a GameObject (`axis: top|bottom|front|back|left|right`), `sceneview_get_camera` reads the current SceneView pose (position/rotation/pivot/projection/size/fov), and `sceneview_set_camera` sets pose-level camera state (position required, optional rotation/orthographic/size) and returns `windowMoved: true`.

Workflow: `scene_list_opened` → `scene_get_data` → mutate → `scene_get_dirty_summary` → `scene_save`. Before opening a new scene in Single mode, check `scene_get_dirty_summary` and save first.

**Package Manager** (`paths_hint = ["Packages/manifest.json"]`; don't list packages-lock.json separately; mutating tools are `restart_then_settle` — UPM resolution can domain-reload) — `package_add` (registry id / `name@version` / Git URL / `file:../path` / `.tgz`) / `package_remove` (by name; trailing `@version` stripped; refuses packages depended-on by others). Read-only: `package_list` (`source`/`name_filter`/`direct_dependencies_only`/`include_indirect` filters; `offline: true` default) / `package_search` (`offline: false` hits live registry for exact matches) / `package_get_info` / `package_get_dependencies` (fastest manifest snapshot, no UPM round-trip) / `package_check`.

Workflow: `package_check` → `package_search` if not installed → `package_add` → after settle, `package_get_info` to confirm resolved version.

**Console / editor state / selection / undo / tags / layers** — write NO assets, so **gate-free** direct-response tools (no gate envelope):

- Console: `console_clear` / `console_log` (`level: log|warning|error`, optional `context_instance_id` / `context_asset_path`).
- Editor state: `editor_set_state` (`state: play|pause|stop`) — writes no assets but runs dirty guard inline; pass `ignore_scene_dirty: true` to accept. Poll `editor_status` after.
- Selection: `selection_get` / `selection_set` (single target by `instance_id`/`asset_path`/`path`/`name`, or `targets[]` for multi; `clear: true`).
- Undo/redo: `editor_undo` / `editor_redo` (`steps`, default 1).
- Undo history/reset: `editor_undo_history` (read recent stack entries; `max_entries`, hard cap 50; returns `truncated`) / `editor_clear_history` (mutating, irreversible stack reset; gate-aware, `paths_hint` = active scene path).
- Tags/layers (gate-free): `editor_get_tags` / `editor_get_layers` (layers include slot indices). Mutators (gate-aware, `paths_hint = ["ProjectSettings/TagManager.asset"]`): `editor_add_tag` (idempotent; refuses reserved names) / `editor_add_layer` (first empty slot 8–31, or explicit `slot`; refuses reserved names + occupied slots).

**Reflection / scripts / object data** — `type_schema` (read-only; structured member schema for one type; use to plan `invoke_method`/`object_modify`) / `script_read` (read-only, line slicing) / `script_write` (Roslyn pre-validated; `validate: true` default refuses non-compiling code with `validation_failed`) / `script_delete` (mutating; removes `.cs` + `.meta`) / `object_get_data` (read-only reflective walk) / `object_modify` (sets public fields/properties by name; safe by default — refuses static/init-only unless `allow_static: true`). Core reflection enhanced: `find_members` lists every overload separately; `invoke_method` accepts `generic_arg_types` (e.g. `GetComponent<Rigidbody>`) and `arg_type_names` (disambiguate overloads). Prefer `component_get`/`component_modify` for one Component's Inspector fields; use these for ScriptableObjects, Materials, or any non-Component Object.

**ScriptableObjects** — `scriptableobject_create` (mutating; instantiate a compiled ScriptableObject type + write the `.asset`, with optional initial field patches reusing `object_modify`'s value shape; read/info stays on `object_get_data`, field edits stay on `object_modify`) / `list_assets_of_type` (read-only, gate-free; enumerate assets of one type under a folder via `t:<Type>`, offline-routeable in principle).

**Assembly Definitions (.asmdef)** — `asmdef_list` (read-only; enumerate `.asmdef` assets under a folder, package asmdefs opt-in) / `asmdef_get` (read-only; full parsed model — references, platforms, define constraints, versionDefines preserved verbatim; offline-routeable, `.asmdef` is JSON) / `asmdef_create` (mutating, restart_then_settle — writing an asmdef triggers a recompile + domain reload; the gate waits for the settle window and the dirty guard preflights it) / `asmdef_modify` (mutating, restart_then_settle; additive `add_references`/`remove_references` or full `references` replacement; setting `include_platforms` clears `exclude_platforms` and vice versa). After create/modify, poll `editor_status`/`compile_check` then `scan_paths` to catch broken references.

**Profiler session/diagnostics** — complement the agent-senses profiler tools (runtime/session layer, not a second per-frame read). Most are gate-free direct-response (write no assets); only `profiler_save_data` runs the gate (`paths_hint` = destination `.json`):

- Session: `profiler_start` (`open_window: false` skips the menu) / `profiler_stop` (idempotent).
- Reads (gate-free): `profiler_get_status` / `profiler_get_config` / `profiler_get_script_stats`.
- Config (gate-free): `profiler_set_config` (`mode: play|edit`, `deep_profile`, `allocation_callstacks`, `binary_log`, `output`, `max_used_memory`, `enable_categories[]` / `disable_categories[]`).
- Modules (gate-free): `profiler_list_modules` / `profiler_enable_module`.
- Buffered frames: `profiler_clear_data` (destructive — save first if needed).
- Snapshots: `profiler_save_data` (mutating, gate-aware) / `profiler_load_data` (read-only).

Workflow: `profiler_start` → poll `profiler_get_status` → run workload → `unity_senses_profiler_capture` for frame data → `profiler_save_data` → `profiler_stop`.

**Build pipeline + project settings** (`*_get_*` are gate-free; mutators run gate path scoped to touched `ProjectSettings/*.asset`):

- Build reads (gate-free): `build_get_targets` / `build_get_active_target` / `build_get_scenes` / `build_get_defines`.
- Build mutators: `build_set_target` (`paths_hint = ["ProjectSettings"]`, restart_then_settle) / `build_set_scenes` (`paths_hint = ["ProjectSettings"]`) / `build_set_defines` (accepts array or `;`-joined string; empty clears; recompiles; `paths_hint = ["ProjectSettings/ProjectSettings.asset"]`) / `build_start` (DESTRUCTIVE — refuses with `build_confirmation_required` unless BOTH `gate: "off"` AND `confirm_bypass: true`).
- Settings reads (gate-free): `settings_get_player` / `settings_get_quality` / `settings_get_physics` / `settings_get_lighting` (scene-scoped, reflects active scene).
- Settings mutators (each takes `fields: [{key, value}]`; per-key failures accumulated as warnings; `restart_then_settle` for `settings_set_player`, `editor_settle` otherwise): `settings_set_player` (`paths_hint = ["ProjectSettings/ProjectSettings.asset"]`) / `settings_set_quality` (`["ProjectSettings/QualitySettings.asset"]`) / `settings_set_physics` (`["ProjectSettings/DynamicsManager.asset"]`) / `settings_set_lighting` (scene-scoped; tool marks active scene dirty — `paths_hint` scopes to active scene path).
- Settings remainder (time / render pipeline / quality level): `settings_get_time` (gate-free; runtime Time values + TimeManager.asset-backed settings) / `settings_set_time` (`fields` of `fixedDeltaTime` / `timeScale` / `maximumDeltaTime` / `captureFramerate`; `paths_hint = ["ProjectSettings/TimeManager.asset"]`; `editor_settle`) / `settings_get_render_pipeline` (gate-free, read-only; reports Built-in / URP / HDRP — **no setter**, switching SRP is a package-level op via `package_add`/`remove`) / `settings_set_quality_level` (`quality_level` name or index + optional `platform`; `paths_hint = ["ProjectSettings/Quality.asset"]`; `editor_settle`. The public API exposes no per-platform active-level setter — when `platform` is given the global level is set and the requested scope is reported).
- KV preferences (PlayerPrefs + EditorPrefs) — **gate-free direct-response** (write to registry / `Library/PlayerPreferences`, NOT project assets; mutating in catalog terms but no `paths_hint` required, mirrors `editor_undo`): `playerprefs_get` (`key` + optional `type` int/float/string, inferred when omitted) / `playerprefs_set` (`key` + `value` + `type`; calls `PlayerPrefs.Save()`) / `playerprefs_delete` (`key`; reports `existed`). Same triple for `editorprefs_*` (writes through immediately, no Save). **`playerprefs_delete_all` is deliberately NOT shipped** — an irreversible project-wide wipe with no key filter; route it through `execute_csharp` with an explicit confirm.

Workflow (CI build prep): `build_get_scenes` → `build_set_scenes` → `build_get_defines` → `build_set_defines` → `build_get_active_target` → `build_set_target` → `build_start` (`gate: "off"` + `confirm_bypass: true`).

**Raw mutators** (when no typed tool fits) — `execute_csharp` (compile + run snippet) / `invoke_method` (reflection call) / `execute_menu` (Unity Editor menu item) / `apply_fix` (verify rule fix) / `reserialize` (round-trip text assets through Unity's serializer).

## Agent senses (live-only, no batch fallback)

- `**unity_senses_run_tests`** — EditMode + PlayMode test runner with per-test pass/fail. Filter by assembly / namespace / class / method. Set `include_passes: false` on large suites to avoid truncation. **Never fire a second `run_tests` before the first resolves** (no concurrency guard; results interleave).
- `**unity_senses_read_console`** — console entries via reflection. Filter `type: "error"` to confirm clean compile. `detail: "summary"` for messages only (saves tokens); `detail: "verbose"` includes Unity-internal frames.
- `**unity_senses_screenshot**` — Scene / Game / isolated 2×2 composite of one GameObject.
- `**unity_senses_screenshot_camera**` — render from an arbitrary world-space pose (position + rotation + fov) without moving the scene/game camera; transient camera, scene camera untouched.
- `**unity_senses_capture_inline**` — same targets as `screenshot` but returns the PNG as an inline base64 image (no temp file) for agents that don't read the filesystem.
- `**unity_senses_screenshot_window**` — capture an Editor window (Console / Hierarchy / Inspector / Project / Scene / Game / custom). Windows-only full-fidelity via PrintWindow; on macOS/Linux a best-effort readback is used and the response carries `platformLimited: true`.
- `**unity_senses_frame_debugger**` — control Unity's Frame Debugger: `action: enable` opens the window and starts capturing, `action: disable` stops it, `action: list` returns the draw-call list (shader / pass / material / render target / vertex/index/instance counts per call). Read-only (gate off); the response reports `windowOpened` so you know Editor UI may have changed. Enable, render a frame, then call `list` to inspect draw calls.
- `**unity_senses_profiler_capture**` / `profiler_memory` / `profiler_rendering` — frame hierarchy, memory allocators, rendering env.
- `**unity_senses_profiler_capture_frame`** — single-frame deep profiler capture. Returns one (or a few, via `frame_count`) frame's full sample tree, optionally filtered by `modules` (Profiler category names, e.g. "CPU,Rendering"). Deeper than `profiler_capture`; bound output with `max_depth` (default 8) and `max_items` (default 200). If the Profiler is off, the tool enables it for one frame and reports `profilerWasEnabled`.
- `**unity_senses_spatial_query**` — physics-based raycast / overlap / bounds / ground_check / nearest against the live scene.
- `**unity_senses_pull_events**` — incremental console logs + editor-state transitions (compile start/stop, play-mode). Cheaper than `read_console` for "what happened since my last call"; first call opens the stream, later calls return only new events. Returns `bridge_unavailable` when the bridge is down.

**Verification habit:** after any C# change, run `read_console` with `type: "error"` (or `run_tests` on the affected assembly) to confirm compile + tests pass before declaring done.

## Key workflows

### Reserialize after direct YAML edits

When you edit `.prefab` / `.unity` / `.asset` / `.mat` / `.controller` / `.anim` directly as YAML text, run `**unity_open_mcp_reserialize`** with the touched `paths` (the `paths` array doubles as gate scope). Round-trip rewrites canonically so missing fields, wrong indentation, and stale `fileID` references surface in `gate.delta`. Whole-project reserialize is intentionally unsupported — enumerate assets you edited. Default targets asset YAML only (no `.meta` diff on body-only edits); pass `include_meta: true` only for upgrade/importer-change workflows.

> **Edit freely, but always reserialize before trusting a direct YAML change.**

### read_asset: map, not dump

Raw Unity YAML is enormous. `read_asset` returns counts, a `cmp` table declaring repeated component sets once (referenced by `c1`/`c2` codes), and a folded `tree`. Drill down with `field_limit` + `component` / `path` / `profile=full` (or `detail=verbose`) instead of re-reading raw YAML. Session cache reuses the parsed model (`_cache: "hit"`). `profile: full` disables render-only folding; `field_limit: 0` (default) returns names only — bump it before `component` drill-down so fields are available. `page_size`/`cursor` page the `tree` rows.

Use `**search_assets`** to locate prefabs/components/GUIDs; each result tags *why* it matched so you know which `read_asset` drill-down to run next.

### Reads that save tokens

Raw Unity data is large. Prefer the cheap, structured reads before reaching for verbose output:

- **`read_asset`** returns a folded `tree` + `cmp` table + counts, not raw YAML. Drill into a subtree with `component` / `path` + `field_limit` instead of re-reading the whole asset; the parsed model is session-cached (`_cache: "hit"`). `field_limit: 0` (default) returns field names only — bump it only for a `component` drill-down where you need values.
- **`manage_tools(action="list_groups")`** — sessions start `core`-only; activate only the group you need so the full 230-tool surface stays out of the prompt.
- **`read_console`** with `detail: "summary"` returns messages only; reserve `detail: "verbose"` for when you need Unity-internal stack frames.
- **`search_assets`** tags *why* each result matched, so you skip broad reads and go straight to the right drill-down.
- **Output profiles + paging.** The heavy tools (`read_asset`, `search_assets`, `scene_get_data`, `find_references`, `validate_edit`, `scan_paths`) take `profile: compact|balanced|full` (`compact` is the default — the cheap folded/counts shape). Raise to `balanced`/`full` to expand. Page large results with `page_size` + the `pagination.next_cursor` token (omit `page_size` to get the whole payload at once). For `find_references` / `validate_edit` / `scan_paths`, the compact default returns counts/groupings only — pass `profile: "balanced"` to get the per-asset / per-issue lists those tools previously returned by default. (The legacy `detail` / `max_results` / `max_nodes` caps remain as aliases.)
- **`capabilities`** before assuming tool names/schemas/route policy — cheaper than discovering a tool's real signature by trial and error.
- **Prefer typed tools** (`component_modify`, `scene_get_data`, …) over `execute_csharp` / `invoke_method` — explicit schemas, smaller request/response envelopes, same gate.

### Checkpoint → mutate → delta (large refactors)

`checkpoint_create` with scoped paths → run mutations (`gate: off` for bulk, or `enforce` per call) → `delta` against the checkpoint for a single verification pass.

- **Session-scoped.** Checkpoints live in an in-memory ring buffer (capacity 20, LRU-evicted on access recency) and are cleared on script recompile, domain reload, or editor restart — they are never persisted to disk. Capture immediately before mutating and delta right after; do not hold a checkpoint id across a recompile.
- **Missing checkpoint is non-blocking.** If `delta` (or `mutation_explain` with a `checkpoint_id`) references a checkpoint that is gone (evicted, or lost to a reload), the call returns success with `"unavailable": true` + `agentNextSteps` rather than an error. Treat it as "no baseline to delta against" and fall back to `validate_edit` / `scan_paths` on the relevant paths. It does not block the workflow.
- **Clear from the editor.** The Bridge window → Gate tab → Checkpoint history has a "Clear history" button (two-click confirm) that empties the session ring buffer. It touches nothing on disk and leaves gate-run history intact.

### find_references before delete

Before deleting or moving an asset, call `**find_references*`* to see who depends on it. Offline-first (no live bridge needed for text-serialized assets).

### dependencies: both directions in one call

When you need **both** directions (what this asset depends on AND what depends on it), or the broken-edge / cycle view, call `**dependencies*`*. It returns the forward + reverse edge sets plus the same `broken_dependency` GUIDs and `dependency_cycle` trails the `dependencies` verify rule computes — no second dependency graph is built (it reuses the verify scanner + the `find_references` reverse walker). Live bridge only (the scanners call AssetDatabase); pass `detail: "summary"` for counts only when you just need the magnitude.

### Return serialization (execute_csharp / invoke_method)

Results are walked by a depth-limited reflective serializer before becoming `mutation.output`:

- Structs/POCOs → JSON objects with public fields/props (`return new Vector3(1,2,3)` → `{"$type":"Vector3","x":1,"y":2,"z":3}`).
- Lists truncate to 100 items (`max_items` configurable); truncated arrays report `{"items":[...],"truncated":N}`.
- Recursion caps at depth 4 (`max_depth` configurable).
- Cycles / `UnityEngine.Object` refs never infinite-loop — back-edges become `{"$ref":"TypeName"}`, Unity objects become `{"$type":...,"name":...,"instanceId":...}`.

## Read-only tools (no gate)

`capabilities` · `manage_tools` (per-session tool-group visibility) · `list_rules` (filter by `asset_kind`/`extension` before `scan_paths`) · `ping` · `find_members` · `validate_edit` (scoped health scan, pre-commit; `include_rules`/`exclude_rules` filter) · `find_references` · `dependencies` (forward + reverse edges, plus broken/cycle view; live-only) · `scan_paths` (`fail_on_severity` defaults to `verify.severityThreshold`; override per call) · `read_asset` · `search_assets` · `list_assets` · `checkpoint_create` · `delta`.

## Optional: project-specific skill

Call `**unity_open_mcp_generate_skill**` with `{ "write": true }` to generate a project-specific SKILL.md reflecting the actual project — Unity version, installed packages, available verify rules, key MonoBehaviour/ScriptableObject types. The `clients` parameter (`cursor`/`claude`/`opencode`/`agents`) writes to the project-relative skill folder(s) declared in `skills/client-paths.json`. Regenerate after package or script changes.

For routing details, see the `routing` object on the capabilities response — not this file.

---

## Agent checklist

**Before mutating**

- [ ] Capabilities refreshed
- [ ] Unity state classified (lock read, PID/heartbeat checked)
- [ ] `paths_hint` prepared
- [ ] (Large refactor) `impact_preview` / `gate_budget_estimate` run

**After mutating**

- [ ] Gate delta reviewed
- [ ] Fixes applied / retried as needed
- [ ] Compile verified (`read_console` `type: "error"`, or `read_compile_errors` if bridge down)
- [ ] Tests run on affected assembly (one run at a time)