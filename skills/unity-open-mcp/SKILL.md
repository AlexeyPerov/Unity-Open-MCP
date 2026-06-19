# Unity Open MCP

Skill for AI agents driving a Unity project through the `unity-open-mcp` MCP server. Covers the gate + verify workflow, capabilities-first discovery, and the agent senses.

> Tool prefixes: `unity_open_mcp_*` (bridge-routed) and `unity_senses_*` (standalone senses). The prefix signals routing — see below.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- If you only need offline reads (asset search, find_references, read_asset), the bridge is optional — those routes parse the project from disk.

## Live bridge discovery (when a tool returns `bridge_offline`)

The bridge port is **per-project** — `20000 + (sha256(projectPath) % 10000)` — NOT a fixed 19120. Two projects = two ports, zero config. The MCP server resolves it at startup, but if a tool returns `bridge_offline` do NOT assume Unity isn't running — verify first.

**Step 1 — read the instance lock.** The bridge writes `~/.unity-open-mcp/instances/<sha256(projectPath)>.json` (lowercase hex sha256 of the absolute project path, forward slashes, no trailing slash). It carries: `pid`, `port`, `authToken`, `projectPath`, `state` (`idle` / `compiling` / `reloading`), `heartbeatAt` (ISO-8601 UTC, refreshed every 0.5s), `isCompiling`, `unityVersion`. To list every bridge the machine knows about:

```bash
cat ~/.unity-open-mcp/instances/*.json
```

**Step 2 — decide from the lock state:**

1. **Lock missing, or `pid` not alive** → Unity isn't running with the bridge. Open the project in Unity (the bridge auto-starts), or use the batch fallback (see below). Verify pid-aliveness with `kill -0 <pid>` (exit 0 = alive).
2. **`pid` alive, `state: "reloading"`, heartbeat stale (>10s old) but pid still alive** → the bridge assembly itself failed to compile (Unity is stuck mid-reload). Use `unity_open_mcp_read_compile_errors` to read `Editor.log` offline — it works even when the bridge is dead.
3. **`pid` alive and heartbeat fresh** → Unity IS running but the MCP server aimed at the wrong port. Either restart the MCP server (it re-runs discovery), or set `UNITY_OPEN_MCP_BRIDGE_PORT` to the lock's `port` value and restart.

**Auth.** The lock's `authToken` is sent as `Authorization: Bearer <token>`. The MCP server does this automatically; if you're debugging a 401 from a hand-rolled request, that token is the source.

**Batch fallback.** `compile_check` is always batch (it spawns a fresh headless Unity to surface broken builds, even when the live bridge is up). Any other tool falls back to batch only when the live bridge is down. The MCP server auto-discovers Unity from the OS-default Hub install paths (macOS `/Applications/Unity/Hub/Editor`, Windows `C:\Program Files\Unity\Hub\Editor`, Linux `~/Unity/Hub/Editor`) plus the `UNITY_HUB` env override — so on a machine with Unity installed you usually don't need to set anything. When several versions are installed, discovery picks the newest unless the instance lock records a `unityVersion`, in which case it matches that minor line; set `UNITY_PATH` to force a specific editor executable (highest priority). For the project path, `UNITY_PROJECT_PATH` is used if set, else the lock's `projectPath` (so batch works with zero env vars once the bridge has run the project once). Explicit env vars:

- `UNITY_PATH` — **optional**. Override the auto-discovered Unity executable: macOS `/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity`, Windows `C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe`, Linux `~/Unity/Hub/Editor/<version>/Unity`.
- `UNITY_PROJECT_PATH` — absolute path to the project root (the folder containing `Assets/`). Optional when a bridge instance lock exists.
- `UNITY_HUB` — **optional**. Override the Hub install root scanned by discovery (use when Unity is installed outside the OS-default path).

If Unity can't be found at all (not installed, or installed somewhere discovery doesn't look), batch tools return `unity_not_discovered` listing the scanned paths; the only tools that still work are the offline reads (`list_assets`, `find_references`, `read_asset`, `search_assets`) and `read_compile_errors`.

## Discover first

Call **`unity_open_mcp_capabilities`** (no args) before guessing which tools, verify rules, or fixes exist. It returns, in one local call:

