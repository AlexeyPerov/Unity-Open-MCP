// Lifecycle policy taxonomy + per-tool declarations.
//
// Every tool is classified into one of five lifecycle classes describing the
// *recovery* concern the agent must reason about when the call fails or the
// bridge becomes unresponsive: read-only (none), a domain reload
// (compile-reload), an OS modal (modal-dialog), a scene/prefab mutation that
// needs the gate + paths_hint (scene-dirty), or a long-running op where the
// bridge may stall (process-stale). The class is exposed per tool via
// `capabilities.tools[].lifecycle` so an agent can pick a recovery strategy
// before it calls, and the full taxonomy is attached as
// `capabilities.lifecycleBlock`.
//
// IMPORTANT — two distinct axes, intentionally not conflated:
//   - This module is the agent-facing RECOVERY axis (5 classes). It is the
//     source of truth for what an agent sees in `capabilities`.
//   - The bridge carries a separate internal SETTLE-TIMING axis
//     (None / EditorSettle / RestartThenSettle / CustomConfirmation) that
//     drives how long the dispatcher blocks before returning and whether the
//     active-scene dirty guard preflights. Agents do not see that enum
//     directly; it is an internal mechanism. The two axes agree because this
//     table is derived from the same per-tool knowledge, but they are not
//     renamed copies of each other — the recovery class is coarser and
//     oriented toward "what do I do when this goes wrong."
//
// This is a pure data module (no I/O, no cross-file runtime imports) so it
// loads cleanly under `node --experimental-strip-types` in tests.

// ---------------------------------------------------------------------------
// Lifecycle classes (5-class taxonomy).
// ---------------------------------------------------------------------------

export type LifecycleClass =
  | "none"
  | "compile-reload"
  | "modal-dialog"
  | "scene-dirty"
  | "process-stale";

export interface ToolLifecycle {
  /** Lifecycle class the tool declares. */
  class: LifecycleClass;
  /**
   * Optional short agent-facing constraint or secondary recovery concern.
   * Examples: `compile_check` notes its batch-only lock; `build_start` notes
   * the secondary scene-dirty concern; `package_add` notes the rare upgrade
   * modal. Absent when the class is self-describing.
   */
  note?: string;
}

// ---------------------------------------------------------------------------
// Per-tool declarations.
//
// Only tools whose class is NOT the safe default (`none`) are listed here —
// `lifecycleFor()` falls back to `{ class: "none" }` for any unlisted tool, so
// a read-only tool added without an entry surfaces correctly without an edit.
// `compile_check` is listed even though it is read-only re: game state because
// its lifecycle class is `compile-reload` (it spawns a recompiling Unity) and
// it carries the batch-only lock note agents must see.
//
// Keep this table in sync with the bridge's internal settle-timing map
// (`packages/bridge/Editor/Bridge/ToolLifecycle.cs`) and the lifecycle doc in
// `docs/api/mcp-tools.md` when a tool is added/removed/reclassified.
// ---------------------------------------------------------------------------

