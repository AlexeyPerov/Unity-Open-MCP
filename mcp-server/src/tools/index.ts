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
import { dependencies } from "./dependencies.js";
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
// M18 Plan 2 / T18.2.2 — manage_tools meta-tool (per-session tool-group
// visibility). Server-only meta-tool; routes local, always visible regardless
// of which groups the current session has activated.
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
import { sceneviewGetCamera } from "./sceneview-get-camera.js";
import { sceneviewSetCamera } from "./sceneview-set-camera.js";
// M16 Plan 4 — typed Package Manager tools.
import { packageList } from "./package-list.js";
import { packageSearch } from "./package-search.js";
import { packageAdd } from "./package-add.js";
import { packageRemove } from "./package-remove.js";
import { reimportPackage } from "./reimport-package.js";
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
import { editorUndoHistory } from "./editor-undo-history.js";
import { editorClearHistory } from "./editor-clear-history.js";
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
// M20 Plan 5 / T20.5 — typed ScriptableObject + Assembly Definition tools.
// scriptableobject_create is a mutating create path (EditorSettle); the read/
// info surface stays on object_get_data / object_modify. list_assets_of_type is
// a read-only typed list-by-type. The asmdef family (list/get/create/modify)
// closes the asmdef parity gap: list/get are read-only; create/modify use the
// RestartThenSettle lifecycle (a recompile + domain reload follows).
import { scriptableObjectCreate } from "./scriptableobject-create.js";
import { listAssetsOfType } from "./list-assets-of-type.js";
import { asmdefList } from "./asmdef-list.js";
import { asmdefGet } from "./asmdef-get.js";
import { asmdefCreate } from "./asmdef-create.js";
import { asmdefModify } from "./asmdef-modify.js";
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
// M20 Plan 9 / T20.9.2 — KV preferences (PlayerPrefs + EditorPrefs). Mutating
// members (set / delete) write to the registry / Library/PlayerPreferences, NOT
// to project assets — they route as gate-free direct-response tools like
// editor_undo (the catalog/toggle still marks them mutating). playerprefs_set
// calls PlayerPrefs.Save(); EditorPrefs writes through immediately.
// playerprefs_delete_all is deliberately omitted (irreversible wipe — route
// through execute_csharp with an explicit confirm).
import { playerprefsGet } from "./playerprefs-get.js";
import { playerprefsSet } from "./playerprefs-set.js";
import { playerprefsDelete } from "./playerprefs-delete.js";
import { editorprefsGet } from "./editorprefs-get.js";
import { editorprefsSet } from "./editorprefs-set.js";
import { editorprefsDelete } from "./editorprefs-delete.js";
// M20 Plan 9 / T20.9.3 — Project Settings remainder. set_time +
// set_quality_level write ProjectSettings/*.asset (full gate path); get_time +
// get_render_pipeline are read-only. get_render_pipeline has no setter —
// switching SRP is a package-level operation.
import { settingsGetTime } from "./settings-get-time.js";
import { settingsSetTime } from "./settings-set-time.js";
import { settingsGetRenderPipeline } from "./settings-get-render-pipeline.js";
import { settingsSetQualityLevel } from "./settings-set-quality-level.js";
// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tools. The bridge-side
// handlers are embedded in the bridge (compile-gated by
// UNITY_OPEN_MCP_EXT_NAVIGATION, active when com.unity.ai.navigation is
// present); the tool definitions live in core so capabilities discovery
// advertises the surface even before the package is added.
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
// M16 Plan 10 / T6.6.4 — Input System extension tools. The bridge-side
// handlers are embedded in the bridge (compile-gated by
// UNITY_OPEN_MCP_EXT_INPUTSYSTEM, active when com.unity.inputsystem is
// present); the tool definitions live in core so capabilities discovery
// advertises the surface even before the package is added.
import { inputsystemAssetCreate } from "./inputsystem-asset-create.js";
import { inputsystemActionmapAdd } from "./inputsystem-actionmap-add.js";
import { inputsystemActionAdd } from "./inputsystem-action-add.js";
import { inputsystemBindingAdd } from "./inputsystem-binding-add.js";
import { inputsystemBindingCompositeAdd } from "./inputsystem-binding-composite-add.js";
import { inputsystemControlschemeAdd } from "./inputsystem-controlscheme-add.js";
import { inputsystemGet } from "./inputsystem-get.js";
// M16 Plan 10 / T6.6.5 — ProBuilder extension tools. The bridge-side handlers
// are embedded in the bridge (compile-gated by UNITY_OPEN_MCP_EXT_PROBUILDER,
// active when com.unity.probuilder is present).
import { probuilderCreateShape } from "./probuilder-create-shape.js";
import { probuilderGetMeshInfo } from "./probuilder-get-mesh-info.js";
import { probuilderExtrude } from "./probuilder-extrude.js";
import { probuilderDeleteFaces } from "./probuilder-delete-faces.js";
import { probuilderSetFaceMaterial } from "./probuilder-set-face-material.js";
// M16 Plan 10 / T6.6.9 — Particle System extension tools. The bridge-side
// handlers are embedded in the bridge (ungated — UnityEngine.ParticleSystem is
// a core engine module present in every install).
import { particleSystemGet } from "./particle-system-get.js";
import { particleSystemModify } from "./particle-system-modify.js";
// M16 Plan 10 / T6.6.10 — Animation extension tools (AnimationClip +
// AnimatorController). The bridge-side handlers are embedded in the bridge
// (compile-gated by UNITY_OPEN_MCP_EXT_ANIMATION, active when the built-in
// animation module is present).
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
// waits for the bake + asset refresh before returning. Read-only members
// (reflection_probe_get / skybox_get) are gate-free.
import { lightAdd } from "./light-add.js";
import { lightSet } from "./light-set.js";
import { lightModify } from "./light-modify.js";
import { reflectionProbeBake } from "./reflection-probe-bake.js";
import { reflectionProbeGet } from "./reflection-probe-get.js";
import { skyboxSet } from "./skybox-set.js";
import { skyboxGet } from "./skybox-get.js";
// M20 Plan 3 / T20.3.1 — Audio domain tools. Built-in audio module (AudioSource /
// AudioListener / AudioMixer / AudioMixerGroup from UnityEngine.AudioModule) —
// ungated in the bridge (no UNITY_OPEN_MCP_EXT_AUDIO define), always compiled.
// The `audio` group is hidden from ListTools until the session activates it via
// manage_tools. Mutating members (audio_source_add / audio_source_modify /
// audio_mixer_set_parameter) run the full gate path with paths_hint scoped to
// the host scene path (or the mixer asset path for audio_mixer_set_parameter).
// Read-only members (audio_listener_get / audio_mixer_get_parameter) are
// gate-free. The mixer-parameter round-trip (set then read-back) is the
// documented advantage — a source-only audio tool touches per-source settings
// only, while this set also reads/writes exposed AudioMixer float parameters.
import { audioSourceAdd } from "./audio-source-add.js";
import { audioSourceModify } from "./audio-source-modify.js";
import { audioMixerSetParameter } from "./audio-mixer-set-parameter.js";
import { audioListenerGet } from "./audio-listener-get.js";
import { audioMixerGetParameter } from "./audio-mixer-get-parameter.js";
// M20 Plan 3 / T20.3.2 — UI (uGUI) domain tools. Built-in UI module (Canvas /
// CanvasScaler / GraphicRaycaster / Image / Text / Button / Slider / Toggle /
// InputField / layout groups / EventSystem from UnityEngine.UI +
// UnityEngine.EventSystems) — ungated in the bridge (no UNITY_OPEN_MCP_EXT_UI
// define), always compiled. TextMesh Pro (TMP_Text) is OPTIONAL and detected
// at call time via reflection — when an agent requests element_type=TMP_Text
// and TMP is absent, the tool returns `tmp_package_required` (no silent
// legacy-Text fallback). The `ui` group is hidden from ListTools until the
// session activates it via manage_tools. All four members (ui_canvas_add /
// ui_element_add / ui_layout_group_add / ui_element_modify) run the full gate
// path with paths_hint scoped to the host / new-root / parent scene path. The
// Canvas-companion ensure (CanvasScaler + GraphicRaycaster + EventSystem) is
// the documented advantage — a canvas-only tool creates the Canvas alone.
import { uiCanvasAdd } from "./ui-canvas-add.js";
import { uiElementAdd } from "./ui-element-add.js";
import { uiLayoutGroupAdd } from "./ui-layout-group-add.js";
import { uiElementModify } from "./ui-element-modify.js";
// M20 Plan 3 / T20.3.3 — Constraints & LOD domain tools. Built-in engine
// modules (UnityEngine.AnimationModule for the constraint components,
// UnityEngine.CoreModule for LODGroup) — ungated in the bridge (no
// UNITY_OPEN_MCP_EXT_CONSTRAINTS define), always compiled. The `constraints`
// group is hidden from ListTools until the session activates it via
// manage_tools. All three members (constraint_add / lod_group_configure /
// lod_add_level) are mutating and run the full gate path with paths_hint
// scoped to the host scene path. The gate + paths_hint contract on every
// mutating member is the documented advantage.
import { constraintAdd } from "./constraint-add.js";
import { lodGroupConfigure } from "./lod-group-configure.js";
import { lodAddLevel } from "./lod-add-level.js";
// M20 Plan 4 / T20.4 — Terrain domain tools. Built-in Terrain module (Terrain
// / TerrainData / TreePrototype / TerrainLayer) — ungated in the bridge (no
// UNITY_OPEN_MCP_EXT_TERRAIN define), always compiled. The `terrain` group is
// hidden from ListTools until the session activates it via manage_tools. All
// five members (terrain_create / terrain_set_heights / terrain_paint_layer /
// terrain_place_trees / terrain_set_neighbors) are mutating and run the full
// gate path with paths_hint scoped to the host scene path (+ the asset path
// for terrain_create's TerrainData .asset and terrain_paint_layer's new
// TerrainLayer .terrainlayer). Closes the Terrain parity gap; the
// large-array cap + tiling hint and the gate + paths_hint contract on every
// mutating member are the documented advantages.
import { terrainCreate } from "./terrain-create.js";
import { terrainSetHeights } from "./terrain-set-heights.js";
import { terrainPaintLayer } from "./terrain-paint-layer.js";
import { terrainPlaceTrees } from "./terrain-place-trees.js";
import { terrainSetNeighbors } from "./terrain-set-neighbors.js";
// M20 Plan 6 / T20.6.1 — Cinemachine extension tools. The only
// reflection-gated domain pack in the bridge: the assembly always compiles
// (no UNITY_OPEN_MCP_EXT_CINEMACHINE compile gate), and Cinemachine 3.x
// presence is detected at call time via the CinemachineVersion reflection
// layer. When 3.x is absent (package missing OR Cinemachine 2.x installed),
// the tools return a clear install/upgrade error envelope. Six mutating
// members (create_camera / set_targets / set_lens / set_body / set_noise /
// brain_ensure) run the full gate path with paths_hint scoped to the host
// scene path (create_camera adds a new GameObject to the active scene). One
// read-only member (camera_list) is gate-free.
import { cinemachineCreateCamera } from "./cinemachine-create-camera.js";
import { cinemachineSetTargets } from "./cinemachine-set-targets.js";
import { cinemachineSetLens } from "./cinemachine-set-lens.js";
import { cinemachineSetBody } from "./cinemachine-set-body.js";
import { cinemachineSetNoise } from "./cinemachine-set-noise.js";
import { cinemachineBrainEnsure } from "./cinemachine-brain-ensure.js";
import { cinemachineCameraList } from "./cinemachine-camera-list.js";
// M20 Plan 6 / T20.6.2 — Timeline extension tools. Compile-gated in the
// bridge (UNITY_OPEN_MCP_EXT_TIMELINE on com.unity.timeline). All five
// members (create / track_add / clip_add / director_bind / modify) are
// mutating and run the full gate path. create produces a .playable asset;
// director_bind mutates a scene PlayableDirector; modify is a reflective
// field-patch escape hatch. The gate + paths_hint contract on every mutating
// member is the documented advantage.
import { timelineCreate } from "./timeline-create.js";
import { timelineTrackAdd } from "./timeline-track-add.js";
import { timelineClipAdd } from "./timeline-clip-add.js";
import { timelineDirectorBind } from "./timeline-director-bind.js";
import { timelineModify } from "./timeline-modify.js";
// M20 Plan 6 / T20.6.3 — Tilemap extension tools. Compile-gated in the
// bridge by UNITY_OPEN_MCP_EXT_TILEMAP (com.unity.2d.tilemap), with an inner
// UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS guard around create_rule_tile's body —
// when tilemap.extras is absent, the tool compiles in but returns a clear
// tilemap_extras_required install error (two defines, two guards). All five
// members (create / set_tile / box_fill / create_tile_asset /
// create_rule_tile) are mutating and run the full gate path.
import { tilemapCreate } from "./tilemap-create.js";
import { tilemapSetTile } from "./tilemap-set-tile.js";
import { tilemapBoxFill } from "./tilemap-box-fill.js";
import { tilemapCreateTileAsset } from "./tilemap-create-tile-asset.js";
import { tilemapCreateRuleTile } from "./tilemap-create-rule-tile.js";
// M20 Plan 7 / T20.7.1 — Shader Graph extension tools. Compile-gated in the
// bridge (UNITY_OPEN_MCP_EXT_SHADERGRAPH on com.unity.shadergraph) AND
// auto-activating — the first domain to ship with package-detection auto-
// activation (M20 Plan 7 / T20.7.0): the `shadergraph` group activates for
// the session automatically when com.unity.shadergraph is installed, no
// manual manage_tools call. Three mutating members (create / node_add /
// node_connect) run the full gate path with paths_hint scoped to the
// .shadergraph asset path; open is a read-only window bring-up (Gate = Off)
// that returns a structured node/edge summary. The editing API is wrapped
// behind a reflection helper (ShaderGraphApi) — when the installed package
// version exposes a different surface, mutating tools return a structured
// shadergraph_api_unavailable error instead of throwing. Complementary to
// the inspect surface (shader_get_data / shader_list_all).
import { shaderGraphCreate } from "./shader-graph-create.js";
import { shaderGraphOpen } from "./shader-graph-open.js";
import { shaderGraphNodeAdd } from "./shader-graph-node-add.js";
import { shaderGraphNodeConnect } from "./shader-graph-node-connect.js";
// M20 Plan 7 / T20.7.2 — VFX Graph extension tools. Compile-gated in the
// bridge (UNITY_OPEN_MCP_EXT_VFX on com.unity.visualeffectgraph) AND auto-
// activating — the second domain under the package-detection auto-activation
// model (M20 Plan 7 / T20.7.0): the `vfx` group activates for the session
// automatically when com.unity.visualeffectgraph is installed, no manual
// manage_tools call. list / open are read-only (Gate = Off) returning a
// structured context/block/property summary; block_edit is the lone mutating
// member (Gate = Enforce, paths_hint = .vfx asset path). VFX Graph's editor
// graph model is internal/unstable — the read paths work over the public
// runtime VisualEffectAsset type and the serialized file format (version-
// stable); block_edit reflects over the editor graph and requires the VFX
// Graph window to be open, degrading to a structured
// vfx_block_edit_requires_editor_window error otherwise.
import { vfxList } from "./vfx-list.js";
import { vfxOpen } from "./vfx-open.js";
import { vfxBlockEdit } from "./vfx-block-edit.js";
// M20 Plan 7 / T20.7.3 — Memory Profiler snapshot capture. Compile-gated in
// the bridge (UNITY_OPEN_MCP_EXT_MEMORYPROFILER on com.unity.memoryprofiler) +
// auto-activating — the third domain under the package-detection auto-activation
// model (M20 Plan 7 / T20.7.0): the `memoryprofiler` group activates for the
// session automatically when com.unity.memoryprofiler is installed. Sense-
// prefixed (unity_senses_*) because it pairs with the existing senses profiler
// family rather than the typed-editor surface. Read-only re: game/project state
// but produces a .snap file — Gate = Off, ReadOnlyHint = true, Lifecycle =
// EditorSettle. The capture is callback-based; the bridge blocks until the
// callback fires. Pairs with profiler_get_script_stats / profiler_capture_frame
// for a fuller performance picture than a standalone memory tool.
import { memorySnapshotCapture } from "./memory-snapshot-capture.js";
// M20 Plan 9 / T20.9.1 — 2D art pipeline: SpriteAtlas + Texture import domain
// tools. Built-in 2D module (SpriteAtlas / SpriteAtlasAsset /
// SpriteAtlasPackingSettings / SpriteAtlasTextureSettings in UnityEngine.U2D /
// UnityEditor.U2D + TextureImporter in UnityEditor) — ungated in the bridge
// (no UNITY_OPEN_MCP_EXT_2D define), always compiled. The `2d` group is hidden
// from ListTools until the session activates it via manage_tools. Two prefixes
// share one group: spriteatlas_* (create/get/add_packable/remove_packable/
// modify/delete/list) and texture_* (get_importer/set_import/reimport/get).
// Mutating members (spriteatlas_create/add_packable/remove_packable/modify/
// delete + texture_set_import/reimport) run the full gate path with
// EditorSettle (the .spriteatlas asset is written/reimported; texture
// reimports can take seconds and may trigger a platform-switch domain reload)
// and paths_hint scoped to the asset path. Read-only members (spriteatlas_get/
// list + texture_get_importer/get) are gate-free. spriteatlas_set_import folds
// sprite + normal-map presets into one structured settings_json patch instead
// of separate set_sprite / set_normalmap tools (cleaner, fewer IDs).
import { spriteatlasCreate } from "./spriteatlas-create.js";
import { spriteatlasGet } from "./spriteatlas-get.js";
import { spriteatlasAddPackable } from "./spriteatlas-add-packable.js";
import { spriteatlasRemovePackable } from "./spriteatlas-remove-packable.js";
import { spriteatlasModify } from "./spriteatlas-modify.js";
import { spriteatlasDelete } from "./spriteatlas-delete.js";
import { spriteatlasList } from "./spriteatlas-list.js";
import { textureGetImporter } from "./texture-get-importer.js";
import { textureSetImport } from "./texture-set-import.js";
import { textureReimport } from "./texture-reimport.js";
import { textureGet } from "./texture-get.js";
import { hubListEditors } from "./hub-list-editors.js";
import { hubAvailableReleases } from "./hub-available-releases.js";
import { hubInstallEditor } from "./hub-install-editor.js";
import { hubInstallModules } from "./hub-install-modules.js";
import { hubGetInstallPath } from "./hub-get-install-path.js";
import { hubSetInstallPath } from "./hub-set-install-path.js";
// M27 Plan 4 — live `batch_execute` meta-tool. Lives in the `core` group
// (always visible) but routes live-only (NOT headless batchCapable — it is not
// in BATCH_TOOL_NAMES). One HTTP round trip runs many typed tools sequentially
// inside the already-open Editor, wrapped in a single batch-level gate cycle.
import { batchExecute } from "./batch-execute.js";

