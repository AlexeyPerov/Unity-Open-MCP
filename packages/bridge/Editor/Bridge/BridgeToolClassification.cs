using System.Collections.Generic;

namespace UnityOpenMcpBridge
{
    // The three compiled-in tool classification tables. These classify tools by
    // how the HTTP dispatcher routes them:
    //   - KnownTools:           every tool name the bridge compiled in (legacy +
    //                            typed-editor). Superset of the other two. Used by
    //                            GET /tools and the request router's known-tool
    //                            check.
    //   - DirectResponseTools:  gate-free tools that return their JSON directly,
    //                            skipping the checkpoint/validate/delta envelope
    //                            (read-only tools, or editor-state mutators that
    //                            write no assets the gate can validate).
    //   - MutatingTools:        tools that run the full gate path and require a
    //                            non-empty paths_hint.
    //
    // Named BridgeToolClassification (not BridgeToolCatalog) to avoid clashing
    // with the UI-side BridgeToolCatalog in Editor/UI/, which builds the
    // Tools-tab catalog of titles/parameters/mutability hints — a different
    // concern.
    //
    // Registry-discovered tools ([BridgeTool] attribute, BridgeToolRegistry) are
    // NOT listed here — they are picked up automatically and carry their own
    // IsMutating/Gate/Group metadata. Per packages/bridge/AGENTS.md §Tool
    // registration: only add to these sets when the tool is NOT registry-discovered.

