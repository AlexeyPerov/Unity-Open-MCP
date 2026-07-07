// M18 Plan 2 / T18.2 — canonical tool-group catalog.
//
// Single source of truth for the per-session tool-group visibility system.
// Drives two surfaces:
//
//  - `unity_open_mcp_manage_tools` meta-tool (session activation state).
//  - `unity_open_mcp_capabilities` `toolGroups` block (compiled-state only).
//
// Every registered MCP tool maps to a group id via `groupFor(toolName)`.
// Tools with no entry map to `null` and are always visible — they are server
// meta-tools (capabilities, ping, manage_tools itself, etc.).
//
// Groups are stable lowercase identifiers. The DEFAULT_ENABLED set is
// `{ "core" }` only — every other group is hidden from ListTools until the
// connected MCP session activates it via manage_tools.
//
// Domain groups (navigation, input, probuilder, particle-system, animation)
// carry a `domainDefine` so capabilities can report compiled-state
// availability — `available: true` when the bridge compiled the domain in
// (the matching `UNITY_OPEN_MCP_EXT_<DOMAIN>` symbol was defined), `false`
// when the Unity domain package is absent. Availability is compiled-state
// only; it does not change per session.
//
// M20 Plan 7 / T20.7.0 — a domain group may additionally carry
// `autoActivate: true` to opt into package-detection auto-activation: when
// the project has the group's `unityPackage` installed, the group is auto-
// activated for the session (no manual manage_tools call). This is additive
// to the manual-activation model — existing groups keep their manual-only
// behavior unless they explicitly opt in. The activation is ephemeral (same
// session store as manual activation) and resets on server restart.

/**
 * Catalog entry for one tool group.
 */
export interface ToolGroup {
  /** Stable lowercase group id (e.g. `"core"`, `"navigation"`). */
  id: string;
  /** Short human-readable description of what the group covers. */
  description: string;
  /**
   * True when the group is enabled by default for every fresh session.
   * Only `"core"` is default-on; everything else must be activated.
   */
  defaultEnabled: boolean;
  /**
   * Optional bridge compile define (`UNITY_OPEN_MCP_EXT_<DOMAIN>`). When
   * present, capabilities reports `available: false (dependency missing)`
   * for the group if the bridge did not compile the domain in. Omitted for
   * groups that ship in every bridge build (core, gate-and-verify, etc.).
   */
  domainDefine?: string;
  /**
   * Optional Unity package name the group depends on. Surfaced in the
   * capabilities `available: false` message so the agent knows what to
   * install to make the group's tools compile in.
   */
  unityPackage?: string;
  /**
   * M20 Plan 7 / T20.7.0 — when true, the group auto-activates for the
   * session when its {@link unityPackage} dependency is detected as
   * installed in the project (no manual manage_tools call required). This
   * mirrors the competitor's "package installed → tools appear" UX, additive
   * to the manual-activation model. Only meaningful when {@link unityPackage}
   * is also set; ignored otherwise. The activation is ephemeral per session
   * (same store as manual activation) — NOT persisted across server restarts.
   */
  autoActivate?: boolean;
}

/**
 * Ordered catalog. Order is preserved in manage_tools / capabilities output
 * so consumers render a stable list.
 */