// M27 Plan 4 — batch_execute ships as a core meta-tool so agents can always
// discover it. It is the agent ergonomics counterpart to the M26 headless batch
// spawn axis (different concern: live sequential invoke vs headless fallback).
export const M27_PLAN4_TOOLS: Tool[] = [batchExecute];

export const M2_TOOLS: Tool[] = [
  ping,
  executeCsharp,
  invokeMethod,
  executeMenu,
  findMembers,
  compileCheck,
];

export const M2_5_TOOLS: Tool[] = [editorStatus];

export const M3_TOOLS: Tool[] = [validateEdit, checkpointCreate, delta, findReferences, dependencies, scanPaths, applyFix];

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

// M18 Plan 2 / T18.2.2 — manage_tools meta-tool. Server-only, local-routed,
// and always visible regardless of which groups the current session has
// activated (see ALWAYS_VISIBLE_TOOLS in tool-session-state.ts).
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
  // M20 Plan 9 / T20.9.4 — SceneView camera pose-level tools.
  sceneviewGetCamera,
  sceneviewSetCamera,
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
  reimportPackage,
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
  // M20 Plan 9 / T20.9.4 — undo stack read/reset tools.
  editorUndoHistory,
  editorClearHistory,
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
  // M20 Plan 9 / T20.9.2 — KV preferences. PlayerPrefs + EditorPrefs share the
  // build-settings group (project configuration surface). set / delete are
  // mutating but gate-free (registry writes — no asset scope).
  playerprefsGet,
  playerprefsSet,
  playerprefsDelete,
  editorprefsGet,
  editorprefsSet,
  editorprefsDelete,
  // M20 Plan 9 / T20.9.3 — Project Settings remainder. set_time /
  // set_quality_level run the gate (ProjectSettings asset writes);
  // get_time / get_render_pipeline are gate-free.
  settingsGetTime,
  settingsSetTime,
  settingsGetRenderPipeline,
  settingsSetQualityLevel,
];

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tools. Extension pack
// tools ship their tool definitions in the core MCP server (so capabilities
// advertises the surface) but require the matching extension UPM package
// installed in the target project for the bridge-side handler to exist.
// Mutating members (surface_add / set_bake_settings / surface_bake /
// modifier_add / modifier_volume_add / link_add / agent_add /
// agent_set_destination / modify) run the full gate path with paths_hint
// scoped to the host scene path; surface_bake is the heavy op (EditorSettle).
// Read-only members (list / get) are gate-free.
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
// gate-free.
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
// SceneView mouse picking.
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
// modify uses a per-module field-patch surface (module discriminator +
// fields_json) instead of an opaque reflective SerializedMember payload —
// agents get up-front field-name validation and a structured unknownFields
// report instead of guessing.
export const M16_PLAN10_PARTICLESYSTEM_TOOLS: Tool[] = [
  particleSystemGet,
  particleSystemModify,
];

