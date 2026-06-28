import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { ping } from "./ping.js";
import { executeCsharp } from "./execute-csharp.js";
import { invokeMethod } from "./invoke-method.js";
import { executeMenu } from "./execute-menu.js";
import { findMembers } from "./find-members.js";
import { compileCheck } from "./compile-check.js";
import { editorStatus } from "./editor-status.js";
import { validateEdit } from "./validate-edit.js";
import { checkpointCreate } from "./checkpoint-create.js";
import { delta } from "./delta.js";
import { findReferences } from "./find-references.js";
import { scanPaths } from "./scan-paths.js";
import { applyFix } from "./apply-fix.js";
import { scanAll } from "./scan-all.js";
import { baselineCreate } from "./baseline-create.js";
import { regressionCheck } from "./regression-check.js";
import { reserialize } from "./reserialize.js";
import { readAsset } from "./read-asset.js";
import { searchAssets } from "./search-assets.js";
import { listAssets } from "./list-assets.js";
import { runTests } from "./run-tests.js";
import { screenshot } from "./screenshot.js";
// M20 Plan 1 / T20.1.1 — senses parity residual: arbitrary-pose screenshot +
// inline-image capture. Both extend the existing screenshot surface; sibling
// files to screenshot.ts.
import { screenshotCamera } from "./screenshot-camera.js";
import { captureInline } from "./capture-inline.js";
// M20 Plan 1 / T20.1.2 — Editor window screenshot. Win-only full-fidelity via
// PrintWindow; cross-platform best-effort readback with platformLimited flag.
import { screenshotWindow } from "./screenshot-window.js";
// M20 Plan 1 / T20.1.3 — Frame Debugger control + draw-call list. Wraps the
// internal Frame Debugger API via reflection; enable/disable is a non-mutating
// Editor state change (gate-free, read-only), list returns the draw calls of
// the currently-debugged frame.
import { frameDebugger } from "./frame-debugger.js";
// M20 Plan 1 / T20.1.4 — single-frame deep profiler capture. Returns one
// frame's full sample tree for the requested modules; deeper than the existing
// per-module profiler_get_* stats. Read-only; pairs with the existing profiler
// session family.
import { profilerCaptureFrame } from "./profiler-capture-frame.js";
import { readConsole } from "./read-console.js";
import { profilerCapture } from "./profiler-capture.js";
import { profilerMemory } from "./profiler-memory.js";
import { profilerRendering } from "./profiler-rendering.js";
import { spatialQuery } from "./spatial-query.js";
import { agentCapabilities } from "./agent-capabilities.js";
import { generateSkill } from "./generate-skill.js";
import { listRules } from "./list-rules.js";
import { pullEvents } from "./pull-events.js";
import { readCompileErrors } from "./read-compile-errors.js";
import { bridgeStatus } from "./bridge-status.js";
// M18 Plan 2 / T18.2.2 — Coplay-style manage_tools meta-tool (session
// tool-group visibility). Server-only meta-tool; routes local, always
// visible regardless of which groups the current session has activated.
import { manageTools } from "./manage-tools.js";
// M16 Plan 1 — typed project & asset management tools.
import { assetsCreateFolder } from "./assets-create-folder.js";
import { assetsCopy } from "./assets-copy.js";
import { assetsMove } from "./assets-move.js";
import { assetsDelete } from "./assets-delete.js";
import { assetsRefresh } from "./assets-refresh.js";
import { materialCreate } from "./material-create.js";
import { materialGetProperties } from "./material-get-properties.js";
import { materialSetProperty } from "./material-set-property.js";
import { materialGetKeywords } from "./material-get-keywords.js";
import { materialSetKeyword } from "./material-set-keyword.js";
import { materialSetShader } from "./material-set-shader.js";
import { shaderListAll } from "./shader-list-all.js";
import { shaderGetData } from "./shader-get-data.js";
import { prefabInstantiate } from "./prefab-instantiate.js";
import { prefabCreate } from "./prefab-create.js";
import { prefabOpen } from "./prefab-open.js";
import { prefabClose } from "./prefab-close.js";
import { prefabSave } from "./prefab-save.js";
import { prefabApply } from "./prefab-apply.js";
import { prefabRevert } from "./prefab-revert.js";
import { prefabUnpack } from "./prefab-unpack.js";
import { prefabGetOverrides } from "./prefab-get-overrides.js";
import { prefabStatus } from "./prefab-status.js";
// M16 Plan 2 — typed GameObject & component tools.
import { gameobjectCreate } from "./gameobject-create.js";
import { gameobjectDestroy } from "./gameobject-destroy.js";
import { gameobjectDuplicate } from "./gameobject-duplicate.js";
import { gameobjectFind } from "./gameobject-find.js";
import { gameobjectModify } from "./gameobject-modify.js";
import { gameobjectSetParent } from "./gameobject-set-parent.js";
import { componentAdd } from "./component-add.js";
import { componentDestroy } from "./component-destroy.js";
import { componentGet } from "./component-get.js";
import { componentModify } from "./component-modify.js";
import { componentListAll } from "./component-list-all.js";
// M16 Plan 3 — typed scene management tools.
import { sceneCreate } from "./scene-create.js";
import { sceneOpen } from "./scene-open.js";
import { sceneSave } from "./scene-save.js";
import { sceneUnload } from "./scene-unload.js";
import { sceneSetActive } from "./scene-set-active.js";
import { sceneListOpened } from "./scene-list-opened.js";
import { sceneGetData } from "./scene-get-data.js";
import { sceneGetDirtySummary } from "./scene-get-dirty-summary.js";
import { sceneFocus } from "./scene-focus.js";
// M16 Plan 4 — typed Package Manager tools.
import { packageList } from "./package-list.js";
import { packageSearch } from "./package-search.js";
import { packageAdd } from "./package-add.js";
import { packageRemove } from "./package-remove.js";
import { packageGetInfo } from "./package-get-info.js";
import { packageGetDependencies } from "./package-get-dependencies.js";
import { packageCheck } from "./package-check.js";
// M16 Plan 5 — typed console / editor state / selection / undo / tags / layers.
import { consoleClear } from "./console-clear.js";
import { consoleLog } from "./console-log.js";
import { editorSetState } from "./editor-set-state.js";
import { selectionGet } from "./selection-get.js";
import { selectionSet } from "./selection-set.js";
import { editorUndo } from "./editor-undo.js";
import { editorRedo } from "./editor-redo.js";
import { editorGetTags } from "./editor-get-tags.js";
import { editorGetLayers } from "./editor-get-layers.js";
import { editorAddTag } from "./editor-add-tag.js";
import { editorAddLayer } from "./editor-add-layer.js";
// M16 Plan 6 — typed reflection / scripts / object data tools.
import { typeSchema } from "./type-schema.js";
import { scriptRead } from "./script-read.js";
import { scriptWrite } from "./script-write.js";
import { scriptDelete } from "./script-delete.js";
import { objectGetData } from "./object-get-data.js";
import { objectModify } from "./object-modify.js";
// M16 Plan 7 — typed profiler session / diagnostics tools.
import { profilerStart } from "./profiler-start.js";
import { profilerStop } from "./profiler-stop.js";
import { profilerGetStatus } from "./profiler-get-status.js";
import { profilerGetConfig } from "./profiler-get-config.js";
import { profilerSetConfig } from "./profiler-set-config.js";
import { profilerListModules } from "./profiler-list-modules.js";
import { profilerEnableModule } from "./profiler-enable-module.js";
import { profilerClearData } from "./profiler-clear-data.js";
import { profilerSaveData } from "./profiler-save-data.js";
import { profilerLoadData } from "./profiler-load-data.js";
import { profilerGetScriptStats } from "./profiler-get-script-stats.js";
// M16 Plan 8 — typed gate intelligence tools.
import { impactPreview } from "./impact-preview.js";
import { gateBudgetEstimate } from "./gate-budget-estimate.js";
import { mutationExplain } from "./mutation-explain.js";
// M16 Plan 9 — typed build pipeline + project-settings tools.
import { buildGetTargets } from "./build-get-targets.js";
import { buildGetActiveTarget } from "./build-get-active-target.js";
import { buildSetTarget } from "./build-set-target.js";
import { buildGetScenes } from "./build-get-scenes.js";
import { buildSetScenes } from "./build-set-scenes.js";
import { buildStart } from "./build-start.js";
import { buildGetDefines } from "./build-get-defines.js";
import { buildSetDefines } from "./build-set-defines.js";
import { settingsGetPlayer } from "./settings-get-player.js";
import { settingsSetPlayer } from "./settings-set-player.js";
import { settingsGetQuality } from "./settings-get-quality.js";
import { settingsSetQuality } from "./settings-set-quality.js";
import { settingsGetPhysics } from "./settings-get-physics.js";
import { settingsSetPhysics } from "./settings-set-physics.js";
import { settingsGetLighting } from "./settings-get-lighting.js";
import { settingsSetLighting } from "./settings-set-lighting.js";
// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tools. Each tool is
// gated on the `com.alexeyperov.unity-open-mcp-ext-navigation` extension pack
// being installed in the target project; the tool definitions live in core so
// capabilities discovery advertises the surface even before the pack is added.
import { navigationSurfaceAdd } from "./navigation-surface-add.js";
import { navigationSetBakeSettings } from "./navigation-set-bake-settings.js";
import { navigationSurfaceBake } from "./navigation-surface-bake.js";
import { navigationModifierAdd } from "./navigation-modifier-add.js";
import { navigationModifierVolumeAdd } from "./navigation-modifier-volume-add.js";
import { navigationLinkAdd } from "./navigation-link-add.js";
import { navigationAgentAdd } from "./navigation-agent-add.js";
import { navigationAgentSetDestination } from "./navigation-agent-set-destination.js";
import { navigationList } from "./navigation-list.js";
import { navigationGet } from "./navigation-get.js";
import { navigationModify } from "./navigation-modify.js";
// M16 Plan 10 / T6.6.4 — Input System extension tools. Each tool is gated on
// the `com.alexeyperov.unity-open-mcp-ext-inputsystem` extension pack being
// installed in the target project; the tool definitions live in core so
// capabilities discovery advertises the surface even before the pack is added.
import { inputsystemAssetCreate } from "./inputsystem-asset-create.js";
import { inputsystemActionmapAdd } from "./inputsystem-actionmap-add.js";
import { inputsystemActionAdd } from "./inputsystem-action-add.js";
import { inputsystemBindingAdd } from "./inputsystem-binding-add.js";
import { inputsystemBindingCompositeAdd } from "./inputsystem-binding-composite-add.js";
import { inputsystemControlschemeAdd } from "./inputsystem-controlscheme-add.js";
import { inputsystemGet } from "./inputsystem-get.js";
// M16 Plan 10 / T6.6.5 — ProBuilder extension tools. Each tool is gated on the
// `com.alexeyperov.unity-open-mcp-ext-probuilder` extension pack being
// installed in the target project.
import { probuilderCreateShape } from "./probuilder-create-shape.js";
import { probuilderGetMeshInfo } from "./probuilder-get-mesh-info.js";
import { probuilderExtrude } from "./probuilder-extrude.js";
import { probuilderDeleteFaces } from "./probuilder-delete-faces.js";
import { probuilderSetFaceMaterial } from "./probuilder-set-face-material.js";
// M16 Plan 10 / T6.6.9 — Particle System extension tools. Each tool is gated
// on the `com.alexeyperov.unity-open-mcp-ext-particlesystem` extension pack
// being installed in the target project.
import { particleSystemGet } from "./particle-system-get.js";
import { particleSystemModify } from "./particle-system-modify.js";
// M16 Plan 10 / T6.6.10 — Animation extension tools (AnimationClip +
// AnimatorController). Each tool is gated on the
// `com.alexeyperov.unity-open-mcp-ext-animation` extension pack being installed
// in the target project.
import { animationCreate } from "./animation-create.js";
import { animationGetData } from "./animation-get-data.js";
import { animationModify } from "./animation-modify.js";
import { animatorCreate } from "./animator-create.js";
import { animatorGetData } from "./animator-get-data.js";
import { animatorModify } from "./animator-modify.js";
// M18 Plan 7 / T18.7.3 — Splines extension tools. First backlog domain
// shipped under the embedded + grouped model. Each tool is compile-gated on
// the com.unity.splines package in the bridge (UNITY_OPEN_MCP_EXT_SPLINES);
// the tool definitions live in core so capabilities discovery advertises the
// surface even before the package is installed.
import { splinesContainerCreate } from "./splines-container-create.js";
import { splinesAddKnot } from "./splines-add-knot.js";
import { splinesSetKnot } from "./splines-set-knot.js";
import { splinesSetTangentMode } from "./splines-set-tangent-mode.js";
import { splinesEvaluate } from "./splines-evaluate.js";
import { splinesGetKnots } from "./splines-get-knots.js";
import { splinesModify } from "./splines-modify.js";
// M20 Plan 2 / T20.2 — Lighting domain tools. Built-in lighting module (Light /
// ReflectionProbe / RenderSettings / Lightmapping) — ungated in the bridge (no
// UNITY_OPEN_MCP_EXT_LIGHTING define), always compiled. The `lighting` group is
// hidden from ListTools until the session activates it via manage_tools.
// Mutating members (light_add / light_set / light_modify /
// reflection_probe_bake / skybox_set) run the full gate path with paths_hint
// scoped to the host scene path (skybox_set dirties the active scene).
// reflection_probe_bake is the long mutation — EditorSettle so the dispatcher
// waits for the bake + asset refresh before returning (the documented
// advantage over AnkleBreaker's ungated bake). Read-only members
// (reflection_probe_get / skybox_get) are gate-free.
import { lightAdd } from "./light-add.js";
import { lightSet } from "./light-set.js";
import { lightModify } from "./light-modify.js";
import { reflectionProbeBake } from "./reflection-probe-bake.js";
import { reflectionProbeGet } from "./reflection-probe-get.js";
import { skyboxSet } from "./skybox-set.js";
import { skyboxGet } from "./skybox-get.js";