- every tool with its **input schema**, **route policy** (`live` / `offline` / `offline-first` / `compressible`), **category**, and a `batchCapable` flag;
- every verify rule with its issue codes, severities, and fix ids;
- every available fix;
- a top-level **`routing`** summary (batch requirements, blocked meta-tools, live-only categories);
- planned-but-unbuilt items with `status: "planned"` and a fallback hint — they tell you what to use *today* instead of failing.

Make this your first step on a fresh project. Re-call it if tool/rule behavior seems to have changed.

## Routing (brief)

The per-tool `routePolicy` and `batchCapable` fields on the capabilities response are authoritative. In short:

- **Live is the default.** When the bridge is connected, most tools route to `POST /tools/{name}` on the Editor.
- **Batch fallback** spawns a headless Unity (`-batchmode`) **only** when a tool has `batchCapable: true` AND the live bridge is unavailable. Batch requires `UNITY_PATH` + `UNITY_PROJECT_PATH` env vars. Mutating meta-tools (`execute_csharp`, `invoke_method`, `execute_menu`) are blocked in batch — they need a live Editor.
- **Agent senses are live-only.** `unity_senses_run_tests`, screenshots, profiler, console, and spatial queries have no batch form — they need a running Editor.
- **Offline reads** (`list_assets`, `find_references`, `read_asset`, `search_assets`) parse the project from disk and never need Unity.
- **`compile_check` is always batch.** It spawns a fresh headless Unity that recompiles from scratch, even when the live bridge is up — running it against an Editor that already compiled would never surface a broken build. Use it to self-diagnose compile state.

Full route-policy and batch tables live in `docs/api/mcp-tools.md` (human/contributor docs). Do not copy them here — read them from `routing` + per-tool fields on the capabilities response.

## Core loop: mutate → gate → fix

1. **Discover** — `unity_open_mcp_capabilities`, then `unity_open_mcp_find_members` before blind reflection.
2. **Declare scope** — pass `paths_hint` with every asset path you intend to touch. Empty `paths_hint` fails with `paths_hint_required`.
3. **Mutate** — `unity_open_mcp_execute_csharp` / `invoke_method` / `execute_menu` with default `gate: enforce`.
4. **Read the gate** — on `isError: true`, inspect `gate.delta.newIssues` and `agentNextSteps`.
5. **Fix** — address the top error; `unity_open_mcp_apply_fix` with `dry_run: true` first when a `fixId` is present.
6. **Retry** — re-run the mutation; confirm `gate.delta.resolvedErrors > 0` or `newErrors == 0`.

**Principle: mutation success ≠ project safe.** A successful C# compile can still break prefab references. The gate is the safety net.

### Lifecycle & scene safety

Every tool declares a **lifecycle policy** (surfaced in the mutation envelope as `lifecycle` and in the bridge window Tools tab) so you know which ops are cheap, which settle, and which survive a domain reload:

| Policy | Meaning | Tools |
|---|---|---|
| `none` | Read-only, returns immediately. | `ping`, `find_members`, `validate_edit`, `checkpoint_create`, `delta`, `find_references`, `scan_paths`, `read_asset`, `search_assets`, `list_assets`, `editor_status`, `read_console`, `screenshot`, `profiler_*`, `spatial_query` |
| `editor_settle` | Mutating; bridge waits for asset refresh/serialization to finish before returning (`settleMs` in the envelope). | `apply_fix`, `reserialize` |
| `restart_then_settle` | Mutating; may trigger a domain reload. The bridge blocks until the editor finishes compiling (cap 60s) so you never observe a half-compiled state. The HTTP listener survives the reload, so `/ping` after the call reflects the post-reload state automatically. | `execute_csharp`, `invoke_method`, `execute_menu`, `compile_check` |
| `custom_confirmation` | Async; returns immediately and the result arrives via an external completion signal you poll. | `run_tests` (file-handoff poll on the MCP server) |

**Active-scene dirty guard.** Before any `restart_then_settle` op, the bridge preflights the loaded scenes. If any scene has unsaved changes, the call is refused with `error.code = "scene_dirty"`, a `dirtyScenes[]` list, and `agentNextSteps` — so Unity's native save modal never interrupts the flow. Recover by:

- saving the scene first (`unity_open_mcp_scene_save`, or `unity_open_mcp_execute_csharp` with `EditorSceneManager.SaveScene(...)`), or
- discarding (`EditorSceneManager.RestoreSavedSceneState()`), or
- passing `ignore_scene_dirty: true` on `execute_csharp` / `invoke_method` / `execute_menu` / `scene_open` to proceed and accept the risk of a native save prompt.

`apply_fix`, `reserialize`, and the non-`scene_open` scene mutators (`scene_create` / `scene_save` / `scene_unload` / `scene_set_active` / `scene_focus`) are **not** guarded — they never trigger the native save modal.

**Power-tool deny heuristic.** `execute_csharp` and `execute_menu` are blocked from destructive patterns by default (`EditorApplication.Exit`, `Application.Quit`, `AssetDatabase.DeleteAsset`, `BuildPipeline.BuildPlayer`, `File/Quit`, etc.). A refused call returns `error.code = "denied_by_policy"` (csharp) or `"menu_blocked"` (menu) with the matched pattern and an alternative. If you genuinely need one of these ops, set **both** `gate: "off"` and `confirm_bypass: true` on the request — the bypass is audited. Prefer the scoped typed tools (`apply_fix`, `reserialize`, `invoke_method`) over raw snippets for destructive work.

### Gate modes

| Mode | When |
|---|---|
| `enforce` (default) | Normal edits — fail fast on new errors (`isError: true`) |
| `warn` | Exploratory — read `gate.delta` but the call does not error |
| `off` | Trusted admin scripts only — no checkpoint/validate |

### Gate failure (canonical shape)

```json
{
  "mutation": { "success": true, "output": "Player(Clone)", "error": null },
  "gate": {
    "mode": "enforce",
    "validation": {
      "passed": false,
      "issues": [
        {
          "severity": "Error",
          "code": "MISSING_SCRIPT",
          "assetPath": "Assets/Prefabs/Player.prefab",
          "fixId": "remove_missing_script",
          "fixSafe": true,
          "agentHint": "Remove the missing script component"
        }
      ]
    },
    "delta": {
      "newErrors": 1, "newWarnings": 0,
      "resolvedErrors": 0, "resolvedWarnings": 0,
      "newIssues": ["missing_references|Error|Assets/Prefabs/Player.prefab|MISSING_SCRIPT"],
      "resolvedIssues": []
    }
  },
  "agentNextSteps": [
    "New error: missing_references MISSING_SCRIPT on Assets/Prefabs/Player.prefab",
    "Fix available: use unity_open_mcp_apply_fix with fix_id=\"remove_missing_script\""
  ]
}
```

On success the same envelope returns `validation.passed: true`, empty `issues`, and `agentNextSteps: []`.

### Verify rules and issue codes

The capabilities response is authoritative (call `unity_open_mcp_capabilities` for the live list). The implemented rules and their issue codes:

- **`missing_references`** — per-PPtr-field view. Codes: `missing_guid` (Error, fix `relink_broken_guid` — unsafe, needs `target_guid`), `missing_fileid` (Error), `missing_script` (Error, fix `remove_missing_script`), `missing_local_fileid` (Warning), `empty_local_ref` (Warning), `missing_method` / `type_mismatch` / `duplicate_component` / `invalid_layer` (Warning, full-scan only).
- **`scene_prefab_health`** — structural health. Codes: `broken_reference` (Error), `high_risk_bootstrap`, `scene_object_count`, `component_hotspot`, `inactive_expensive`, `inactive_heavy`, `deep_nesting`, `override_explosion` (Warning).
- **`dependencies`** — forward-graph view of what each scoped asset depends on. Codes: `broken_dependency` (Error — an asset-graph edge to a missing asset; fix `relink_broken_guid` — unsafe, needs `target_guid`; complements `missing_references` which scans PPtr fields), `dependency_cycle` (Warning — the scoped asset participates in a forward cycle).

### Fixes

`unity_open_mcp_apply_fix` always defaults to `dry_run: true` (the dry-run short-circuits the gate entirely — it returns the fix description / candidates without checkpoint+validate). Implemented fixes:

- **`remove_missing_script`** (safe) — strips `MonoBehaviour` components whose script GUID no longer resolves. Works on `.prefab` and `.unity`.
- **`relink_broken_guid`** (unsafe) — rewrites a broken external GUID reference to a chosen replacement. Dry-run advertises candidate targets (matched by name/type heuristics); apply requires `target_guid` (the chosen replacement). Never auto-applied — wrong choices silently rewire the asset graph.

Planned fixes (no rule emits them yet; capabilities advertises them with guidance): `remove_orphan_meta`, `fix_duplicate_guid`, `reassign_missing_texture`, `reassign_missing_shader`.

If `fix_id` is omitted, the response lists every fix that can resolve the given `issue_id` — use it when capabilities shows more than one applicable fix.

Issue keys in `gate.delta.newIssues` are `ruleId|severity|assetPath|issueCode` (severity is `ERROR` / `WARN`).

## Key workflows

### Reserialize after direct YAML edits

When you edit a `.prefab` / `.unity` / `.asset` / `.mat` / `.controller` / `.anim` file directly as YAML text, run **`unity_open_mcp_reserialize`** with the touched `paths` (the `paths` array doubles as the gate scope). The round-trip rewrites the file canonically so missing fields, wrong indentation, and stale `fileID` references surface in `gate.delta`. Supported extensions: `.prefab`, `.unity`, `.asset`, `.mat`, `.controller`, `.anim`. Whole-project reserialize is intentionally unsupported — enumerate the assets you edited.

By default the round-trip targets **asset YAML only** — the companion `.meta` is left untouched, so a body-only edit produces no `.meta` diff. Pass `include_meta: true` only when you intentionally round-trip importer metadata (upgrade / importer-change workflows).

**Principle: edit freely, but always reserialize before trusting a direct YAML change.**

### read_asset: map, not dump

Raw Unity YAML is enormous. `unity_open_mcp_read_asset` returns counts, a `cmp` table that declares repeated component sets once (referenced by `c1`/`c2` codes), and a folded `tree`. Drill down with `field_limit` + `component` / `path` / `detail=verbose` instead of re-reading raw YAML. The session cache reuses the parsed model (`_cache: "hit"`). `detail: verbose` disables render-only folding; `field_limit: 0` (default) returns names only — bump it before `component` drill-down so fields are available.

Use **`unity_open_mcp_search_assets`** to locate prefabs/components/GUIDs; each result tags *why* it matched so you know which `read_asset` drill-down to run next.

### checkpoint → mutate → delta

For large refactors: `unity_open_mcp_checkpoint_create` with scoped paths → run mutations (`gate: off` for bulk, or `enforce` per call) → `unity_open_mcp_delta` against the checkpoint for a single verification pass.

### find_references before delete

Before deleting or moving an asset, call **`unity_open_mcp_find_references`** to see who depends on it. Offline-first (no live bridge needed for text-serialized assets).

### Diagnose a broken build (read_compile_errors / compile_check)

There are two distinct failure shapes, with different recovery paths:

**Bridge offline after a C# edit — `bridge_compile_failed`.** If a tool call returns an error with `code: "bridge_compile_failed"`, the bridge assembly itself failed to recompile (the edit was in `packages/bridge/` or a dependency), so the live Editor is stuck and every live tool refuses — including `unity_senses_read_console`. In this state `compile_check` does **not** work either (its batch entry point lives in the same broken assembly, and Unity's per-project lock blocks a second instance). The one channel that survives is the live Editor's `Editor.log`, which Unity writes regardless of bridge health.

- Call **`unity_open_mcp_read_compile_errors`** — it reads the tail of Unity's `Editor.log` directly (offline, no bridge, no Unity spawn) and returns structured compiler errors (`status`, `errorCount`, `errors[]` with `code`/`file`/`line`/`message`).
- Read `errors[].file`/`line`/`code` (e.g. `Assets/.../Foo.cs` line 75, `CS1061`) to locate the break, fix it, then trigger a recompile (a no-op edit + focus Unity, or `compile_check` once the source compiles). Once the bridge assembly recompiles, the listener reloads and live tools return.

**Project compiles but you want a clean-from-scratch check — `compile_check`.** When the live bridge is up but you want to verify the project compiles independently of the current Editor state, call **`unity_open_mcp_compile_check`**. It spawns a fresh headless Unity, recompiles from scratch, and returns structured compiler errors. It always routes to batch, so it works even when the live bridge reports compiling. Use it for a deliberate "does this build clean?" check, **not** as the recovery path for a broken bridge assembly.