// M16 Plan 10 / T6.6.10 — Animation extension tools (AnimationClip + Animator
// Controller). Mutating members (animation_create / animation_modify /
// animator_create / animator_modify) run the full gate path with paths_hint
// scoped to the asset path (.anim or .controller); the two modify tools are
// DESTRUCTIVE — some modification types are irreversible without undo. Read-
// only members (animation_get_data / animator_get_data) are gate-free. modify
// accepts a JSON array of modification entries dispatched by `type`; per-entry
// errors are accumulated, not thrown.
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
// skybox_get) are gate-free.
export const M20_PLAN2_LIGHTING_TOOLS: Tool[] = [
  lightAdd,
  lightSet,
  lightModify,
  reflectionProbeBake,
  reflectionProbeGet,
  skyboxSet,
  skyboxGet,
];

// M20 Plan 3 / T20.3.1 — Audio domain tools. Built-in audio module — ungated
// in the bridge (always compiled). Three mutating members (audio_source_add /
// audio_source_modify / audio_mixer_set_parameter) run the full gate path with
// paths_hint scoped to the host scene path (audio_source_*) or the mixer asset
// path (audio_mixer_set_parameter). Two read-only members (audio_listener_get /
// audio_mixer_get_parameter) are gate-free.
export const M20_PLAN3_AUDIO_TOOLS: Tool[] = [
  audioSourceAdd,
  audioSourceModify,
  audioMixerSetParameter,
  audioListenerGet,
  audioMixerGetParameter,
];