    internal static class BridgeToolClassification
    {
        internal static readonly HashSet<string> KnownTools = new()
        {
            "unity_open_mcp_execute_csharp",
            "unity_open_mcp_invoke_method",
            "unity_open_mcp_execute_menu",
            // M27 Plan 4 — live batch_execute meta-tool. Runs many nested typed
            // tools sequentially inside the open Editor; classified as mutating
            // (the batch can include scene/asset mutators) so paths_hint is
            // required at the batch level (the union of nested scopes). NOT
            // headless batchCapable — it is never spawned via -batchmode.
            "unity_open_mcp_batch_execute",
            "unity_open_mcp_find_members",
            "unity_open_mcp_validate_edit",
            "unity_open_mcp_checkpoint_create",
            "unity_open_mcp_delta",
            "unity_open_mcp_find_references",
            // unity_open_mcp_dependencies is registry-discovered (M22 Plan 3 /
            // T-fix-1) — it carries its own IsMutating/Gate metadata via the
            // [BridgeTool] attribute and is NOT listed in any of these sets.
            "unity_open_mcp_scan_paths",
            "unity_open_mcp_apply_fix",
            "unity_open_mcp_reserialize",
            "unity_open_mcp_read_asset",
            "unity_open_mcp_search_assets",
            // M16 Plan 1 — typed asset/material/shader/prefab tools.
            "unity_open_mcp_assets_create_folder",
            "unity_open_mcp_assets_copy",
            "unity_open_mcp_assets_move",
            "unity_open_mcp_assets_delete",
            "unity_open_mcp_assets_refresh",
            "unity_open_mcp_material_create",
            "unity_open_mcp_material_get_properties",
            "unity_open_mcp_material_set_property",
            "unity_open_mcp_material_get_keywords",
            "unity_open_mcp_material_set_keyword",
            "unity_open_mcp_material_set_shader",
            "unity_open_mcp_shader_list_all",
            "unity_open_mcp_shader_get_data",
            "unity_open_mcp_prefab_instantiate",
            "unity_open_mcp_prefab_create",
            "unity_open_mcp_prefab_open",
            "unity_open_mcp_prefab_close",
            "unity_open_mcp_prefab_save",
            "unity_open_mcp_prefab_apply",
            "unity_open_mcp_prefab_revert",
            "unity_open_mcp_prefab_unpack",
            "unity_open_mcp_prefab_get_overrides",
            "unity_open_mcp_prefab_status",
            // M16 Plan 2 — typed GameObject/component tools.
            "unity_open_mcp_gameobject_create",
            "unity_open_mcp_gameobject_destroy",
            "unity_open_mcp_gameobject_duplicate",
            "unity_open_mcp_gameobject_find",
            "unity_open_mcp_gameobject_modify",
            "unity_open_mcp_gameobject_set_parent",
            "unity_open_mcp_component_add",
            "unity_open_mcp_component_destroy",
            "unity_open_mcp_component_get",
            "unity_open_mcp_component_modify",
            "unity_open_mcp_component_list_all",
            // M16 Plan 3 — typed scene management tools.
            "unity_open_mcp_scene_create",
            "unity_open_mcp_scene_open",
            "unity_open_mcp_scene_save",
            "unity_open_mcp_scene_unload",
            "unity_open_mcp_scene_set_active",
            "unity_open_mcp_scene_list_opened",
            "unity_open_mcp_scene_get_data",
            "unity_open_mcp_scene_get_dirty_summary",
            "unity_open_mcp_scene_focus",
            "unity_open_mcp_sceneview_get_camera",
            "unity_open_mcp_sceneview_set_camera",
            // M16 Plan 4 — typed Package Manager tools.
            "unity_open_mcp_package_list",
            "unity_open_mcp_package_search",
            "unity_open_mcp_package_add",
            "unity_open_mcp_package_remove",
            "unity_open_mcp_reimport_package",
            "unity_open_mcp_package_get_info",
            "unity_open_mcp_package_get_dependencies",
            "unity_open_mcp_package_check",
            // M16 Plan 5 — typed console / editor state / selection / undo /
            // tags / layers tools.
            "unity_open_mcp_console_clear",
            "unity_open_mcp_console_log",
            "unity_open_mcp_editor_set_state",
            "unity_open_mcp_selection_get",
            "unity_open_mcp_selection_set",
            "unity_open_mcp_editor_undo",
            "unity_open_mcp_editor_redo",
            "unity_open_mcp_editor_undo_history",
            "unity_open_mcp_editor_clear_history",
            "unity_open_mcp_editor_get_tags",
            "unity_open_mcp_editor_get_layers",
            "unity_open_mcp_editor_add_tag",
            "unity_open_mcp_editor_add_layer",
            // M16 Plan 6 — typed reflection / scripts / object data tools.
            "unity_open_mcp_type_schema",
            "unity_open_mcp_script_read",
            "unity_open_mcp_script_write",
            "unity_open_mcp_script_delete",
            "unity_open_mcp_object_get_data",
            "unity_open_mcp_object_modify",
            // M16 Plan 7 — typed profiler session / diagnostics tools. Most
            // mutate editor state but write no assets (gate-free); save_data
            // writes a .json snapshot (MutatingTools below).
            "unity_open_mcp_profiler_start",
            "unity_open_mcp_profiler_stop",
            "unity_open_mcp_profiler_get_status",
            "unity_open_mcp_profiler_get_config",
            "unity_open_mcp_profiler_set_config",
            "unity_open_mcp_profiler_list_modules",
            "unity_open_mcp_profiler_enable_module",
            "unity_open_mcp_profiler_clear_data",
            "unity_open_mcp_profiler_save_data",
            "unity_open_mcp_profiler_load_data",
            "unity_open_mcp_profiler_get_script_stats",
            // M16 Plan 8 — typed gate intelligence tools (read-only).
            "unity_open_mcp_impact_preview",
            "unity_open_mcp_gate_budget_estimate",
            "unity_open_mcp_mutation_explain",
            // M16 Plan 9 — typed build pipeline + project-settings tools. The
            // read members are gate-free; build_set_target / build_set_scenes /
            // build_set_defines / settings_set_* / build_start run the full
            // gate path (build_start additionally requires the deny bypass).
            "unity_open_mcp_build_get_targets",
            "unity_open_mcp_build_get_active_target",
            "unity_open_mcp_build_set_target",
            "unity_open_mcp_build_get_scenes",
            "unity_open_mcp_build_set_scenes",
            "unity_open_mcp_build_start",
            "unity_open_mcp_build_get_defines",
            "unity_open_mcp_build_set_defines",
            "unity_open_mcp_settings_get_player",
            "unity_open_mcp_settings_set_player",
            "unity_open_mcp_settings_get_quality",
            "unity_open_mcp_settings_set_quality",
            "unity_open_mcp_settings_get_physics",
            "unity_open_mcp_settings_set_physics",
            "unity_open_mcp_settings_get_lighting",
            "unity_open_mcp_settings_set_lighting",
            "unity_senses_run_tests",
            "unity_senses_screenshot",
            // M20 Plan 1 / T20.1.1 — senses parity residual. screenshot_camera
            // (arbitrary pose, file path) and capture_inline (inline base64 PNG)
            // extend the screenshot surface. Both are registry-discovered and
            // also listed here so they return tool JSON directly (matching
            // unity_senses_screenshot) — capture_inline's inlineImage field must
            // surface at the top level for the MCP server to unwrap into an
            // image content block.
            //
            // M20 Plan 1 / T20.1.2 — screenshot_window captures an EditorWindow
            // (Win-only full-fidelity, cross-platform best-effort readback).
            "unity_senses_screenshot_camera",
            "unity_senses_capture_inline",
            "unity_senses_screenshot_window",
            "unity_senses_read_console",
            "unity_senses_profiler_capture",
            "unity_senses_profiler_memory",
            "unity_senses_profiler_rendering",
            "unity_senses_spatial_query",
            // M20 Plan 5 / T20.5.1 — typed ScriptableObject create + list-by-
            // type. scriptableobject_create is mutating; list_assets_of_type is
            // read-only (DirectResponseTools).
            "unity_open_mcp_scriptableobject_create",
            "unity_open_mcp_list_assets_of_type",
            // M20 Plan 5 / T20.5.2 — typed Assembly Definition tools. asmdef_list
            // / asmdef_get are read-only; asmdef_create / asmdef_modify are
            // mutating (MutatingTools).
            "unity_open_mcp_asmdef_list",
            "unity_open_mcp_asmdef_get",
            "unity_open_mcp_asmdef_create",
            "unity_open_mcp_asmdef_modify",
            // M20 Plan 9 / T20.9.2 — KV preferences. PlayerPrefs + EditorPrefs
            // write to the registry / Library/PlayerPreferences (NOT project
            // assets), so they are mutating editor state but route as gate-free
            // direct-response tools (mirrors editor_undo / editor_redo).
            "unity_open_mcp_playerprefs_get",
            "unity_open_mcp_playerprefs_set",
            "unity_open_mcp_playerprefs_delete",
            "unity_open_mcp_editorprefs_get",
            "unity_open_mcp_editorprefs_set",
            "unity_open_mcp_editorprefs_delete",
            // M20 Plan 9 / T20.9.3 — Project Settings remainder. set_time +
            // set_quality_level write ProjectSettings/*.asset (MutatingTools);
            // get_time + get_render_pipeline are read-only (DirectResponseTools).
            "unity_open_mcp_settings_get_time",
            "unity_open_mcp_settings_set_time",
            "unity_open_mcp_settings_get_render_pipeline",
            "unity_open_mcp_settings_set_quality_level"
        };