## Agent senses (live-only)

These give you direct project feedback and are **live-only** (no batch fallback):

- **`unity_senses_run_tests`** — EditMode + PlayMode test runner with per-test pass/fail. Filter by assembly / namespace / class / method. Use this to verify your changes — e.g. after a C# edit, run the affected assembly's EditMode tests. PlayMode is domain-reload-safe via a file handoff. Set `include_passes: false` on large suites to avoid truncation.
- **`unity_senses_read_console`** — Unity console entries via reflection. Filter `type: "error"` to confirm a clean compile after edits. Use `detail: "summary"` for messages only (no stack traces) to save tokens; `detail: "verbose"` includes Unity-internal frames.
- **`unity_senses_screenshot`** — Scene / Game / isolated 2×2 composite of one GameObject.
- **`unity_senses_profiler_capture`** / **`profiler_memory`** / **`profiler_rendering`** — frame hierarchy, memory allocators, rendering env.
- **`unity_senses_spatial_query`** — physics-based raycast / overlap / bounds / ground_check / nearest against the live scene.
- **`unity_senses_pull_events`** — incremental console logs + editor-state transitions (compile start/stop, play-mode). Cheaper than `read_console` for a "what happened since my last call" check; the first call opens the stream, later calls return only new events. Returns `bridge_unavailable` when the bridge is down.

**Verification habit:** after any C# change, run `unity_senses_read_console` with `type: "error"` (or `unity_senses_run_tests` on the affected assembly) to confirm the change compiled and tests pass before declaring done.

## Mutating tools (gate-aware)

All accept `gate` (`enforce` / `warn` / `off`, default `enforce`) and require a non-empty `paths_hint`:

- `unity_open_mcp_execute_csharp` — compile + run a C# snippet.
- `unity_open_mcp_invoke_method` — call a method via reflection.
- `unity_open_mcp_execute_menu` — run a Unity Editor menu item.
- `unity_open_mcp_apply_fix` — apply a verify rule fix (e.g. `remove_missing_script`, `relink_broken_guid`).
- `unity_open_mcp_reserialize` — round-trip text assets through Unity's serializer.

**Typed project & asset tools (M16 Plan 1).** Prefer these over `execute_csharp` for routine asset/material/prefab workflows — they have explicit schemas, run the same gate envelope, and surface structured results:

- Asset CRUD: `unity_open_mcp_assets_create_folder` / `assets_copy` / `assets_move` / `assets_delete` / `assets_refresh` (`assets_refresh` may bind whole-project scope via `whole_project: true`).
- Material helpers: `unity_open_mcp_material_create` / `material_get_properties` / `material_set_property` (typed by `type: color|float|int|vector|texture`) / `material_get_keywords` / `material_set_keyword` / `material_set_shader`.
- Shader reads (gate-free): `unity_open_mcp_shader_list_all` / `shader_get_data` (folds compile errors into `errors[]`).
- Prefab lifecycle: `prefab_instantiate` / `prefab_create` / `prefab_open` / `prefab_close` / `prefab_save` / `prefab_apply` / `prefab_revert` / `prefab_unpack`. Read-only: `prefab_get_overrides` / `prefab_status`. Address scene instances by `instance_id` > `path` > `name` (same model as `spatial_query`).

Materials resolve by `asset_path` (.mat) or `instance_id` of a scene GameObject whose Renderer.sharedMaterial is read (or the Material instance directly). Use `shader_list_all` to discover valid shader names before `material_create` / `material_set_shader`.

**Typed GameObject & component tools (M16 Plan 2).** Prefer these over `execute_csharp` for hierarchy/component workflows. Address targets by `instance_id` > `path` > `name` (same model as `spatial_query` / prefab tools). GameObjects are scene-backed, so `paths_hint` for every mutating tool here is the active scene path. Every mutator is undo-recorded.