// M20 Plan 3 / T20.3.2 — UI (uGUI) domain tools. Built-in UI module — ungated
// in the bridge (always compiled). Four mutating members (ui_canvas_add /
// ui_element_add / ui_layout_group_add / ui_element_modify) run the full gate
// path with paths_hint scoped to the host / new-root / parent scene path.
// TextMesh Pro (TMP_Text) is optional — when absent, ui_element_add returns
// `tmp_package_required` (no silent legacy-Text fallback). The Canvas-companion
// ensure (CanvasScaler + GraphicRaycaster + EventSystem) is the documented
// advantage — a canvas-only tool creates the Canvas alone.
export const M20_PLAN3_UI_TOOLS: Tool[] = [
  uiCanvasAdd,
  uiElementAdd,
  uiLayoutGroupAdd,
  uiElementModify,
];

// M20 Plan 3 / T20.3.3 — Constraints & LOD domain tools. Built-in engine
// modules — ungated in the bridge (always compiled). Three mutating members
// (constraint_add / lod_group_configure / lod_add_level) run the full gate
// path with paths_hint scoped to the host scene path. constraint_add seeds an
// optional source Transform + weight + activation; lod_group_configure
// allocates the LOD array; lod_add_level wires renderers per level. One
// `constraints` group covers both concerns because they are small and closely
// related.
export const M20_PLAN3_CONSTRAINTS_TOOLS: Tool[] = [
  constraintAdd,
  lodGroupConfigure,
  lodAddLevel,
];