export const M2_TOOLS: Tool[] = [
  ping,
  executeCsharp,
  invokeMethod,
  executeMenu,
  findMembers,
  compileCheck,
];

export const M2_5_TOOLS: Tool[] = [editorStatus];

export const M3_TOOLS: Tool[] = [validateEdit, checkpointCreate, delta, findReferences, scanPaths, applyFix];

export const M5_TOOLS: Tool[] = [scanAll, baselineCreate, regressionCheck];

export const M9_TOOLS: Tool[] = [reserialize, readAsset, searchAssets, listAssets];

export const M10_TOOLS: Tool[] = [
  runTests,
  screenshot,
  readConsole,
  profilerCapture,
  profilerMemory,
  profilerRendering,
  spatialQuery,
];

// M20 Plan 1 / T20.1.1 — senses parity residual. Two new read-only senses
// tools that extend the existing screenshot surface: screenshot_camera renders
// from an arbitrary world-space pose (transient camera, scene camera untouched),
// and capture_inline returns the PNG as an inline base64 image content block
// (no temp file) so agents that don't read the filesystem still get a viewable
// image. Both are live-only (no batch fallback) and route as direct-response
// tools — the bridge returns tool JSON directly (inlineImage field carried by
// capture_inline is unwrapped into an MCP image block in live-client.ts).
//
// M20 Plan 1 / T20.1.2 adds screenshot_window (EditorWindow capture; Win-only
// full-fidelity, cross-platform best-effort readback with platformLimited).
//
// M20 Plan 1 / T20.1.3 adds frame_debugger (reflection over Unity's internal
// Frame Debugger — enable/disable/list draw calls; non-mutating Editor state,
// gate-free).
//
// M20 Plan 1 / T20.1.4 adds profiler_capture_frame (single-frame deep profiler
// capture; returns the sample tree for the requested modules).
export const M20_PLAN1_TOOLS: Tool[] = [
  screenshotCamera,
  captureInline,
  screenshotWindow,
  frameDebugger,
  profilerCaptureFrame,
];