export const TOOL_LIFECYCLE: Record<string, ToolLifecycle> = {
  // ----- compile-reload: triggers or observes a domain reload --------------
  // The bridge blocks up to 60s for the compile to settle and the dirty guard
  // preflights these. `compile_check` is the batch-only observer: it spawns a
  // fresh headless Unity and cannot share the project with a live Editor.
  "unity_open_mcp_execute_csharp": { class: "compile-reload" },
  "unity_open_mcp_invoke_method": { class: "compile-reload" },
  "unity_open_mcp_execute_menu": { class: "compile-reload" },
  "unity_open_mcp_asmdef_create": { class: "compile-reload" },
  "unity_open_mcp_asmdef_modify": { class: "compile-reload" },
  "unity_open_mcp_script_write": { class: "compile-reload" },
  "unity_open_mcp_script_delete": { class: "compile-reload" },
  "unity_open_mcp_build_set_target": { class: "compile-reload" },
  "unity_open_mcp_build_set_defines": { class: "compile-reload" },
  "unity_open_mcp_settings_set_player": {
    class: "compile-reload",
    note:
      "Flipping scripting backend / input handler knobs can force a recompile " +
      "in addition to the settings write.",
  },
  // scene_open in single mode can lose unsaved changes in currently-open
  // scenes; the dirty guard preflights it (same recovery class as a reload).
  "unity_open_mcp_scene_open": { class: "compile-reload" },
  // compile_check is read-only re: game state but its lifecycle IS a domain
  // reload: it spawns a recompiling Unity. Batch-only by design — see note.
  "unity_open_mcp_compile_check": {
    class: "compile-reload",
    note:
      "Batch-only by design — spawns a fresh headless Unity that recompiles " +
      "from scratch. Cannot run while a live Editor holds the project lock; " +
      "expect `editor_instance_locked` and do not retry blindly (close the " +
      "live Editor, or verify compile state via the live bridge instead). " +
      "Edits under a local `packages/` source live outside Unity's Assets/ " +
      "watch root, so assets_refresh / RequestScriptCompilation are not " +
      "reliable for forcing a rebuild in a long-running live session — " +
      "verify via Library/ScriptAssemblies/*.dll mtime instead.",
  },

  // ----- modal-dialog: may raise an OS modal -------------------------------
  // Plan 1 declares the class; full detect/classify/dismiss/escalate policy
  // ships separately. The note points at the secondary concern.
  "unity_open_mcp_build_start": {
    class: "modal-dialog",
    note:
      "BuildPipeline.BuildPlayer may raise an OS modal (build settings, " +
      "platform mismatch). Also a scene-dirty mutation — the gate + " +
      "paths_hint cover the mutate side; the modal is the harder recovery.",
  },
  "unity_open_mcp_package_add": {
    class: "compile-reload",
    note:
      "Rewrites Packages/manifest.json + triggers UPM resolution which can " +
      "force a domain reload. A first import of a major package version can " +
      "additionally raise a project-upgrade modal (rare).",
  },
  "unity_open_mcp_package_remove": {
    class: "compile-reload",
    note:
      "Rewrites Packages/manifest.json + triggers UPM resolution which can " +
      "force a domain reload.",
  },
  "unity_open_mcp_reimport_package": {
    class: "compile-reload",
    note:
      "Force-reimports a local file: package's source and nudges a script " +
      "recompile (RequestScriptCompilation); a domain reload can follow. The " +
      "package id is the scope (source lives outside Assets/, so the gate has " +
      "no Assets/ path to validate — paths_hint defaults to " +
      "Packages/<package_id>). The response reports dllMtimeBefore/After so " +
      "an agent can detect a no-op recompile and fall back to a standalone " +
      "Roslyn compile (documented in agentNextSteps on a no-op).",
  },

  // ----- process-stale: long-running / async; bridge may stall -------------
  // The heartbeat may stop advancing during these; an agent should expect a
  // stale-heartbeat signature and not interpret it as a crash without the
  // PID-liveness check.
  "unity_senses_run_tests": {
    class: "process-stale",
    note:
      "Async — returns immediately and the result is delivered via an external " +
      "completion signal you poll. A long test run can stall the heartbeat; " +
      "treat stale-heartbeat + live-PID as 'still running', not crashed.",
  },
  "unity_open_mcp_reflection_probe_bake": {
    class: "process-stale",
    note:
      "Reflection-probe bake can take seconds to minutes; the bridge blocks " +
      "until it finishes. A stale heartbeat during a bake is expected.",
  },
  "unity_senses_memory_snapshot_capture": {
    class: "process-stale",
    note:
      "Callback-based capture; the bridge blocks until the snapshot file is " +
      "written. Large captures can take seconds and stall the heartbeat.",
  },

  // ----- scene-dirty: mutates scene/prefab/hierarchy; gate + paths_hint -----
  // The bridge blocks up to 5s for asset refresh / serialization. No domain
  // reload. These are the bulk of the typed mutators and domain mutators.
  "unity_open_mcp_apply_fix": { class: "scene-dirty" },
  "unity_open_mcp_reserialize": { class: "scene-dirty" },
  // Asset CRUD.
  "unity_open_mcp_assets_create_folder": { class: "scene-dirty" },
  "unity_open_mcp_assets_copy": { class: "scene-dirty" },
  "unity_open_mcp_assets_move": { class: "scene-dirty" },
  "unity_open_mcp_assets_delete": { class: "scene-dirty" },
  "unity_open_mcp_assets_refresh": { class: "scene-dirty" },
  // Material mutators.
  "unity_open_mcp_material_create": { class: "scene-dirty" },
  "unity_open_mcp_material_set_property": { class: "scene-dirty" },
  "unity_open_mcp_material_set_keyword": { class: "scene-dirty" },
  "unity_open_mcp_material_set_shader": { class: "scene-dirty" },
  // Prefab mutators (open/close are editor-stage state, still asset-scoped).
  "unity_open_mcp_prefab_instantiate": { class: "scene-dirty" },
  "unity_open_mcp_prefab_create": { class: "scene-dirty" },
  "unity_open_mcp_prefab_open": { class: "scene-dirty" },
  "unity_open_mcp_prefab_close": { class: "scene-dirty" },
  "unity_open_mcp_prefab_save": { class: "scene-dirty" },
  "unity_open_mcp_prefab_apply": { class: "scene-dirty" },
  "unity_open_mcp_prefab_revert": { class: "scene-dirty" },
  "unity_open_mcp_prefab_unpack": { class: "scene-dirty" },
  // GameObject + component mutators.
  "unity_open_mcp_gameobject_create": { class: "scene-dirty" },
  "unity_open_mcp_gameobject_destroy": { class: "scene-dirty" },
  "unity_open_mcp_gameobject_duplicate": { class: "scene-dirty" },
  "unity_open_mcp_gameobject_modify": { class: "scene-dirty" },
  "unity_open_mcp_gameobject_set_parent": { class: "scene-dirty" },
  "unity_open_mcp_component_add": { class: "scene-dirty" },
  "unity_open_mcp_component_destroy": { class: "scene-dirty" },
  "unity_open_mcp_component_modify": { class: "scene-dirty" },
  // Object/script mutators.
  "unity_open_mcp_object_modify": { class: "scene-dirty" },
  "unity_open_mcp_scriptableobject_create": { class: "scene-dirty" },
  // Scene mutators (non-open; open is compile-reload above).
  "unity_open_mcp_scene_create": { class: "scene-dirty" },
  "unity_open_mcp_scene_save": { class: "scene-dirty" },
  "unity_open_mcp_scene_unload": { class: "scene-dirty" },
  "unity_open_mcp_scene_set_active": { class: "scene-dirty" },
  "unity_open_mcp_scene_focus": { class: "scene-dirty" },
  "unity_open_mcp_sceneview_set_camera": { class: "scene-dirty" },
  // TagManager + console + selection + editor state mutators.
  "unity_open_mcp_editor_add_tag": { class: "scene-dirty" },
  "unity_open_mcp_editor_add_layer": { class: "scene-dirty" },
  "unity_open_mcp_console_clear": { class: "scene-dirty" },
  "unity_open_mcp_console_log": { class: "scene-dirty" },
  "unity_open_mcp_editor_set_state": {
    class: "scene-dirty",
    note:
      "Entering play mode can trigger Unity's native save modal; the bridge " +
      "runs its own inline dirty guard for this tool.",
  },
  "unity_open_mcp_selection_set": { class: "scene-dirty" },
  "unity_open_mcp_editor_undo": { class: "scene-dirty" },
  "unity_open_mcp_editor_redo": { class: "scene-dirty" },
  "unity_open_mcp_editor_clear_history": { class: "scene-dirty" },
  // Profiler snapshot write.
  "unity_open_mcp_profiler_save_data": { class: "scene-dirty" },
  // Build/settings mutators (non-reload).
  "unity_open_mcp_build_set_scenes": { class: "scene-dirty" },
  "unity_open_mcp_settings_set_quality": { class: "scene-dirty" },
  "unity_open_mcp_settings_set_physics": { class: "scene-dirty" },
  "unity_open_mcp_settings_set_lighting": { class: "scene-dirty" },
  "unity_open_mcp_settings_set_time": { class: "scene-dirty" },
  "unity_open_mcp_settings_set_quality_level": { class: "scene-dirty" },
  // KV preferences setters (registry / Library writes, not project assets).
  "unity_open_mcp_playerprefs_set": { class: "scene-dirty" },
  "unity_open_mcp_playerprefs_delete": { class: "scene-dirty" },
  "unity_open_mcp_editorprefs_set": { class: "scene-dirty" },
  "unity_open_mcp_editorprefs_delete": { class: "scene-dirty" },
  // M27 Plan 4 — batch_execute runs many nested typed tools sequentially; a
  // batch that includes scene/prefab/asset mutators behaves like the heaviest
  // nested tool. The bridge wraps the whole sequence in one batch-level gate
  // cycle and a single undo group; the worst-case recovery concern is the
  // scene-dirty / paths_hint contract of the nested mutators.
  "unity_open_mcp_batch_execute": {
    class: "scene-dirty",
    note:
      "Runs many nested typed tools sequentially; the batch shares one gate " +
      "cycle (one checkpoint → N steps → one validate/delta) and one undo " +
      "group. The recovery concern is the union of the nested tools' " +
      "scene-dirty / paths_hint contracts. v1 does not roll back successful " +
      "steps when a later step fails — inspect gate.delta for new issues.",
  },

  // ----- Domain mutators (extension packs) — all scene-dirty ---------------
  // Navigation.
  "unity_open_mcp_navigation_surface_add": { class: "scene-dirty" },
  "unity_open_mcp_navigation_set_bake_settings": { class: "scene-dirty" },
  "unity_open_mcp_navigation_surface_bake": { class: "scene-dirty" },
  "unity_open_mcp_navigation_modifier_add": { class: "scene-dirty" },
  "unity_open_mcp_navigation_modifier_volume_add": { class: "scene-dirty" },
  "unity_open_mcp_navigation_link_add": { class: "scene-dirty" },
  "unity_open_mcp_navigation_agent_add": { class: "scene-dirty" },
  "unity_open_mcp_navigation_agent_set_destination": { class: "scene-dirty" },
  "unity_open_mcp_navigation_modify": { class: "scene-dirty" },
  // Input System.
  "unity_open_mcp_inputsystem_asset_create": { class: "scene-dirty" },
  "unity_open_mcp_inputsystem_actionmap_add": { class: "scene-dirty" },
  "unity_open_mcp_inputsystem_action_add": { class: "scene-dirty" },
  "unity_open_mcp_inputsystem_binding_add": { class: "scene-dirty" },
  "unity_open_mcp_inputsystem_binding_composite_add": { class: "scene-dirty" },
  "unity_open_mcp_inputsystem_controlscheme_add": { class: "scene-dirty" },
  // ProBuilder.
  "unity_open_mcp_probuilder_create_shape": { class: "scene-dirty" },
  "unity_open_mcp_probuilder_extrude": { class: "scene-dirty" },
  "unity_open_mcp_probuilder_delete_faces": { class: "scene-dirty" },
  "unity_open_mcp_probuilder_set_face_material": { class: "scene-dirty" },
  // Particle System.
  "unity_open_mcp_particle_system_modify": { class: "scene-dirty" },
  // Animation (AnimationClip + AnimatorController).
  "unity_open_mcp_animation_create": { class: "scene-dirty" },
  "unity_open_mcp_animation_modify": { class: "scene-dirty" },
  "unity_open_mcp_animator_create": { class: "scene-dirty" },
  "unity_open_mcp_animator_modify": { class: "scene-dirty" },
  // Splines.
  "unity_open_mcp_splines_container_create": { class: "scene-dirty" },
  "unity_open_mcp_splines_add_knot": { class: "scene-dirty" },
  "unity_open_mcp_splines_set_knot": { class: "scene-dirty" },
  "unity_open_mcp_splines_set_tangent_mode": { class: "scene-dirty" },
  "unity_open_mcp_splines_modify": { class: "scene-dirty" },
  // Lighting.
  "unity_open_mcp_light_add": { class: "scene-dirty" },
  "unity_open_mcp_light_set": { class: "scene-dirty" },
  "unity_open_mcp_light_modify": { class: "scene-dirty" },
  "unity_open_mcp_skybox_set": { class: "scene-dirty" },
  // Audio.
  "unity_open_mcp_audio_source_add": { class: "scene-dirty" },
  "unity_open_mcp_audio_source_modify": { class: "scene-dirty" },
  "unity_open_mcp_audio_mixer_set_parameter": { class: "scene-dirty" },
  // UI (uGUI).
  "unity_open_mcp_ui_canvas_add": { class: "scene-dirty" },
  "unity_open_mcp_ui_element_add": { class: "scene-dirty" },
  "unity_open_mcp_ui_layout_group_add": { class: "scene-dirty" },
  "unity_open_mcp_ui_element_modify": { class: "scene-dirty" },
  // Constraints & LOD.
  "unity_open_mcp_constraint_add": { class: "scene-dirty" },
  "unity_open_mcp_lod_group_configure": { class: "scene-dirty" },
  "unity_open_mcp_lod_add_level": { class: "scene-dirty" },
  // Terrain.
  "unity_open_mcp_terrain_create": { class: "scene-dirty" },
  "unity_open_mcp_terrain_set_heights": { class: "scene-dirty" },
  "unity_open_mcp_terrain_paint_layer": { class: "scene-dirty" },
  "unity_open_mcp_terrain_place_trees": { class: "scene-dirty" },
  "unity_open_mcp_terrain_set_neighbors": { class: "scene-dirty" },
  // Cinemachine.
  "unity_open_mcp_cinemachine_create_camera": { class: "scene-dirty" },
  "unity_open_mcp_cinemachine_set_targets": { class: "scene-dirty" },
  "unity_open_mcp_cinemachine_set_lens": { class: "scene-dirty" },
  "unity_open_mcp_cinemachine_set_body": { class: "scene-dirty" },
  "unity_open_mcp_cinemachine_set_noise": { class: "scene-dirty" },
  "unity_open_mcp_cinemachine_brain_ensure": { class: "scene-dirty" },
  // Timeline.
  "unity_open_mcp_timeline_create": { class: "scene-dirty" },
  "unity_open_mcp_timeline_track_add": { class: "scene-dirty" },
  "unity_open_mcp_timeline_clip_add": { class: "scene-dirty" },
  "unity_open_mcp_timeline_director_bind": { class: "scene-dirty" },
  "unity_open_mcp_timeline_modify": { class: "scene-dirty" },
  // Tilemap.
  "unity_open_mcp_tilemap_create": { class: "scene-dirty" },
  "unity_open_mcp_tilemap_set_tile": { class: "scene-dirty" },
  "unity_open_mcp_tilemap_box_fill": { class: "scene-dirty" },
  "unity_open_mcp_tilemap_create_tile_asset": { class: "scene-dirty" },
  "unity_open_mcp_tilemap_create_rule_tile": { class: "scene-dirty" },
  // Shader Graph.
  "unity_open_mcp_shader_graph_create": { class: "scene-dirty" },
  "unity_open_mcp_shader_graph_node_add": { class: "scene-dirty" },
  "unity_open_mcp_shader_graph_node_connect": { class: "scene-dirty" },
  // VFX Graph.
  "unity_open_mcp_vfx_block_edit": { class: "scene-dirty" },
  // 2D art pipeline (SpriteAtlas + Texture).
  "unity_open_mcp_spriteatlas_create": { class: "scene-dirty" },
  "unity_open_mcp_spriteatlas_add_packable": { class: "scene-dirty" },
  "unity_open_mcp_spriteatlas_remove_packable": { class: "scene-dirty" },
  "unity_open_mcp_spriteatlas_modify": { class: "scene-dirty" },
  "unity_open_mcp_spriteatlas_delete": { class: "scene-dirty" },
  "unity_open_mcp_texture_set_import": { class: "scene-dirty" },
  "unity_open_mcp_texture_reimport": {
    class: "scene-dirty",
    note:
      "Reimport can take seconds and occasionally triggers a platform-switch " +
      "domain reload; if a reload follows, treat it as compile-reload.",
  },
};

