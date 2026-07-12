# MCP Tools API

This page summarizes the MCP tool surface exposed by `unity-open-mcp`.

> **Install / connect.** The MCP server ships on npm as [`unity-open-mcp`](https://www.npmjs.com/package/unity-open-mcp). Most users never install it manually — the AI client spawns it via `npx -y unity-open-mcp@0.6.1` (no repo clone required). See [Manual setup](../setup/manual-setup.md) for the full client-config snippets and environment variables, and the [Maintainer publish flow](../setup/development-setup.md#maintainer-publish-flow) for how the package is published and updated.

For exact schemas, see tool files in `mcp-server/src/tools/` and use `unity_open_mcp_capabilities`.

| ![plot](../screenshots/bridge-status.png) | ![plot](../screenshots/bridge-tools.png) |

## Tool families

- **Core runtime**: ping, C# execution, method invoke, menu calls, reflection, compile checks, editor status.
- **Gate and validation**: validate edit, checkpoints, deltas, reference scan, path scan, regression baseline/check, fixes.
- **Asset intelligence**: reserialize, read/search/list assets.
- **Agent senses**: tests, screenshots (scene/game/isolated, arbitrary camera pose, inline image, editor window), Frame Debugger (enable/disable/draw-call list), console read, profiler capture (per-frame + single-frame deep capture), memory/rendering snapshots, Memory Profiler `.snap` capture (com.unity.memoryprofiler), spatial queries, event pull.
- **Typed editor surface**: scenes, GameObjects, components, packages, profiler session controls, build/project settings, script/object helpers, ScriptableObject create + list-by-type, Assembly Definition (asmdef) list/get/create/modify.
- **Extension domains**: navigation, input system, probuilder, particle system, animation, splines, lighting, audio, ui, constraints, terrain, cinemachine, timeline, tilemap, shader graph, vfx graph, 2D art pipeline (sprite atlas + texture import).
- **Discovery utilities**: capabilities, rules list, skill generation, manage_tools.
- **Unity Hub control**: list installed editors (+ build-target platforms), available releases feed, install editor/modules via the `unityhub://` deep link, get/set the default editor install path. Local-routed (no running Unity or bridge required).

## Tool groups and session visibility

Sessions start with five main groups enabled (`core`, `gate-and-verify`, `asset-intelligence`, `typed-editor`, `diagnostics`). Every other group is hidden from `ListTools` until the agent activates it via `unity_open_mcp_manage_tools` — **except auto-activating groups**, which activate automatically when their Unity package is installed (see §Auto-activation below). This keeps the prompt surface small (the full tool set is 263 tools) — a per-session group-visibility model so only the relevant tools are advertised.

### Groups


| Group                | Default | Description                                                                                                                                                                     |
| -------------------- | ------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `core`               | on      | ping, execute_csharp, invoke_method, find_members, execute_menu, editor_status                                                                                                  |
| `gate-and-verify`    | on      | validate_edit, checkpoint_create, delta, find_references, dependencies, scan_paths, apply_fix, scan_all, baseline_create, regression_check                                                    |
| `asset-intelligence` | on      | reserialize, read_asset, search_assets, list_assets                                                                                                                             |
| `typed-editor`       | on      | typed editor surface (assets, materials, shaders, prefabs, GameObjects, components, scenes, SceneView camera pose, packages, console, selection, undo + undo history/clear, tags, layers, reflection, scripts, object data, ScriptableObject create + list-by-type, Assembly Definition list/get/create/modify) |
| `diagnostics`        | off     | Profiler session controls + per-frame capture/memory/rendering reads                                                                                                            |
| `gate-intelligence`  | off     | impact_preview, gate_budget_estimate, mutation_explain                                                                                                                          |
| `build-settings`     | off     | Build pipeline + ProjectSettings reads and mutators (player/quality/physics/lighting + time/render-pipeline/quality-level), plus KV preferences (PlayerPrefs + EditorPrefs)      |
| `navigation`         | off     | NavMesh tools — compile-gated on `com.unity.ai.navigation`                                                                                                                      |
| `input-system`       | off     | Input System tools — compile-gated on `com.unity.inputsystem`                                                                                                                   |
| `probuilder`         | off     | ProBuilder modeling tools — compile-gated on `com.unity.probuilder`                                                                                                             |
| `particle-system`    | off     | Particle System tools — compile-gated on `UnityEngine.ParticleSystemModule`                                                                                                     |
| `animation`          | off     | AnimationClip + AnimatorController tools — compile-gated on `com.unity.modules.animation`                                                                                       |
| `splines`            | off     | Splines tools — compile-gated on `com.unity.splines`                                                                                                                            |
| `lighting`           | off     | Lighting tools — per-Light manipulation (add/set/modify), reflection probe bake (EditorSettle), skybox assignment. Built-in lighting module (always compiled)                   |
| `audio`              | off     | Audio tools — AudioSource add/modify, AudioMixer exposed-parameter set/get, AudioListener read (duplicate warning). Built-in audio module (always compiled)                      |
| `ui`                 | off     | UI (uGUI) tools — Canvas (+ CanvasScaler + GraphicRaycaster + EventSystem ensure), element add (Text/TMP_Text/Image/Button/Slider/Toggle/InputField), layout group add, element modify. Built-in UI module (always compiled); TMP_Text optional   |
| `constraints`        | off     | Constraints & LOD tools — animation constraints (Position/Rotation/Aim/Parent/Scale) add with source + weight + activation, LODGroup configure (fade mode / cross-fade / LOD array), LOD level add (per-index renderers). Built-in engine modules (always compiled)   |
| `terrain`            | off     | Terrain tools — create (TerrainData + GameObject), heightmap region write, splat layer paint, tree instance placement, neighbor stitching. Built-in Terrain module (always compiled); heightmap/splat writes cap at 513×513 per call (tile large writes)   |
| `sprite2d`           | off     | 2D art pipeline tools — SpriteAtlas (create/get/add_packable/remove_packable/modify/delete/list) + Texture import (get_importer/set_import/reimport/get). Two prefixes share one group so the 2D pipeline activates together. Built-in 2D module (always compiled). Mutating members run the gate with EditorSettle (reimport can take seconds / trigger a platform-switch reload); read-only members gate-free. texture_set_import folds sprite + normal-map presets into one settings_json patch   |
| `cinemachine`        | off     | Cinemachine tools — create/configure virtual cameras, set targets/lens/Body/Noise, ensure Brain, list cameras. **Reflection-gated**: the assembly always compiles; Cinemachine 3.x presence is detected at call time (returns `cinemachine_3x_required` / `cinemachine_package_required` when absent)   |
| `timeline`           | off     | Timeline tools — create TimelineAsset, add tracks (Animation/Activation/Audio/Signal/Control/Group/Playable), add clips, bind PlayableDirector, reflective modify. Compile-gated on `com.unity.timeline`   |
| `tilemap`            | off     | Tilemap tools — create Grid + Tilemap, paint single tiles, box-fill regions, create Tile assets, create RuleTile (requires tilemap.extras). Compile-gated on `com.unity.2d.tilemap`; RuleTile additionally inner-guarded on `com.unity.2d.tilemap.extras` at call time (two defines, two guards)   |
| `shadergraph`        | off†    | Shader Graph tools — create a Shader Graph asset, open it in the graph editor (returns a structured node/edge summary), add a node, connect two node ports. Compile-gated on `com.unity.shadergraph`. **Auto-activating** (†): activates automatically when `com.unity.shadergraph` is installed — no manual manage_tools call. The editing API is wrapped behind a reflection helper; mutating tools degrade to a structured `shadergraph_api_unavailable` error when the installed version exposes a different surface. Complementary to `shader_get_data` / `shader_list_all` (compiled-shader inspect).   |
| `vfx`                | off†    | VFX Graph tools — list VisualEffectGraph assets, open a `.vfx` in the VFX Graph editor (returns a structured context/block/property summary), patch a single block property. Compile-gated on `com.unity.visualeffectgraph`. **Auto-activating** (†): activates automatically when `com.unity.visualeffectgraph` is installed — no manual manage_tools call. The read paths (`list`/`open`) work over the public runtime `VisualEffectAsset` type (version-stable); the mutating `block_edit` requires the VFX Graph window to be open and degrades to a structured `vfx_block_edit_requires_editor_window` error otherwise.   |
| `memoryprofiler`     | off†    | Memory Profiler tool — capture a Memory Profiler snapshot to a `.snap` file via the com.unity.memoryprofiler package API. Sense-prefixed (`unity_senses_*`) because it pairs with the existing profiler family (profiler_get_script_stats / profiler_capture_frame). **Auto-activating** (†): activates automatically when `com.unity.memoryprofiler` is installed — no manual manage_tools call. Read-only re: game/project state but produces a file — Gate = Off, ReadOnlyHint = true, Lifecycle = EditorSettle (capture can take seconds). The capture is callback-based; the bridge blocks until the snapshot file is written.   |
| `agent-senses`       | off     | run_tests, screenshot, screenshot_camera, capture_inline, screenshot_window, frame_debugger, read_console, profiler capture/capture_frame/memory/rendering, spatial_query (live-only)                        |
| `unity-hub-control`  | off     | Unity Hub control — hub_list_editors (installed editors + build-target platforms), hub_available_releases (download feed), hub_install_editor / hub_install_modules (unityhub:// deep link), hub_get/set_install_path. Local-routed (no bridge/Unity required); mutating members are system-level ops (paths_hint N/A, gate-free)   |


Always-visible meta-tools (no group assignment): `unity_open_mcp_capabilities`, `unity_open_mcp_list_rules`, `unity_open_mcp_generate_skill`, `unity_open_mcp_manage_tools`, `unity_open_mcp_pull_events` / `unity_senses_pull_events`, `unity_open_mcp_read_compile_errors`, `unity_open_mcp_bridge_status`.

### manage_tools actions

```json
// List every group with active flag, description, and tool roster.
{ "action": "list_groups" }

// Activate a group — its tools appear in subsequent ListTools responses.
{ "action": "activate", "group": "navigation" }

// Deactivate a group — its tools disappear from ListTools.
{ "action": "deactivate", "group": "navigation" }

// Restore the default active set (`core` only).
{ "action": "reset" }
```

### State lifecycle

- **Ephemeral, per session.** The MCP server holds the state in memory; it is not persisted.
- **Resets to `core`-only on MCP-server restart.** Each agent session starts fresh.
- **Per-session independent.** Two concurrent agent sessions do not share activation state.
- **List-changed notifications.** The server declares `tools.listChanged: true`. When `activate`, `deactivate`, or `reset` actually changes the filtered `ListTools` surface, the server emits `notifications/tools/list_changed`. MCP clients should re-issue `tools/list` to refresh their tool descriptors (no server restart required). Idempotent activate/deactivate and no-op reset do not emit a notification.

### Compiled-state availability vs session activation

Two distinct concerns, intentionally not conflated:

- **Compiled-state availability** — whether the bridge compiled a domain in (e.g. `UNITY_OPEN_MCP_EXT_NAVIGATION` when `com.unity.ai.navigation` is installed). Reported in:
  - `unity_open_mcp_capabilities` → `toolGroups[].available` (probed from the bridge `GET /tools` endpoint when live; `null` when the bridge is offline). The top-level `bridgeReachable` flag distinguishes "group genuinely not compiled in" (`available:false` + `availableReason`) from "I can't tell because the bridge is down" (`available:null` while `bridgeReachable:false`). When `available:false`, the reason names both possible causes — the Unity package not installed, or the installed bridge binary not built with the domain extension pack.
  - `unity_open_mcp_manage_tools(action="list_groups")` → `groups[].available` (same probe).
- **Session activation** — whether the current session has activated the group (managed by manage_tools). Reported in `manage_tools(list_groups)` → `groups[].active`.

An agent can activate a group whose dependency is missing; its tools will appear in `ListTools` but error at call time. `capabilities` is the authoritative compiled-state source.

### Auto-activation

Most groups are **manual-activation** — the agent must call
`manage_tools(action="activate", group="<id>")` before the group's tools appear.
A domain group may additionally opt into **package-detection auto-activation**
(`autoActivate: true` + `unityPackage` in the catalog): when the project has
the group's Unity package installed, the group activates **automatically** for
the session — no manual call required. Shader Graph (`shadergraph` on
`com.unity.shadergraph`), VFX Graph (`vfx` on `com.unity.visualeffectgraph`),
and Memory Profiler (`memoryprofiler` on `com.unity.memoryprofiler`) are the
shipped auto-activating domains.

Auto-activation is:

- **Ephemeral** — same in-memory session store as manual activation; resets on
  MCP-server restart.
- **Reconciled lazily** from the live bridge's compiled-tool inventory
  (`GET /tools`): a group is package-present when any of its compiled-in tool
  names appears in that set. The reconciliation runs on `capabilities` and
  `manage_tools(list_groups)` calls, so an agent that calls either sees a
  fresh snapshot (and a `tools/list_changed` notification fires when the
  active set changes).
- **Reported** in `manage_tools(list_groups)` and `capabilities` per group as
  `activationSource: "auto"` (with `autoActivated: true`) plus
  `packageDependency`, so an agent can tell *why* a group is visible.
- **Overridable** — manual activation wins: auto-activation never flips a
  group the operator deliberately deactivated, and a package that goes away
  only drops a group that was auto-activated (not one re-activated by hand).

Existing domain groups keep their manual-activation behavior unless they
explicitly opt in.

## Discover tools programmatically

Call `unity_open_mcp_capabilities` first.

Example:

```json
{
  "kind": "tools",
  "include_planned": true
}
```

Use the response fields:

- `tools[].name`
- `tools[].category`
- `tools[].group` — tool-group id (or null for always-visible meta-tools)
- `tools[].routePolicy`
- `tools[].batchCapable`
- `tools[].lifecycle` — lifecycle class describing the recovery concern (`none` / `compile-reload` / `modal-dialog` / `scene-dirty` / `process-stale`); see [Lifecycle policy](#lifecycle-policy)
- `tools[].lifecycleNote` — optional tool-specific constraint (e.g. `compile_check` notes its batch-only lock); null when the class is self-describing
- `tools[].inputSchema`
- `toolGroups[]` — per-group catalog (compiled-state availability, default-enabled flag, tool roster, usage hint)
- `routing` — one-shot routing narrative (live default, batch fallback, blocked meta-tools, live-only categories)
- `costHints` — per-tool profile cost bands + recommended page sizes + recommended tool chains (see [Cost hints](#cost-hints-capabilitiescosthints--server-instructions))
- `lifecycleBlock` — the 5-class lifecycle taxonomy (meaning / bridge behaviour / recovery per class) + guidance (see [Lifecycle policy](#lifecycle-policy))

## Route policy

The router chooses one of:

- `live`: calls Unity bridge (`/tools/{name}`).
- `batch`: headless Unity fallback for supported tools.
- `offline`: local readers/parsers for selected tools.
- `local`: no Unity dependency (capabilities, catalog-style tools).

Common route behavior:

- Prefer `live` when available.
- Use `batch` only for tools marked `batchCapable`.

**Live-preferred routing.** `capabilities.tools[].routePolicy` and `batchCapable` describe what the router *can* do when the live bridge is down — they are not a promise of the route on every call. When the bridge is reachable:

- **Most tools, including every `batchCapable: true` tool except `compile_check` and the verify-family scans, route `live`.** Batch is a *fallback* used only when `live.isLiveAvailable()` is false (`_route.fallbackReason: "live_unavailable"`).
- **`offline-first` tools (`find_references`, `read_asset`, `search_assets`, …) also prefer the live bridge when it is up.** `find_references` and `dependencies` (without `include_impact`) call the bridge; offline disk parsers are the fallback when the bridge is down.
- **`read_asset` / `search_assets` may still parse from disk while the bridge is up** (text-serialized assets). Check `_source` (`"offline"` vs `"live"`) for the data origin; `_route.route` on these tools is always `"live"` because they enter through the compressible router.
- **Pinned exceptions (same route regardless of bridge):** `compile_check` and the verify-family scans (`scan_all`, `baseline_create`, `regression_check`) → always `batch`; `list_assets` / `read_compile_errors` → always `offline`; `capabilities` / `manage_tools` / `generate_skill` / `bridge_status` / `hub_*` / `pull_events` → always `local`; `dependencies` with `include_impact=true` → always `offline` (transitive impact is offline-only today).

When asserting routes in tests or tooling, treat `live` as acceptable for any batch-capable or offline-first tool whenever the bridge is up (see `scripts/mcp-protocol.mjs` §route spot-check).

- Keep some tools route-pinned:
  - `unity_open_mcp_compile_check` always uses batch. When a live Editor already holds the project lock, the headless spawn returns `editor_instance_locked` (close the live Editor, or verify compile state via the live bridge).
  - `unity_open_mcp_scan_all` / `unity_open_mcp_baseline_create` / `unity_open_mcp_regression_check` always use batch — they run in the headless verify package and are not registered on the live bridge. (`_route.fallbackReason: "verify_always_batch"`.) When a live Editor holds the project lock the spawn returns `editor_instance_locked` (close the live Editor and retry, or use the live-bridge `validate_edit` / `scan_paths` for a scoped health check instead).
  - `unity_open_mcp_read_compile_errors` always uses offline. Reads `Editor.log` for CSxxxx compiler errors + package/assembly red flags + an Editor hang signal. Issue kinds: `assembly_resolution`, `package_deprecated`, `package_manager_error`, and `editor_fd_exhaustion` (the Editor hung mid-build after hitting an internal Mono/Unity file-descriptor limit — the Bee build driver's "Could not register to wait for file descriptor N" exception; NOT a code error, and only an Editor restart recovers it; the hint carries restart instructions). When the response carries `staleLogSuspected: true`, the cited source files were edited more recently than the log — the error block may reference on-disk code you have already fixed (Unity's incremental compiler no-op'd the recompile). Force a recompile via `reimport_package` (local package) or `compile_check` before trusting the errors. The response also carries `staleLogHint` + `staleLogNewerFiles[]` (the offending files).
  - `unity_open_mcp_capabilities`, `unity_open_mcp_generate_skill`, and `unity_open_mcp_manage_tools` are local.
  - `unity_open_mcp_bridge_status` is local (server-resolved from the instance lock + one `/ping` probe).
  - `unity_open_mcp_hub_*` tools are local (filesystem discovery + Unity archive feed + `unityhub://` deep link + Hub CLI; no bridge or Unity dependency).

### Offline read coverage

`read_asset`, `search_assets`, `find_references`, and `dependencies` parse text-serialized assets from disk without a running Editor — the hybrid live/offline differentiator. The offline parser covers two asset families:

- **Text-serialized Unity YAML** — `.prefab`, `.unity`, `.asset`, `.mat`, `.controller`, `.anim`, `.playable`, `.preset`, `.spriteatlas`, `.terrainlayer`, `.vfx` (YAML object headers + GUID refs parse; embedded binary blobs are skipped).
- **JSON assets** (a path unity-scanner does not handle) — `.asmdef` (single JSON object), `.shadergraph` / `.shadersubgraph` (a stream of pretty-printed JSON objects, one per graph element).

Binary formats (`.png`, `.wav`, `.fbx`, …) stay live-routed. When the bridge is down, offline-parseable assets still read; non-parseable assets surface a `source_unavailable` error pointing at the bridge.

The offline reader reconstructs the **full** GameObject/component tree (the compact default folds render-only leaves; `profile=full` shows every node), parses **prefab variant overrides** from `PrefabInstance.m_Modifications` (matching the live `prefab_get_overrides` shape), and emits **integrity signals** (`integrity[]` on the `read_asset` response) for malformed JSON, missing references, missing scripts, and orphaned prefab instances. The signals surface on every read so an agent sees them without a separate verify call; the verify rule suite consumes the same primitives as structured rules.

**Dependency graph + impact analysis offline.** `dependencies` is offline-routeable: forward edges (what this asset depends on) come from parsing the queried asset's YAML on disk, reverse edges (what references this asset) reuse the offline reference scan, and broken forward-edge GUIDs + dependency cycles are computed offline. Set `include_impact=true` for the **transitive reverse closure** — every asset that (transitively) depends on the queried asset, with hop depth. This is the "what breaks if I delete/move this?" answer; it is offline-only (the live tool has no multi-hop reverse surface yet), so it routes offline even when the bridge is up. Non-YAML queried assets (JSON/binary) surface a `forwardSkipped` reason and empty forward arrays; reverse edges are unaffected.

**Project-wide integrity scan offline.** The offline integrity scanner walks the whole `Assets/` tree and emits three rule families that the per-read check cannot surface project-wide: `orphan_meta` (a `.meta` whose companion asset was deleted), `duplicate_guid` (a GUID shared by two+ assets), and the aggregated `missing_reference` / `missing_script_reference` set. These feed the `offline_integrity` verify rule (the link keys for the `remove_orphan_meta` and `fix_duplicate_guid` fixes). The live Editor counterpart is the `project_health` rule — same canonical codes (`orphan_meta`, `duplicate_guid`), plus `missing_project_setting` (required ProjectSettings files missing), emitted during a full `scan_paths` run.

**Verify rule catalog.** `unity_open_mcp_capabilities` and `unity_open_mcp_list_rules` enumerate every implemented + planned rule. Implemented families: `missing_references` (broken PPtrs, missing scripts, duplicate components, invalid layers), `scene_prefab_health` (structural health), `dependencies` (forward graph + cycles), `offline_integrity` (project-wide orphan meta / duplicate GUID / missing refs, offline), `asmdef_audit` (broken assembly-definition references, missing name, malformed JSON), `project_health` (project-wide integrity, live Editor), `materials` (missing shader/texture references), `animation_analysis` (missing motion clips, empty clips), and `shader_analysis` (shader compile errors). Each rule declares stable issue codes that link bidirectionally to fix providers in the fix catalog.

**Fix providers + safe auto-fix rollback.** `unity_open_mcp_apply_fix` dispatches a verify rule's fix action and defaults to `dry_run: true` (the preview short-circuits the gate). The `issue_id` is the pipe-delimited key `ruleId|severity|assetPath|issueCode` you copy verbatim from a `scan_paths` / `validate_edit` issue — severity is case-insensitive (`error`/`Error`/`ERROR`, `warn`/`Warn`/`WARN`/`warning`/`Warning` all parse) so the documented scan→fix loop works across separate calls regardless of how the producing tool spelled it. Implemented fixes: `remove_missing_script` (safe), `remove_orphan_meta` (safe — deletes a detached `.meta`), `relink_broken_guid` (unsafe — needs `target_guid`), `fix_duplicate_guid` (unsafe — regenerates a colliding GUID), `reassign_missing_texture` (unsafe — needs `target_texture`), `reassign_missing_shader` (unsafe — needs `target_shader`). Safe fixes are auto-suggested by the gate; unsafe fixes require a deliberate operator choice and surface candidates in the dry-run preview. A non-dry-run apply runs checkpoint → apply → validate → delta, and if the fix fails to apply **or** introduces new errors under `enforce`, the touched files are restored to their pre-fix state and the response carries a top-level `rollback` block (`{rolledBack, reason, restoredPaths[]}`). Rollback is high-confidence-only: new *warnings* do not trigger it (informational), and `warn`/`off` gate modes are report-only. Read `gate.delta.newErrors` together with `rollback` — a rolled-back fix left no project change, so inspect the issue manually before retrying.

**Issue explainability.** Beyond the base fields (`ruleId`, `severity`, `code`/`issueCode`, `assetPath`, `description`), every issue in a `scan_paths` / `validate_edit` response carries three optional explainability blocks so an agent can move from "it found a problem" to "here is how to fix it":

- `rootCause` — a stable, machine-readable code identifying *why* the issue class happens (e.g. `missing_guid_reference`, `duplicate_guid`, `orphaned_meta`, `build_blocker`, `resource_missing`, `structural_complexity`, `configuration_mismatch`, `missing_dependency`, `missing_script_class`). Agents branch recovery on this code rather than parsing free-text. The same codes are declared on each issue descriptor in `unity_open_mcp_capabilities` / `unity_open_mcp_list_rules` (`issues[].rootCause`).
- `evidence` — the per-instance payload that triggered *this* issue: the broken reference's GUID / fileID / line, the expected-vs-actual value, the duplicate group's member paths, etc. Keys are issue-class specific and always string-valued. Absent when the rule has no per-instance detail.
- `fixCandidates` — every fix that can resolve the issue, each with its `safe` flag (`[{fixId, safe}]`), so an agent sees safe vs unsafe options in one pass. This supersedes the legacy single `fixId` / `fixSafe` pair (kept for backwards compatibility). When no fix exists, the field is absent.

`remediation` (a short, clean, user-visible playbook for the issue class) is declared alongside `rootCause` on each issue descriptor in `unity_open_mcp_capabilities` / `unity_open_mcp_list_rules`, and is also emitted per-issue in `scan_paths` / `validate_edit`. Use `rootCause` to branch programmatically and `remediation` as the human-readable next step.

## Lifecycle policy

Every tool declares a **lifecycle class** that describes the recovery concern an agent must reason about when the call fails or the bridge becomes unresponsive. It is exposed per tool as `capabilities.tools[].lifecycle` (+ an optional `lifecycleNote` for tool-specific constraints), and the full taxonomy is attached as `capabilities.lifecycleBlock`. Read it *before* the call to pick a recovery strategy.

The 5 classes:

| Class           | Meaning                                                       | What the bridge does                                                                                              | Recovery                                                                                                                                                              |
| --------------- | ------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `none`          | Read-only / side-effect-free.                                 | Returns immediately; no settle wait, no dirty guard.                                                              | Retry on the next call. A failure is a real error, not a settle/reload artifact.                                                                                      |
| `compile-reload`| Triggers or observes a domain reload (script / asmdef / package edit). | Blocks up to 60s for the compile to settle; the active-scene dirty guard preflights.                              | After the call, poll `editor_status` / `read_compile_errors` before assuming success. `compile_check` is batch-only and returns `editor_instance_locked` when a live Editor holds the project lock — do not retry blindly. |
| `modal-dialog`  | May raise an OS modal (build, project upgrade, version mismatch). | Startup modals (Safe Mode, Non-Matching Editor, Project Upgrade, Auto Graphics API) are auto-dismissed by the MCP server while it waits for bridge readiness, per `UNITY_OPEN_MCP_DIALOG_POLICY`. Mid-call modals (build, save-scene) still need the operator or the scene-dirty guard. | Set `UNITY_OPEN_MCP_DIALOG_POLICY` (`ignore` default) and, for project-upgrade consent, `UNITY_OPEN_MCP_ALLOW_PROJECT_UPGRADE=1`. See [Dialog policy](../dialog-policy.md). |
| `scene-dirty`   | Mutates scene / prefab / hierarchy / asset state.             | Runs the gate (checkpoint → mutate → validate → delta); blocks up to 5s for asset refresh. Requires non-empty `paths_hint`. | Read `gate.delta` + `agentNextSteps` from the response. On a `scene_dirty` refusal, save or discard first, or pass `ignore_scene_dirty: true` to accept the risk.    |
| `process-stale` | Long-running / async op where the bridge may become unresponsive. | Blocks until the op finishes; the heartbeat may stop advancing during the wait.                                   | Treat stale-heartbeat + live-PID as "still running", not crashed. Wait, then re-probe with `ping` / `editor_status` before concluding the bridge died.                |

This lifecycle axis is the **recovery** view agents reason about. It is distinct from the bridge's internal settle-timing axis (which drives how long the dispatcher blocks and whether the dirty guard preflights); agents only see the 5-class recovery view via `capabilities`.

### `compile-reload` agent limits

Three constraints apply specifically to the `compile-reload` class — internalize them before relying on the compile-verify loop:

1. **`compile_check` is batch-only by design.** It spawns a fresh headless Unity that recompiles from scratch and cannot share the project with a live Editor. When a live Editor already holds the project lock it returns `editor_instance_locked` — do **not** retry blindly. The response carries a structured `agentNextSteps[]` pointing at the live-bridge recovery branch: either close the live Editor and retry, or verify compile state without closing via `read_compile_errors` (reads `Editor.log` offline), or `reimport_package` (with the `dllMtimeBefore`/`After` delta) to confirm a specific local package recompiled.

2. **Local `packages/` source lives outside Unity's `Assets/` watch root.** When you develop against local package source, `assets_refresh` and `RequestScriptCompilation` (via `execute_csharp`) are **not reliable** for forcing a rebuild in a long-running live session — the editor can keep serving stale assemblies. Recovery branches: `reimport_package` (force-reimports the package's source and reports `dllMtimeBefore`/`After` so a no-op recompile is detectable, with `agentNextSteps` pointing at a standalone Roslyn-compile fallback), a no-op `package_add` / `package_remove` to nudge UPM resolution, or operator refocus of the Editor window. There is no code-level fix for Unity's incremental-compile no-ops; `reimport_package` makes them *detectable* rather than silent.

3. **Tools that trigger a domain reload declare `compile-reload`; read-only introspection stays `none`.** `execute_csharp`, `invoke_method`, `execute_menu`, `asmdef_create` / `asmdef_modify`, `script_write` / `script_delete`, `package_add` / `package_remove`, `reimport_package`, `build_set_target`, `build_set_defines`, `settings_set_player`, `scene_open`, and `compile_check` are all `compile-reload`. `find_members`, `read_asset`, `editor_status`, the senses, etc. are `none`.

4. **`execute_csharp` runs on Unity's main thread — never block it.** The snippet executes synchronously inline on `EditorApplication.update`. A blocking primitive (`WaitOne`, `.Result`, `Thread.Sleep`, `while(!done)` awaiting a callback) or driving `TestRunnerApi` (which delivers its callbacks on the same main thread) **deadlocks the editor unrecoverably**: the HTTP timeout fires on the worker thread and cannot unwind a stuck main thread, no further editor tick runs, and recovery requires killing the editor externally. The timeout envelope's `agentNextSteps` says exactly this — heed it (check `editor_status` / `bridge_status` before retrying; **do not** just raise `timeout_ms`). `TestRunnerApi` is deny-listed in `execute_csharp` for this reason and redirects to `unity_senses_run_tests` (async, does not block). For other work, prefer a typed tool or write the snippet fire-and-forget and poll via a follow-up call.

A tool can carry a secondary concern in `lifecycleNote` (e.g. `build_start` is `modal-dialog` and notes its secondary `scene-dirty` concern; `package_add` is `compile-reload` and notes the rare project-upgrade modal). Read the note when present.

### Scene identity (path-first)

The scene mutators `scene_set_active`, `scene_unload`, and `scene_save`, plus the read `scene_get_data`, identify an opened scene by **asset path first, display name second**. Agents should prefer `path` (e.g. `Assets/Scenes/Foo.unity`) — it is the authoritative identity for an asset-centric MCP and is unaffected by Unity's transient in-memory scene names.

- `scene_set_active` / `scene_unload` — provide `path` to resolve by asset path, or `name` to resolve by display name. When both are supplied, **`path` wins** and `name` is ignored. If neither resolves an opened scene, the call returns `scene_not_found` naming whichever key(s) you supplied. On success the response carries `resolvedBy` (`"path"` or `"name"`) so the agent can confirm which identity key resolved the target — useful for diagnosing duplicate-name ambiguity.
- `scene_save` — `path` is dual-purpose: when it matches an opened scene's asset path it selects that scene and saves it back to its own path (path-as-identity); when it does not match an opened scene it is a save-as destination for the active (or `name`-selected) scene and must end with `.unity`. `name` always selects an opened scene by display name when supplied.
- `scene_get_data` — `path` resolves by asset path (takes precedence); `name` is the fallback; omit both to read the active scene.

**Name sync after `scene_create`.** `scene_create` syncs the in-memory scene name to the asset filename stem after saving, so a subsequent name-only `scene_set_active` / `scene_save` / `scene_unload` by the stem resolves without a separate `scene_open`. This is complementary hardening — prefer `path` for robustness, but name-only flows no longer silently drift after create.

## Batch support notes

Batch is intended for non-interactive scenarios and fallback operation.

- Typical batch-friendly tools: scan/regression surfaces, compile check, member lookup, code execution, reflection dispatch, and the batch-viable menu subset.
- All four meta-tools (`find_members`, `execute_csharp`, `invoke_method`, `execute_menu`) run in batch. The mutating three (`execute_csharp`, `invoke_method`, `execute_menu`) **skip the gate** in batch — the envelope reports `gate.mode:"off"`, `gate.skipped:true` — because the checkpoint/validate/delta flow runs against the live AssetDatabase in an interactive Editor and is unavailable headless. Operators who want a guarded mutation connect a live Editor; batch is the unguarded CI/script path.
- `execute_menu` is gated by a **batch-viable allow-list** (`Assets/Refresh`, `Assets/Reimport All`, `File/Save Project`, `File/Save Scenes`) because most Editor menus open a window or dialog and fail under `-batchmode`. Non-viable menus return `menu_not_viable_in_batchmode`.
- Agent senses (screenshots, profiler, console, spatial) need a live Editor — they have no batch form.
- **`unity_senses_run_tests` likewise has no MCP batch form**, but tests can be run headlessly via Unity's own CLI with `-runTests`. **Omit `-quit`** — with `-quit` the editor shuts down after the initial asset refresh, before the runner starts, so no tests execute and no results XML is written despite an exit code of 0. Correct invocation: `Unity -batchmode -projectPath <p> -runTests -testPlatform EditMode -testResults out.xml -logFile run.log -nographics`. Exit codes: `0` = all passed, `2` = test failures present (not a process error), `3` = runner setup failure. Several test classes are **batchmode-only failures** that pass in the GUI editor — in-process HTTP-server lifecycle (socket-disposed), GPU/SceneView (no graphic device under `-nographics`), Unity-6's removed `LogEntries.GetEntries(List<LogEntry>)` internal API, MSBuild/Roslyn `Microsoft.Build.Utilities.Core` host assembly load, and TimeManager SerializedProperty float access. Validate these in the GUI editor rather than "fixing" them.
- **`compile_check` omits `-quit` for the same reason.** The operation is asynchronous — it waits for compilation to settle and emits JSON markers from `EditorApplication.update` before calling `EditorApplication.Exit`. With `-quit`, Unity exits as soon as the synchronous `executeMethod` returns, producing exit code 0 but no markers. The MCP spawn uses `-batchmode` only; termination is via `EmitCompileResult`.
- Required environment for batch fallback:
  - `UNITY_PROJECT_PATH`
  - `UNITY_PATH` (when editor auto-discovery is unavailable)

### In-Editor Batch panel (Activity tab)

The Unity Open MCP bridge window's **Activity** tab has a **Batch runs** section — a read-only view of in-Editor batch runs. The section auto-expands when a run is active or recently completed; while a batch is running it shows live progress (entries pending / running / done / failed / skipped) and per-entry results (tool name, args summary, pass/fail, error text on failure); completed runs are retained in a session ring buffer for inspection. The panel observes batch state only — it does not start or stop batches (that stays with the MCP batch surface and the Hub). It re-renders automatically as entries transition, so no manual refresh is needed. In-memory only, cleared on domain reload. The live `unity_open_mcp_batch_execute` tool feeds this panel — a run appears as it executes, with one entry per nested step.

### Live batch execution: `unity_open_mcp_batch_execute`

Distinct from the headless batch spawn above: one **live HTTP call** runs many typed tools sequentially inside the **already-open** Editor, cutting agent↔Unity round trips for multi-object setup (spawn N cubes + create materials + assign in one call). It is `batchCapable: false` (NOT in `BATCH_TOOL_NAMES`) — there is no headless spawn fallback; the live bridge must be up. It lives in the `core` group (always visible) and routes live-only.

**Contract:**

| Field | Type | Required | Notes |
|---|---|---|---|
| `commands` | `{ tool: string, params: object }[]` | yes | Each `tool` is a full MCP id. Non-empty. |
| `fail_fast` | `boolean` | no | Default **`true`**. Stop on first step failure; later entries are `skipped`. |
| `gate` | `"enforce"\|"warn"\|"off"` | no | Default **`enforce`**. Applies to the **whole batch** (one checkpoint → N steps → one validate/delta). |
| `paths_hint` | `string[]` | yes | Union of all paths the batch may touch. |
| `parallel` | `boolean` | no | Default `false`. **Ignored in v1** (Unity API is main-thread; sequential only). |

**Safety model — strictly safer than sequential invoke:**

- **One gate cycle** for the whole batch (one checkpoint → all steps → one validate/delta). The `gate.delta` reports new issues introduced by the entire sequence.
- **One undo group** — a single `editor_undo` reverts the whole batch.
- **Partial failure** — v1 does NOT roll back successful steps when a later step fails (same as the competitor). The `agentNextSteps` point at the failed step + fixes. Undo the whole batch if needed.
- **Deny-list** — `batch_execute`, `compile_check`, `execute_csharp`, `invoke_method`, `execute_menu` cannot be nested (agents use batch for typed tools; the power tools stay single-call). Local-only tools (capabilities, manage_tools, hub_*, etc.) dispatch to `tool_not_found` as a per-step error.

**Limits:** default **25** commands, hard max **100** — configurable in `.unity-open-mcp/settings.json` (`batchExecuteMaxCommands`, clamped 1–100), mirrored in the bridge Settings tab → "Batch execute" section.

**Response shape:**

```json
{
  "batch": {
    "success": true,
    "callSuccessCount": 3,
    "callFailureCount": 0,
    "failFast": true,
    "results": [
      { "index": 0, "tool": "unity_open_mcp_gameobject_create", "status": "success", "output": { "…": "…" } },
      { "index": 1, "tool": "unity_open_mcp_material_create", "status": "failed", "error": { "code": "missing_parameter", "message": "…" } },
      { "index": 2, "tool": "unity_open_mcp_gameobject_modify", "status": "skipped" }
    ]
  },
  "gate": { "mode": "enforce", "validation": { "passed": true }, "delta": { "newErrors": 0, "resolvedErrors": 0 } },
  "logs": [],
  "agentNextSteps": []
}
```

### In-Editor Tools tab token estimate

The bridge window's **Tools** tab surfaces a per-tool token estimate so operators can reason about the context-window cost of an active tool set *before* an agent connects. With 263 tools across the always-on + grouped + auto-activated domains, the cost of an active set is otherwise invisible until an agent sees the tool list.

The estimate is **regenerated from the same source as the tool catalog** — the MCP-server tool schemas (`mcp-server/src/tools/*`) — by `scripts/generate-token-estimates.mjs`, which serializes each tool's `{ name, description, inputSchema }` to its MCP wire JSON and estimates tokens via a `chars / 4` heuristic (dependency-free; a real BPE tokenizer is out of scope — the value is for *relative* cost, not exact counts). The generated `packages/bridge/Editor/UI/BridgeToolTokenEstimates.cs` is checked in and read by the bridge at runtime; there is no second hand-maintained list, and a CI drift gate (`.github/workflows/version-sync.yml`) fails any PR where the table disagrees with the schemas.

Where it shows:

- **Per tool** — each catalog row renders `~{N} tokens` (K-formatted above 1000, e.g. `~1.2K`) as a chip alongside the mutability / gate / source chips.
- **Per group** — a collapsible "Per-group token estimate" breakdown lists every group with its active vs total token cost (e.g. `core: ~2K active / ~2K total (6/6 tools)`), so the operator can see which groups dominate the budget. Each row also has **Enable** / **Disable** buttons that toggle exactly that group's tools in one action.
- **Total** — the filters header reports `Active tokens: ~{N}` — the headline context-window cost of the enabled tool set.

The active total is recomputed every frame from the live per-tool toggle state, so disabling a tool (or every tool in a group) drops its tokens from the total immediately. Note this reflects the **bridge toggle policy** (per-tool enable/disable in `.unity-open-mcp/settings.json`), not per-session `manage_tools` activation — session activation lives in the MCP server and is not tracked by the bridge window.

**Bulk actions** — the Tools tab offers **Enable all** / **Disable all** that act on the **current filtered view** (search + Enabled/Disabled filter), not the whole catalog, so a search-narrowed bulk is surgical. Tools outside the filtered set keep their state. Disabling more than a small number of tools shows a native confirm dialog first; re-enabling is one click. Both bulk paths and the per-group toggles persist through the same `.unity-open-mcp/settings.json` `disabledTools` field as a single per-tool toggle.

## Asset dependency graph: `unity_open_mcp_dependencies`

A typed tool that returns forward AND reverse dependency edges for a single asset in one call, plus the broken forward-edge GUIDs and dependency-cycle trails the `dependencies` verify rule computes. It reuses the same scanners as the verify rule and `find_references` — **no second dependency graph is built**:

- **Forward edges** — `Dependencies.Scanner` (`packages/verify`), the same `AssetDatabase.GetDependencies` + `m_AssetGUID` walk the `dependencies` verify rule runs.
- **Reverse edges** — `ReferenceGraph.Find` (`packages/verify`), the same reverse walker `unity_open_mcp_find_references` exposes.

Use `find_references` for reverse-only lookups (it also works offline for text-serialized assets). Use `dependencies` when you need both directions, or the broken-edge / cycle view, in a single typed call. Live bridge only — the underlying scanners call AssetDatabase; there is no offline form.

Parameters:

- `asset_path` OR `guid` (one required) — the target asset.
- `detail`: `summary` (counts only) | `normal` (default — full rosters).
- `max_results`: caps the reverse-dependency roster (default 100); `reverseCount` reports the untruncated total.

Response shape (normal detail, healthy asset):

```json
{
  "queriedAssetPath": "Assets/Prefabs/Player.prefab",
  "queriedAssetGuid": "<32-hex>",
  "forwardDependencies": [{"assetPath": "...", "guid": "..."}],
  "forwardCount": 7,
  "brokenForwardGuids": [],
  "cycles": [],
  "reverseDependencies": [{"assetPath": "...", "guid": "..."}],
  "reverseCount": 3,
  "truncated": 0,
  "detail": "normal"
}
```

An input that does not resolve to a real asset returns `status: "asset_not_found"` with empty edge arrays (not an error) so an agent can branch cleanly.

## Output shaping

Many tools support output controls to reduce token usage:

- `detail`: `summary | normal | verbose`
- pagination and limits (`max_results`, `max_entries`, `max_items`, `max_nodes`, etc.)
- explicit truncation indicators in the response

### Output profiles (`compact` / `balanced` / `full`) + uniform paging

The heavy tools (the ones that return variable-size payloads) share a uniform
token-budget knob and a resumable paging convention:

- **`profile`**: `compact` (default) | `balanced` | `full`. This is the public,
  documented knob. It maps onto each tool's existing `detail` axis
  (`compact`→`summary`, `balanced`→`normal`, `full`→`verbose`) so the M9
  compression module does the folding — the profile is just its public name.
  An explicit `profile` wins over a legacy `detail`; the two are otherwise
  interchangeable (`detail` is a backwards-compatible alias).
- **`page_size` / `cursor` / `next_cursor`**: uniform paging. When `page_size`
  is set, the response carries a `pagination` block with a `next_cursor` to
  resume. Omit `page_size` to receive the whole (profile-shaped) payload in one
  response. `cursor` is the opaque continuation token from a previous
  `pagination.next_cursor`.

Every paginated response includes a `pagination` block:

```json
"pagination": {
  "page_size": 40,
  "cursor": null,          // the cursor this page was requested with (null = first page)
  "next_cursor": "read_asset:40",  // null when this is the last page
  "truncated": 120         // items remaining after this page (the resumable tail)
}
```

Profile- and paging-aware tools, and what each axis controls:

| Tool | `profile` controls | `page_size` pages |
| --- | --- | --- |
| `read_asset` | TREE folding (compact = CMP codes + omission counts) | TREE rows |
| `search_assets` | per-file object cap (compact tight; balanced/full larger) | result-file matches |
| `scene_get_data` | scene overview vs nested children vs transforms | flattened node stream |
| `find_references` | counts/groupings (compact) vs per-asset list vs field locations | referencing-assets list |
| `validate_edit` | counts by severity (compact) vs full issues list | issues list |
| `scan_paths` | counts by severity (compact) vs full issues list | issues list |
| `component_get` | top-level serialized fields vs + leaf public properties vs nested serialized children | combined fields+properties stream |

**Back-compat / deprecation path.** The legacy caps (`detail`, `max_results`,
`max_nodes`, `object_limit`, `max_per_folder`) remain as aliases. They still
work unchanged when `page_size` is omitted: `detail` selects the same level as
`profile`, and the legacy caps request a single bounded page. Migrate callers
to `profile` + `page_size`/`cursor` over time; the aliases are not removed in
this milestone. The compact default means `find_references` /
`validate_edit` / `scan_paths` now return counts/groupings by default — pass
`profile: "balanced"` (or `detail: "normal"`/`"verbose"`) to get the
per-asset / per-issue lists those tools previously returned by default.

**`compact` is the default for all heavy tools.** It is a no-op for
`read_asset` / `scene_get_data` / `search_assets` (whose previous default was
already the summary/folded shape) but changes the default output of
`find_references` / `validate_edit` / `scan_paths` to counts/groupings only.

### Cost hints (`capabilities.costHints`) + server instructions

`unity_open_mcp_capabilities` carries a `costHints` block so an agent can
reason about prompt cost *before* choosing a profile, and learn the
budget-aware way to accomplish common tasks:

```json
"costHints": {
  "tools": [
    {
      "tool": "unity_open_mcp_read_asset",
      "profileControls": "TREE folding (compact = CMP codes + omission counts; full = verbose tree).",
      "pageSizePages": "TREE rows.",
      "profiles": {
        "compact":  { "band": "small",  "approxTokens": "~0.3–0.8k tokens" },
        "balanced": { "band": "medium", "approxTokens": "~1–4k tokens" },
        "full":     { "band": "large",  "approxTokens": "large — unbounded; set page_size to bound it" }
      }
    }
    // …one entry per heavy tool
  ],
  "recommendedPageSize": {
    "unity_open_mcp_read_asset": 40,
    "unity_open_mcp_search_assets": 25,
    "unity_open_mcp_scene_get_data": 50,
    "unity_open_mcp_find_references": 50,
    "unity_open_mcp_validate_edit": 25,
    "unity_open_mcp_scan_paths": 25,
    "unity_open_mcp_component_get": 25
  },
  "recommendedToolChains": [ /* discover, asset-inspect, component-inspect, find-references, mutate-then-verify, verify-api-before-coding */ ],
  "guidance": "Start with the default compact profile on every heavy tool, then expand (profile=\"balanced\" / \"full\") or drill down (component / path / id flags) only for the slice you need. Set page_size to bound any profile; follow pagination.next_cursor to resume. compact is the documented default for all heavy tools."
}
```

- `band` is one of `small` (~0.3–0.8k tokens) / `medium` (~1–4k) / `large`
  (unbounded — set `page_size`). Bands are monotonic across profiles
  (`compact ≤ balanced ≤ full`) and full is never `small` on the heavy tools.
- `approxTokens` is the same range as a display string, so an agent can show
  it inline without mapping the band back to a range.
- `recommendedPageSize` is a starting point, not a hard cap — an agent may
  choose a different `page_size`.
- `recommendedToolChains` names the canonical sequence for common tasks
  (discover, asset-inspect, component-inspect, find-references, mutate-then-verify,
  verify-api-before-coding).

The bands are *planning hints*, not measurements — real payloads can fall
outside the range on very large or very small inputs; `page_size` is the
mechanism that enforces an actual cap.

The MCP `initialize` response also carries a rich `instructions` string
(client-injected into the system prompt where supported) covering payload
sizing + paging, the Unity-API verification workflow (`search_assets` →
`find_members` → `type_schema` → `execute_csharp`), the mutate→gate→fix loop
with inline `logs`, and offline / bridge-offline recovery.

### Per-call `logs` (inline console capture)

Every mutating-tool response carries a `logs` array — Unity console
entries emitted *during that specific call* (checkpoint + validate + mutate),
captured as a before/after delta by the bridge:

```json
"logs": [
  { "severity": "warning", "message": "...", "source": "unity" }
]
```

- `severity`: `info` | `warning` | `error`
- `source`: the origin. Currently always `"unity"` for console-captured
  entries (reserved for future `gate` / `bridge` sources).
- Stacks are omitted from inline logs (compact). Use `unity_senses_read_console`
  for the global console buffer with stack traces.

This surfaces warnings/errors inline so an agent does not have to poll
`read_console` after every mutation to learn what happened. The field is always
present (empty `[]` when nothing was emitted); it does not replace
`read_console`, which reads the whole console. Read-only (direct-response)
tools also carry a `logs` sibling (usually empty), spliced into their flat
output object.

### Three-surface `gameobject_modify` (RFC 7396 JSON Merge Patch)

`unity_open_mcp_gameobject_modify` accepts three additive surfaces on top of
its legacy flat fields, so several kinds of change ship in one call:

- **`gameObjectDiffs`** — root-target patches grouped in one object (`name`,
  `tag`, `layer`, `active`, `position`, `rotation`, `scale`, `local_space`).
  Same field shape as the legacy flat fields; takes precedence over them when
  present.
- **`pathPatchesPerGameObject`** — `{childPath: diffs}` applied to descendants
  of the target. Each key is a slash-delimited path relative to the target
  (e.g. `"Body/Arm"`); the value is the same diffs shape as `gameObjectDiffs`.
- **`jsonPatchesPerGameObject`** — `{componentTypeName: mergePatch}` applied to
  the target's components via reflection. The key names a component type on the
  target (class name first, then full name); the value is a RFC 7396 merge patch
  `{field: value}` reusing `object_modify`'s value shape (scalars, `[x,y,z]`
  vectors, `{"path":...}` refs).

Apply order: **jsonPatches → pathPatches → gameObjectDiffs/flat**. Per-entry
errors accumulate and never abort the batch (matches `object_modify` /
`component_modify`). When any path or json surface is present the response
carries a `surfaces` breakdown (`diffs` / `pathPatches` / `jsonPatches`, each
with `applied` and `failed`); a legacy (root-only) call keeps the original
compact result shape.

**`component_get` response shape.** Default `profile: compact` returns top-level
serialized `fields[]` (public `properties[]` omitted unless
`include_properties: true`). Each entry carries `path`, `type`, and `value`.
Two distinct truncation signals are reported so the agent can pick the right
remedy: the top-level `truncated` count is fields hidden by the `max_fields` cap
(raise `max_fields` or switch to `profile: full` to see them; `truncated_by`
names the limit), and when `page_size` is set the `pagination` block's
`truncated` count is fields after the current page window (page on via
`next_cursor`, form `component_get:<offset>`). Use `property_path` to read one
subtree without re-fetching the whole component.

## Error contract

Errors are returned as JSON with:

- `error.code`
- `error.message`

Every retryable failure returns a **machine-readable error code** so agents can branch recovery programmatically instead of parsing free-text. The codes an agent is most likely to see:

| Code | When | Agent recovery branch |
| --- | --- | --- |
| `bridge_offline` | Bridge listener unreachable (Unity not running, wrong port, toolbar off) | Open Unity with the bridge started; check the instance lock for the live port/pid. Offline reads + `read_compile_errors` still work. |
| `bridge_response_unparsable` | Bridge returned HTTP 200 with a SUBSTANTIAL but unparseable body. Usually a mutating tool's serialized output was corrupted (e.g. an `execute_csharp` snippet whose result threw a `TypeLoadException` during the reflective walk, leaving unbalanced JSON that got interpolated into the gate envelope). | Do not trust any partial output. Check `editor_status` / `bridge_status`, then retry. The bridge-side guard (`OutputSerializer` output validated before it enters the envelope) prevents the common case, so this is now rare. **Note:** an EMPTY body on a compile-reload tool is NOT this error — it is the `triggered_reload` outcome (see below), because the empty-body signature means a domain reload tore down the bridge's HTTP socket mid-response and the mutation very likely committed. |
| `triggered_reload` (outcome, not an error) | A compile-reload tool (`reimport_package`, `asmdef_create`/`asmdef_modify`, `package_add`/`package_remove`, `script_write`, `execute_csharp`, …) returned HTTP 200 with an EMPTY body. This is the signature of a domain reload that the mutation itself triggered — the bridge tore down its HTTP socket for the reload and the in-flight response was lost before any bytes were written. The mutation almost certainly committed (compile-reload tools commit before the reload begins). Returned as `isError:false` with `status:"triggered_reload"`, NOT as `bridge_response_unparsable`, so the agent does not misread a successful operation as corruption. | Do NOT retry blindly — that would re-run the mutation. Verify post-state instead: poll `editor_status` / `bridge_status` until the bridge reports running and not compiling, then `read_compile_errors` to confirm no CSxxxx errors. The response carries `agentNextSteps[]` spelling this out. |
| `bridge_compile_failed` | Unity PID alive but heartbeat stale — bridge assembly failed to recompile (Safe Mode). Also fires on **cold Safe Mode**: Unity launched straight into Safe Mode before the bridge's `[InitializeOnLoad]` ever wrote an instance lock, detected by a process scan for a live Unity editor matching this project's `-projectPath`. | Call `read_compile_errors` (reads `Editor.log` offline, survives the dead bridge); fix the CS error; trigger a recompile. **Branch on the reported `issues[].kind`:** if `editor_fd_exhaustion` is present, the Editor is hung on an internal file-descriptor limit (not a code error) — there is no CS fix; the operator must restart Unity to recover. **Exception:** during/after `unity_senses_run_tests`, a domain reload can freeze the heartbeat briefly and look like a dead bridge — the MCP server recognizes an in-flight run (a fresh `test-pending-*.json` signal) and keeps waiting instead of failing fast, so this code should not surface for ~5 min after a run starts. If it does, poll `ping` / `editor_status` once before treating it as a real compile failure. |
| `main_thread_blocked` | The Unity main thread did not pick up the dispatch within the per-call timeout — a Unity modal dialog (unsaved changes, scene modified externally, safe mode) or a long editor operation is blocking it. The dispatch never started, so do NOT raise `timeout_ms`. | Recovery hints are in `agentNextSteps[]`: if a scene is dirty call `scene_save` then retry; check `editor_status` / `bridge_status`; set `UNITY_OPEN_MCP_DIALOG_POLICY` / `UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS=1` to let the dismiss loop handle the modal; if unreachable, restart the editor. Distinct from a plain timeout (which means the work started but ran long). |
| `compile_timeout` | Compile-wait exceeded (`UNITY_OPEN_MCP_COMPILE_WAIT_MS`, default 120s) | Re-probe `ping` / `bridge_status`; if still compiling, wait. If a dead-bridge signature appears, switch to `read_compile_errors`. |
| `editor_instance_locked` | `compile_check` or a verify-family scan (`scan_all` / `baseline_create` / `regression_check`) while a live Editor holds the project lock | Response carries a structured `agentNextSteps[]`. Close the live Editor for a headless run, or — for verify scans — use the live-bridge `validate_edit` / `scan_paths` for a scoped health check instead. For `compile_check`, verify compile state without closing via `read_compile_errors`, or `reimport_package` (with `dllMtimeBefore`/`After`) to confirm a specific local package recompiled. |
| `unity_not_discovered` | Batch spawn attempted but no Unity Editor executable was found (auto-discovery scanned Hub paths and found nothing; `UNITY_PATH` unset) | Install Unity under the OS-default Hub path, or set `UNITY_PATH` to an explicit editor executable. Distinct from `unity_path_not_found` (explicit path missing/inaccessible). |
| `unity_spawn_refused` | The Unity binary could not be executed (spawn `ENOENT`/`EACCES`, or exit code `127`) | Do not retry `compile_check` blindly. Verify `UNITY_PATH` points to a valid executable, then fall back to `read_compile_errors` / `bridge_status` to check compile state without headless spawn. |
| `batch_spawn_failed` | Headless Unity ran but produced no JSON markers (and no CS compiler errors or project-lock signature to classify) | Response carries `agentNextSteps[]` pointing at `read_compile_errors`, `reimport_package`, and lock/path checks. Distinct from `unity_spawn_refused` (binary never ran) and `unity_not_discovered` (no binary found). |
| `compile_noop` | Recompile reported success but the bridge tool registry + `ScriptAssemblies/*.dll` mtime did not advance (incremental no-op). Surfaced as an additive `_compileVerify` annotation on a successful result, not an error. | Do not trust the success alone; force a rebuild (no-op `package_add`/`package_remove`, or operator refocus), then re-check DLL mtime. |
| `dll_stale` | After a recompile, `Library/ScriptAssemblies/*.dll` is older than the source edit — the new code was not compiled in. Surfaced as `_compileVerify`. | Expected for local `packages/` source (outside Unity's `Assets/` watch root); trigger a recompile and verify DLL mtime > source mtime. |
| `scene_dirty` | A mutating op that can disrupt the editor was refused because a loaded scene has unsaved changes | Save or discard the scene first, pass `ignore_scene_dirty: true` to accept the risk, or enable `autoSaveDirtyScenes` in `.unity-open-mcp/settings.json` (or launch Unity with `UNITY_OPEN_MCP_AUTO_SAVE_DIRTY_SCENES=1`) so the bridge saves dirty scenes before the guard refuses. For stuck modals, set `UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS=1` on the MCP server process (destructive). |
| `bridge_unavailable` | Event stream / offline-first read needs a live bridge that is down | Same as `bridge_offline`. |

Settle / retry behavior is governed by the tool's lifecycle class (`none` / `compile-reload` / `modal-dialog` / `scene-dirty` / `process-stale` — see [Lifecycle policy](#lifecycle-policy)). The retry tunables are env-overridable: `UNITY_OPEN_MCP_COMPILE_WAIT_MS`, `UNITY_OPEN_MCP_COMPILE_POLL_INTERVAL_MS`, `UNITY_OPEN_MCP_TRANSIENT_RETRY_ATTEMPTS`, `UNITY_OPEN_MCP_TRANSIENT_BACKOFF_MS`.

## Multi-agent scheduling

When multiple agents share one MCP stdio process — or multiple MCP processes target one bridge — two primitives keep them from starving each other:

**Per-request port override.** A tool call may carry `_meta.port` (or a top-level `port`) to route to a specific bridge instance, bypassing the default port resolution. `_meta.agentId` optionally overrides the per-process agent identity. Both are stripped before the call reaches the bridge. This is the parallel-safe primitive: agents sharing one MCP process each target their own bridge.

```json
{ "_meta": { "port": 24678, "agentId": "agent-coworker-1" }, "gate": "enforce", "paths_hint": ["Assets"] }
```

**Fair round-robin queue (bridge-side).** When ≥2 distinct agents (by `X-Agent-Id` header) share one bridge instance, the bridge schedules requests fairly per Editor frame: up to **N reads per frame** (default 5, round-robin across agents) + exactly **1 write per frame** (serialized). This prevents a write-heavy agent from starving read-heavy agents. The queue is **opt-in**: single-agent traffic bypasses scheduling entirely (dispatched immediately, identical to the pre-queue path); the fair scheduler activates only when a second agent arrives.

Configurable in `.unity-open-mcp/settings.json`:

- `fairQueueEnabled` (default `true`) — kill-switch; when `false`, every request is dispatched FIFO with no per-frame batching.
- `fairQueueReadsPerFrame` (default `5`, clamped to [1, 50]) — read-batch size per Editor frame.
- `editorSettleCapMs` (default `5000`) / `restartSettleCapMs` (default `60000`) — compile-settle wait caps, clamped to [1000, 120000].

The agent identity sent as `X-Agent-Id` is `agent-<pid>-<6 hex chars>` per MCP process (pid for cross-process disambiguation, hex suffix for uniqueness within a pid); `_meta.agentId` overrides it per call.

## Bridge admin tools (operator-only)

A small surface for operators and tooling that needs a coarse bridge health signal stronger than a raw `/ping`. These tools carry **no tool-group assignment** → they sit in the always-visible meta-tool bucket, but they are intentionally **not documented in the agent skill** (`skills/unity-open-mcp/SKILL.md`): they exist for the Validation Suite app and operators driving manual offline scenarios, not for general agent workflow. Agents use `unity_open_mcp_ping` / `read_compile_errors` for health.

### `unity_open_mcp_bridge_status`

Wraps the instance-lock classifier (`instance-discovery.ts#classifyInstance`) + one `/ping` probe and returns a coarse `status` token the Validation Suite drives its manual offline-scenario gate off.

```json
{
  "status": "running",            // running | compiling | stopped | dead_bridge | unreachable
  "ready": true,                  // true only when connected AND idle
  "projectPath": "/abs/project",
  "classification": "healthy",    // top-level mirror of instance.classification
  "recoveryHint": null,           // { tool, reason } when status has a recovery tool
  "instance": {
    "lockPath": "~/.unity-open-mcp/instances/<sha256>.json",
    "classification": "healthy",  // healthy | reloading | dead_bridge | gone
    "lock": { "pid": 12345, "port": 24678, "state": "idle", ... },
    "unityProcessPid": 4242       // present only on cold Safe Mode (no lock + live Unity process)
  },
  "ping": { "reachable": true, "connected": true, "compiling": false, ... },
  "nextStep": "Bridge is ready. Proceed with live-only MCP tools.",
  "_source": "local"
}
```

`status` derivation:

| Lock classification        | /ping         | `status`       |
| -------------------------- | ------------- | -------------- |
| `dead_bridge`              | —             | `dead_bridge`  |
| —                          | reachable, `compiling: true` | `compiling` |
| —                          | reachable, `connected: true` | `running`   |
| —                          | unreachable   | `unreachable` (Unity alive, listener down — reload window / toolbar off) |
| `gone`                     | unreachable + Unity process matches project | `dead_bridge` (cold Safe Mode) |
| otherwise                  | —             | `stopped`      |

`stopped` intentionally folds two indistinguishable cases from the MCP-server's vantage point: Unity is not running at all, OR Unity is running but the operator toggled the bridge off via the toolbar. `instance.lock` in the response disambiguates (`lock === null` → no Unity; `lock.pid` alive but no listener → toolbar off). The tool **never** errors on an offline bridge — `stopped` IS the answer in that case. Read-only, gate-free, never spawns Unity.

**Cold Safe Mode detection.** When Unity launches straight into Safe Mode, the bridge's `[InitializeOnLoad]` never runs and **no instance lock is written**, so `classifyInstance` returns `gone` — the same classification as "Unity is not running." Previously this folded into the generic `stopped` with no hint that `read_compile_errors` was the right next step. The server now scans for a live Unity process whose command line references this project (`-projectPath <project>`); a match reuses the `dead_bridge` status token + `recoveryHint` so the recovery path is identical to the mid-session dead-bridge case. The `instance.unityProcessPid` field carries the scanned PID so operators can confirm the diagnosis. Cross-platform: macOS (`ps`) + Windows (PowerShell `Get-CimInstance`); Linux and the bare-editor (no `-projectPath`) case are known false negatives that fall through to `stopped` (no regression — the Hub has the same limitation without `lastLaunchPid`). The scan runs only when the lock is `gone` AND `/ping` is unreachable, so the happy path never pays the cost.

**`classification` + `recoveryHint` (M23 Plan 2).** The top-level `classification` mirrors `instance.classification` so agents can branch on a single field. `recoveryHint` is non-null only when the status has a specific recovery tool — today, `dead_bridge` returns `{ tool: "unity_open_mcp_read_compile_errors", reason: "..." }` and every other status returns `null`. A `dead_bridge` signature (live PID + stale heartbeat, OR cold Safe Mode with no lock + a live Unity process) usually means Unity is sitting in Safe Mode / showing compile errors, so the hint and `nextStep` wording name Safe Mode explicitly rather than the generic "toolbar off" reading. It can also mean the Editor has **hung** on an internal file-descriptor limit (`editor_fd_exhaustion` in the `read_compile_errors` issues list) after many domain reloads — in that case there is no CS fix; the operator must restart Unity. Call `read_compile_errors` and branch on `issues[].kind` to tell the two apart. This is the machine-readable recovery signal — `ping` / live tools return `bridge_compile_failed` with the same wording when the dead-bridge signature is observed mid-call or in the cold-start case.

### Deferred: `bridge_stop` / `bridge_start`

`bridge_stop` and `bridge_start` are **not** shipped. Two reasons, recorded so a future revisit has context:

1. **No bridge HTTP route for start/stop.** Today only the Unity Editor toolbar toggles the bridge (`packages/bridge/Editor/UI/BridgeToolbarToggle.cs`). Adding those tools means new bridge-side work — a `/bridge/stop` + `/bridge/start` route that calls `BridgeHttpServer.Stop()/Start()` on the main thread.
2. **Self-disconnect hazard on `stop`.** The `stop` request arrives over the very HTTP listener it is about to tear down. A correct implementation must send the response *before* the listener stops (deferred / async stop), or the caller gets a connection-reset instead of the OK.

Offline scenarios are therefore **operator-driven** in v1: stop/start via the toolbar, gated by `manual` setup actions and confirmed by `bridge_status` (or `ping` / the CLI `wait-for-ready`). Revisit the stop/start routes only if the manual pattern proves too painful in practice.

## Unity Hub control tools

A six-tool surface (`unity_open_mcp_hub_*`) for managing Unity Editor installs **without** a running Unity Editor or bridge connection. All are **local-routed** — resolved entirely in the MCP server from filesystem discovery, Unity's public download-archive feed, the `unityhub://` deep link, and (for install-path management) the Hub headless CLI. Activate the `unity-hub-control` group via `manage_tools` to surface them.

The install path is the `unityhub://` deep link (single-instance install inside Unity Hub with its native progress UI), not a headless `Unity Hub --headless install` subprocess — that earlier approach spawned a fresh Hub process per call and showed a disconnected window. The deep link brings the Hub to the foreground at its real install dialog.

| Tool | Purpose | Backend | Mutating |
|---|---|---|---|
| `hub_list_editors` | List installed Unity editors (version, path, build-target platforms from `Data/PlaybackEngines/`, release stream) | Filesystem scan of OS-default Hub roots (`+ UNITY_HUB` env) | no |
| `hub_available_releases` | List Unity versions available for download (version, stream, release date, release-notes URL, changeset) | Unity's public download-archive page (`unity.com/releases/editor/archive`); falls back to a bundled snapshot (`stale: true`) on network failure | no |
| `hub_install_editor` | Install a Unity version (+ optional changeset) via the `unityhub://` deep link | OS URL handler (`open` / `xdg-open` / `start`) | yes |
| `hub_install_modules` | Add platform modules to an installed editor (opens the Hub's install dialog) | OS URL handler (`unityhub://` deep link) | yes |
| `hub_get_install_path` | Get the default editor install directory | Hub headless CLI (`install-path`), falls back to filesystem inference | no |
| `hub_set_install_path` | Set the default editor install directory | Hub headless CLI (`install-path --set <path>`) | yes |

**Mutating ops are system-level, not project-asset.** `install_editor`, `install_modules`, and `set_install_path` install software or change Hub configuration at the system level, so `paths_hint` is N/A and the call is gate-free (the gate validates project-asset-reference fallout, which does not apply). They still return the gate-consistent `mutation` / `gate` / `agentNextSteps` envelope shape so agents parse them uniformly — `gate.skipped` is `true` and `gate.mode` is `"off"`.

**No in-call install completion detection.** The install runs inside Unity Hub, which owns the process. The `install_editor` / `install_modules` calls return once the deep link is accepted, not when the download finishes — poll `hub_list_editors` afterwards to confirm the new editor / modules landed.

**Cross-platform Hub CLI resolution.** `get_install_path` and `set_install_path` need the Hub binary directly (the deep link cannot set the install path). Resolution honors the `UNITY_HUB_PATH` env var, then per-platform defaults:

| Platform | Default Hub binary |
|---|---|
| macOS | `/Applications/Unity Hub.app/Contents/MacOS/Unity Hub` |
| Windows | `%ProgramFiles%\Unity Hub\Unity Hub.exe` |
| Linux | `~/Unity Hub/Unity Hub` |

When the Hub binary is not found, `get_install_path` falls back to filesystem inference (and reports `source: "filesystem"`); `set_install_path` returns a structured `hub_cli_not_found` error pointing at `UNITY_HUB_PATH`.

Example `hub_list_editors` response:

```json
{
  "editors": [
    {
      "version": "6000.3.18f1",
      "path": "/Applications/Unity/Hub/Editor/6000.3.18f1/Unity.app/Contents/MacOS/Unity",
      "platforms": ["Android", "iOS", "WebGL"],
      "releaseType": "LTS"
    }
  ],
  "count": 1,
  "_source": "local"
}
```

Example `hub_install_editor` response (envelope shape):

```json
{
  "mutation": { "success": true, "output": { "deepLink": "unityhub://6000.3.18f1/5ebeb53e4c07", "version": "6000.3.18f1", "changeset": "5ebeb53e4c07" }, "error": null },
  "gate": { "mode": "off", "skipped": true, "validation": null, "delta": null },
  "agentNextSteps": [
    "Unity Hub opened at the install dialog for 6000.3.18f1. The download runs inside the Hub (watch its progress UI).",
    "There is no in-call completion detection — poll unity_open_mcp_hub_list_editors after the Hub finishes to confirm the new editor."
  ],
  "_source": "local"
}
```

## Automation CLI

The `unity-open-mcp` package ships a first-class CLI for CI / scripting (`npx unity-open-mcp <command>`). A single `bin` serves both MCP clients (no subcommand → stdio server) and automation (subcommand → CLI). Commands share the same router stack the MCP server uses, so a CLI `run-tool` call returns byte-for-byte the same JSON an MCP client would receive.

### Commands

| Command | Purpose | Maps to |
|---|---|---|
| `ping` | One `/ping` against the bridge | `unity_open_mcp_ping` |
| `wait-for-ready` | Poll until the bridge is responsive (or timeout) | `/ping` polling loop |
| `status` | Resolved port, instance lock, readiness, compat | `unity_open_mcp_bridge_status` |
| `run-tool <name>` | Invoke any MCP tool from CI | any tool by name |
| `stream-events` | Drain bridge SSE events (console logs + state) to stdout | `unity_senses_pull_events` |
| `verify [paths...]` | Run a verify scan; 4-level exit code | `scan_paths` / `validate_edit` / `scan_all` |
| `baseline create\|update` | Create or refresh the regression baseline | `unity_open_mcp_baseline_create` |
| `regression check` | Compare current scan against the baseline | `unity_open_mcp_regression_check` |

### Exit-code contract

`verify`, `baseline`, and `regression` follow a 4-level exit code so CI pipelines can branch on outcome without parsing JSON:

| Code | Meaning | When |
|---|---|---|
| 0 | Success | no issues / no regression |
| 1 | Warnings only | only severities below `--fail-on-severity` |
| 2 | Errors | issues at/above threshold, or a regression detected |
| 3 | Timeout | bridge unreachable, or a call timed out |

`ping` / `wait-for-ready` / `status` / `run-tool` keep their simpler binary exit codes (0 success, non-zero failure); the 4-level contract applies to the verify/regression family.

### `--json` everywhere

Every command accepts `--json` and emits a machine-readable payload. Without `--json`, output is human-readable (success on stdout, failures on stderr).

### CI templates

Bundled templates for common CI providers live under [`docs/ci/`](../ci/README.md) — a GitHub Actions workflow and a GitLab CI pipeline covering the health-check → verify-on-PR → regression-on-main shape.

## Source references

- `mcp-server/src/tools/index.ts`
- `mcp-server/src/tool-router.ts`
- `mcp-server/src/batch-spawn.ts`
- `mcp-server/src/compressible-router.ts`
- `mcp-server/src/capabilities/build-capabilities.ts`
- `mcp-server/src/capabilities/tool-groups.ts` — canonical tool-group catalog (single source of truth).
- `mcp-server/src/tool-session-state.ts` — per-session visibility store + ListTools filter.
- `mcp-server/src/tools/bridge-status.ts` — operator-only bridge admin tool.
- `mcp-server/src/cli/` — automation CLI (`args.ts`, `commands.ts`, `cli.ts`, `exit-codes.ts`, `ping-poller.ts`).