export const TOOL_GROUPS: ToolGroup[] = [
  {
    id: "core",
    description:
      "Essential editor entry points: ping, execute_csharp, invoke_method, " +
      "find_members, execute_menu, editor_status, compile_check. Always on.",
    defaultEnabled: true,
  },
  {
    id: "gate-and-verify",
    description:
      "Gate, checkpoint, delta, find_references, dependencies, scan_paths, " +
      "apply_fix, scan_all, baseline_create, regression_check — the verify surface.",
    defaultEnabled: true,
  },
  {
    id: "asset-intelligence",
    description:
      "reserialize, read_asset, search_assets, list_assets — offline asset " +
      "reads and reference intelligence.",
    defaultEnabled: true,
  },
  {
    id: "typed-editor",
    description:
      "Typed editor surface (M16 Plans 1–6, 9): assets, materials, shaders, " +
      "prefabs, GameObjects, components, scenes, packages, console, editor " +
      "state, selection, undo, tags, layers, reflection, scripts, object " +
      "data, build pipeline, project settings.",
    defaultEnabled: true,
  },
  {
    id: "diagnostics",
    description:
      "Profiler session control, counters, memory snapshots, and the M10 " +
      "per-frame capture / memory / rendering reads.",
    defaultEnabled: true,
  },
  {
    id: "gate-intelligence",
    description:
      "Pre/post-mutation intelligence: impact_preview, gate_budget_estimate, " +
      "mutation_explain (read-only compositions of the gate foundations).",
    defaultEnabled: false,
  },
  {
    id: "build-settings",
    description:
      "Build pipeline + ProjectSettings reads and mutators (build_set_target, " +
      "build_start, settings_set_player, …).",
    defaultEnabled: false,
  },
  {
    id: "navigation",
    description:
      "NavMesh (AI Navigation) tools — surfaces, modifiers, links, agents. " +
      "Compile-gated on the com.unity.ai.navigation package.",
    defaultEnabled: false,
    domainDefine: "UNITY_OPEN_MCP_EXT_NAVIGATION",
    unityPackage: "com.unity.ai.navigation",
  },
  {
    id: "input-system",
    description:
      "Input System tools — .inputactions asset authoring (action maps, " +
      "actions, bindings, composites, control schemes). Compile-gated on " +
      "com.unity.inputsystem.",
    defaultEnabled: false,
    domainDefine: "UNITY_OPEN_MCP_EXT_INPUTSYSTEM",
    unityPackage: "com.unity.inputsystem",
  },
  {
    id: "probuilder",
    description:
      "ProBuilder 3D modeling tools — shapes, mesh info, extrude, face " +
      "delete, face material. Compile-gated on com.unity.probuilder.",
    defaultEnabled: false,
    domainDefine: "UNITY_OPEN_MCP_EXT_PROBUILDER",
    unityPackage: "com.unity.probuilder",
  },
  {
    id: "particle-system",
    description:
      "Particle System tools — per-module reads and field-patch mutator. " +
      "Compile-gated on the built-in ParticleSystemModule.",
    defaultEnabled: false,
    domainDefine: "UNITY_OPEN_MCP_EXT_PARTICLESYSTEM",
    unityPackage: "UnityEngine.ParticleSystemModule",
  },
  {
    id: "animation",
    description:
      "AnimationClip + AnimatorController tools — create / read / modify " +
      ".anim and .controller assets. Compile-gated on the built-in animation " +
      "module.",
    defaultEnabled: false,
    domainDefine: "UNITY_OPEN_MCP_EXT_ANIMATION",
    unityPackage: "com.unity.modules.animation",
  },
  {
    id: "splines",
    description:
      "Splines tools — SplineContainer authoring, knots, tangent modes, " +
      "evaluation. Compile-gated on the com.unity.splines package. First " +
      "backlog domain under the embedded + grouped model (M18 Plan 7).",
    defaultEnabled: false,
    domainDefine: "UNITY_OPEN_MCP_EXT_SPLINES",
    unityPackage: "com.unity.splines",
  },
  {
    id: "lighting",
    description:
      "Lighting tools — per-Light manipulation (add/set/modify), reflection " +
      "probe bake (realtime/baked/custom), skybox assignment. Built-in " +
      "lighting module — always compiled (no domain define). The bake tool " +
      "routes through the gate with EditorSettle.",
    defaultEnabled: false,
  },
  {
    id: "audio",
    description:
      "Audio tools — AudioSource add/modify, AudioMixer exposed-parameter " +
      "set/get, AudioListener read (duplicate warning). Built-in audio " +
      "module — always compiled (no domain define).",
    defaultEnabled: false,
  },
  {
    id: "ui",
    description:
      "UI (uGUI) tools — Canvas (+ CanvasScaler + GraphicRaycaster + EventSystem " +
      "ensure), element add (Text / TMP_Text / Image / Button / Slider / Toggle / " +
      "InputField), layout group add (Horizontal / Vertical / Grid), element modify " +
      "(typed field patch). Built-in UI module — always compiled (no domain define). " +
      "TextMesh Pro (TMP_Text) is optional and detected at call time.",
    defaultEnabled: false,
  },
  {
    id: "constraints",
    description:
      "Constraints & LOD tools — animation constraints (Position / Rotation / Aim / " +
      "Parent / Scale) add with source + weight + activation, LODGroup configure " +
      "(fade mode / cross-fade / LOD array), LOD level add (per-index renderers). " +
      "Built-in engine modules — always compiled (no domain define).",
    defaultEnabled: false,
  },
  {
    id: "terrain",
    description:
      "Terrain tools — create (TerrainData + GameObject), heightmap region write, " +
      "splat layer paint, tree instance placement, neighbor stitching for LOD. " +
      "Built-in Terrain module — always compiled (no domain define). Large-array " +
      "cap (513x513) pushes agents toward tiled writes.",
    defaultEnabled: false,
  },
  {
    id: "cinemachine",
    description:
      "Cinemachine tools — create / configure virtual cameras, set targets / " +
      "lens / Body / Noise, ensure Brain, list cameras. Reflection-gated in the " +
      "bridge (the assembly always compiles; Cinemachine 3.x presence is detected " +
      "at call time). Requires com.unity.cinemachine >= 3.x.",
    defaultEnabled: false,
  },
  {
    id: "timeline",
    description:
      "Timeline tools — create TimelineAsset, add tracks (Animation / Activation " +
      "/ Audio / Signal / Control / Group / Playable), add clips, bind " +
      "PlayableDirector, reflective modify. Compile-gated on com.unity.timeline.",
    defaultEnabled: false,
    domainDefine: "UNITY_OPEN_MCP_EXT_TIMELINE",
    unityPackage: "com.unity.timeline",
  },
  {
    id: "tilemap",
    description:
      "Tilemap tools — create Grid + Tilemap, paint single tiles, box-fill " +
      "regions, create Tile assets, create RuleTile (requires tilemap.extras). " +
      "Compile-gated on com.unity.2d.tilemap; RuleTile additionally gated on " +
      "com.unity.2d.tilemap.extras at call time (two defines, two guards).",
    defaultEnabled: false,
    domainDefine: "UNITY_OPEN_MCP_EXT_TILEMAP",
    unityPackage: "com.unity.2d.tilemap",
  },
  {
    id: "shadergraph",
    description:
      "Shader Graph tools — create a Shader Graph asset, open it in the graph " +
      "editor, add nodes, and connect node ports. Compile-gated on " +
      "com.unity.shadergraph (UNITY_OPEN_MCP_EXT_SHADERGRAPH). Mutating graph " +
      "edits (create / node_add / node_connect) run the gate path with " +
      "EditorSettle; open is a non-mutating window bring-up (Gate = Off). " +
      "Auto-activates for the session when com.unity.shadergraph is installed " +
      "(M20 Plan 7 / T20.7.0) — no manual manage_tools call required.",
    defaultEnabled: false,
    domainDefine: "UNITY_OPEN_MCP_EXT_SHADERGRAPH",
    unityPackage: "com.unity.shadergraph",
    autoActivate: true,
  },
  {
    id: "vfx",
    description:
      "VFX Graph tools — list VisualEffectGraph assets, open a .vfx in the " +
      "VFX Graph editor (with a structured context/block/property summary), " +
      "and patch a single block property. Compile-gated on " +
      "com.unity.visualeffectgraph (UNITY_OPEN_MCP_EXT_VFX). list / open are " +
      "read-only (Gate = Off); block_edit runs the gate path with " +
      "EditorSettle. VFX Graph's editor graph model is internal/unstable — " +
      "list/open work over the public runtime VisualEffectAsset type " +
      "(version-stable); block_edit requires the VFX Graph window to be open " +
      "and degrades to a structured vfx_block_edit_requires_editor_window " +
      "error otherwise. Auto-activates for the session when " +
      "com.unity.visualeffectgraph is installed (M20 Plan 7 / T20.7.0) — no " +
      "manual manage_tools call required.",
    defaultEnabled: false,
    domainDefine: "UNITY_OPEN_MCP_EXT_VFX",
    unityPackage: "com.unity.visualeffectgraph",
    autoActivate: true,
  },
  {
    id: "memoryprofiler",
    description:
      "Memory Profiler tool — capture a Memory Profiler snapshot to a .snap " +
      "file via the com.unity.memoryprofiler package API. Sense-prefixed " +
      "(unity_senses_*) because it pairs with the existing senses profiler " +
      "family (profiler_get_script_stats / profiler_capture_frame) for a " +
      "fuller performance picture than a standalone memory tool. Compile-gated " +
      "on com.unity.memoryprofiler (UNITY_OPEN_MCP_EXT_MEMORYPROFILER). " +
      "Read-only re: game/project state but produces a file — Gate = Off, " +
      "ReadOnlyHint = true, Lifecycle = EditorSettle (capture can take " +
      "seconds). The capture is callback-based (async); the bridge blocks " +
      "until the callback fires. Auto-activates for the session when " +
      "com.unity.memoryprofiler is installed (M20 Plan 7 / T20.7.0) — no " +
      "manual manage_tools call required.",
    defaultEnabled: false,
    domainDefine: "UNITY_OPEN_MCP_EXT_MEMORYPROFILER",
    unityPackage: "com.unity.memoryprofiler",
    autoActivate: true,
  },
  {
    id: "sprite2d",
    description:
      "2D art pipeline tools — SpriteAtlas (create / get / add_packable / " +
      "remove_packable / modify / delete / list) + Texture import (get_importer " +
      "/ set_import / reimport / get). Two prefixes share one group so the " +
      "whole 2D art pipeline activates together. Built-in 2D module " +
      "(SpriteAtlas / SpriteAtlasAsset / SpriteAtlasPackingSettings / " +
      "SpriteAtlasTextureSettings in UnityEngine.U2D / UnityEditor.U2D + " +
      "TextureImporter in UnityEditor) — always compiled (no domain define). " +
      "Mutating members run the full gate path with EditorSettle (the " +
      ".spriteatlas asset is written/reimported; texture reimports can take " +
      "seconds and may trigger a platform-switch domain reload); read-only " +
      "members (spriteatlas_get / list + texture_get_importer / get) are " +
      "gate-free. texture_set_import folds sprite + normal-map presets into " +
      "one structured settings_json patch instead of separate tools.",
    defaultEnabled: false,
  },
  {
    id: "agent-senses",
    description:
      "Agent senses surface (run_tests, screenshot variants, capture_inline, " +
      "frame_debugger, read_console, profiler capture / capture_frame / memory " +
      "/ rendering, spatial_query). Live-only.",
    defaultEnabled: false,
  },
  {
    id: "unity-hub-control",
    description:
      "Unity Hub control tools — list installed editors (with build-target " +
      "platforms), available releases feed, install editor/modules via the " +
      "unityhub:// deep link, get/set the default editor install path. " +
      "Local-routed (resolved inside the MCP server: filesystem discovery + " +
      "Unity archive feed + deep link + Hub CLI); no Unity Editor or bridge " +
      "connection required. Built-in module (always compiled, no domain " +
      "define). Mutating members are system-level ops (paths_hint N/A, " +
      "gate-free).",
    defaultEnabled: false,
  },
];