// M20 Plan 4 / T20.4 — Terrain domain tools. Built-in Terrain module —
// ungated in the bridge (always compiled). Five mutating members
// (terrain_create / terrain_set_heights / terrain_paint_layer /
// terrain_place_trees / terrain_set_neighbors) run the full gate path with
// paths_hint scoped to the host scene path (+ asset paths when
// terrain_create / terrain_paint_layer write assets). terrain_create allocates
// TerrainData + a Terrain GameObject; terrain_set_heights writes a heightmap
// region (cap 513x513 per call); terrain_paint_layer paints a splat layer
// (seeding a new TerrainLayer when the index is new); terrain_place_trees
// places tree instances (seeding a prototype from a prefab when the index is
// new); terrain_set_neighbors stitches neighbor terrains for LOD. Absorbs
// backlog T6.6.7.
export const M20_PLAN4_TERRAIN_TOOLS: Tool[] = [
  terrainCreate,
  terrainSetHeights,
  terrainPaintLayer,
  terrainPlaceTrees,
  terrainSetNeighbors,
];

// M20 Plan 5 / T20.5 — typed ScriptableObject + Assembly Definition tools. Two
// core (always-on, no Unity package dependency) tool sets for ScriptableObject
// CRUD (§B7) and Assembly Definitions (§B8). scriptableobject_create is the
// gate-integrated create path (the read/info surface stays on object_get_data /
// object_modify); it applies optional initial field patches reusing
// object_modify's value vocabulary. list_assets_of_type is a read-only typed
// list-by-type (offline-routeable in principle). The asmdef family parses
// .asmdef as JSON (hand-rolled reader/writer — no Newtonsoft, per the bridge
// AGENTS.md): list/get are read-only (offline-routeable); create/modify use
// the RestartThenSettle lifecycle so the gate waits for the recompile to
// settle (the advantage over an ungated asmdef mutator).
export const M20_PLAN5_TOOLS: Tool[] = [
  scriptableObjectCreate,
  listAssetsOfType,
  asmdefList,
  asmdefGet,
  asmdefCreate,
  asmdefModify,
];