export const M11_TOOLS: Tool[] = [agentCapabilities, generateSkill];

export const M12_TOOLS: Tool[] = [listRules];

export const M13_TOOLS: Tool[] = [pullEvents];

// Offline Editor.log compiler-error reader. Routed offline (no bridge, no
// Unity spawn) — the one channel that works when the bridge assembly itself
// has failed to compile.
export const M14_TOOLS: Tool[] = [readCompileErrors];

// testsuite-tauri phase-3 — operator-only bridge admin surface. v1 ships
// `bridge_status` only (a thin wrapper over the instance-lock classifier +
// one /ping). `bridge_stop` / `bridge_start` are deferred (need new bridge
// HTTP routes; `stop` has a self-disconnect hazard). Like `read_compile_errors`,
// these carry no tool-group assignment → they are always-visible meta-tools
// (operators / the Validation Suite reach them; the agent skill does NOT
// document them in mutate/gate sections). See docs/api/mcp-tools.md.
export const BRIDGE_ADMIN_TOOLS: Tool[] = [bridgeStatus];

// M18 Plan 2 / T18.2.2 — Coplay-style manage_tools meta-tool. Server-only,
// local-routed, and always visible regardless of which groups the current
// session has activated (see ALWAYS_VISIBLE_TOOLS in tool-session-state.ts).
export const M18_PLAN2_TOOLS: Tool[] = [manageTools];