        internal static readonly HashSet<string> DirectResponseTools = new()
        {
            "unity_open_mcp_validate_edit",
            "unity_open_mcp_checkpoint_create",
            "unity_open_mcp_delta",
            "unity_open_mcp_find_references",
            // unity_open_mcp_dependencies is registry-discovered (M22 Plan 3 /
            // T-fix-1) but stays in DirectResponseTools so it keeps returning
            // flat tool JSON (no gate envelope) — DispatchTool's switch falls
            // through to BridgeToolRegistry.TryDispatch for it.
            "unity_open_mcp_dependencies",
            "unity_open_mcp_scan_paths",
            // Compact drill-down reads: bridge returns the structured model JSON
            // directly; the MCP server applies the shared compression module.
            "unity_open_mcp_read_asset",
            "unity_open_mcp_search_assets",
            // Test runner: starts async test run, returns { status, runId } directly.
            "unity_senses_run_tests",
            // Agent senses (non-mutating): return tool JSON directly.
            "unity_senses_screenshot",
            // M20 Plan 1 / T20.1.1 — senses parity residual. screenshot_camera
            // + capture_inline are read-only (gate off, no scene dirty); route
            // as direct-response tools so the inlineImage field on capture_inline
            // surfaces at the top level for MCP-side unwrapping.
            //
            // M20 Plan 1 / T20.1.2 — screenshot_window is read-only (captures an
            // EditorWindow; transient repaint only, no asset/scene write).
            "unity_senses_screenshot_camera",
            "unity_senses_capture_inline",
            "unity_senses_screenshot_window",
            "unity_senses_read_console",
            "unity_senses_profiler_capture",
            "unity_senses_profiler_memory",
            "unity_senses_profiler_rendering",
            "unity_senses_spatial_query",
            // M16 Plan 1 — read-only typed tools (gate-free). They return JSON
            // directly without the gate envelope, matching search_assets/read_asset.
            "unity_open_mcp_material_get_properties",
            "unity_open_mcp_material_get_keywords",
            "unity_open_mcp_shader_list_all",
            "unity_open_mcp_shader_get_data",
            "unity_open_mcp_prefab_get_overrides",
            "unity_open_mcp_prefab_status",
            // M16 Plan 2 — read-only typed tools (gate-free).
            "unity_open_mcp_gameobject_find",
            "unity_open_mcp_component_get",
            "unity_open_mcp_component_list_all",
            // M16 Plan 3 — read-only typed tools (gate-free). scene_get_data
            // is the structured scene hierarchy read that supersedes the
            // standalone M10 scene snapshot.
            "unity_open_mcp_scene_list_opened",
            "unity_open_mcp_scene_get_data",
            "unity_open_mcp_scene_get_dirty_summary",
            // M20 Plan 9 / T20.9.4 — sceneview_get_camera is read-only and
            // gate-free. sceneview_set_camera mutates editor UI state and runs
            // the full gate path (MutatingTools).
            "unity_open_mcp_sceneview_get_camera",
            // M16 Plan 4 — read-only typed Package Manager tools (gate-free).
            // list / search / get_info hit UPM async requests;
            // get_dependencies / check read Packages/manifest.json directly.
            "unity_open_mcp_package_list",
            "unity_open_mcp_package_search",
            "unity_open_mcp_package_get_info",
            "unity_open_mcp_package_get_dependencies",
            "unity_open_mcp_package_check",
            // M16 Plan 5 — gate-free typed editor-state tools. They mutate
            // editor state (console, selection, play mode, undo/redo) but
            // write NO assets, so the gate (asset-reference validation) has
            // nothing to validate. editor_set_state runs its own inline dirty
            // guard (entering play mode can trigger Unity's native save modal).
            // editor_get_tags / editor_get_layers are pure reads.
            // editor_add_tag / editor_add_layer are NOT here — they write
            // TagManager.asset and run the full gate (see MutatingTools).
            "unity_open_mcp_console_clear",
            "unity_open_mcp_console_log",
            "unity_open_mcp_editor_set_state",
            "unity_open_mcp_selection_get",
            "unity_open_mcp_selection_set",
            "unity_open_mcp_editor_undo",
            "unity_open_mcp_editor_redo",
            // M20 Plan 9 / T20.9.4 — undo_history is read-only and gate-free.
            // clear_history is an irreversible reset and runs the full gate path
            // (MutatingTools).
            "unity_open_mcp_editor_undo_history",
            "unity_open_mcp_editor_get_tags",
            "unity_open_mcp_editor_get_layers",
            // M16 Plan 6 — read-only typed reflection / object tools (gate-
            // free). type_schema reflects on a type's members; script_read
            // reads a .cs file from disk; object_get_data reflects on a live
            // UnityEngine.Object. None mutate project state.
            "unity_open_mcp_type_schema",
            "unity_open_mcp_script_read",
            "unity_open_mcp_object_get_data",
            // M16 Plan 7 — typed profiler tools that mutate editor state but
            // write NO assets (start / stop / set_config / enable_module /
            // clear_data) plus the read-only members (get_status /
            // get_config / list_modules / load_data / get_script_stats). The
            // gate validates asset-reference fallout, which does not apply —
            // they route as direct-response tools. profiler_save_data is NOT
            // here — it writes a .json snapshot and runs the full gate
            // (MutatingTools).
            "unity_open_mcp_profiler_start",
            "unity_open_mcp_profiler_stop",
            "unity_open_mcp_profiler_get_status",
            "unity_open_mcp_profiler_get_config",
            "unity_open_mcp_profiler_set_config",
            "unity_open_mcp_profiler_list_modules",
            "unity_open_mcp_profiler_enable_module",
            "unity_open_mcp_profiler_clear_data",
            "unity_open_mcp_profiler_load_data",
            "unity_open_mcp_profiler_get_script_stats",
            // M16 Plan 8 — read-only gate intelligence tools (gate-free). They
            // compose checkpoint / validate / delta / run-history foundations
            // to project pre-mutation scope risk + cost and post-mutation
            // narrative. None mutate project state.
            "unity_open_mcp_impact_preview",
            "unity_open_mcp_gate_budget_estimate",
            "unity_open_mcp_mutation_explain",
            // M16 Plan 9 — read-only build + settings reads (gate-free). The
            // mutating members (build_set_target / build_set_scenes /
            // build_set_defines / build_start / settings_set_*) run the full
            // gate path (MutatingTools).
            "unity_open_mcp_build_get_targets",
            "unity_open_mcp_build_get_active_target",
            "unity_open_mcp_build_get_scenes",
            "unity_open_mcp_build_get_defines",
            "unity_open_mcp_settings_get_player",
            "unity_open_mcp_settings_get_quality",
            "unity_open_mcp_settings_get_physics",
            "unity_open_mcp_settings_get_lighting",
            // M20 Plan 5 / T20.5.1 — read-only typed list-by-type (gate-free).
            // Offline-routeable in principle (the offline YAML/GUID index can
            // answer t:<Type> filter queries).
            "unity_open_mcp_list_assets_of_type",
            // M20 Plan 5 / T20.5.2 — read-only typed asmdef reads (gate-free).
            // Both are offline-routeable (.asmdef is plain JSON).
            "unity_open_mcp_asmdef_list",
            "unity_open_mcp_asmdef_get",
            // M20 Plan 9 / T20.9.2 — KV preferences. PlayerPrefs + EditorPrefs
            // mutate editor state but write NO project assets (they target the
            // registry / Library/PlayerPreferences). Like editor_undo /
            // editor_set_state, the gate (asset-reference validation) has nothing
            // to validate, so all six members (get/set/delete × 2) route as
            // gate-free direct-response tools. The mutating nature is still
            // visible in the catalog/toggle (they are in KnownTools).
            "unity_open_mcp_playerprefs_get",
            "unity_open_mcp_playerprefs_set",
            "unity_open_mcp_playerprefs_delete",
            "unity_open_mcp_editorprefs_get",
            "unity_open_mcp_editorprefs_set",
            "unity_open_mcp_editorprefs_delete",
            // M20 Plan 9 / T20.9.3 — read-only ProjectSettings remainder.
            // get_time reads TimeManager (in-memory Time values); get_render_
            // pipeline probes GraphicsSettings. Neither writes a project asset.
            "unity_open_mcp_settings_get_time",
            "unity_open_mcp_settings_get_render_pipeline"
        };