/**
 * Set of group ids enabled by default for every fresh session.
 * Per the resolved decision (M18 execution-plan.md), this is `core` only.
 */
export const DEFAULT_ENABLED_GROUPS: ReadonlySet<string> = new Set(
  TOOL_GROUPS.filter((g) => g.defaultEnabled).map((g) => g.id),
);

/** All known group ids (validates activate / deactivate input). */
export const GROUP_IDS: ReadonlySet<string> = new Set(
  TOOL_GROUPS.map((g) => g.id),
);

/**
 * M20 Plan 7 / T20.7.0 — groups that auto-activate for the session when
 * their Unity package dependency is detected as installed. The set is
 * computed once from the catalog; the per-session activation still flows
 * through {@link ToolSessionState.activate} so it shares the same ephemeral
 * store as manual activation (and resets on server restart).
 */
export const AUTO_ACTIVATE_GROUPS: readonly {
  groupId: string;
  packageId: string;
}[] = TOOL_GROUPS.filter((g) => g.autoActivate && g.unityPackage).map((g) => ({
  groupId: g.id,
  packageId: g.unityPackage!,
}));

const GROUP_BY_ID: ReadonlyMap<string, ToolGroup> = new Map(
  TOOL_GROUPS.map((g) => [g.id, g]),
);