/**
 * Resolve the lifecycle declaration for a tool. Unlisted tools (the read-only
 * majority) default to `{ class: "none" }` — the safe recovery class that
 * promises no settle wait, no dirty guard, and no stale-heartbeat risk.
 */
export function lifecycleFor(toolName: string): ToolLifecycle {
  return TOOL_LIFECYCLE[toolName] ?? { class: "none" };
}

// ---------------------------------------------------------------------------
// Taxonomy documentation table (agent-facing, clean of internal IDs).
// ---------------------------------------------------------------------------

export interface LifecycleTaxonomyEntry {
  /** Lifecycle class id (matches `ToolLifecycle.class`). */
  class: LifecycleClass;
  /** One-line description of what the tool does to the editor. */
  meaning: string;
  /** What the bridge does for this class (settle / guard / heartbeat). */
  bridge: string;
  /** How an agent should recover when a call of this class fails / stalls. */
  recovery: string;
}

export const LIFECYCLE_TAXONOMY: LifecycleTaxonomyEntry[] = [
  {
    class: "none",
    meaning: "Read-only / side-effect-free.",
    bridge: "Returns immediately; no settle wait, no dirty guard.",
    recovery:
      "Retry on the next call. A failure is a real error, not a settle/reload artifact.",
  },
  {
    class: "compile-reload",
    meaning: "Triggers or observes a domain reload (script / asmdef / package edit).",
    bridge:
      "Blocks up to 60s for the compile to settle; the active-scene dirty guard preflights.",
    recovery:
      "After the call, poll editor_status / read_compile_errors before assuming success. " +
      "compile_check is batch-only and returns editor_instance_locked when a live Editor " +
      "holds the project lock — do not retry blindly.",
  },
  {
    class: "modal-dialog",
    meaning: "May raise an OS modal (build, project upgrade, version mismatch).",
    bridge:
      "No first-class dismiss yet (planned); the call can hang on the modal until dismissed.",
    recovery:
      "Have an operator dismiss the dialog, or avoid the call in headless contexts. " +
      "Detect/classify/dismiss policy is forthcoming.",
  },
  {
    class: "scene-dirty",
    meaning: "Mutates scene / prefab / hierarchy / asset state.",
    bridge:
      "Runs the gate (checkpoint → mutate → validate → delta); blocks up to 5s for asset " +
      "refresh. Requires non-empty paths_hint (no whole-project fallback).",
    recovery:
      "Read gate.delta + agentNextSteps from the response. On scene_dirty refusal, save or " +
      "discard first, or pass ignore_scene_dirty: true to accept the risk.",
  },
  {
    class: "process-stale",
    meaning: "Long-running / async op where the bridge may become unresponsive.",
    bridge:
      "Blocks until the op finishes; the heartbeat may stop advancing during the wait.",
    recovery:
      "Treat stale-heartbeat + live-PID as 'still running', not crashed. Wait, then re-probe " +
      "with ping / editor_status before concluding the bridge died.",
  },
];

// ---------------------------------------------------------------------------
// Public aggregator — what gets attached to the capabilities response.
// ---------------------------------------------------------------------------

export interface LifecycleBlock {
  /** The 5-class taxonomy (meaning / bridge behaviour / recovery per class). */
  classes: LifecycleTaxonomyEntry[];
  /**
   * One-line guidance restating how to read the per-tool `lifecycle` field.
   * Kept here so the block is self-describing without an agent reading the doc.
   */
  guidance: string;
}

export function buildLifecycle(): LifecycleBlock {
  return {
    classes: LIFECYCLE_TAXONOMY,
    guidance:
      "Each tool carries a `lifecycle` class describing the recovery concern: " +
      "none (read-only), compile-reload (domain reload; settle + dirty guard), " +
      "modal-dialog (OS modal; may hang), scene-dirty (gate + paths_hint), or " +
      "process-stale (long-running; heartbeat may stall). Read it before the " +
      "call to pick a recovery strategy; `lifecycleNote` carries tool-specific " +
      "constraints (e.g. compile_check is batch-only).",
  };
}
