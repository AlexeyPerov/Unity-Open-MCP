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

export const M11_TOOLS: Tool[] = [agentCapabilities, generateSkill];

export const M12_TOOLS: Tool[] = [listRules];

export const M13_TOOLS: Tool[] = [pullEvents];

// Offline Editor.log compiler-error reader. Routed offline (no bridge, no
// Unity spawn) — the one channel that works when the bridge assembly itself
// has failed to compile.
export const M14_TOOLS: Tool[] = [readCompileErrors];

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

export const ALL_TOOLS: Tool[] = [
  ...M2_TOOLS,
  ...M2_5_TOOLS,
  ...M3_TOOLS,
  ...M5_TOOLS,
  ...M9_TOOLS,
  ...M10_TOOLS,
  ...M11_TOOLS,
  ...M12_TOOLS,
  ...M13_TOOLS,
  ...M14_TOOLS,
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
];