// M20 Plan 6 / T20.6.1 — Cinemachine extension tools. The only reflection-
// gated domain pack in the bridge (the assembly always compiles; Cinemachine
// 3.x presence is detected at call time). Six mutating members
// (create_camera / set_targets / set_lens / set_body / set_noise /
// brain_ensure) run the full gate path with paths_hint scoped to the host
// scene path (create_camera adds a new GameObject to the active scene). One
// read-only member (camera_list) is gate-free. The canonical reflection case
// named in M18 Plan 1 T18.1.1 task 5 (version-split API trigger).
export const M20_PLAN6_CINEMACHINE_TOOLS: Tool[] = [
  cinemachineCreateCamera,
  cinemachineSetTargets,
  cinemachineSetLens,
  cinemachineSetBody,
  cinemachineSetNoise,
  cinemachineBrainEnsure,
  cinemachineCameraList,
];

// M20 Plan 6 / T20.6.2 — Timeline extension tools. Compile-gated in the
// bridge (UNITY_OPEN_MCP_EXT_TIMELINE on com.unity.timeline). All five
// members (create / track_add / clip_add / director_bind / modify) are
// mutating and run the full gate path. create produces a .playable asset;
// director_bind mutates a scene PlayableDirector; modify is a reflective
// field-patch escape hatch.
export const M20_PLAN6_TIMELINE_TOOLS: Tool[] = [
  timelineCreate,
  timelineTrackAdd,
  timelineClipAdd,
  timelineDirectorBind,
  timelineModify,
];