export function getGroup(id: string): ToolGroup | undefined {
  return GROUP_BY_ID.get(id);
}

// ---------------------------------------------------------------------------
// Per-tool group assignment — the authoritative mapping from a registered MCP
// tool name to its group id. Tools not listed here default to `null` (always
// visible). Mirrors the capability categories in build-capabilities.ts; the
// group vocabulary is the curated, session-visibility-relevant subset.
//
// KEEP THIS TABLE ALIGNED with TOOL_CATEGORY in build-capabilities.ts when
// adding tools. A tool that gets a non-`core` category in capabilities should
// map to the matching group id here, and vice versa.
// ---------------------------------------------------------------------------

const TOOL_GROUP_ASSIGNMENT: Record<string, string> = {};

function assign(group: string, names: string[]): void {
  for (const name of names) TOOL_GROUP_ASSIGNMENT[name] = group;
}

// --- core -------------------------------------------------------------------
assign("core", [
  "unity_open_mcp_ping",
  "unity_open_mcp_execute_csharp",
  "unity_open_mcp_invoke_method",
  "unity_open_mcp_execute_menu",
  "unity_open_mcp_find_members",
  "unity_open_mcp_editor_status",
  // M27 Plan 4 — batch_execute is a core meta-tool (always visible). Live-only:
  // one HTTP round trip runs many typed tools sequentially inside the open
  // Editor. NOT headless batchCapable (no batch spawn fallback).
  "unity_open_mcp_batch_execute",
]);

// --- gate-and-verify --------------------------------------------------------
assign("gate-and-verify", [
  "unity_open_mcp_compile_check",
  "unity_open_mcp_validate_edit",
  "unity_open_mcp_checkpoint_create",
  "unity_open_mcp_delta",
  "unity_open_mcp_find_references",
  "unity_open_mcp_dependencies",
  "unity_open_mcp_scan_paths",
  "unity_open_mcp_apply_fix",
  "unity_open_mcp_scan_all",
  "unity_open_mcp_baseline_create",
  "unity_open_mcp_regression_check",
]);