- GameObject lifecycle: `gameobject_create` / `gameobject_destroy` / `gameobject_duplicate` / `gameobject_modify` (name/tag/layer/active + transform — note: target name is `name_target` so `name` stays free for the new value) / `gameobject_set_parent` (cycle-safe). `paths_hint` = scene path containing the target.
- Component lifecycle: `component_add` (by `component_types[]`, full name preferred) / `component_destroy` / `component_modify` (per-path serialized patches via `fields: [{path, value, type?}]`). `paths_hint` = scene path containing the host.
- Read-only: `gameobject_find` (targeted lookup or list mode with `name_contains`/`tag`/`component`/`root_only` filters) / `component_get` (serialized fields + public properties) / `component_list_all` (attachable types catalog from loaded assemblies). Gate-free.

Resolve components by `component_instance_id` (specific instance) or `type_name` (full name preferred, class-name fallback). Use `component_list_all` to discover attachable types before `component_add`; use `component_get` to discover serialized paths before `component_modify`.

**Typed scene tools (M16 Plan 3).** Prefer these over `execute_csharp` for scene workflows. `paths_hint` for the mutating tools is the scene asset path (or scene hierarchy path for `scene_focus`).

- Scene lifecycle: `scene_create` (`.unity` path, `setup: empty|default`, `mode: single|additive`) / `scene_open` (Single/Additive — Single mode is `restart_then_settle`, so the dirty guard preflights it; pass `ignore_scene_dirty: true` to skip) / `scene_save` (active scene when `name` omitted; idempotent on a clean scene) / `scene_unload` (refuses the last opened scene) / `scene_set_active` (target must already be opened).
- Read-only: `scene_list_opened` (shallow snapshot of every opened scene + active-scene pointer) / `scene_get_data` (compact hierarchy with `detail: summary|normal|verbose`, `depth`, `max_nodes` — supersedes the M10 scene snapshot; reflects unsaved editor state, unlike `read_asset` on the `.unity`) / `scene_get_dirty_summary` (per-scene dirty tally). Gate-free.
- `scene_focus` — frame a GameObject in the SceneView (target by `instance_id` > `path` > `name`); optional `axis: top|bottom|front|back|left|right`.

Workflow: `scene_list_opened` → `scene_get_data` to see what's there → mutate via GameObject/component tools → `scene_get_dirty_summary` to confirm → `scene_save`. Before opening a new scene in Single mode, check `scene_get_dirty_summary` and save first to avoid the dirty-scene refusal.

**Typed Package Manager tools (M16 Plan 4).** Prefer these over `execute_csharp` for UPM workflows. `paths_hint` for the mutating tools is `["Packages/manifest.json"]` (packages-lock.json is touched implicitly — do not list it separately).

- Mutating (gate-aware, `restart_then_settle` — UPM resolution can domain-reload; the dirty guard preflights them, pass `ignore_scene_dirty: true` to opt out): `package_add` (registry id / `name@version` / Git URL / `file:../path` / `.tgz`) / `package_remove` (by name; trailing `@version` stripped; refuses packages not installed or depended-on by others).
- Read-only (gate-free): `package_list` (filter by `source` / `name_filter` / `direct_dependencies_only`; `include_indirect`; `offline: true` default) / `package_search` (substring over name/displayName/description; `offline: false` hits live registry for exact matches) / `package_get_info` (one package by name/id/displayName; falls back to registry when not installed and `offline: false`) / `package_get_dependencies` (top-level manifest entries parsed directly, no UPM request) / `package_check` (presence + pinned reference for one id, reads manifest directly).

Workflow: `package_check` (fast manifest hit) → `package_search` if not installed → `package_add` with `paths_hint: ["Packages/manifest.json"]` → after the post-add settle, `package_get_info` to confirm the resolved version. `package_get_dependencies` is the fastest manifest snapshot (no UPM round-trip).

**Typed console / editor state / selection / undo / tags / layers tools (M16 Plan 5).** These complement existing reads — `unity_senses_read_console` (console read) and `unity_open_mcp_editor_status` (state read) — without duplicating them. Most mutate editor state but write NO assets, so the gate (asset-reference validation) has nothing to validate: they are gate-free direct-response tools that return JSON without the gate envelope.