// M16 Plan 1 — Project & Asset Management typed tools. Mutating members run
// the full gate path with `paths_hint`; read-only members (shader reads,
// material property/keyword reads, prefab status/overrides) are gate-free.
export const M16_PLAN1_TOOLS: Tool[] = [
  assetsCreateFolder,
  assetsCopy,
  assetsMove,
  assetsDelete,
  assetsRefresh,
  materialCreate,
  materialGetProperties,
  materialSetProperty,
  materialGetKeywords,
  materialSetKeyword,
  materialSetShader,
  shaderListAll,
  shaderGetData,
  prefabInstantiate,
  prefabCreate,
  prefabOpen,
  prefabClose,
  prefabSave,
  prefabApply,
  prefabRevert,
  prefabUnpack,
  prefabGetOverrides,
  prefabStatus,
];

// M16 Plan 2 — GameObject & Components typed tools. Mutating members run the
// full gate path with `paths_hint`; read-only members (gameobject_find,
// component_get, component_list_all) are gate-free.
export const M16_PLAN2_TOOLS: Tool[] = [
  gameobjectCreate,
  gameobjectDestroy,
  gameobjectDuplicate,
  gameobjectFind,
  gameobjectModify,
  gameobjectSetParent,
  componentAdd,
  componentDestroy,
  componentGet,
  componentModify,
  componentListAll,
];

