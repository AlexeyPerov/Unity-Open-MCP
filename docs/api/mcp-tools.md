# MCP Tools

MCP tools are registered in `mcp-server/src/tools/index.ts` and exposed by the stdio server in `mcp-server/src/index.ts`.

> **CLI surface.** The same tool set is reachable from a thin CLI
> (`unity-open-mcp run-tool <name>`), which shares the routing layer with the
> stdio server. A CLI invocation returns the same JSON an MCP client would
> receive. See [Manual setup → CLI for CI / automation](../manual-setup.md#cli-for-ci--automation)
> for command shapes and examples.

## Quick lookup

| Question | Section |
|---|---|
| Which tool names are available? | Tool catalog |
| How do I discover everything at once? | Capability discovery |
| How route selection works? | Route policy |
| Which tools can run in batch? | Batch support |
| Which tools are offline-first? | Offline/compressible reads |

## Tool catalog

### Core tools (M2 + M2.5)

- `unity_open_mcp_ping`
- `unity_open_mcp_execute_csharp`
- `unity_open_mcp_invoke_method`
- `unity_open_mcp_execute_menu`
- `unity_open_mcp_find_members`
- `unity_open_mcp_compile_check`
- `unity_open_mcp_read_compile_errors`
- `unity_open_mcp_editor_status`

### Gate and validation tools (M3 + M5)

- `unity_open_mcp_validate_edit`
- `unity_open_mcp_checkpoint_create`
- `unity_open_mcp_delta`
- `unity_open_mcp_find_references`
- `unity_open_mcp_scan_paths`
- `unity_open_mcp_apply_fix`
- `unity_open_mcp_scan_all`
- `unity_open_mcp_baseline_create`
- `unity_open_mcp_regression_check`

### Asset intelligence tools (M9)

- `unity_open_mcp_reserialize`
- `unity_open_mcp_read_asset`
- `unity_open_mcp_search_assets`
- `unity_open_mcp_list_assets`

### Agent senses tools (M10)

- `unity_senses_run_tests` — EditMode + PlayMode test runner with per-test pass/fail, filter by assembly/namespace/class/method, domain-reload-safe PlayMode via file handoff.
- `unity_senses_screenshot` — Capture Scene view, Game view, or isolated 2×2 composite (Front/Right/Back/Top) of a single GameObject with layer culling. Returns saved PNG file path.
- `unity_senses_read_console` — Read Unity console entries via reflection on internal `LogEntries`. Filter by type (error/warning/log/all), user-code stack filter, optional clear, token-bounded output.
- `unity_senses_profiler_capture` — Read the Unity Profiler frame hierarchy via `ProfilerDriver.GetHierarchyFrameDataView`. Drill-down by parent ID / root name-substring / depth, multi-frame averaging, token-bounded top-N by self/total/calls.
- `unity_senses_profiler_memory` — Live memory allocator stats (allocated/reserved/unused/temp/managed heap) with optional GC first.
- `unity_senses_profiler_rendering` — Rendering environment batch: GPU/SystemInfo, active render pipeline, QualitySettings, screen resolution, target frame rate, Time stats.
- `unity_senses_spatial_query` — Physics-based spatial reasoning (raycast / overlap / bounds / ground_check / nearest) against the live scene. Targets addressed by instance_id/path/name; returns hit object instanceId/name/path.

### Capability discovery

- `unity_open_mcp_capabilities` — Returns the full capability surface in one call: every tool with its input schema and route policy, every verify rule with applicable asset kinds and issue severities, and every available fix. Each capability carries an `implemented` boolean; planned-but-unbuilt items return with `status: "planned"` and guidance instead of failing. Call this first to learn what is available.
- `unity_open_mcp_list_rules` — Purpose-built rule discovery. Lists every verify rule (implemented + planned) with applicable asset kinds/extensions, derived `defaultSeverity` (worst severity the rule can emit), flat `availableFixIds`, and issue codes. Filter by `asset_kind` / `extension` / `implemented_only`. Routes locally from the versioned rule catalog — never hits the live bridge or batch Unity. Use this before `scan_paths` / `validate_edit` to learn which rules apply to a given asset type without trial-and-error.
- `unity_open_mcp_generate_skill` — Generates a project-specific SKILL.md reflecting the actual project state: Unity version, installed packages (including bridge/verify versions), available tools and verify rules, key MonoBehaviour/ScriptableObject types discovered from source, and the mutate→gate→fix workflow. Set `write: true` to persist the file into `.claude/skills/`, `.cursor/skills/`, `.opencode/skills/`, or `.agents/skills/`. Regenerate after package or script changes.

### Streaming & event pull (M13)

- `unity_senses_pull_events` — Drains incremental bridge events (console logs + editor-state transitions) since the previous call. The first call opens a server-side SSE subscription to the bridge's `GET /events` stream; later calls return only new events. Use this after `execute_csharp` / mutations to stream console output without polling `/ping` or re-reading the full console. Each event carries `seq`, `ts`, `type` (`log` | `editor_state`), and type-specific fields (`logType`/`message`/`stack` for logs, `state`/`isCompiling`/`isPlaying` for state). `dropped` reports events evicted from the queue before this pull; `connected` reports the SSE reader state. Live-only — returns `bridge_unavailable` when the bridge is down.

### Typed editor tools (M16)

M16 adds a curated typed surface on top of existing meta-tools. Duplicates are intentionally avoided:
- keep `unity_open_mcp_execute_csharp`, `unity_open_mcp_invoke_method`, `unity_open_mcp_find_members` as core
- keep M9 read/list/search/reserialize as the asset intelligence baseline
- keep M10 sense tools for screenshots/test run/profiler capture/memory/rendering/spatial

The full planned surface (~97 tools) is enumerated by `unity_open_mcp_capabilities` (each entry carries `status: "planned"` until implemented). The source of truth is `mcp-server/src/capabilities/build-capabilities.ts`, synchronized with the per-plan tables in `specs/execution/M16/execution-plan-*.md`. Planned categories:

- **Project & Asset Management:** typed asset CRUD (`assets_create_folder`, `assets_copy`, `assets_move`, `assets_delete`, `assets_refresh`), material helpers (`material_create`, `material_get/set_property`, `material_get/set_keywords`, `material_set_shader`), shader reads (`shader_list_all`, `shader_get_data`), and prefab lifecycle (`prefab_instantiate/create/open/close/save` + `prefab_apply/revert/unpack/get_overrides/status`) — **implemented in Plan 1**
- **GameObject & Components:** typed hierarchy/component lifecycle (`gameobject_create/destroy/duplicate/find/modify/set_parent`, `component_add/destroy/get/modify/list_all`) — **implemented in Plan 2**
- **Scene Management:** typed scene lifecycle/data (`scene_create/open/save/unload/set_active/list_opened`, `scene_get_data`, `scene_get_dirty_summary`, `scene_focus`) — **implemented in Plan 3**
- **Package Manager:** `package_list`, `package_search`, `package_add`, `package_remove`, `package_get_info`, `package_get_dependencies`, `package_check` — **implemented in Plan 4**
- **Console + Editor state/selection/tags/layers/undo:** `console_clear`, `console_log`, `editor_set_state`, `selection_get`, `selection_set`, `editor_undo`, `editor_redo`, `editor_get_tags`, `editor_get_layers`, `editor_add_tag`, `editor_add_layer`
- **Reflection/scripts/object data:** `type_schema`, `script_read`, `script_write`, `script_delete`, `object_get_data`, `object_modify`
- **Profiler & Diagnostics session:** `profiler_start/stop/get_status`, `profiler_get/set_config`, `profiler_list_modules`, `profiler_enable_module`, `profiler_clear_data`, `profiler_save_data`, `profiler_load_data`, `profiler_get_script_stats` (non-duplicate with M10 capture/memory/rendering)
- **Gate intelligence:** `impact_preview`, `gate_budget_estimate`, `mutation_explain`
- **Project configuration & build:** build pipeline (`build_get_targets`, `build_get/set_target`, `build_get/set_scenes`, `build_start`, `build_get/set_defines`) and project settings (`settings_get/set_player/quality/physics/lighting`)

#### Project & Asset Management (M16 Plan 1 — implemented)

Mutating members run the full gate path with `paths_hint`; read-only members are gate-free (returned as direct JSON without the gate envelope). Asset/material/prefab mutators use `lifecycle: "editor_settle"` (wait for asset refresh); the read-only members use `lifecycle: "none"`.

Folder / filesystem asset operations:

- `unity_open_mcp_assets_create_folder` — Create folders under Assets/. `paths_hint` enumerates each new folder path.
- `unity_open_mcp_assets_copy` — Copy asset(s) (`{source, destination}` pairs). `paths_hint` = destinations.
- `unity_open_mcp_assets_move` — Move / rename asset(s). `paths_hint` should include BOTH source and destination paths so the gate can flag dangling references.
- `unity_open_mcp_assets_delete` — Delete asset(s). `paths_hint` = deleted paths. Prefer `unity_open_mcp_find_references` first.
- `unity_open_mcp_assets_refresh` — Refresh AssetDatabase. Light mutation; when `whole_project: true` (default), `paths_hint` may be omitted (refresh is whole-project by nature).

Material helpers (resolved by `asset_path` (.mat) or `instance_id` of a scene GameObject whose Renderer.sharedMaterial is read, or the Material instance directly):

- `unity_open_mcp_material_create` — Create a `.mat` at a path with a named shader (defaults to URP/Lit or Standard). `paths_hint` = new .mat path.
- `unity_open_mcp_material_get_properties` — List all shader properties with values. Read-only, gate-free, token-bounded by `max_results`.
- `unity_open_mcp_material_set_property` — Set one property; `type` infers value kind (color/float/int/vector/texture). Undo-recorded. `paths_hint` = .mat or scene path.
- `unity_open_mcp_material_get_keywords` — List enabled shader keywords. Read-only, gate-free.
- `unity_open_mcp_material_set_keyword` — Enable/disable a keyword. Undo-recorded. `paths_hint` = .mat or scene path.
- `unity_open_mcp_material_set_shader` — Swap shader (gate delta surfaces missing-property references). Undo-recorded. `paths_hint` = .mat or scene path.

Shader helpers (read-only, gate-free):

- `unity_open_mcp_shader_list_all` — List shader assets with name + asset path. Token-bounded by `max_results`.
- `unity_open_mcp_shader_get_data` — Read shader properties, attributes, subshader info, and compile errors (`errors[]` folds UCP `shader/errors`). Resolve by `asset_path` or `name`.

Prefab lifecycle (scene instances resolved by `instance_id` > `path` > `name`, matching `spatial_query`):

- `unity_open_mcp_prefab_instantiate` — Instantiate a prefab into the active scene. Returns the new instance's instanceId/name/path.
- `unity_open_mcp_prefab_create` — Create prefab (or variant) asset from a scene GameObject.
- `unity_open_mcp_prefab_open` / `prefab_close` / `prefab_save` — Prefab edit stage lifecycle. `close`/`save` are no-ops with a note when no stage is open.
- `unity_open_mcp_prefab_apply` / `prefab_revert` / `prefab_unpack` — Apply instance overrides to the asset / revert to the asset / unpack into a plain GameObject.
- `unity_open_mcp_prefab_get_overrides` — Read-only: list propertyModifications / addedComponents / removedComponents on an instance. Gate-free.
- `unity_open_mcp_prefab_status` — Read-only: report isPrefab/isInstance/isRoot/hasOverrides + source asset. Gate-free.

#### GameObject & Components (M16 Plan 2 — implemented)

Mutating members run the full gate path with `paths_hint`; read-only members are gate-free. GameObjects are scene-backed, so `paths_hint` for every mutating tool here is the active scene path (the GameObject is a scene side-effect). All mutating members are undo-recorded and mark the active scene dirty. Scene instances resolved by `instance_id` > `path` > `name`, matching `spatial_query`.

GameObject lifecycle:

- `unity_open_mcp_gameobject_create` — Create a new GameObject in the active scene (optionally parented and pre-positioned). Pass `primitive_type` (Cube/Sphere/Capsule/Cylinder/Plane/Quad) to spawn a primitive. `paths_hint` = destination scene path.
- `unity_open_mcp_gameobject_destroy` — Destroy a GameObject (and its children). `paths_hint` = scene path containing the target.
- `unity_open_mcp_gameobject_duplicate` — Duplicate a GameObject preserving parent + transform. `paths_hint` = scene path containing the source.
- `unity_open_mcp_gameobject_modify` — Update name / tag / layer / active / transform in one call. Only provided fields are touched. Note: target name is `name_target` so `name` stays free for the new value. `paths_hint` = scene path containing the target.
- `unity_open_mcp_gameobject_set_parent` — Reparent a GameObject; cycle-safe (refuses cycles). `paths_hint` = scene path containing the child.
- `unity_open_mcp_gameobject_find` — Read-only: targeted lookup (instance_id/path/name → single object) OR list mode (no target) with optional `name_contains` / `tag` / `component` / `root_only` filters. Each result includes instanceId/name/path/active/tag/layer/scene/transform/components. Gate-free, token-bounded by `max_results`.

Component lifecycle (host resolved by `instance_id` > `path` > `name`; component resolved by `component_instance_id` or `type_name`):

- `unity_open_mcp_component_add` — Add one or more components by type name (full name preferred, class-name fallback). Per-type errors are accumulated. `paths_hint` = scene path containing the host.
- `unity_open_mcp_component_destroy` — Remove components by type name. `paths_hint` = scene path containing the host.
- `unity_open_mcp_component_modify` — Apply per-path serialized patches via `SerializedObject` (`fields: [{path, value, type?}]`). Per-entry errors are accumulated. Use `component_get` first to discover paths. `paths_hint` = scene path containing the host.
- `unity_open_mcp_component_get` — Read-only: serialized fields + (optional) public properties for one component. Token-bounded by `max_fields`. Gate-free.
- `unity_open_mcp_component_list_all` — Read-only: catalog of attachable component types from loaded assemblies (built-in + project MonoBehaviours). Token-bounded by `max_results`. Use `query` to narrow by namespace/class-name. Gate-free.

#### Scene Management (M16 Plan 3 — implemented)

Mutating members run the full gate path with `paths_hint` scoped to the scene asset path (or scene hierarchy path for `scene_focus`); read-only members are gate-free (returned as direct JSON without the gate envelope). `scene_open` is `lifecycle: "restart_then_settle"` (Single-mode open can lose unsaved changes in currently-open scenes — the active-scene dirty guard preflights it; pass `ignore_scene_dirty: true` to opt out); the other mutators use `lifecycle: "editor_settle"`; the read-only members use `lifecycle: "none"`.

Scene lifecycle:

- `unity_open_mcp_scene_create` — Create a new scene asset and save it at a `.unity` path (opening it). `setup: empty|default`, `mode: single|additive`. `paths_hint` = new `.unity` path.
- `unity_open_mcp_scene_open` — Open a scene asset in Single or Additive mode. `paths_hint` = target `.unity` path (include currently-open paths when `mode: 'single'` would close them). Pass `ignore_scene_dirty: true` to skip the active-scene dirty guard.
- `unity_open_mcp_scene_save` — Save an opened scene back to its asset (or a new `path`). `name` optional (active scene when omitted). Idempotent: `saved: false` with a note when the scene was not dirty. `paths_hint` = destination `.unity` path.
- `unity_open_mcp_scene_unload` — Unload an opened scene (without saving — call `scene_save` first if needed). Refuses the last opened scene (open another first). `paths_hint` = scene asset path (or name).
- `unity_open_mcp_scene_set_active` — Mark an opened scene as active. The target must already be opened (`scene_open` first). Idempotent no-op when already active. `paths_hint` = scene asset path.
- `unity_open_mcp_scene_list_opened` — Read-only: every opened scene as a shallow snapshot (name/path/isDirty/isLoaded/rootCount/buildIndex/isActive) + the active-scene pointer. Gate-free.

Scene data:

- `unity_open_mcp_scene_get_data` — Read-only: compact, drill-down hierarchy of an opened scene. `detail: summary|normal|verbose` (default `summary` = scene overview + root roster, no nested children; `normal` = + nested children to `depth` with active/tag/layer/components; `verbose` = + per-node instance_id + transform). `max_nodes` caps total nodes (`truncated` reports the overflow; `moreHidden` reports per-parent hidden counts). Supersedes the standalone M10 scene snapshot — reflects unsaved editor state, unlike `read_asset` on the `.unity` file. Gate-free.
- `unity_open_mcp_scene_get_dirty_summary` — Read-only: per-scene dirty flag + rootCount across every opened scene, with a `dirtySceneCount` tally. Use before `scene_save` / `scene_unload` / `scene_open`. Gate-free.
- `unity_open_mcp_scene_focus` — Frame a GameObject in the SceneView camera (computes combined renderer+collider bounds); optional `axis: top|bottom|front|back|left|right` and `size`. Returns the resulting pivot/camera position/rotation/size. Resolve target by `instance_id` > `path` > `name`. `paths_hint` = scene path containing the target.

#### Package Manager (M16 Plan 4 — implemented)

Mutating members (`package_add` / `package_remove`) run the full gate path with `paths_hint` = `["Packages/manifest.json"]` (packages-lock.json is touched implicitly — do not list it separately); they use `lifecycle: "restart_then_settle"` because UPM resolution can install/remove assemblies and force a domain reload, and the active-scene dirty guard preflights them (pass `ignore_scene_dirty: true` to opt out). Read-only members (`package_list` / `package_search` / `package_get_info` / `package_get_dependencies` / `package_check`) are gate-free (returned as direct JSON without the gate envelope) and use `lifecycle: "none"`.

- `unity_open_mcp_package_list` — Read-only: list installed UPM packages (name/displayName/version/packageId/source/resolvedPath/description/category/versions/registry/dependencies). Filter by `source` (registry/embedded/local/git/builtin/localtarball), `name_filter` substring (with exact-match priority), `direct_dependencies_only` (manifest.json entries); `include_indirect: true` includes transitive deps; `offline: true` (default) uses cached resolution. Token-bounded by `max_results`. Gate-free.
- `unity_open_mcp_package_search` — Read-only: search the UPM registry + installed local packages by query substring. Returns install status + installed version + top-5 compatible versions per result. Prioritized: exact name → exact displayName → name/displayName/description substring. `offline: true` (default) uses cached registry data; `offline: false` hits the live registry first for exact matches. Token-bounded by `max_results`. Gate-free.
- `unity_open_mcp_package_add` — Install a package from the registry, a Git URL, a Git URL with branch/tag (`#v1.0.0`), a local path (`file:../MyPackage`), or a local tarball. Mutating; `paths_hint` = `["Packages/manifest.json"]`.
- `unity_open_mcp_package_remove` — Uninstall a package by name (trailing `@version` is stripped). Refuses packages that are not installed; built-in and dependency-of-other packages cannot be removed. Mutating; `paths_hint` = `["Packages/manifest.json"]`.
- `unity_open_mcp_package_get_info` — Read-only: inspect one package by name / packageId / displayName. Looks up installed first (`Client.List`); falls back to a live registry search when not installed and `offline: false`. Returns the full package descriptor + `installed` boolean. Gate-free.
- `unity_open_mcp_package_get_dependencies` — Read-only: top-level dependency list from `Packages/manifest.json` parsed directly (no UPM request). Each entry is `{ name, reference }` where `reference` is the version pin / Git URL / file path / embedded marker as written. Use `package_list` with `include_indirect: true` for the resolved graph. Gate-free.
- `unity_open_mcp_package_check` — Read-only: presence + pinned reference for one package id against `Packages/manifest.json` (read directly, no UPM request). Accepts a versioned input — the check runs against the name half. Returns `{ installed, reference }`. Gate-free.

Workflow: `package_check` (fast manifest hit) → if not installed, `package_search` to discover the id → `package_add` with `paths_hint: ["Packages/manifest.json"]` → after the post-add compile settles, `package_get_info` to confirm the resolved version. Before `package_remove`, run `package_get_info` to confirm the package is present and not depended-on by others.

## Capability discovery

`unity_open_mcp_capabilities` lets an agent self-discover the entire tool + rule + fix surface in a single call, including what is planned but not yet built.

- Implementation: `mcp-server/src/capabilities/build-capabilities.ts`, `mcp-server/src/capabilities/rule-catalog.ts`.
- Routes locally (`_source: "local"`) — never hits the live bridge or batch Unity.

### Response shape

```json
{
  "tools": [
    {
      "name": "unity_open_mcp_scan_paths",
      "implemented": true,
      "status": "implemented",
      "category": "gate-and-verify",
      "routePolicy": "live",
      "batchCapable": false,
      "inputSchema": { "type": "object", "properties": { "...": "..." } }
    },
    {
      "name": "unity_open_mcp_type_schema",
      "implemented": false,
      "status": "planned",
      "category": "reflection",
      "guidance": "Planned reflection surface. Use find_members ..."
    }
  ],
  "rules": [
    {
      "id": "missing_references",
      "implemented": true,
      "status": "implemented",
      "title": "Missing references",
      "applicableAssetKinds": ["prefab", "scene", "scriptable_object"],
      "issues": [
        { "code": "missing_guid", "severity": "Error", "fixIds": ["relink_broken_guid"] },
        { "code": "missing_script", "severity": "Error", "fixIds": ["remove_missing_script"] }
      ]
    },
    {
      "id": "dependencies",
      "implemented": true,
      "status": "implemented",
      "title": "Forward dependency graph",
      "applicableAssetKinds": ["prefab", "scene", "scriptable_object", "material", "animation"],
      "issues": [
        { "code": "broken_dependency", "severity": "Error", "fixIds": ["relink_broken_guid"] },
        { "code": "dependency_cycle", "severity": "Warning", "fixIds": [] }
      ]
    },
    {
      "id": "materials",
      "implemented": false,
      "status": "planned",
      "guidance": "Not yet ported. Use find_references ..."
    }
  ],
  "fixes": [
    { "id": "remove_missing_script", "implemented": true, "safe": true, "rules": ["missing_references"] },
    { "id": "relink_broken_guid", "implemented": true, "safe": false, "rules": ["missing_references", "dependencies"] },
    { "id": "fix_duplicate_guid", "implemented": false, "status": "planned", "safe": false, "guidance": "Not yet ported ..." }
  ],
  "counts": {
    "toolsImplemented": 32,
    "toolsPlanned": 97,
    "rulesImplemented": 3,
    "rulesPlanned": 7,
    "fixesImplemented": 2,
    "fixesPlanned": 4
  },
  "routing": {
    "liveDefault": true,
    "batchFallback": true,
    "batchRequirements": ["UNITY_PATH", "UNITY_PROJECT_PATH"],
    "batchBlocked": [
      { "tool": "unity_open_mcp_execute_csharp", "reason": "Requires a live Editor compile context." },
      { "tool": "unity_open_mcp_invoke_method", "reason": "Requires a live Editor reflection context." },
      { "tool": "unity_open_mcp_execute_menu", "reason": "Menu execution needs the Editor UI; most menus fail in -batchmode." }
    ],
    "liveOnlyCategories": ["agent-senses"],
    "perToolFlag": "batchCapable"
  },
  "_source": "local"
}
```

### `routing` summary

The top-level `routing` object is a one-shot narrative for agents so a single `unity_open_mcp_capabilities` call gives the batch/route story without reading these docs. It is independent of the `kind` filter — asking for `kind: "rules"` still returns `routing`.

| Field | Meaning |
|---|---|
| `liveDefault` | `true` — most tools prefer the live bridge when connected. |
| `batchFallback` | `true` — when the live bridge is unavailable, only `batchCapable` tools fall back to a headless Unity spawn. |
| `batchRequirements` | Env vars a headless batch spawn requires (`UNITY_PATH`, `UNITY_PROJECT_PATH`). |
| `batchBlocked` | Mutating meta-tools intentionally rejected in batch, each with a short `reason`. |
| `liveOnlyCategories` | Tool categories that have no batch form (e.g. `agent-senses`). |
| `perToolFlag` | The per-tool flag name (`batchCapable`) agents should read for the authoritative per-tool answer. |

Per-tool route details live on each tool entry (`routePolicy`, `batchCapable`, `category`); the `routing` object only summarizes them.

### Parameters

| Parameter | Type | Default | Description |
|---|---|---|---|
| `kind` | `"tools"` \| `"rules"` \| `"fixes"` | (all) | Filter to a single surface. |
| `include_planned` | boolean | `true` | Set false to see only implemented items. |

### Planned-vs-implemented contract

- Every registered tool ships `implemented: true`.
- Planned typed tools and planned verify rules ship `implemented: false` with `status: "planned"` and a `guidance` string explaining the fallback — they never raise hard errors.
- The rule catalog is versioned with the package, so the `implemented` flags reflect what ships in the matching bridge/verify release.

## Rule selection: include / exclude

`scan_paths` and `validate_edit` accept `include_rules` and `exclude_rules` to filter the resolved rule set without trial-and-error on `categories`. Composition rules:

| Caller input | Behaviour |
|---|---|
| `categories` only | Run exactly the listed rules. |
| `categories` + `include_rules` | Run the intersection (narrowing). |
| `include_rules` only (no `categories`) | Additive: the auto-selected set ∪ the include list. |
| `exclude_rules` (any combination) | Always wins. Excluded rules are dropped after every other step. |

When the filters reduce the set to nothing, the tool returns an explicit empty result (no issues, `rulesApplied: []`) rather than running every rule. The response carries `rulesApplied` (post-filter effective set) alongside the legacy `categoriesRun`.

### Issue payload fields

Every issue in `scan_paths` / `validate_edit` responses carries:

| Field | Description |
|---|---|
| `ruleId` / `categoryId` | The verify rule that emitted the issue (aliases — same value). |
| `severity` | `"Error"` or `"Warning"`. |
| `code` / `issueCode` | The stable issue code (e.g. `missing_script`). Aliases — same value. |
| `assetPath` | The asset the issue is on. |
| `description` | Human-readable description. |
| `fixId` / `fixSafe` | Present when a fix is registered for this issue. `fixSafe: true` means the gate will auto-suggest it. |

`scan_paths` additionally carries `failOnSeverity` (the resolved threshold) and `rulesApplied` (the post-filter rule set).

## Severity thresholds

Projects differ in what counts as a failure. `scan_paths` / `validate_edit` / `regression_check` accept an explicit threshold; when omitted, `scan_paths` falls back to the project default.

### Project default (`scan_paths`)

`.unity-open-mcp/settings.json` carries a project-level default under the `verify` key:

```json
{
  "verify": { "severityThreshold": "warning" }
}
```

Accepted values: `error` (default), `warning` (alias `warn`), `info`, `verbose`, `never` (alias `off`). With `warning`, a scan over an asset with only warnings still flips `passed: false`. `scan_paths` echoes the resolved `failOnSeverity` in its response so the caller can confirm whether the project default or a per-call value was applied. `validate_edit` is intentionally strict-error: it answers "is this asset currently healthy?", independent of the project threshold.

### Per-call override

`scan_paths` accepts `fail_on_severity` explicitly to override the project default for one call. Same enum as above.

### Per-category regression thresholds (`regression_check`)

`regression_check` compares the current error count against a baseline. The global `regression_threshold` (default `0`) is the max tolerated *total* error-count increase. `per_category_thresholds` adds per-rule overrides:

```json
{
  "baseline_path": "CI/baseline.json",
  "regression_threshold": 0,
  "per_category_thresholds": { "missing_references": 2, "dependencies": 1 }
}
```

- Rules named in the map use their per-rule threshold; rules not named fall back to `regression_threshold`.
- The response carries a `regression.perRule` breakdown (ruleId, baselineError, currentError, errorDelta, errorThreshold, regressed) when any per-category threshold is set.
- The overall `regressed` verdict is the OR of the global check and every per-rule check — a regression under any scope fails the gate.

## Skill generation

`unity_open_mcp_generate_skill` produces a project-specific SKILL.md that gives the LLM up-to-date context for the specific project — installed tool versions, available verify rules, key MonoBehaviour/ScriptableObject types, and the core workflow.

- Implementation: `mcp-server/src/skill/generate-skill.ts`.
- Routes locally (`_source: "local"`) — reads `ProjectSettings/ProjectVersion.txt`, `Packages/manifest.json`, and scans `.cs` files under `Assets/` for type declarations. Never hits the live bridge or batch Unity.

### Parameters

| Parameter | Type | Default | Description |
|---|---|---|---|
| `write` | boolean | `false` | When `true`, write the generated skill to client skill directories. |
| `clients` | string[] | `["claude"]` | Which client skill dirs to write to. Only used when `write: true`. Allowed values are derived from the single-source manifest at `skills/client-paths.json` (`cursor`, `claude`, `opencode`, `agents`). `agents` writes to `.agents/skills/` for ZCode and other `.agents`-aware clients. |

### Response shape

```json
{
  "skill": "# Unity Agent Skill — MyGame\n...",
  "project": {
    "projectName": "MyGame",
    "unityVersion": "6000.0.1f1",
    "packages": [{ "id": "com.unity.ugui", "version": "2.0.0" }],
    "bridgeVersion": "0.3.0",
    "verifyVersion": "0.3.0",
    "monoBehaviours": [{ "name": "PlayerController", "namespace": "MyGame", "filePath": "Assets/Scripts/PlayerController.cs" }],
    "scriptableObjects": []
  },
  "written": [
    { "client": "claude", "relativePath": ".claude/skills/unity-open-mcp/SKILL.md", "absolutePath": "/path/.claude/skills/unity-open-mcp/SKILL.md", "written": true, "existed": false }
  ],
  "_source": "local"
}
```

When `write: false` (default), `written` is an empty array and the skill content is returned as a preview string.

## Object handle system

Live `UnityEngine.Object` values returned by `invoke_method` and `execute_csharp` are serialized as object handles (instance ID + type + fallback locators) instead of reflected JSON. This lets agents pass live objects back in subsequent tool calls:

- `invoke_method` — `object_id` parameter targets a live object for instance methods; args that are handle JSON are auto-resolved for `UnityEngine.Object` parameters.
- `execute_csharp` — `object_ids` parameter injects resolved objects as `Snippet.Refs[i]` / `Snippet.Ref<T>(i)`.

Handles include fallback locators (`path`, `assetPath`, `assetGuid`, `gameObjectPath`) so they degrade gracefully after domain reload. See [bridge-http.md](bridge-http.md#object-handles) for the wire format and resolution priority.

## Route policy

Route selection is implemented in `mcp-server/src/tool-router.ts`.

- `unity_open_mcp_list_assets`: always offline route.
- `unity_open_mcp_read_compile_errors`: always offline route. Reads Unity's `Editor.log` directly — the one channel that works when the bridge assembly itself has failed to compile (every in-bridge channel is dead with it, and `compile_check` can't run because its batch entry point shares the broken assembly and Unity's per-project lock blocks a second instance). No bridge, no Unity spawn.
- `unity_open_mcp_capabilities`: always local route (static catalog).
- `unity_open_mcp_generate_skill`: always local route (reads project files from disk).
- `unity_open_mcp_find_references`: live when available, otherwise offline reader.
- `unity_open_mcp_read_asset` and `unity_open_mcp_search_assets`: compressible router with offline-first behavior and live fallback.
- `unity_open_mcp_compile_check`: **always** routes to batch (a fresh Unity recompiling from scratch), even when the live bridge is connected — running it against an Editor that already compiled would never surface a broken build. Response `_route.fallbackReason` is `"compile_check_always_batch"`. Note: `compile_check` is **not** a recovery path for a broken bridge assembly (its entry point lives in that assembly); use `read_compile_errors` for that case.
- `unity_senses_pull_events`: live-only. Drains a per-process SSE subscription against the bridge `GET /events` endpoint; returns `bridge_unavailable` when the live bridge is down. See [Streaming & event pull](#streaming--event-pull-m13).
- Other tools:
  - prefer live bridge when connected,
  - use batch fallback only for tools in batch-eligible set,
  - return batch-style ping result when live is unavailable and tool is `unity_open_mcp_ping`.

Tool responses include route metadata under `_route`:
- live: `{ route: "live" }`
- batch fallback: `{ route: "batch", fallbackReason: "live_unavailable" }`
- compile check: `{ route: "batch", fallbackReason: "compile_check_always_batch" }`

## Batch support

Batch tool allow-list is defined by `BATCH_TOOL_NAMES` in `mcp-server/src/batch-spawn.ts`.

Supported operations:
- `unity_open_mcp_scan_all`
- `unity_open_mcp_baseline_create`
- `unity_open_mcp_regression_check`
- `unity_open_mcp_find_members`
- `unity_open_mcp_compile_check` — headless compile check; **always** routes to batch (spawns a fresh Unity that recompiles from scratch), even when the live bridge is available. Returns structured compiler errors (`status`, `errorCount`, `errors[]` with `code`/`file`/`line`/`message`). Uses the auto-discovered Unity (OS-default Hub install paths + `UNITY_HUB` env override, matching the bridge's `unityVersion` when known) or `UNITY_PATH` when set; `UNITY_PROJECT_PATH` falls back to the instance lock's `projectPath`. When the bridge assembly itself fails to compile, the JSON markers never print — batch-spawn then extracts `error CSxxxx` lines from the Unity log and surfaces them in the rejection so every batch tool self-diagnoses a broken build. **Not** a recovery path for a broken bridge assembly: its entry point lives in that assembly, and Unity's per-project lock blocks a second instance. For that case use `read_compile_errors`.

### `read_compile_errors` (offline)

`unity_open_mcp_read_compile_errors` — reads the tail of Unity's platform `Editor.log` (macOS `~/Library/Logs/Unity/Editor.log`, Windows `%LOCALAPPDATA%\Unity\Editor\Editor.log`, Linux `~/.config/unity3d/Editor.log`) and extracts structured C# compiler errors. Always offline (no bridge, no Unity spawn). This is the **only** error channel that works when the bridge assembly itself has failed to compile — every in-bridge channel (`read_console`, `editor_status`) is dead with it, and `compile_check` can't run.

Input: `tail_bytes` (default 262144, max 1048576) — bytes read from the end of the log, where Unity writes compiler diagnostics contiguously.

Response:
- `status`: `"compile_failed"` | `"no_errors_found"` | `"log_not_found"`
- `errorCount`, `errors[]` (`raw`, `file`, `line`, `code`, `message`)
- `logPath`, `tailBytes`, `_source: "offline"`

The live-client returns a `bridge_compile_failed` error (pointing here) when it detects the dead-bridge signature: instance lock present with a live PID but a stale heartbeat (the bridge's `[InitializeOnLoad]` never re-ran after the failed recompile, so the heartbeat writer is gone).


Recognized but intentionally blocked in batch mode (`batch_not_supported`):
- `unity_open_mcp_execute_csharp`
- `unity_open_mcp_invoke_method`
- `unity_open_mcp_execute_menu`

Batch runtime requirements:
- `UNITY_PATH` set to Unity executable.
- `UNITY_PROJECT_PATH` set to project root.

## Offline/compressible reads

`mcp-server/src/compressible-router.ts` handles:
- `unity_open_mcp_read_asset`
- `unity_open_mcp_search_assets`

Behavior:
- Parse text-serialized assets offline first.
- Fall back to live bridge for binary formats or offline parse failures.
- Return source marker (`_source: "offline"` or `_source: "live"`).
- `read_asset` uses LRU model cache and returns cache marker (`_cache: "hit" | "miss"`).

## Tool naming and contract notes

- Tool names use `unity_open_mcp_*`.
- Input schema and descriptions live in each tool file under `mcp-server/src/tools/`.
- Errors are returned as JSON text payloads with `error.code` and `error.message`.

## Lifecycle policy

Every bridge tool declares a **lifecycle policy** so callers know which ops are cheap, which settle, and which survive a domain reload. It is classified in the bridge (the source of truth — `[BridgeTool(Lifecycle = ...)]` for registry tools, `ToolLifecycle.Map` for legacy hardcoded tools) and surfaced in the gate response envelope as `lifecycle` + `settleMs`.

| Policy | Meaning |
|---|---|
| `none` | Read-only, returns immediately. Most tools. |
| `editor_settle` | Mutating; bridge waits for asset refresh/serialization (`apply_fix`, `reserialize`). |
| `restart_then_settle` | Mutating; may trigger a domain reload. Bridge blocks until the editor finishes compiling (cap 60s) so the caller never observes a half-compiled state (`execute_csharp`, `invoke_method`, `execute_menu`, `compile_check`, `scene_open`). |
| `custom_confirmation` | Async; returns immediately, result arrives via an external completion signal (`run_tests`). |

**Active-scene dirty guard.** `restart_then_settle` tools are refused with `error.code = "scene_dirty"` when any loaded scene has unsaved changes, so Unity's native save modal never interrupts the flow. Recover by saving/discarding the scene, or pass `ignore_scene_dirty: true` on `execute_csharp` / `invoke_method` / `execute_menu` / `scene_open`. See [bridge-http.md](bridge-http.md#active-scene-dirty-guard) for the envelope shape.

## Token-bounded output (M13 T4.6)

Every list-returning tool honors the same three controls so agents can bound token cost without reading per-tool docs:

- `detail: summary | normal | verbose` — compression level. Defaults vary per tool (`summary` for `read_asset` and `scene_get_data`, `normal` for `read_console` / `find_references`). `summary` omits the largest sub-fields (component fields, stack traces, field locations); `verbose` lifts the per-item caps and includes Unity-internal frames.
- `max_results` / `max_entries` / `max_items` / `max_nodes` — the cap on returned rows. Honored by `read_asset`, `search_assets`, `find_references`, `find_members`, `read_console`, `profiler_capture`, `list_assets`, `scene_get_data`, and `pull_events`.
- **Truncation is always reported.** Every capped response carries a count field so elision is never silent:
  - `read_asset` — `moreHidden` (folded tree rows past `limit`/`depth`).
  - `search_assets` — `truncated` (result files past `max_results`) and `moreObjectsHidden` per file.
  - `find_references` — `totalCount` vs returned entries; `pattern_threshold` collapses folders.
  - `find_members` — `count` (returned) and `truncated` (additional matches dropped).
  - `read_console` — `truncated` (entries dropped by `max_entries`) and `totalAfterFilter` (post-type-filter count).
  - `profiler_capture` — `truncated` on nested enumerables.
  - `scene_get_data` — `truncated` (nodes past `max_nodes`) and `moreHidden` (per-parent hidden child counts when `depth` caps the walk).
  - `pull_events` — `dropped` (events evicted from the in-memory queue before this pull).

When the cap is not hit, the count is `0` (or omitted for legacy fields); a missing count never means "unknown".

## Streaming & event pull (M13)

The bridge emits console-log and editor-state (compile / play-mode) notifications over an SSE stream at `GET /events`. See [bridge-http.md](bridge-http.md#events-sse--events-poll-m13) for the wire format.

`unity_senses_pull_events` is the MCP surface for that stream:

- The MCP server opens one SSE subscription per process on first call (lazy), keeps it connected across calls, and buffers events into a 500-entry queue.
- Each call drains up to `max_events` events and advances the cursor; calling again immediately returns only events that arrived since.
- The subscriber id is server-scoped — a restarted MCP server begins "now"; agents that need historical logs should still call `unity_senses_read_console`.
- Returns `bridge_unavailable` when the live bridge is down (no batch fallback — there is no headless Unity console to stream).

`pull_events` vs `read_console`: `read_console` is a snapshot of the *current* console contents (every call returns the whole buffered log). `pull_events` is *incremental* — it returns only logs emitted since the previous pull, plus editor-state transitions that never appear in the console (compile start/stop, play-mode changes). For a "what happened after my mutation" check, `pull_events` is cheaper; for a "what's the full current state" check, use `read_console`.



## Source-of-truth files

- `mcp-server/src/index.ts`
- `mcp-server/src/tools/index.ts`
- `mcp-server/src/tool-router.ts`
- `mcp-server/src/batch-spawn.ts`
- `mcp-server/src/compressible-router.ts`
- `mcp-server/src/event-stream.ts`
- `mcp-server/src/capabilities/build-capabilities.ts`
- `mcp-server/src/capabilities/list-rules.ts`
- `mcp-server/src/capabilities/rule-catalog.ts`
- `mcp-server/src/skill/generate-skill.ts`
- `mcp-server/src/skill/client-paths.ts`
- `skills/client-paths.json` — single source of truth for project-relative skill install paths and the MCP-client → skill-target mapping (consumed by both the Hub wizard and `unity_open_mcp_generate_skill`).