// --- asset-intelligence -----------------------------------------------------
assign("asset-intelligence", [
  "unity_open_mcp_reserialize",
  "unity_open_mcp_read_asset",
  "unity_open_mcp_search_assets",
  "unity_open_mcp_list_assets",
]);

// --- typed-editor (M16 Plans 1–6, 9) ---------------------------------------
assign(
  "typed-editor",
  [
    "assets_create_folder",
    "assets_copy",
    "assets_move",
    "assets_delete",
    "assets_refresh",
    "material_create",
    "material_get_properties",
    "material_set_property",
    "material_get_keywords",
    "material_set_keyword",
    "material_set_shader",
    "shader_list_all",
    "shader_get_data",
    "prefab_instantiate",
    "prefab_create",
    "prefab_open",
    "prefab_close",
    "prefab_save",
    "prefab_apply",
    "prefab_revert",
    "prefab_unpack",
    "prefab_get_overrides",
    "prefab_status",
    "gameobject_create",
    "gameobject_destroy",
    "gameobject_duplicate",
    "gameobject_find",
    "gameobject_modify",
    "gameobject_set_parent",
    "component_add",
    "component_destroy",
    "component_get",
    "component_modify",
    "component_list_all",
    "scene_create",
    "scene_open",
    "scene_save",
    "scene_unload",
    "scene_set_active",
    "scene_list_opened",
    "scene_get_data",
    "scene_get_dirty_summary",
    "scene_focus",
    "sceneview_get_camera",
    "sceneview_set_camera",
    "package_list",
    "package_search",
    "package_add",
    "package_remove",
    "reimport_package",
    "package_get_info",
    "package_get_dependencies",
    "package_check",
    "console_clear",
    "console_log",
    "editor_set_state",
    "selection_get",
    "selection_set",
    "editor_undo",
    "editor_redo",
    "editor_undo_history",
    "editor_clear_history",
    "editor_get_tags",
    "editor_get_layers",
    "editor_add_tag",
    "editor_add_layer",
    "type_schema",
    "script_read",
    "script_write",
    "script_delete",
    "object_get_data",
    "object_modify",
    // M20 Plan 5 / T20.5 — typed ScriptableObject + Assembly Definition tools.
    // Core (always-on) typed-editor tools. scriptableobject_create is mutating;
    // list_assets_of_type is read-only. asmdef_list/get are read-only;
    // asmdef_create/modify are mutating (RestartThenSettle).
    "scriptableobject_create",
    "list_assets_of_type",
    "asmdef_list",
    "asmdef_get",
    "asmdef_create",
    "asmdef_modify",
  ].map((suffix) => `unity_open_mcp_${suffix}`),
);

// --- diagnostics (profiler + per-frame reads) ------------------------------
assign(
  "diagnostics",
  [
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
  ],
);

// --- gate-intelligence ------------------------------------------------------
assign("gate-intelligence", [
  "unity_open_mcp_impact_preview",
  "unity_open_mcp_gate_budget_estimate",
  "unity_open_mcp_mutation_explain",
]);

// --- build-settings ---------------------------------------------------------
assign(
  "build-settings",
  [
    "build_get_targets",
    "build_get_active_target",
    "build_set_target",
    "build_get_scenes",
    "build_set_scenes",
    "build_start",
    "build_get_defines",
    "build_set_defines",
    "settings_get_player",
    "settings_set_player",
    "settings_get_quality",
    "settings_set_quality",
    "settings_get_physics",
    "settings_set_physics",
    "settings_get_lighting",
    "settings_set_lighting",
    // M20 Plan 9 / T20.9.2 — KV preferences ride the build-settings group
    // (project configuration surface). Mutating members are gate-free (registry
    // writes — no asset scope); playerprefs_delete_all is deliberately absent.
    "playerprefs_get",
    "playerprefs_set",
    "playerprefs_delete",
    "editorprefs_get",
    "editorprefs_set",
    "editorprefs_delete",
    // M20 Plan 9 / T20.9.3 — Project Settings remainder. set_time +
    // set_quality_level run the gate (ProjectSettings asset writes);
    // get_time + get_render_pipeline are gate-free reads.
    "settings_get_time",
    "settings_set_time",
    "settings_get_render_pipeline",
    "settings_set_quality_level",
  ].map((suffix) => `unity_open_mcp_${suffix}`),
);

// --- navigation -------------------------------------------------------------
assign(
  "navigation",
  [
    "surface_add",
    "set_bake_settings",
    "surface_bake",
    "modifier_add",
    "modifier_volume_add",
    "link_add",
    "agent_add",
    "agent_set_destination",
    "list",
    "get",
    "modify",
  ].map((suffix) => `unity_open_mcp_navigation_${suffix}`),
);