// M16 Plan 3 — Scene Management typed tools. Mutating members run the full
// gate path with `paths_hint` scoped to the scene asset (or scene path for
// scene_focus); read-only members (scene_list_opened, scene_get_data,
// scene_get_dirty_summary) are gate-free. scene_get_data supersedes the
// standalone M10 scene snapshot.
export const M16_PLAN3_TOOLS: Tool[] = [
  sceneCreate,
  sceneOpen,
  sceneSave,
  sceneUnload,
  sceneSetActive,
  sceneListOpened,
  sceneGetData,
  sceneGetDirtySummary,
  sceneFocus,
];

// M16 Plan 4 — Package Manager typed tools. Mutating members (add / remove)
// run the full gate path with `paths_hint` = ["Packages/manifest.json"] and
// use the restart_then_settle lifecycle (UPM resolution may domain-reload);
// read-only members (list / search / get_info / get_dependencies / check)
// are gate-free. get_dependencies + check read Packages/manifest.json
// directly (no UPM request) for fast manifest snapshots.
export const M16_PLAN4_TOOLS: Tool[] = [
  packageList,
  packageSearch,
  packageAdd,
  packageRemove,
  packageGetInfo,
  packageGetDependencies,
  packageCheck,
];

// M16 Plan 5 — Console + editor state / selection / undo / tags / layers typed
// tools. Most mutate editor state but write no assets (console, editor state,
// selection, undo/redo) — they are gate-free direct-response tools (the gate
// validates asset-reference fallout, which does not apply). editor_set_state
// still runs the active-scene dirty guard (entering play mode can trigger
// Unity's native save modal). editor_add_tag / editor_add_layer write
// ProjectSettings/TagManager.asset and run the full gate path with `paths_hint`
// scoped to it; editor_get_tags / editor_get_layers are read-only gate-free.
export const M16_PLAN5_TOOLS: Tool[] = [
  consoleClear,
  consoleLog,
  editorSetState,
  selectionGet,
  selectionSet,
  editorUndo,
  editorRedo,
  editorGetTags,
  editorGetLayers,
  editorAddTag,
  editorAddLayer,
];