// M20 Plan 6 / T20.6.3 — Tilemap extension tools. Compile-gated in the
// bridge by UNITY_OPEN_MCP_EXT_TILEMAP (com.unity.2d.tilemap), with an inner
// UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS guard around create_rule_tile's body
// (when tilemap.extras is absent, the tool compiles in but returns a clear
// tilemap_extras_required install error — two defines, two guards). All five
// members (create / set_tile / box_fill / create_tile_asset /
// create_rule_tile) are mutating and run the full gate path.
export const M20_PLAN6_TILEMAP_TOOLS: Tool[] = [
  tilemapCreate,
  tilemapSetTile,
  tilemapBoxFill,
  tilemapCreateTileAsset,
  tilemapCreateRuleTile,
];

// M20 Plan 7 / T20.7.1 — Shader Graph extension tools. Compile-gated in the
// bridge (UNITY_OPEN_MCP_EXT_SHADERGRAPH on com.unity.shadergraph) AND auto-
// activating — the first domain under the package-detection auto-activation
// model (M20 Plan 7 / T20.7.0). Three mutating members (create / node_add /
// node_connect) run the full gate path with paths_hint scoped to the
// .shadergraph asset path; open is a read-only window bring-up (Gate = Off)
// returning a structured node/edge summary. The editing API is wrapped behind
// a reflection helper; when the installed package version exposes a different
// surface, mutating tools return a structured shadergraph_api_unavailable
// error instead of throwing. Complementary to shader_get_data / shader_list_all
// (which read compiled shader properties, not the graph).
export const M20_PLAN7_SHADERGRAPH_TOOLS: Tool[] = [
  shaderGraphCreate,
  shaderGraphOpen,
  shaderGraphNodeAdd,
  shaderGraphNodeConnect,
];

