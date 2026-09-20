# Routing and lifecycle

## Lifecycle & scene safety


| Policy                | Meaning                                                                             | Tools                                                                                                                                                                                                                              |
| --------------------- | ----------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `none`                | Read-only, returns immediately                                                      | `ping`, `find_members`, `validate_edit`, `checkpoint_create`, `delta`, `find_references`, `dependencies`, `scan_paths`, `read_asset`, `search_assets`, `list_assets`, `editor_status`, `read_console`, `screenshot`, `profiler_*`, `spatial_query`, `visual_compare` |
| `editor_settle`       | Mutating; bridge waits for asset refresh/serialization to finish                    | `apply_fix`, `reserialize`                                                                                                                                                                                                         |
| `restart_then_settle` | Mutating; may trigger domain reload; bridge blocks until compile finishes (cap 60s) | `execute_csharp`, `invoke_method`, `execute_menu`, `compile_check`                                                                                                                                                                 |
| `custom_confirmation` | Async; returns immediately, result via external completion signal you poll          | `run_tests`                                                                                                                                                                                                                        |


> The table above is the bridge's settle-timing surface (how long it blocks before returning). For the **recovery-axis** taxonomy — `none` / `compile-reload` / `modal-dialog` / `scene-dirty` / `process-stale`, describing what to do when a call fails or stalls — read `capabilities.tools[].lifecycle` (+ `lifecycleNote`) per tool, and `capabilities.lifecycleBlock` for the full class table. See the docs §Lifecycle policy for the `compile-reload` agent limits (notably `compile_check` is batch-only and returns `editor_instance_locked` when a live Editor holds the project lock).

**Active-scene dirty guard.** Before any `restart_then_settle` op, the bridge preflights loaded scenes. If any scene has unsaved changes, the call refuses with `error.code = "scene_dirty"` + `dirtyScenes[]` + `agentNextSteps` so Unity's native save modal never interrupts. Recover by: saving first (`scene_save`), discarding (`EditorSceneManager.RestoreSavedSceneState()`), or passing `ignore_scene_dirty: true` on `execute_csharp` / `invoke_method` / `execute_menu` / `scene_open` / `editor_set_state` / `build_set_target` / `build_set_defines` / `settings_set_player`.

`apply_fix`, `reserialize`, and the non-`scene_open` scene mutators (`scene_create` / `scene_save` / `scene_unload` / `scene_set_active` / `scene_focus` / `sceneview_set_camera`) are **not** guarded.

**Power-tool deny heuristic.** `execute_csharp` / `execute_menu` are blocked from destructive patterns by default (`EditorApplication.Exit`, `Application.Quit`, `AssetDatabase.DeleteAsset`, `BuildPipeline.BuildPlayer`, `File/Quit`, `TestRunnerApi`). Refused calls return `error.code = "denied_by_policy"` (csharp) or `"menu_blocked"` (menu) with the matched pattern + alternative. If you genuinely need one, set **both** `gate: "off"` and `confirm_bypass: true` — the bypass is audited.

**`execute_csharp` runs on Unity's main thread — never block it.** The snippet executes synchronously inline on `EditorApplication.update`, so any blocking primitive wedges the editor **unrecoverably**: `WaitOne` / `.Result` / `Thread.Sleep` / `while(!done)` waiting for a callback, and driving `TestRunnerApi` (which delivers its callbacks on the same main thread), all deadlock. The HTTP timeout fires on the worker thread and **cannot self-heal a stuck main thread** — no further editor tick runs, so the editor must be killed externally. The timeout envelope's `agentNextSteps` will tell you this; heed it (check `editor_status` / `bridge_status` before retrying, **don't** just raise `timeout_ms`). For test execution use `unity_senses_run_tests` (async, does not block). For everything else, prefer a typed tool or write the snippet to be non-blocking (fire-and-forget + poll via a follow-up call).

## Routing rules

Treat `capabilities.routePolicy` + `batchCapable` as source of truth. `batchCapable` is about the **headless batch-spawn fallback** only (`capabilities.routing.perToolFlagMeaning` says so inline) — it does not decide what may be a nested `batch_execute` step.

- **Live is the default** — when the bridge is connected, most tools route to `POST /tools/{name}` on the Editor.
- **Batch fallback** — spawns headless Unity (`-batchmode`) **only** for `batchCapable: true` tools when the live bridge is unavailable. Mutating meta-tools (`execute_csharp`, `invoke_method`, `execute_menu`) are blocked in batch — they need a live Editor.
- **Senses are live-only** — `run_tests`, screenshots, profiler, console, spatial queries have no batch form.
- **Offline reads** (`list_assets`, `find_references`, `read_asset`, `search_assets`) parse the project from disk and never need Unity. Coverage: text-serialized Unity YAML (`.prefab`/`.unity`/`.asset`/`.mat`/`.controller`/`.anim`/`.playable`/`.preset`/`.spriteatlas`/`.terrainlayer`/`.vfx`) **plus** JSON assets not covered by YAML-only parsers (`.asmdef`, `.shadergraph`/`.shadersubgraph`). `read_asset` also reconstructs the full hierarchy offline, parses prefab-variant overrides (matching `prefab_get_overrides`), and surfaces `integrity[]` signals (missing refs, missing scripts, malformed JSON, orphaned prefab instances) on the response — act on them before a `validate_edit` round-trip.
- **`dependencies` is live-only** — it reuses the verify `Dependencies.Scanner` (forward) + `ReferenceGraph` (reverse), both of which call AssetDatabase. No offline form.
- **`compile_check` is always batch** — spawns a fresh headless Unity that recompiles from scratch, even when the live bridge is up.
- **Verify scans (`scan_all`, `baseline_create`, `regression_check`) are always batch** — they run in the headless verify package and are not registered on the live bridge, so even with the bridge up they spawn fresh Unity (`_route.fallbackReason: "verify_always_batch"`). Expect `editor_instance_locked` when a live Editor holds the project lock — close it and retry, or use the live `validate_edit` / `scan_paths` for a scoped health check instead.


Snippets import `Object = UnityEngine.Object` automatically unless the caller supplies an Object alias. Snippets run in a separate assembly and can access public APIs. Use explicit reflection for inspection of internals; assembly-access bypass is not supported.

## Session-owned jobs

`unity_open_mcp_jobs` stays local and visible while Unity reloads or disconnects.
Use it only when an operation has an explicit job adapter; declarations alone do
not enable a job. `wait` observes for at most 30 seconds without owning execution.
A lost connection is not proof of failure: `orphaned` requires operation-specific
evidence before any retry. `cancel_requested` is not cancellation confirmation.
Terminal records and idempotency keys survive for 30 minutes in the same server
session, but not across a server restart. Keep routing and agent metadata stable.