- Console: `console_clear` (clear the console — folds `read_console`'s `clear: true`) / `console_log` (`level: log|warning|error`, optional `context_instance_id` / `context_asset_path` to attach a ping target).
- Editor state: `editor_set_state` (`state: play|pause|stop`) — writes no assets (gate-free) but runs the active-scene dirty guard inline, so entering play mode with a dirty scene refuses with `scene_dirty`; pass `ignore_scene_dirty: true` to accept. Poll `editor_status` after to confirm the transition settled.
- Selection: `selection_get` (active object + full `selection[]`, with `isAsset` / `assetPath` / scene `path`) / `selection_set` (single target by `instance_id` / `asset_path` / `path` / `name`, or `targets[]` for multi-selection; `clear: true` to clear; returns `target_not_found` when nothing resolved).
- Undo / redo: `editor_undo` / `editor_redo` (`steps`, default 1) — surface the post-op active selection.
- Tags / layers reads (gate-free): `editor_get_tags` / `editor_get_layers` (layers include slot indices for `gameobject_modify`).
- Tags / layers mutators (gate-aware, `paths_hint = ["ProjectSettings/TagManager.asset"]`): `editor_add_tag` (idempotent; refuses reserved built-in names) / `editor_add_layer` (first empty slot 8–31 by default, or explicit `slot` 8–31; refuses reserved names + occupied slots).

Workflow: `editor_get_tags` / `editor_get_layers` to discover valid names → `gameobject_modify` to apply → `editor_add_tag` / `editor_add_layer` to create new ones. For "see what the human clicked": `selection_get` → mutate via typed tools → `selection_set` (pairs with `scene_focus`).

**Typed reflection / scripts / object data tools (M16 Plan 6).** These complement the core reflection tools — `find_members`, `invoke_method`, `execute_csharp` — without duplicating them. Read-only members are gate-free direct-response tools; mutating members run the full gate path with `paths_hint`.

- Type schema: `unity_open_mcp_type_schema` (read-only) — structured member schema for one loadable C# type (fields/properties on by default; methods/constructors optional; enum values for enums). Use to plan `invoke_method` / `object_modify` without trial-and-error.
- Script files: `unity_open_mcp_script_read` (read-only, line slicing) / `unity_open_mcp_script_write` (Roslyn pre-validated create/overwrite; `validate: true` default refuses to write code that doesn't compile, returning `validation_failed`) / `unity_open_mcp_script_delete` (mutating; removes `.cs` + `.meta`). Paths must be project-relative, end in `.cs`, and live under the project root. After write/delete a recompile may follow — poll `editor_status` / `compile_check`.
- Object data: `unity_open_mcp_object_get_data` (read-only, reflective walk over any live `UnityEngine.Object`) / `unity_open_mcp_object_modify` (mutating, sets public fields/properties by name; safe by default — refuses static/init-only members unless `allow_static: true`, never invokes methods). Prefer `component_get` / `component_modify` for one Component's Inspector fields (they use SerializedObject); use these for ScriptableObjects, Materials, or any non-Component Object.
- Core reflection enhanced in place: `find_members` now lists every overload separately with structured fields (`returnType`, `parameters[]`, `isStatic`, `isGeneric`, `genericParameters[]` for methods; `propertyType`, `canRead`, `canWrite` for properties); pass `include_signatures: false` for a names-only payload. `invoke_method` accepts `generic_arg_types` (call generic methods like `GetComponent<Rigidbody>`) and `arg_type_names` (disambiguate overloads).

Workflow: `find_members` → `type_schema` for the one type → `invoke_method` (with `generic_arg_types` / `arg_type_names` for generic/overloaded calls). For authoring: `script_read` → edit → `script_write` → `compile_check` / `read_compile_errors`.

**Typed profiler session / diagnostics tools (M16 Plan 7).** These complement the M10 senses (`unity_senses_profiler_capture` / `profiler_memory` / `profiler_rendering`) — they are the runtime/session layer (enabled flag, modules, config knobs, buffered-frames clear, snapshot save/load, script timing), NOT a second per-frame hierarchy read. Most mutate editor state but write NO assets, so they are gate-free direct-response tools; only `profiler_save_data` writes a `.json` snapshot and runs the gate (paths_hint scoped to the destination `.json`).

- Session: `profiler_start` (enable runtime + open window; `open_window: false` skips the menu) / `profiler_stop` (disable runtime). Idempotent.
- Status / config reads (gate-free): `profiler_get_status` (enabled / supported / max-used-memory / active module bookkeeping) / `profiler_get_config` (driverEnabled, profileEditor, deepProfile, allocationCallstacks, binaryLog, logFile, maxUsedMemory, available+enabled categories — version-gated knobs return false / empty with a warning). `profiler_get_script_stats` is a single-frame Time + Mono/GC snapshot.
- Config mutator (gate-free): `profiler_set_config` (mode `play`/`edit`, `deep_profile`, `allocation_callstacks`, `binary_log`, `output`, `max_used_memory`, `enable_categories[]` / `disable_categories[]`). Non-applicable knobs are recorded + surfaced as warnings, not silently dropped.
- Module bookkeeping (gate-free): `profiler_list_modules` (canonical CPU/GPU/Memory/.../VirtualTexturing list) / `profiler_enable_module` (toggle a name in the local set — Unity's runtime API does not expose per-module control; use the Profiler window for actual visibility).
- Buffered frames: `profiler_clear_data` (ProfilerDriver.ClearAllFrames — destructive; save first if needed).
- Snapshots: `profiler_save_data` (mutating, gate-aware; writes status + rendering + script to a `.json` under the project root) / `profiler_load_data` (read-only; raw JSON + optional `add_to_profiler` for raw-binary captures).

Workflow: `profiler_start` → poll `profiler_get_status` to confirm recording → run the workload → `unity_senses_profiler_capture` for frame data → `profiler_save_data` to persist → `profiler_stop`. The M10 senses stay the per-frame reads; this surface is the runtime/session layer.

### Return serialization (execute_csharp / invoke_method)

Results are walked by a depth-limited reflective serializer before becoming `mutation.output`:

- Structs/POCOs → JSON objects with public fields/props (`return new Vector3(1,2,3)` → `{"$type":"Vector3","x":1,"y":2,"z":3}`).
- Lists truncate to 100 items (configurable via `max_items`); truncated arrays report `{"items":[...],"truncated":N}`.
- Recursion caps at depth 4 (configurable via `max_depth`).
- Cycles / `UnityEngine.Object` refs never infinite-loop — back-edges become `{"$ref":"TypeName"}`, Unity objects become `{"$type":...,"name":...,"instanceId":...}`.

## Read-only tools (no gate)

- `unity_open_mcp_capabilities` — discover the surface (call first).
- `unity_open_mcp_list_rules` — list every verify rule (implemented + planned) with applicable asset kinds, default severity, and available fixes. Filter by `asset_kind` / `extension` before calling `scan_paths` so you know which rules apply.
- `unity_open_mcp_ping` — bridge health.
- `unity_open_mcp_find_members` — types, methods, properties.
- `unity_open_mcp_validate_edit` — scoped health scan, no mutation (pre-commit check). Accepts `include_rules` / `exclude_rules` to filter the auto-selected rule set; each issue carries `ruleId`/`categoryId`, `severity`, `code`/`issueCode`, `assetPath`, `description`, `fixId`/`fixSafe`.
- `unity_open_mcp_find_references` — reverse dependency lookup (offline-first).
- `unity_open_mcp_scan_paths` — run specific verify rules over scoped paths. `fail_on_severity` defaults to the project setting `verify.severityThreshold` in `.unity-open-mcp/settings.json`; override per call. Accepts `include_rules` / `exclude_rules` (exclude always wins). The response echoes the resolved `failOnSeverity` and the post-filter `rulesApplied`.
- `unity_open_mcp_read_asset` — compact drill-down asset read.
- `unity_open_mcp_search_assets` — compact asset search.
- `unity_open_mcp_list_assets` — offline asset listing.
- `unity_open_mcp_checkpoint_create` / `unity_open_mcp_delta` — manual checkpoint + delta.

## Project-specific skill (optional)

Call **`unity_open_mcp_generate_skill`** with `{ "write": true }` to generate a project-specific SKILL.md reflecting the actual project — Unity version, installed packages, available verify rules, and key MonoBehaviour/ScriptableObject types from source. The `clients` parameter writes to the project-relative skill folder(s) declared in `skills/client-paths.json` (`cursor` / `claude` / `opencode` / `agents`). Regenerate after package or script changes.

For routing details, see the `routing` object on the capabilities response — not this file.