// M20 Plan 7 / T20.7.2 — VFX Graph extension tools. Compile-gated in the
// bridge (UNITY_OPEN_MCP_EXT_VFX on com.unity.visualeffectgraph) AND auto-
// activating — the second domain under the package-detection auto-activation
// model (M20 Plan 7 / T20.7.0). list / open are read-only (Gate = Off) returning
// a structured context/block/property summary; block_edit is the lone mutating
// member (Gate = Enforce, paths_hint = .vfx asset path). VFX Graph's editor
// graph model is internal/unstable — list/open work over the public runtime
// VisualEffectAsset type (version-stable); block_edit reflects over the editor
// graph and requires the VFX Graph window to be open, degrading to a structured
// vfx_block_edit_requires_editor_window error otherwise.
export const M20_PLAN7_VFX_TOOLS: Tool[] = [
  vfxList,
  vfxOpen,
  vfxBlockEdit,
];

// M20 Plan 7 / T20.7.3 — Memory Profiler snapshot capture. Compile-gated in
// the bridge (UNITY_OPEN_MCP_EXT_MEMORYPROFILER on com.unity.memoryprofiler) +
// auto-activating — the third domain under the package-detection auto-activation
// model (M20 Plan 7 / T20.7.0). Sense-prefixed (unity_senses_*) because it
// pairs with the existing senses profiler family rather than the typed-editor
// surface. Read-only re: game/project state but produces a .snap file —
// Gate = Off, ReadOnlyHint = true, Lifecycle = EditorSettle. The capture is
// callback-based (async); the bridge reflects over whichever capture surface
// the installed version exposes (Unity.Profiling.Memory.MemoryProfiler or
// UnityEditor.MemoryProfiler) and blocks until the callback fires, so the tool
// returns a definitive path/result. Pairs with profiler_get_script_stats /
// profiler_capture_frame for a fuller performance picture than a standalone
// memory tool.
export const M20_PLAN7_MEMORYPROFILER_TOOLS: Tool[] = [
  memorySnapshotCapture,
];

// M20 Plan 9 / T20.9.1 — 2D art pipeline (SpriteAtlas + Texture import) domain
// tools. Built-in 2D module — ungated in the bridge (always compiled). Seven
// spriteatlas_* members + four texture_* members share one `2d` group. Five
// mutating spriteatlas members (create / add_packable / remove_packable /
// modify / delete) run the full gate path with EditorSettle and paths_hint
// scoped to the .spriteatlas asset path; spriteatlas_delete is destructive.
// Two mutating texture members (set_import / reimport) run the full gate path
// with EditorSettle (reimport can take seconds / trigger a platform-switch
// domain reload); set_import folds sprite + normal-map presets into one
// structured settings_json patch. Two read-only spriteatlas members (get /
// list) and two read-only texture members (get_importer / get) are gate-free.
export const M20_PLAN9_2D_TOOLS: Tool[] = [
  spriteatlasCreate,
  spriteatlasGet,
  spriteatlasAddPackable,
  spriteatlasRemovePackable,
  spriteatlasModify,
  spriteatlasDelete,
  spriteatlasList,
  textureGetImporter,
  textureSetImport,
  textureReimport,
  textureGet,
];

// M26 Plan 2 — Unity Hub control tools. Local-routed (resolved inside the MCP
// server: filesystem discovery + Unity archive feed + unityhub:// deep link +
// Hub CLI). Never hit the Unity bridge or spawn Unity. Read-only members
// (list_editors, available_releases, get_install_path) are gate-free; mutating
// members (install_editor, install_modules, set_install_path) are system-level
// ops where paths_hint is N/A, so they are gate-free too (the gate validates
// project-asset fallout, which does not apply). Live in the `unity-hub-control`
// group (activate via manage_tools); not always-visible meta-tools.
export const M26_PLAN2_HUB_TOOLS: Tool[] = [
  hubListEditors,
  hubAvailableReleases,
  hubInstallEditor,
  hubInstallModules,
  hubGetInstallPath,
  hubSetInstallPath,
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
  ...M20_PLAN3_AUDIO_TOOLS,
  ...M20_PLAN3_UI_TOOLS,
  ...M20_PLAN3_CONSTRAINTS_TOOLS,
  ...M20_PLAN4_TERRAIN_TOOLS,
  ...M20_PLAN5_TOOLS,
  ...M20_PLAN6_CINEMACHINE_TOOLS,
  ...M20_PLAN6_TIMELINE_TOOLS,
  ...M20_PLAN6_TILEMAP_TOOLS,
  ...M20_PLAN7_SHADERGRAPH_TOOLS,
  ...M20_PLAN7_VFX_TOOLS,
  ...M20_PLAN7_MEMORYPROFILER_TOOLS,
  ...M20_PLAN9_2D_TOOLS,
  ...M26_PLAN2_HUB_TOOLS,
  ...M27_PLAN4_TOOLS,
];