// --- input-system -----------------------------------------------------------
assign(
  "input-system",
  [
    "asset_create",
    "actionmap_add",
    "action_add",
    "binding_add",
    "binding_composite_add",
    "controlscheme_add",
    "get",
  ].map((suffix) => `unity_open_mcp_inputsystem_${suffix}`),
);

// --- probuilder -------------------------------------------------------------
assign(
  "probuilder",
  [
    "create_shape",
    "get_mesh_info",
    "extrude",
    "delete_faces",
    "set_face_material",
  ].map((suffix) => `unity_open_mcp_probuilder_${suffix}`),
);

// --- particle-system --------------------------------------------------------
assign(
  "particle-system",
  ["get", "modify"].map((suffix) => `unity_open_mcp_particle_system_${suffix}`),
);

// --- animation --------------------------------------------------------------
// Two domain prefixes (animation_* + animator_*) share one tool group, so the
// entries are fully-qualified tool names rather than a single-prefix .map().
assign(
  "animation",
  [
    "unity_open_mcp_animation_create",
    "unity_open_mcp_animation_get_data",
    "unity_open_mcp_animation_modify",
    "unity_open_mcp_animator_create",
    "unity_open_mcp_animator_get_data",
    "unity_open_mcp_animator_modify",
  ],
);

// --- splines (M18 Plan 7 / T18.7.3 — first backlog domain) -----------------
assign(
  "splines",
  [
    "container_create",
    "add_knot",
    "set_knot",
    "set_tangent_mode",
    "evaluate",
    "get_knots",
    "modify",
  ].map((suffix) => `unity_open_mcp_splines_${suffix}`),
);

// --- lighting (M20 Plan 2 / T20.2 — built-in lighting module, ungated) -----
// light_* and reflection_probe_* use distinct domain prefixes but share one
// tool group. skybox_* folds in the same group. Built-in lighting types are
// always present, so this group carries no domainDefine.
assign(
  "lighting",
  [
    "light_add",
    "light_set",
    "light_modify",
    "reflection_probe_bake",
    "reflection_probe_get",
    "skybox_set",
    "skybox_get",
  ].map((suffix) => `unity_open_mcp_${suffix}`),
);

// --- audio (M20 Plan 3 / T20.3.1 — built-in audio module, ungated) --------
// audio_source_* and audio_mixer_* use distinct domain prefixes but share one
// tool group. audio_listener_get folds in the same group. Built-in audio types
// (AudioSource / AudioListener / AudioMixer / AudioMixerGroup) are always
// present, so this group carries no domainDefine.
assign(
  "audio",
  [
    "audio_source_add",
    "audio_source_modify",
    "audio_mixer_set_parameter",
    "audio_listener_get",
    "audio_mixer_get_parameter",
  ].map((suffix) => `unity_open_mcp_${suffix}`),
);

// --- ui (M20 Plan 3 / T20.3.2 — built-in UI module, ungated) --------------
// All four ui_* tools share one domain prefix and one tool group. Built-in
// uGUI types (Canvas / CanvasScaler / GraphicRaycaster / Image / Text / Button
// / Slider / Toggle / InputField / layout groups / EventSystem) are always
// present, so this group carries no domainDefine. TextMesh Pro (TMP_Text) is
// optional and detected at call time, not at compile time.
assign(
  "ui",
  [
    "canvas_add",
    "element_add",
    "layout_group_add",
    "element_modify",
  ].map((suffix) => `unity_open_mcp_ui_${suffix}`),
);

// --- constraints (M20 Plan 3 / T20.3.3 — built-in engine modules, ungated) -
// constraint_add and lod_* use distinct domain prefixes but share one tool
// group (Constraints & LOD are small and closely related). Built-in types
// (PositionConstraint / RotationConstraint / AimConstraint / ParentConstraint /
// ScaleConstraint from UnityEngine.AnimationModule + LODGroup from
// UnityEngine.CoreModule) are always present, so this group carries no
// domainDefine.
assign(
  "constraints",
  [
    "constraint_add",
    "lod_group_configure",
    "lod_add_level",
  ].map((suffix) => `unity_open_mcp_${suffix}`),
);

// --- terrain (M20 Plan 4 / T20.4 — built-in Terrain module, ungated) ------
// All five terrain_* tools share one domain prefix and one tool group. Built-in
// Terrain types (Terrain / TerrainData / TreePrototype / TerrainLayer from
// UnityEngine.TerrainModule + UnityEngine.CoreModule) are always present, so
// this group carries no domainDefine.
assign(
  "terrain",
  [
    "create",
    "set_heights",
    "paint_layer",
    "place_trees",
    "set_neighbors",
  ].map((suffix) => `unity_open_mcp_terrain_${suffix}`),
);