// M16 Plan 6 — typed reflection / scripts / object data tools. Read-only
// members (type_schema, script_read, object_get_data) are gate-free direct-
// response tools; mutating members (script_write, script_delete,
// object_modify) run the full gate path with paths_hint. script_write runs
// Roslyn pre-write validation and exposes the diagnostics as a return field.
// Enhancements to find_members / invoke_method (richer overload/signature
// metadata, generic-arg resolution) ship in place — see those tool files.
export const M16_PLAN6_TOOLS: Tool[] = [
  typeSchema,
  scriptRead,
  scriptWrite,
  scriptDelete,
  objectGetData,
  objectModify,
];

// M16 Plan 7 — typed profiler session / diagnostics tools. Most mutate editor
// state but write NO assets (start / stop / set_config / enable_module /
// clear_data) — they are gate-free direct-response tools (the gate validates
// asset-reference fallout, which does not apply). Read-only members
// (get_status / get_config / list_modules / load_data / get_script_stats) are
// gate-free as well. save_data is the lone asset-writing mutator and runs the
// full gate path with paths_hint scoped to the destination .json path. The
// M10 capture / memory / rendering reads are NOT duplicated — agents use them
// for per-frame hierarchy / allocator bytes / GPU + QualitySettings batch.
export const M16_PLAN7_TOOLS: Tool[] = [
  profilerStart,
  profilerStop,
  profilerGetStatus,
  profilerGetConfig,
  profilerSetConfig,
  profilerListModules,
  profilerEnableModule,
  profilerClearData,
  profilerSaveData,
  profilerLoadData,
  profilerGetScriptStats,
];

// M16 Plan 8 — gate intelligence typed tools. All three are read-only, gate-
// free direct-response tools that compose existing checkpoint / validate /
// delta / verify / run-history foundations — they add NO new verify rules and
// re-implement NO existing tool. impact_preview + gate_budget_estimate are
// pre-mutation (scope-first, deterministic); mutation_explain is post-
// mutation (projects the latest gate run or an explicit checkpoint into a
// narrative + structured summary). confidence / heuristic boundaries are
// stated in each tool's response so agents treat the outputs as guidance.
export const M16_PLAN8_TOOLS: Tool[] = [
  impactPreview,
  gateBudgetEstimate,
  mutationExplain,
];