        internal static readonly HashSet<string> MutatingTools = new()
        {
            "unity_open_mcp_execute_csharp",
            "unity_open_mcp_invoke_method",
            "unity_open_mcp_execute_menu",
            // M27 Plan 4 — batch_execute is mutating (its nested commands can
            // include scene/asset mutators). paths_hint is required at the
            // batch level and scopes the single batch-level gate cycle.
            "unity_open_mcp_batch_execute",
            "unity_open_mcp_apply_fix",
            "unity_open_mcp_reserialize",
            // M16 Plan 1 — typed asset/material/prefab mutators. Each requires
            // paths_hint; assets_refresh is a light mutation that may bind
            // whole-project scope when whole_project: true (handled below).
            "unity_open_mcp_assets_create_folder",
            "unity_open_mcp_assets_copy",
            "unity_open_mcp_assets_move",
            "unity_open_mcp_assets_delete",
            "unity_open_mcp_assets_refresh",
            "unity_open_mcp_material_create",
            "unity_open_mcp_material_set_property",
            "unity_open_mcp_material_set_keyword",
            "unity_open_mcp_material_set_shader",
            "unity_open_mcp_prefab_instantiate",
            "unity_open_mcp_prefab_create",
            "unity_open_mcp_prefab_open",
            "unity_open_mcp_prefab_close",
            "unity_open_mcp_prefab_save",
            "unity_open_mcp_prefab_apply",
            "unity_open_mcp_prefab_revert",
            "unity_open_mcp_prefab_unpack",
            // M16 Plan 2 — typed GameObject/component mutators. Each requires
            // paths_hint scoped to the scene that contains (or will contain)
            // the target. They touch scene hierarchy only — no asset writes.
            "unity_open_mcp_gameobject_create",
            "unity_open_mcp_gameobject_destroy",
            "unity_open_mcp_gameobject_duplicate",
            "unity_open_mcp_gameobject_modify",
            "unity_open_mcp_gameobject_set_parent",
            "unity_open_mcp_component_add",
            "unity_open_mcp_component_destroy",
            "unity_open_mcp_component_modify",
            // M16 Plan 3 — typed scene mutators. paths_hint is the scene asset
            // path (or scene hierarchy path for scene_focus). scene_open is
            // RestartThenSettle (Single-mode open can lose unsaved changes in
            // currently-open scenes — the dirty guard preflights it).
            "unity_open_mcp_scene_create",
            "unity_open_mcp_scene_open",
            "unity_open_mcp_scene_save",
            "unity_open_mcp_scene_unload",
            "unity_open_mcp_scene_set_active",
            "unity_open_mcp_scene_focus",
            // M20 Plan 9 / T20.9.4 — SceneView camera and Undo-history mutators.
            // set_camera mutates SceneView pose; clear_history irreversibly resets
            // the editor undo/redo stacks. Both require paths_hint and run the
            // full gate path even though they do not write project assets.
            "unity_open_mcp_sceneview_set_camera",
            "unity_open_mcp_editor_clear_history",
            // M16 Plan 4 — typed Package Manager mutators. Each writes
            // Packages/manifest.json and triggers package resolution; the
            // caller must scope paths_hint to "Packages/manifest.json"
            // (the lock file is touched implicitly).
            "unity_open_mcp_package_add",
            "unity_open_mcp_package_remove",
            // reimport_package force-reimports a local file: package's source
            // and nudges a recompile; it does not write Packages/manifest.json
            // (the package id IS the scope — see the paths_hint default in
            // BridgeHttpServer). RestartThenSettle covers the recompile; the
            // tool also returns dllMtimeBefore/After so a no-op recompile is
            // detectable (specs/feedback.md).
            "unity_open_mcp_reimport_package",
            // M16 Plan 5 — typed TagManager mutators. Each rewrites
            // ProjectSettings/TagManager.asset; the caller must scope
            // paths_hint to that asset. The other Plan 5 tools mutate editor
            // state (console, selection, play mode, undo/redo) but write NO
            // assets, so they are NOT mutating in gate terms — they route as
            // gate-free direct-response tools (see DirectResponseTools).
            "unity_open_mcp_editor_add_tag",
            "unity_open_mcp_editor_add_layer",
            // M16 Plan 6 — typed reflection / scripts / object mutators.
            // script_write creates/overwrites a .cs (Roslyn pre-validated);
            // script_delete removes .cs (+.meta). Both refresh AssetDatabase
            // (recompile / domain reload may follow). object_modify sets
            // public fields/properties on any live Object via reflection.
            // Each requires paths_hint scoped to the affected .cs path /
            // asset / scene.
            "unity_open_mcp_script_write",
            "unity_open_mcp_script_delete",
            "unity_open_mcp_object_modify",
            // M16 Plan 7 — typed profiler mutator. profiler_save_data writes a
            // .json snapshot to disk (composed from the read surfaces in this
            // tool family); the caller must scope paths_hint to the
            // destination .json path. The other Plan 7 tools mutate editor
            // state but write no assets and route as gate-free direct-
            // response tools (DirectResponseTools).
            "unity_open_mcp_profiler_save_data",
            // M16 Plan 9 — typed build + settings mutators. Each rewrites
            // ProjectSettings/*.asset (or EditorBuildSettings) and runs the
            // full gate path; the caller scopes paths_hint to the touched
            // ProjectSettings asset (see each tool's description). build_start
            // additionally requires the deny bypass (gate: "off" +
            // confirm_bypass: true) because BuildPipeline.BuildPlayer is on the
            // default deny list.
            "unity_open_mcp_build_set_target",
            "unity_open_mcp_build_set_scenes",
            "unity_open_mcp_build_set_defines",
            "unity_open_mcp_build_start",
            "unity_open_mcp_settings_set_player",
            "unity_open_mcp_settings_set_quality",
            "unity_open_mcp_settings_set_physics",
            "unity_open_mcp_settings_set_lighting",
            // M20 Plan 5 / T20.5.1 — typed ScriptableObject create. Writes a
            // .asset via AssetDatabase.CreateAsset; the caller scopes paths_hint
            // to the new asset path.
            "unity_open_mcp_scriptableobject_create",
            // M20 Plan 5 / T20.5.2 — typed asmdef mutators. Each writes the
            // .asmdef JSON + forces a reimport (recompile / domain reload). The
            // caller scopes paths_hint to the asset path; RestartThenSettle
            // covers the recompile settle window.
            "unity_open_mcp_asmdef_create",
            "unity_open_mcp_asmdef_modify",
            // M20 Plan 9 / T20.9.3 — Project Settings remainder mutators.
            // set_time writes ProjectSettings/TimeManager.asset; set_quality_level
            // writes ProjectSettings/Quality.asset. Each runs the full gate path
            // with paths_hint scoped to the touched asset (see each tool's
            // description).
            "unity_open_mcp_settings_set_time",
            "unity_open_mcp_settings_set_quality_level"
        };
    }
}