// --- cinemachine (M20 Plan 6 / T20.6.1 — reflection-gated) -----------------
// The only reflection-gated domain pack. The bridge assembly ALWAYS compiles
// (no UNITY_OPEN_MCP_EXT_CINEMACHINE compile gate) — so this group carries no
// domainDefine and capabilities reports it as always-compiled-in. Cinemachine
// 3.x presence is detected at call time via the CinemachineVersion reflection
// layer; when 3.x is absent (package missing OR 2.x installed), the tools
// return a clear install/upgrade error envelope. cinemachine_* use one domain
// prefix and one tool group; six mutating members (create_camera / set_targets
// / set_lens / set_body / set_noise / brain_ensure) run the full gate path; the
// read-only member (camera_list) is gate-free.
assign(
  "cinemachine",
  [
    "create_camera",
    "set_targets",
    "set_lens",
    "set_body",
    "set_noise",
    "brain_ensure",
    "camera_list",
  ].map((suffix) => `unity_open_mcp_cinemachine_${suffix}`),
);

// --- timeline (M20 Plan 6 / T20.6.2 — compile-gated) ----------------------
// All five timeline_* tools share one domain prefix and one tool group.
// Compile-gated on com.unity.timeline in the bridge (UNITY_OPEN_MCP_EXT_TIMELINE).
// All five members are mutating and run the full gate path with paths_hint
// scoped to the timeline asset path (+ the host scene path for director_bind).
assign(
  "timeline",
  [
    "create",
    "track_add",
    "clip_add",
    "director_bind",
    "modify",
  ].map((suffix) => `unity_open_mcp_timeline_${suffix}`),
);

// --- tilemap (M20 Plan 6 / T20.6.3 — compile-gated + inner extras guard) --
// All five tilemap_* tools share one domain prefix and one tool group.
// Compile-gated on com.unity.2d.tilemap in the bridge (UNITY_OPEN_MCP_EXT_TILEMAP);
// create_rule_tile additionally inner-guards on UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS
// (com.unity.2d.tilemap.extras) — when extras is absent, the tool compiles in
// (the outer gate passes) but returns a clear tilemap_extras_required install
// error (two defines, two guards). All five members are mutating and run the
// full gate path.
assign(
  "tilemap",
  [
    "create",
    "set_tile",
    "box_fill",
    "create_tile_asset",
    "create_rule_tile",
  ].map((suffix) => `unity_open_mcp_tilemap_${suffix}`),
);

// --- shadergraph (M20 Plan 7 / T20.7.1 — compile-gated + auto-activating) --
// The FIRST domain under the package-detection auto-activation model
// (T20.7.0): the `shadergraph` group activates automatically for the session
// when com.unity.shadergraph is installed (no manual manage_tools call).
// All four shader_graph_* tools share one domain prefix and one tool group.
// Compile-gated on com.unity.shadergraph in the bridge
// (UNITY_OPEN_MCP_EXT_SHADERGRAPH). create / node_add / node_connect are
// mutating and run the full gate path with paths_hint scoped to the
// .shadergraph asset path; open is read-only (Gate = Off). The editing API
// is wrapped behind a reflection helper; when the installed package version
// exposes a different surface, mutating tools return a structured
// shadergraph_api_unavailable error instead of throwing.
assign(
  "shadergraph",
  [
    "create",
    "open",
    "node_add",
    "node_connect",
  ].map((suffix) => `unity_open_mcp_shader_graph_${suffix}`),
);

// --- vfx (M20 Plan 7 / T20.7.2 — compile-gated + auto-activating) ----------
// The SECOND domain under the package-detection auto-activation model
// (T20.7.0): the `vfx` group activates automatically for the session when
// com.unity.visualeffectgraph is installed (no manual manage_tools call).
// All three vfx_* tools share one domain prefix and one tool group.
// Compile-gated on com.unity.visualeffectgraph in the bridge
// (UNITY_OPEN_MCP_EXT_VFX). list / open are read-only (Gate = Off);
// block_edit is mutating and runs the full gate path with paths_hint scoped to
// the .vfx asset path. VFX Graph's editor graph model is internal/unstable —
// list/open work over the public runtime VisualEffectAsset type (version-
// stable); block_edit requires the VFX Graph window to be open and degrades to
// a structured vfx_block_edit_requires_editor_window error otherwise.
assign(
  "vfx",
  [
    "list",
    "open",
    "block_edit",
  ].map((suffix) => `unity_open_mcp_vfx_${suffix}`),
);