// M16 Plan 9 — build pipeline + project-settings typed tools. Read-only
// members (build_get_targets / build_get_active_target / build_get_scenes /
// build_get_defines / settings_get_*) are gate-free direct-response tools.
// build_set_target / build_set_defines use restart_then_settle (they can
// recompile); build_set_scenes / settings_set_quality / settings_set_physics /
// settings_set_lighting use editor_settle; settings_set_player uses
// restart_then_settle. Each runs the full gate path with paths_hint scoped to
// the touched ProjectSettings asset. build_start additionally requires the
// deny bypass (gate: "off" + confirm_bypass: true) because
// BuildPipeline.BuildPlayer is on the default deny list.
export const M16_PLAN9_TOOLS: Tool[] = [
  buildGetTargets,
  buildGetActiveTarget,
  buildSetTarget,
  buildGetScenes,
  buildSetScenes,
  buildStart,
  buildGetDefines,
  buildSetDefines,
  settingsGetPlayer,
  settingsSetPlayer,
  settingsGetQuality,
  settingsSetQuality,
  settingsGetPhysics,
  settingsSetPhysics,
  settingsGetLighting,
  settingsSetLighting,
];

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tools. Extension pack
// tools ship their tool definitions in the core MCP server (so capabilities
// advertises the surface) but require the matching extension UPM package
// installed in the target project for the bridge-side handler to exist.
// Mutating members (surface_add / set_bake_settings / surface_bake /
// modifier_add / modifier_volume_add / link_add / agent_add /
// agent_set_destination / modify) run the full gate path with paths_hint
// scoped to the host scene path; surface_bake is the heavy op (EditorSettle).
// Read-only members (list / get) are gate-free. The eleven tools mirror the
// kebab `navigation-*` ids in the upstream Unity-MCP reference pack.
export const M16_PLAN10_TOOLS: Tool[] = [
  navigationSurfaceAdd,
  navigationSetBakeSettings,
  navigationSurfaceBake,
  navigationModifierAdd,
  navigationModifierVolumeAdd,
  navigationLinkAdd,
  navigationAgentAdd,
  navigationAgentSetDestination,
  navigationList,
  navigationGet,
  navigationModify,
];

// M16 Plan 10 / T6.6.4 — Input System extension tools. Extension pack tools
// ship their tool definitions in the core MCP server (so capabilities
// advertises the surface) but require the matching extension UPM package
// installed in the target project for the bridge-side handler to exist. Six
// mutating members (asset_create / actionmap_add / action_add / binding_add /
// binding_composite_add / controlscheme_add) run the full gate path with
// paths_hint scoped to the .inputactions asset; the read-only member (get) is
// gate-free. The seven tools mirror the kebab `inputsystem-*` ids in the
// upstream Unity-MCP reference pack.
export const M16_PLAN10_INPUTSYSTEM_TOOLS: Tool[] = [
  inputsystemAssetCreate,
  inputsystemActionmapAdd,
  inputsystemActionAdd,
  inputsystemBindingAdd,
  inputsystemBindingCompositeAdd,
  inputsystemControlschemeAdd,
  inputsystemGet,
];

// M16 Plan 10 / T6.6.5 — ProBuilder extension tools. Four mutating members
// (create_shape / extrude / delete_faces / set_face_material) run the full
// gate path with paths_hint scoped to the host scene path (create_shape adds a
// new GameObject to the active scene); delete_faces is the lone DESTRUCTIVE
// tool. The read-only member (get_mesh_info) is gate-free. Face selection is
// index-based or semantic (Up / Down / Left / Right / Forward / Back) — never
// SceneView mouse picking. The five tools mirror the kebab `probuilder-*` ids
// in the upstream Unity-MCP reference pack.
export const M16_PLAN10_PROBUILDER_TOOLS: Tool[] = [
  probuilderCreateShape,
  probuilderGetMeshInfo,
  probuilderExtrude,
  probuilderDeleteFaces,
  probuilderSetFaceMaterial,
];

// M16 Plan 10 / T6.6.9 — Particle System extension tools. The lone mutator
// (particle_system_modify) runs the full gate path with paths_hint scoped to
// the host scene path; the read-only member (particle_system_get) is gate-free.
// The two tools mirror the kebab `particle-system-*` ids in the upstream
// Unity-MCP reference pack. modify uses a per-module field-patch surface
// (module discriminator + fields_json) instead of the opaque ReflectorNet
// SerializedMember payload — agents get up-front field-name validation and a
// structured unknownFields report instead of guessing.
export const M16_PLAN10_PARTICLESYSTEM_TOOLS: Tool[] = [
  particleSystemGet,
  particleSystemModify,
];