// --- memoryprofiler (M20 Plan 7 / T20.7.3 — compile-gated + auto-activating) -
// The THIRD domain under the package-detection auto-activation model
// (T20.7.0): the `memoryprofiler` group activates automatically for the
// session when com.unity.memoryprofiler is installed (no manual manage_tools
// call). Compile-gated on com.unity.memoryprofiler in the bridge
// (UNITY_OPEN_MCP_EXT_MEMORYPROFILER). The capture tool is sense-prefixed
// (unity_senses_*) because it pairs with the existing senses profiler family
// rather than the typed-editor surface. Read-only re: game/project state but
// produces a file — Gate = Off, ReadOnlyHint = true, Lifecycle = EditorSettle
// (capture can take seconds). The capture is callback-based (async); the bridge
// blocks until the callback fires.
assign(
  "memoryprofiler",
  ["unity_senses_memory_snapshot_capture"],
);

// --- 2d (M20 Plan 9 / T20.9.1 — built-in 2D module, ungated) ---------------
// spriteatlas_* and texture_* use distinct domain prefixes but share one tool
// group so the whole 2D art pipeline (atlas packing + texture import) activates
// together. Built-in 2D types (SpriteAtlas / SpriteAtlasAsset / packing /
// texture settings structs from UnityEngine.U2D / UnityEditor.U2D +
// TextureImporter from UnityEditor) are always present, so this group carries
// no domainDefine. Five mutating spriteatlas members (create / add_packable /
// remove_packable / modify / delete) run the full gate path with EditorSettle
// and paths_hint scoped to the .spriteatlas asset path (delete is destructive);
// two read-only spriteatlas members (get / list) are gate-free. Two mutating
// texture members (set_import / reimport) run the full gate path with
// EditorSettle (reimport can take seconds / trigger a platform-switch domain
// reload); set_import folds sprite + normal-map presets into one structured
// settings_json patch instead of separate tools. Two read-only texture members
// (get_importer / get) are gate-free.
assign(
  "sprite2d",
  [
    "create",
    "get",
    "add_packable",
    "remove_packable",
    "modify",
    "delete",
    "list",
  ].map((suffix) => `unity_open_mcp_spriteatlas_${suffix}`),
);
assign(
  "sprite2d",
  [
    "get_importer",
    "set_import",
    "reimport",
    "get",
  ].map((suffix) => `unity_open_mcp_texture_${suffix}`),
);

// --- agent-senses (live-only reads) ----------------------------------------
assign("agent-senses", [
  "unity_senses_run_tests",
  "unity_senses_screenshot",
  // M20 Plan 1 / T20.1.1 — senses parity residual. Arbitrary-pose screenshot
  // + inline-image capture extend the screenshot surface.
  "unity_senses_screenshot_camera",
  "unity_senses_capture_inline",
  // M20 Plan 1 / T20.1.2 — EditorWindow capture.
  "unity_senses_screenshot_window",
  // M20 Plan 1 / T20.1.3 — Frame Debugger control + draw-call list.
  "unity_senses_frame_debugger",
  // M20 Plan 1 / T20.1.4 — single-frame deep profiler capture.
  "unity_senses_profiler_capture_frame",
  "unity_senses_read_console",
  "unity_senses_profiler_capture",
  "unity_senses_profiler_memory",
  "unity_senses_profiler_rendering",
  "unity_senses_spatial_query",
]);

// --- unity-hub-control (M26 Plan 2 — local-routed, built-in, ungated) -------
assign("unity-hub-control", [
  "unity_open_mcp_hub_list_editors",
  "unity_open_mcp_hub_available_releases",
  "unity_open_mcp_hub_install_editor",
  "unity_open_mcp_hub_install_modules",
  "unity_open_mcp_hub_get_install_path",
  "unity_open_mcp_hub_set_install_path",
]);

// ---------------------------------------------------------------------------
// Read API
// ---------------------------------------------------------------------------

/**
 * Resolve a tool name to its group id. Returns `null` for tools with no
 * assignment (server meta-tools: capabilities, list_rules, generate_skill,
 * manage_tools, pull_events, read_compile_errors). Null means "always
 * visible" — manage_tools and ListTools never hide a null-group tool.
 */
export function groupFor(toolName: string): string | null {
  return TOOL_GROUP_ASSIGNMENT[toolName] ?? null;
}

/**
 * Inverse map: group id → sorted tool names. Used by manage_tools
 * `list_groups` to enumerate every group with its tool roster. Tools with a
 * null group are intentionally omitted (they are always-visible meta-tools).
 */
export function groupToTools(): Record<string, string[]> {
  const out: Record<string, string[]> = {};
  for (const group of TOOL_GROUPS) {
    out[group.id] = [];
  }
  for (const [tool, group] of Object.entries(TOOL_GROUP_ASSIGNMENT)) {
    if (!out[group]) out[group] = [];
    out[group].push(tool);
  }
  for (const names of Object.values(out)) names.sort();
  return out;
}