// M16 Plan 10 / T6.6.10 — Animation extension tools (AnimationClip + Animator
// Controller). Mutating members (animation_create / animation_modify /
// animator_create / animator_modify) run the full gate path with paths_hint
// scoped to the asset path (.anim or .controller); the two modify tools are
// DESTRUCTIVE — some modification types are irreversible without undo. Read-
// only members (animation_get_data / animator_get_data) are gate-free. The six
// tools mirror the kebab `animation-*` / `animator-*` ids in the upstream
// Unity-MCP reference pack. modify accepts a JSON array of modification
// entries dispatched by `type`; per-entry errors are accumulated, not thrown.
export const M16_PLAN10_ANIMATION_TOOLS: Tool[] = [
  animationCreate,
  animationGetData,
  animationModify,
  animatorCreate,
  animatorGetData,
  animatorModify,
];

// M18 Plan 7 / T18.7.3 — Splines extension tools. The first backlog domain
// shipped under the embedded + grouped model (Cinemachine, the recommended
// first domain, was swapped for Splines per the plan's fallback path — see
// the M18 changelog). Five mutating members (container_create / add_knot /
// set_knot / set_tangent_mode / modify) run the full gate path with paths_hint
// scoped to the host scene path (container_create adds a new GameObject to the
// active scene). Two read-only members (evaluate / get_knots) are gate-free.
// The seven tools mirror the kebab `splines-*` ids in the upstream
// Unity-AI-Splines reference pack.
export const M18_PLAN7_SPLINES_TOOLS: Tool[] = [
  splinesContainerCreate,
  splinesAddKnot,
  splinesSetKnot,
  splinesSetTangentMode,
  splinesEvaluate,
  splinesGetKnots,
  splinesModify,
];

// M20 Plan 2 / T20.2 — Lighting domain tools. Built-in lighting module —
// ungated in the bridge (always compiled). Five mutating members (light_add /
// light_set / light_modify / reflection_probe_bake / skybox_set) run the full
// gate path with paths_hint scoped to the host scene path. The bake is the
// long mutation (EditorSettle). Two read-only members (reflection_probe_get /
// skybox_get) are gate-free. Closes the Lighting & environment parity gap with
// AnkleBreaker's lighting category.
export const M20_PLAN2_LIGHTING_TOOLS: Tool[] = [
  lightAdd,
  lightSet,
  lightModify,
  reflectionProbeBake,
  reflectionProbeGet,
  skyboxSet,
  skyboxGet,
];

export const ALL_TOOLS: Tool[] = [
  ...M2_TOOLS,
  ...M2_5_TOOLS,
  ...M3_TOOLS,
  ...M5_TOOLS,
  ...M9_TOOLS,
  ...M10_TOOLS,
  ...M20_PLAN1_TOOLS,
  ...M11_TOOLS,
  ...M12_TOOLS,
  ...M13_TOOLS,
  ...M14_TOOLS,
  ...BRIDGE_ADMIN_TOOLS,
  ...M16_PLAN1_TOOLS,
  ...M16_PLAN2_TOOLS,
  ...M16_PLAN3_TOOLS,
  ...M16_PLAN4_TOOLS,
  ...M16_PLAN5_TOOLS,
  ...M16_PLAN6_TOOLS,
  ...M16_PLAN7_TOOLS,
  ...M16_PLAN8_TOOLS,
  ...M16_PLAN9_TOOLS,
  ...M16_PLAN10_TOOLS,
  ...M16_PLAN10_INPUTSYSTEM_TOOLS,
  ...M16_PLAN10_PROBUILDER_TOOLS,
  ...M16_PLAN10_PARTICLESYSTEM_TOOLS,
  ...M16_PLAN10_ANIMATION_TOOLS,
  ...M18_PLAN2_TOOLS,
  ...M18_PLAN7_SPLINES_TOOLS,
  ...M20_PLAN2_LIGHTING_TOOLS,
];
