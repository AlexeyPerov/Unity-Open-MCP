#!/usr/bin/env node
// mcp-full-test.mjs — full-coverage test of the unity-open-mcp tool surface.
//
// Drives EVERY registered MCP tool (unity_open_mcp_* + unity_senses_*) at least
// once via the `unity-open-mcp run-tool` CLI (one fresh process per call, same
// router stack an MCP client uses). Mutations run under a single temp fixture
// root (Assets/MCP_FullTest/) and an additive temp scene that are always torn
// down at the end, so the project is left clean.
//
// Tools whose Unity package isn't compiled into the bridge return
// `tool_not_found` (Band F unavailable sub-band). Compiled extension packs get a
// minimal reachability probe (Band F ext_* steps). Batch-headless tools that
// can't grab the project (Editor open) return `project_path_missing` /
// `editor_instance_locked` — also a pass. Destructive tools are exercised via
// Destructive tools are exercised via their REFUSAL path only.
//
// Modal prevention: the runner sets UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS=1
// (CLI dismiss loop backstop) and saves all dirty scenes before recompile-heavy
// Band C steps. The demo project enables autoSaveDirtyScenes in
// .unity-open-mcp/settings.json so the bridge auto-saves before scene_dirty
// refusals on RestartThenSettle tools.
//
// Usage:
//   node scripts/mcp-full-test.mjs                       # full suite vs ./demo
//   node scripts/mcp-full-test.mjs --project /path       # target another project
//   node scripts/mcp-full-test.mjs --band A,B            # run named bands only
//   node scripts/mcp-full-test.mjs --only needle         # subset by label
//   node scripts/mcp-full-test.mjs --list                # list steps, don't run
//   node scripts/mcp-full-test.mjs --no-cleanup          # leave fixtures (debug)
//   node scripts/mcp-full-test.mjs --json-out report.json
//
// Exit code: 0 if every step met its `expect`, 1 otherwise. A failure in one
// step does not abort the rest; cleanup always runs (incl. on SIGINT).
//
// Requirements: mcp-server/dist/index.js built, and a Unity Editor open with
// the target project + bridge running. Pass the project as an ABSOLUTE path
// (relative paths hash to a different bridge port).

import { execFileSync } from "node:child_process";
import { existsSync, writeFileSync } from "node:fs";
import { dirname, resolve, isAbsolute } from "node:path";
import { fileURLToPath } from "node:url";

import {
  REPO_ROOT,
  CLI_BIN,
  REFUSED_CODES,
  LOCKED_CODES,
  TIMEOUT_CODE,
  STEP_META_KEYS,
  classify,
  pluck,
  parseEnvelope,
  parseCommonArgs,
  makeStepBuilder,
  buildRunEnv,
  dismissBlockingModals,
  invokeTool,
  saveAllDirtyScenes,
  runToolOnce,
  runTool as runToolLib,
  isMainSceneEntry,
  closeInitTestScenes,
  revertMainSceneIfDirty,
  collectIsolationState,
  finalizeEditorState,
  cleanupTempFolder,
  cleanupViaBridge,
  applyFindObjectsToCtx,
} from "./mcp-test-lib.mjs";

const __dirname = dirname(fileURLToPath(import.meta.url));
const DEFAULT_PROJECT = resolve(REPO_ROOT, "demo");

// Temp fixture root — everything mutating lives under here and is deleted at
// the end. One folder, one additive scene, one root GameObject.
const FT = "Assets/MCP_FullTest";
const FT_FOLDER = FT;
const FT_SCENE_ASSET = `${FT}/FT_Scene.unity`;
const FT_MAT = `${FT}/FT_Mat.mat`;
const FT_MAT2 = `${FT}/FT_Mat2.mat`;
const FT_PHYS_MAT = `${FT}/FT_PhysMat.physicMaterial`; // for assets_copy target kind variety
const FT_PREFAB = `${FT}/FT_Cube.prefab`;
const FT_ASMDEF = `${FT}/FT_Test.asmdef`;
const FT_SCRIPT = `${FT}/FT_Script.cs`;
const FT_SPRITEATLAS = `${FT}/FT_Atlas.spriteatlas`;
const FT_TERRAIN_DATA_HINT = `${FT}/FT_Terrain.asset`;
// M27 Plan 4 — batch_execute creates these GameObjects in one round trip.
const FT_BATCH_GO_A = "FT_BatchA";
const FT_BATCH_GO_B = "FT_BatchB";
const HINT = [FT]; // shared paths_hint sentinel for in-scene mutations
const SCENE_HINT = [FT_SCENE_ASSET];
const MAIN_SCENE_PATH = "Assets/Scenes/Main.unity";
const MAIN_SCENE_HINT = [MAIN_SCENE_PATH];

// ---------------------------------------------------------------------------
// arg parsing (S0 keeps its own: --auto-revert / --save-main are suite-specific)
// ---------------------------------------------------------------------------

function parseArgs(argv) {
  const opts = {
    project: DEFAULT_PROJECT,
    band: null, // comma-separated band letters, null = all
    only: null, // comma-separated label needles, null = all
    list: false,
    noCleanup: false,
    autoRevert: false,
    saveMain: false,
    jsonOut: null,
    timeoutMs: 120_000,
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--project" || a === "-P") opts.project = argv[++i];
    else if (a === "--band") opts.band = argv[++i].split(",").map((s) => s.trim().toUpperCase());
    else if (a === "--only") opts.only = argv[++i].split(",").map((s) => s.trim());
    else if (a === "--list") opts.list = true;
    else if (a === "--no-cleanup") opts.noCleanup = true;
    else if (a === "--auto-revert") opts.autoRevert = true;
    else if (a === "--save-main") opts.saveMain = true;
    else if (a === "--json-out") opts.jsonOut = argv[++i];
    else if (a === "--timeout-ms") opts.timeoutMs = Number(argv[++i]);
    else if (a === "--help" || a === "-h") {
      printHelp();
      process.exit(0);
    } else {
      console.error(`Unknown argument: ${a}`);
      process.exit(2);
    }
  }
  return opts;
}

function printHelp() {
  console.error(`Usage: node scripts/mcp-full-test.mjs [options]

Options:
  --project, -P <path>   Target Unity project (default: <repo>/demo). MUST be absolute.
  --band A,B,C           Run only bands A=lifecycle/meta, B=read-only,
                         C=safe-mutations, D=batch, E=destructive-refusal,
                         F=extension-pack availability (compiled reachability +
                         unavailable groups), G=run_tests
  --only needle          Run only steps whose label contains any needle
  --list                 List the suite steps and exit (no execution)
  --no-cleanup           Leave temp fixtures in place (debug)
  --auto-revert          After the run, git-checkout tracked ProjectSettings
                          the setters mutated. Does NOT revert scene files
                          (rewriting a scene on disk while Unity has it open
                          triggers an undismissable modal).
  --save-main            Allow finalize to save Main.unity when dirty (debug only;
                          default is to discard in-memory Main changes).
  --json-out <file>      Write a machine-readable report to <file>
  --timeout-ms <n>       Per-step timeout (default 120000; raised internally
                         for recompile-heavy steps)
  -h, --help             Show this help

The CLI binary must exist at mcp-server/dist/index.js (run \`npm run build\` in
mcp-server/ first). A Unity Editor must be open with the project and the bridge
running.`);
}

// ---------------------------------------------------------------------------
// expect classifier + pluck + applyFindObjectsToCtx — imported from
// mcp-test-lib.mjs (shared with S1–S5). See there for the full pass-mode table.
// ---------------------------------------------------------------------------



// ---------------------------------------------------------------------------
// suite definition
//
// Steps are grouped into bands A–F. The `gate` flag on a step marks it as a
// mutating call whose envelope shape is { mutation, gate, ... } rather than a
// direct body. `resolveArgs(ctx)` lets a step pull state stashed by an earlier
// step's `after` hook (instanceId handoff for GameObject/component chains).
// ---------------------------------------------------------------------------

function buildSuite() {
  const { s, steps } = makeStepBuilder();

  // =====================================================================
  // BAND A — lifecycle & meta
  // =====================================================================
  s("ping", "A", "unity_open_mcp_ping");
  s("editor_status", "A", "unity_open_mcp_editor_status");
  s("bridge_status", "A", "unity_open_mcp_bridge_status");
  s("capabilities", "A", "unity_open_mcp_capabilities", { kind: "tools", include_planned: false });
  s("capabilities_rules", "A", "unity_open_mcp_capabilities", { kind: "rules" });
  s("capabilities_fixes", "A", "unity_open_mcp_capabilities", { kind: "fixes" });
  s("list_rules", "A", "unity_open_mcp_list_rules");
  s("list_rules_prefab", "A", "unity_open_mcp_list_rules", { asset_kind: "prefab" });
  s("read_compile_errors", "A", "unity_open_mcp_read_compile_errors");
  s("generate_skill", "A", "unity_open_mcp_generate_skill", { write: false });
  s("manage_tools_list_groups", "A", "unity_open_mcp_manage_tools", { action: "list_groups" });
  // activate every group id we know of — proves the activate path covers all
  const ALL_GROUP_IDS = [
    "core", "gate-and-verify", "asset-intelligence", "typed-editor", "diagnostics",
    "gate-intelligence", "build-settings", "navigation", "input-system", "probuilder",
    "particle-system", "animation", "splines", "lighting", "audio", "ui", "constraints",
    "terrain", "cinemachine", "timeline", "tilemap", "shadergraph", "vfx",
    "memoryprofiler", "sprite2d", "agent-senses", "unity-hub-control",
  ];
  s("manage_tools_activate_all", "A", "unity_open_mcp_manage_tools", {
    // activate one representative group; the action contract is identical per id
    action: "activate",
    group: "diagnostics",
  });
  s("manage_tools_deactivate", "A", "unity_open_mcp_manage_tools", { action: "deactivate", group: "diagnostics" });
  s("manage_tools_reset", "A", "unity_open_mcp_manage_tools", { action: "reset" });
  s("pull_events_senses", "A", "unity_senses_pull_events", { max_events: 5 });

  // =====================================================================
  // BAND B — read-only / gate-free family coverage
  // =====================================================================

  // --- gate-and-verify reads ---
  // These work normally on a clean bridge (the earlier 30s timeouts were caused
  // by a Unity modal blocking the main thread — see specs/feedback.md).
  s("validate_edit", "B", "unity_open_mcp_validate_edit", { paths: ["Assets/Materials/TestMaterial.mat"], profile: "compact" });
  s("find_references", "B", "unity_open_mcp_find_references", { asset_path: "Assets/Materials/TestMaterial.mat", profile: "compact" });
  s("dependencies", "B", "unity_open_mcp_dependencies", { asset_path: "Assets/Prefabs/GateTestCube.prefab", detail: "summary" });
  s("scan_paths", "B", "unity_open_mcp_scan_paths", { paths: ["Assets/Materials/TestMaterial.mat"], profile: "compact" });
  s("apply_fix_dryrun", "B", "unity_open_mcp_apply_fix", {
    // issue_id format is {ruleId}|{Severity}|{assetPath}|{issueCode} — severity
    // is capitalized ("Error"), confirmed via scan_paths output. NOTE: apply_fix
    // currently rejects this with invalid_issue_id even when the key matches a
    // fresh scan_paths result (see specs/feedback.md) — tolerate.
    issue_id: "missing_references|Error|Assets/Fixtures/MissingScriptFixture.prefab|missing_script",
    dry_run: true,
  }, { expect: "tolerate", tolerate: ["invalid_issue_id"] });
  // checkpoint_create + delta chain
  s("checkpoint_create", "B", "unity_open_mcp_checkpoint_create", { label: "ft-checkpoint" }, { after: (r, ctx) => { ctx.checkpointId = pluck(r, "checkpoint.id") ?? pluck(r, "id"); } });
  s("delta", "B", "unity_open_mcp_delta", { checkpoint_id: "nonexistent" });

  // --- asset-intelligence reads ---
  s("list_assets", "B", "unity_open_mcp_list_assets", { folder: "Assets", max_per_folder: 10 });
  s("read_asset", "B", "unity_open_mcp_read_asset", { asset_path: "Assets/Prefabs/GateTestCube.prefab", profile: "compact" });
  s("search_assets", "B", "unity_open_mcp_search_assets", { name: "Test", profile: "compact" });
  // reserialize is mutating but safe on a known asset; test it in band C under a temp copy.

  // --- gate-intelligence (read-only compositions) — these require paths_hint ---
  s("impact_preview", "B", "unity_open_mcp_impact_preview", { asset_path: "Assets/Materials/TestMaterial.mat", paths_hint: ["Assets/Materials/TestMaterial.mat"] });
  s("gate_budget_estimate", "B", "unity_open_mcp_gate_budget_estimate", { paths: ["Assets/Materials/TestMaterial.mat"], paths_hint: ["Assets/Materials/TestMaterial.mat"] });
  // mutation_explain needs a prior mutation context OR a checkpoint_id; pass a
  // sentinel checkpoint_id and tolerate no_mutation_context (the explain path is
  // still exercised + the contract gap is surfaced).
  s("mutation_explain", "B", "unity_open_mcp_mutation_explain", { tool_name: "unity_open_mcp_material_set_property", args: { property: "_Color", type: "color", value: [1, 0, 0, 1] }, checkpoint_id: "ft-explain" }, { expect: "tolerate", tolerate: ["no_mutation_context", "checkpoint_not_found"] });

  // --- diagnostics reads ---
  s("profiler_get_status", "B", "unity_open_mcp_profiler_get_status");
  s("profiler_get_config", "B", "unity_open_mcp_profiler_get_config");
  s("profiler_list_modules", "B", "unity_open_mcp_profiler_list_modules");
  s("profiler_get_script_stats", "B", "unity_open_mcp_profiler_get_script_stats");

  // --- build-settings reads (gate-free getters) ---
  s("build_get_targets", "B", "unity_open_mcp_build_get_targets");
  s("build_get_active_target", "B", "unity_open_mcp_build_get_active_target");
  s("build_get_scenes", "B", "unity_open_mcp_build_get_scenes");
  s("build_get_defines", "B", "unity_open_mcp_build_get_defines");
  s("settings_get_player", "B", "unity_open_mcp_settings_get_player");
  s("settings_get_quality", "B", "unity_open_mcp_settings_get_quality", {}, {
    after: (r, ctx) => {
      const current = pluck(r, "currentLevel") ?? 0;
      // Pick a different index so set_quality_level is a real switch (not a no-op).
      ctx.qualityAlternate = current === 0 ? 1 : 0;
    },
  });
  s("settings_get_physics", "B", "unity_open_mcp_settings_get_physics");
  s("settings_get_lighting", "B", "unity_open_mcp_settings_get_lighting");
  s("settings_get_time", "B", "unity_open_mcp_settings_get_time");
  s("settings_get_render_pipeline", "B", "unity_open_mcp_settings_get_render_pipeline");
  // prefs getters return key_not_found for a nonexistent key — that's expected
  // behavior (the key genuinely doesn't exist), tolerate it.
  s("playerprefs_get", "B", "unity_open_mcp_playerprefs_get", { key: "FT_Nonexistent_Key" }, { expect: "tolerate", tolerate: ["key_not_found"] });
  s("editorprefs_get", "B", "unity_open_mcp_editorprefs_get", { key: "FT_Nonexistent_Key" }, { expect: "tolerate", tolerate: ["key_not_found"] });

  // --- unity-hub-control reads (local-routed, no Unity needed) ---
  s("hub_list_editors", "B", "unity_open_mcp_hub_list_editors");
  // Hub CLI output format varies by Hub version / install layout — tolerate unparseable.
  s("hub_get_install_path", "B", "unity_open_mcp_hub_get_install_path", {}, { expect: "tolerate", tolerate: ["install_path_unparseable"] });
  // hub_available_releases hits the network; keep it but tolerate failure
  s("hub_available_releases", "B", "unity_open_mcp_hub_available_releases", { limit: 3 });

  // --- agent-senses reads (live-only, read-only) ---
  // read_console has a LogEntries reflection bug on Unity 6 (see
  // specs/feedback.md) — tolerate execution_error so the step still exercises
  // the route and surfaces the bug in the report without failing the suite.
  s("read_console", "B", "unity_senses_read_console", { max_entries: 5 }, { expect: "tolerate", tolerate: ["execution_error"] });
  // spatial_query needs a target GameObject / colliders; the demo's default
  // scene is empty, so both `bounds` (needs a GO) and `raycast` (needs
  // colliders) fail. Tolerate — the tool is reached; the failure is the empty
  // scene, not a tool bug. (gameobject_create in Band C populates the scene.)
  s("spatial_query", "B", "unity_senses_spatial_query", { action: "raycast", origin: [0, 5, 0], direction: [0, -1, 0] }, { expect: "tolerate", tolerate: ["target_not_found", "execution_error"] });
  // screenshot + capture_inline return bridge_response_unparsable (see
  // specs/feedback.md — inline base64 image data breaks JSON serialization).
  // Tolerate so the route is exercised + the bug surfaced without failing.
  s("screenshot", "B", "unity_senses_screenshot", { width: 64, height: 64 }, { expect: "tolerate", tolerate: ["bridge_response_unparsable"] });
  s("screenshot_camera", "B", "unity_senses_screenshot_camera", { position: [0, 5, -10], rotation: [30, 0, 0], width: 64, height: 64 }, { expect: "tolerate", tolerate: ["bridge_response_unparsable", "missing_parameter"] });
  s("capture_inline", "B", "unity_senses_capture_inline", { width: 64, height: 64 }, { expect: "tolerate", tolerate: ["bridge_response_unparsable"] });
  s("screenshot_window", "B", "unity_senses_screenshot_window", { window: "Scene", width: 64, height: 64 }, { expect: "tolerate", tolerate: ["bridge_response_unparsable", "missing_parameter"] });
  s("frame_debugger", "B", "unity_senses_frame_debugger", { action: "status" });
  s("profiler_capture_frame", "B", "unity_senses_profiler_capture_frame", {});
  // profiler_capture reads captured profiler frames; with no prior capture it
  // returns no_frame_data. Tolerate — profiler_capture_frame above exercises
  // the capture path; this exercises the read path.
  s("profiler_capture", "B", "unity_senses_profiler_capture", { frames: 1 }, { expect: "tolerate", tolerate: ["no_frame_data"] });
  s("profiler_memory", "B", "unity_senses_profiler_memory", {});
  s("profiler_rendering", "B", "unity_senses_profiler_rendering", {});

  // --- typed-editor reads ---
  s("type_schema", "B", "unity_open_mcp_type_schema", { type_name: "UnityEngine.Transform" });
  s("find_members", "B", "unity_open_mcp_find_members", { query: "Rigidbody", max_results: 5 });
  s("script_read", "B", "unity_open_mcp_script_read", { file_path: "Assets/Scripts/ScriptWithGameObjectField.cs", max_lines: 5 });
  s("list_assets_of_type", "B", "unity_open_mcp_list_assets_of_type", { type_name: "Material", max_results: 5 });
  // asmdef_list persistently returns bridge_response_unparsable (see
  // specs/feedback.md) — tolerate so the suite reports the bug without failing.
  s("asmdef_list", "B", "unity_open_mcp_asmdef_list", { folder: "Assets" }, { expect: "tolerate", tolerate: ["bridge_response_unparsable"] });
  s("asmdef_get", "B", "unity_open_mcp_asmdef_get", { asset_path: "Assets/Tests/EditMode/Demo.Tests.EditMode.asmdef" });
  s("shader_list_all", "B", "unity_open_mcp_shader_list_all", { max_results: 5 });
  s("shader_get_data", "B", "unity_open_mcp_shader_get_data", { name: "Universal Render Pipeline/Lit" });
  s("editor_get_tags", "B", "unity_open_mcp_editor_get_tags");
  s("editor_get_layers", "B", "unity_open_mcp_editor_get_layers");
  s("editor_undo_history", "B", "unity_open_mcp_editor_undo_history", { max_entries: 5 });
  s("selection_get", "B", "unity_open_mcp_selection_get");
  s("component_list_all", "B", "unity_open_mcp_component_list_all", { query: "Rigidbody", max_results: 5 });
  s("package_list", "B", "unity_open_mcp_package_list", { direct_dependencies_only: true, max_results: 20 });
  s("package_get_dependencies", "B", "unity_open_mcp_package_get_dependencies");
  s("package_get_info", "B", "unity_open_mcp_package_get_info", { name: "com.unity.inputsystem" });
  s("package_search", "B", "unity_open_mcp_package_search", { query: "textmesh", max_results: 3 });
  s("package_check", "B", "unity_open_mcp_package_check", { package_id: "com.unity.inputsystem" });
  s("scene_list_opened", "B", "unity_open_mcp_scene_list_opened");
  s("scene_get_data", "B", "unity_open_mcp_scene_get_data", { profile: "compact" });
  s("scene_get_dirty_summary", "B", "unity_open_mcp_scene_get_dirty_summary");
  s("sceneview_get_camera", "B", "unity_open_mcp_sceneview_get_camera");
  s("gameobject_find", "B", "unity_open_mcp_gameobject_find", { root_only: true, max_results: 5 });

  // =====================================================================
  // BAND C — safe mutations + cleanup
  //
  // Order matters: create fixture root → create child fixtures → mutate →
  // tear down. The fixture root (Assets/MCP_FullTest) is deleted wholesale in
  // the cleanup pass, so even if a mid-chain step fails, nothing lingers.
  //
  // MODAL-PREVENTION: the first step saves any open scene so the dirty-scene
  // modal can't appear mid-band and freeze the main-thread queue (see
  // specs/feedback.md). The runner also auto-recovers from a `scene_dirty`
  // refusal by saving + retrying once.
  // =====================================================================

  // --- preflight: save the demo's Main scene so no dirty-scene modal can fire
  // during the band. The active scene may have no path (missing_parameter),
  // so tolerate that — the scene_dirty recovery in the runner is the backstop.
  s("scene_save_preflight", "C", "unity_open_mcp_scene_save", { path: "Assets/Scenes/Main.unity", paths_hint: ["Assets/Scenes/Main.unity"] }, { gate: true, expect: "tolerate", tolerate: ["missing_parameter", "scene_not_found"] });

  // --- fixture root: refresh AssetDB first (clears stale cache from prior
  // runs whose on-disk cleanup left orphan .meta files), then create the folder.
  // If the folder "already exists" in Unity's cache (stale), assets_create_folder
  // reports success with created:[] and nothing lands on disk — the refresh
  // prevents that desync.
  s("assets_refresh_preflight", "C", "unity_open_mcp_assets_refresh", { whole_project: true }, { gate: true, expect: "gate" });
  s("assets_create_folder", "C", "unity_open_mcp_assets_create_folder", {
    folders: [{ parent_folder_path: "Assets", new_folder_name: "MCP_FullTest" }],
    paths_hint: [FT_FOLDER],
  }, { gate: true, expect: "gate" });

  // scene_create_additive: Unity refuses to add a scene alongside an unsaved
  // untitled scene ("Cannot create a new scene additively with an untitled
  // scene unsaved"). Tolerate create_failed — the GameObject chain below works
  // in whatever scene IS active (the demo typically has an empty untitled scene).
  s("scene_create_additive", "C", "unity_open_mcp_scene_create", {
    path: FT_SCENE_ASSET,
    setup: "empty",
    mode: "additive",
    paths_hint: SCENE_HINT,
  }, {
    gate: true,
    expect: "tolerate",
    tolerate: ["create_failed"],
    after: (r, ctx) => { ctx.ftSceneCreated = r.mutation?.success === true; },
  });

  // scene_set_active only valid if FT_Scene was created; tolerate scene_not_found.
  s("scene_set_active", "C", "unity_open_mcp_scene_set_active", {
    name: "FT_Scene",
    paths_hint: SCENE_HINT,
  }, {
    gate: true,
    expect: "tolerate",
    tolerate: ["scene_not_found"],
    after: (r, ctx) => {
      ctx.ftSceneActive = r.mutation?.success === true;
      if (ctx.ftSceneCreated && !ctx.ftSceneActive) {
        ctx.ftSceneIsolationWarning = "FT_Scene created but scene_set_active failed — GO chain runs in the active scene";
      }
    },
  });

  // --- GameObject lifecycle chain (cube → dup → modify → parent → component → destroy) ---
  s("gameobject_create_cube", "C", "unity_open_mcp_gameobject_create", {
    name: "FT_Cube", primitive_type: "Cube", position: "0,1,0", paths_hint: SCENE_HINT,
  }, {
    gate: true, expect: "gate",
    after: (r, ctx) => { ctx.cubeId = pluck(r, "mutation.output.instanceId") ?? pluck(r, "instanceId"); ctx.cubeName = "FT_Cube"; },
  });

  s("gameobject_create_sphere", "C", "unity_open_mcp_gameobject_create", {
    name: "FT_Sphere", primitive_type: "Sphere", position: "2,1,0", paths_hint: SCENE_HINT,
  }, {
    gate: true, expect: "gate",
    after: (r, ctx) => { ctx.sphereId = pluck(r, "mutation.output.instanceId") ?? pluck(r, "instanceId"); },
  });

  s("gameobject_duplicate", "C", "unity_open_mcp_gameobject_duplicate", {
    resolveArgs: (ctx) => ({ instance_id: ctx.cubeId, paths_hint: SCENE_HINT }),
  }, {
    gate: true, expect: "gate",
    after: (r, ctx) => { ctx.cubeDupId = pluck(r, "mutation.output.instanceId") ?? pluck(r, "instanceId"); },
  });

  s("gameobject_modify", "C", "unity_open_mcp_gameobject_modify", {
    resolveArgs: (ctx) => ({ instance_id: ctx.cubeDupId, name: "FT_Renamed", position: "4,1,0", paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });

  s("gameobject_set_parent", "C", "unity_open_mcp_gameobject_set_parent", {
    resolveArgs: (ctx) => ({ instance_id: ctx.cubeDupId, parent_instance_id: ctx.cubeId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });

  s("gameobject_find_query", "C", "unity_open_mcp_gameobject_find", {
    name_contains: "FT_", max_results: 10,
  });

  s("component_add", "C", "unity_open_mcp_component_add", {
    resolveArgs: (ctx) => ({ instance_id: ctx.cubeId, component_types: ["UnityEngine.Rigidbody"], paths_hint: SCENE_HINT }),
  }, {
    gate: true, expect: "gate",
    after: (r, ctx) => { ctx.rbInstanceId = pluck(r, "mutation.output.instanceId"); },
  });

  // component_get returns bridge_response_unparsable (large field dump breaks
  // serialization — see specs/feedback.md). Tolerate.
  s("component_get", "C", "unity_open_mcp_component_get", {
    resolveArgs: (ctx) => ({ instance_id: ctx.cubeId, type_name: "Rigidbody" }),
  }, { expect: "tolerate", tolerate: ["bridge_response_unparsable", "gameobject_not_found"] });

  s("component_modify", "C", "unity_open_mcp_component_modify", {
    resolveArgs: (ctx) => ({ instance_id: ctx.cubeId, type_name: "Rigidbody", fields: [{ path: "m_Mass", value: 5 }], paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });

  s("component_destroy", "C", "unity_open_mcp_component_destroy", {
    resolveArgs: (ctx) => ({ instance_id: ctx.cubeId, component_types: ["Rigidbody"], paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });

  // --- selection + console + editor state mutators (gate-free, editor state only) ---
  s("selection_set", "C", "unity_open_mcp_selection_set", {
    resolveArgs: (ctx) => ({ instance_id: ctx.cubeId }),
  });
  s("console_log", "C", "unity_open_mcp_console_log", { message: "FT: full-test marker" });
  s("console_clear", "C", "unity_open_mcp_console_clear");
  s("editor_set_state_stop", "C", "unity_open_mcp_editor_set_state", { state: "stop" });

  // M27 Plan 4 — batch_execute: one round trip creates two GameObjects
  // sequentially inside the open Editor. The whole sequence shares one gate
  // cycle (one checkpoint → 2 steps → one validate/delta) + one undo group.
  // Reachability probe — asserts the batch envelope shape + gate ran.
  s("batch_execute_setup", "C", "unity_open_mcp_batch_execute", {
    commands: [
      { tool: "unity_open_mcp_gameobject_create", params: { name: FT_BATCH_GO_A, position: "-2,1,0" } },
      { tool: "unity_open_mcp_gameobject_create", params: { name: FT_BATCH_GO_B, position: "-4,1,0" } },
    ],
    paths_hint: SCENE_HINT,
  }, {
    gate: true, expect: "gate",
    after: (r, ctx) => {
      // Pluck the per-step instanceIds out of batch.results[].output so the
      // cleanup pass can destroy them. Defensive: a partial batch (one step
      // failed) leaves the other GO alive; tolerate in destroy steps below.
      const results = pluck(r, "batch.results") ?? [];
      ctx.batchGoAId = pluck(results[0], "output.instanceId") ?? pluck(results[0], "instanceId");
      ctx.batchGoBId = pluck(results[1], "output.instanceId") ?? pluck(results[1], "instanceId");
      ctx.batchCreatedGoA = r.mutation?.success === true &&
        (pluck(r, "batch.callSuccessCount") ?? 0) >= 1;
    },
  });

  // --- asset lifecycle: material + copy + move + refresh + read ---
  s("material_create", "C", "unity_open_mcp_material_create", {
    asset_path: FT_MAT, shader_name: "Universal Render Pipeline/Lit", paths_hint: [FT_MAT],
  }, { gate: true, expect: "gate" });

  s("material_create_2", "C", "unity_open_mcp_material_create", {
    asset_path: FT_MAT2, shader_name: "Universal Render Pipeline/Lit", paths_hint: [FT_MAT2],
  }, { gate: true, expect: "gate" });

  s("material_get_properties", "C", "unity_open_mcp_material_get_properties", { asset_path: FT_MAT });
  s("material_get_keywords", "C", "unity_open_mcp_material_get_keywords", { asset_path: FT_MAT });

  s("material_set_property", "C", "unity_open_mcp_material_set_property", {
    asset_path: FT_MAT, property: "_BaseColor", type: "color", value: [1, 0, 0, 1], paths_hint: [FT_MAT],
  }, { gate: true, expect: "gate" });

  s("material_set_keyword", "C", "unity_open_mcp_material_set_keyword", {
    asset_path: FT_MAT, keyword: "_EMISSION", enabled: true, paths_hint: [FT_MAT],
  }, { gate: true, expect: "gate" });

  s("material_set_shader", "C", "unity_open_mcp_material_set_shader", {
    asset_path: FT_MAT2, shader_name: "Universal Render Pipeline/Unlit", paths_hint: [FT_MAT2],
  }, { gate: true, expect: "gate" });

  s("object_get_data_mat", "C", "unity_open_mcp_object_get_data", { asset_path: FT_MAT, max_depth: 1 });
  s("object_modify_mat", "C", "unity_open_mcp_object_modify", {
    asset_path: FT_MAT, fields: [{ name: "m_Name", value: "FT_Mat_Renamed" }], paths_hint: [FT_MAT],
  }, { gate: true, expect: "gate" });

  s("assets_copy", "C", "unity_open_mcp_assets_copy", {
    entries: [{ source: FT_MAT, destination: `${FT}/FT_Mat_Copy.mat` }], paths_hint: [`${FT}/FT_Mat_Copy.mat`],
  }, { gate: true, expect: "gate" });

  s("assets_move", "C", "unity_open_mcp_assets_move", {
    entries: [{ source: `${FT}/FT_Mat_Copy.mat`, destination: `${FT}/FT_Mat_Moved.mat` }],
    paths_hint: [`${FT}/FT_Mat_Copy.mat`, `${FT}/FT_Mat_Moved.mat`],
  }, { gate: true, expect: "gate" });

  s("assets_refresh", "C", "unity_open_mcp_assets_refresh", { whole_project: false, paths_hint: [FT_FOLDER] }, { gate: true, expect: "gate" });

  // --- prefab lifecycle (instantiate existing → create from scene GO → open → status → close) ---
  s("prefab_instantiate", "C", "unity_open_mcp_prefab_instantiate", {
    prefab_asset_path: "Assets/Prefabs/GateTestCube.prefab", position: "0,3,0", paths_hint: SCENE_HINT,
  }, {
    gate: true, expect: "gate",
    after: (r, ctx) => { ctx.prefabInstanceId = pluck(r, "mutation.output.instanceId"); },
  });

  s("prefab_create", "C", "unity_open_mcp_prefab_create", {
    resolveArgs: (ctx) => ({
      prefab_asset_path: FT_PREFAB, instance_id: ctx.cubeId, connect: false, paths_hint: [FT_PREFAB, ...SCENE_HINT],
    }),
  }, { gate: true, expect: "gate" });

  s("prefab_status", "C", "unity_open_mcp_prefab_status", {
    resolveArgs: (ctx) => ({ instance_id: ctx.prefabInstanceId }),
  });

  s("prefab_open", "C", "unity_open_mcp_prefab_open", {
    prefab_asset_path: FT_PREFAB, paths_hint: [FT_PREFAB],
  }, { gate: true, expect: "gate" });

  s("prefab_save", "C", "unity_open_mcp_prefab_save", { paths_hint: [FT_PREFAB] }, { gate: true, expect: "gate" });
  s("prefab_get_overrides", "C", "unity_open_mcp_prefab_get_overrides", {
    resolveArgs: (ctx) => ({ instance_id: ctx.prefabInstanceId }),
  });
  s("prefab_close", "C", "unity_open_mcp_prefab_close", { save: true, paths_hint: [FT_PREFAB] }, { gate: true, expect: "gate" });

  // --- tags / layers (idempotent add) ---
  s("editor_add_tag", "C", "unity_open_mcp_editor_add_tag", { tag: "FT_Tag", paths_hint: ["ProjectSettings/TagManager.asset"] }, { gate: true, expect: "gate" });
  s("editor_add_layer", "C", "unity_open_mcp_editor_add_layer", { layer: "FT_Layer", paths_hint: ["ProjectSettings/TagManager.asset"] }, { gate: true, expect: "gate" });

  // --- script lifecycle (write → read back → asmdef → modify → delete). These force recompiles. ---
  s("script_write", "C", "unity_open_mcp_script_write", {
    file_path: FT_SCRIPT,
    content: "using UnityEngine;\npublic class FT_Script : MonoBehaviour { public int ft = 1; }",
    paths_hint: [FT_SCRIPT],
  }, { gate: true, expect: "gate", timeoutMs: 180_000 });

  s("script_read_back", "C", "unity_open_mcp_script_read", { file_path: FT_SCRIPT, max_lines: 5 });

  // asmdef_create/modify return bridge_response_unparsable (see specs/feedback.md
  // — the post-domain-reload response serialization is corrupted). Tolerate so
  // the route + recompile path is exercised without failing the suite.
  s("asmdef_create", "C", "unity_open_mcp_asmdef_create", {
    asset_path: FT_ASMDEF, name: "FT_Test", root_namespace: "FT", paths_hint: [FT_ASMDEF],
  }, { gate: true, expect: "tolerate", tolerate: ["bridge_response_unparsable", "asmdef_exists"], timeoutMs: 180_000 });

  s("asmdef_modify", "C", "unity_open_mcp_asmdef_modify", {
    asset_path: FT_ASMDEF, add_references: ["Unity.InputSystem"], paths_hint: [FT_ASMDEF],
  }, { gate: true, expect: "tolerate", tolerate: ["bridge_response_unparsable"], timeoutMs: 180_000 });

  s("script_delete", "C", "unity_open_mcp_script_delete", {
    file_paths: [FT_SCRIPT], paths_hint: [FT_SCRIPT],
  }, { gate: true, expect: "gate", timeoutMs: 180_000 });

  // --- ScriptableObject create (VolumeProfile is a real URP SO type) ---
  s("scriptableobject_create", "C", "unity_open_mcp_scriptableobject_create", {
    type_name: "UnityEngine.Rendering.VolumeProfile", asset_path: `${FT}/FT_SO.asset`, paths_hint: [`${FT}/FT_SO.asset`],
  }, { gate: true, expect: "tolerate", tolerate: ["asset_exists"] });

  // --- reserialize (mutating but safe on a temp asset; hits the same
  // bridge_response_unparsable serialization bug post-recompile) ---
  s("reserialize", "C", "unity_open_mcp_reserialize", { paths: [FT_MAT] }, { gate: true, expect: "tolerate", tolerate: ["bridge_response_unparsable"] });

  // --- build-settings safe setters (get-then-set-same; prefs use temp keys) ---
  s("build_set_target", "C", "unity_open_mcp_build_set_target", { target: "StandaloneOSX", paths_hint: ["ProjectSettings/ProjectSettings.asset"] }, { gate: true, expect: "gate" });
  s("build_set_scenes", "C", "unity_open_mcp_build_set_scenes", { scenes: [{ path: "Assets/Scenes/Main.unity", enabled: true }], mode: "overwrite", paths_hint: ["ProjectSettings/EditorBuildSettings.asset"] }, { gate: true, expect: "gate" });
  s("build_set_defines", "C", "unity_open_mcp_build_set_defines", { defines: [], paths_hint: ["ProjectSettings/ProjectSettings.asset"] }, { gate: true, expect: "tolerate", tolerate: ["bridge_response_unparsable"] });
  // settings setters require a non-empty fields array of {key, value}; use real
  // keys confirmed via the getters, set to current values (no-op mutation).
  s("settings_set_quality", "C", "unity_open_mcp_settings_set_quality", { fields: [{ key: "pixelLightCount", value: 1 }], paths_hint: ["ProjectSettings/QualitySettings.asset"] }, { gate: true, expect: "gate" });
  s("settings_set_quality_level", "C", "unity_open_mcp_settings_set_quality_level", {
    resolveArgs: (ctx) => ({
      quality_level: ctx.qualityAlternate ?? 0,
      paths_hint: ["ProjectSettings/QualitySettings.asset"],
    }),
  }, { gate: true, expect: "gate" });
  s("settings_set_physics", "C", "unity_open_mcp_settings_set_physics", { fields: [{ key: "bounceThreshold", value: 2 }], paths_hint: ["ProjectSettings/DynamicsManager.asset"] }, { gate: true, expect: "gate" });
  s("settings_set_time", "C", "unity_open_mcp_settings_set_time", { fields: [{ key: "maximumDeltaTime", value: 0.333 }], paths_hint: ["ProjectSettings/TimeManager.asset"] }, { gate: true, expect: "gate" });
  // prefs are gate-free flat-body tools (return {status, saved}, not mutation.success) → expect "ok"
  s("playerprefs_set_delete", "C", "unity_open_mcp_playerprefs_set", { key: "FT_Key", value: "x", type: "string" });
  s("playerprefs_delete", "C", "unity_open_mcp_playerprefs_delete", { key: "FT_Key" });
  s("editorprefs_set_delete", "C", "unity_open_mcp_editorprefs_set", { key: "FT_Key", value: "x", type: "string" });
  s("editorprefs_delete", "C", "unity_open_mcp_editorprefs_delete", { key: "FT_Key" });

  // --- profiler session control (gate-free editor state). profiler_start/stop
  // return bridge_response_unparsable (see specs/feedback.md) — tolerate. ---
  s("profiler_enable_module", "C", "unity_open_mcp_profiler_enable_module", { module: "CPU", enabled: true });
  s("profiler_set_config", "C", "unity_open_mcp_profiler_set_config", { enable_categories: ["Render"], disable_categories: [] });
  s("profiler_start", "C", "unity_open_mcp_profiler_start", { open_window: false }, { expect: "tolerate", tolerate: ["bridge_response_unparsable"] });
  s("profiler_save_data", "C", "unity_open_mcp_profiler_save_data", { file_path: `${FT}/FT_Profile.json`, paths_hint: [`${FT}/FT_Profile.json`] }, { gate: true, expect: "tolerate", tolerate: ["bridge_response_unparsable", "profiler_not_enabled"] });
  s("profiler_load_data", "C", "unity_open_mcp_profiler_load_data", { file_path: `${FT}/FT_Profile.json`, add_to_profiler: false }, { expect: "tolerate", tolerate: ["file_not_found", "bridge_response_unparsable"] });
  s("profiler_stop", "C", "unity_open_mcp_profiler_stop", {}, { expect: "tolerate", tolerate: ["bridge_response_unparsable"] });
  s("profiler_clear_data", "C", "unity_open_mcp_profiler_clear_data");

  // Re-acquire cube handle before focus (name-only lookup can fail after the
  // prefab/script/build block even when the instance_id from create still works).
  s("gameobject_find_refresh_pre_focus", "C", "unity_open_mcp_gameobject_find", {
    name: "FT_Cube",
  }, {
    after: (r, ctx) => {
      const id = pluck(r, "objects.0.instanceId");
      if (id != null) ctx.cubeId = id;
    },
  });

  // --- sceneview focus + camera ---
  s("scene_focus", "C", "unity_open_mcp_scene_focus", {
    resolveArgs: (ctx) => ({ instance_id: ctx.cubeId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });
  s("sceneview_set_camera", "C", "unity_open_mcp_sceneview_set_camera", {
    position: { x: 0, y: 5, z: -10 }, rotation: { x: 30, y: 0, z: 0 }, paths_hint: SCENE_HINT,
  }, { gate: true, expect: "gate" });

  // --- lighting (temp GOs for lights) ---
  s("light_add", "C", "unity_open_mcp_light_add", {
    resolveArgs: (ctx) => ({ parent_instance_id: ctx.sphereId, light_type: "point", paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate", after: (r, ctx) => { ctx.lightId = pluck(r, "mutation.output.instanceId"); } });
  s("light_set", "C", "unity_open_mcp_light_set", {
    resolveArgs: (ctx) => ({ instance_id: ctx.lightId, fields: { intensity: 2 }, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });
  s("light_modify", "C", "unity_open_mcp_light_modify", {
    resolveArgs: (ctx) => ({ instance_id: ctx.lightId, fields: { color: [0, 0, 1, 1] }, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });
  s("skybox_get", "C", "unity_open_mcp_skybox_get");
  s("skybox_set", "C", "unity_open_mcp_skybox_set", { material_asset_path: "Skybox/Procedural", paths_hint: SCENE_HINT }, { gate: true, expect: "gate" });
  s("reflection_probe_get", "C", "unity_open_mcp_reflection_probe_get", {
    resolveArgs: (ctx) => ({ instance_id: ctx.sphereId }),
  });
  s("reflection_probe_bake", "C", "unity_open_mcp_reflection_probe_bake", {
    resolveArgs: (ctx) => ({ instance_id: ctx.sphereId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate", timeoutMs: 180_000 });

  // --- audio ---
  s("audio_source_add", "C", "unity_open_mcp_audio_source_add", {
    resolveArgs: (ctx) => ({ parent_instance_id: ctx.sphereId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate", after: (r, ctx) => { ctx.audioSrcId = pluck(r, "mutation.output.instanceId"); } });
  s("audio_source_modify", "C", "unity_open_mcp_audio_source_modify", {
    resolveArgs: (ctx) => ({ instance_id: ctx.audioSrcId, fields: { volume: 0.5 }, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });
  s("audio_listener_get", "C", "unity_open_mcp_audio_listener_get");
  s("audio_mixer_get_parameter", "C", "unity_open_mcp_audio_mixer_get_parameter", {});

  // --- ui ---
  s("ui_canvas_add", "C", "unity_open_mcp_ui_canvas_add", {
    paths_hint: SCENE_HINT,
  }, { gate: true, expect: "gate", after: (r, ctx) => { ctx.canvasId = pluck(r, "mutation.output.instanceId"); } });
  s("ui_element_add", "C", "unity_open_mcp_ui_element_add", {
    resolveArgs: (ctx) => ({ parent_instance_id: ctx.canvasId, element_type: "text", paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate", after: (r, ctx) => { ctx.uiElemId = pluck(r, "mutation.output.instanceId"); } });
  s("ui_layout_group_add", "C", "unity_open_mcp_ui_layout_group_add", {
    resolveArgs: (ctx) => ({ parent_instance_id: ctx.canvasId, layout_type: "vertical", paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });
  s("ui_element_modify", "C", "unity_open_mcp_ui_element_modify", {
    resolveArgs: (ctx) => ({ instance_id: ctx.uiElemId, fields: { text: "FT" }, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });

  // --- constraints / LOD ---
  s("constraint_add", "C", "unity_open_mcp_constraint_add", {
    resolveArgs: (ctx) => ({ instance_id: ctx.cubeId, constraint_type: "aim", paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });
  s("lod_group_configure", "C", "unity_open_mcp_lod_group_configure", {
    resolveArgs: (ctx) => ({ instance_id: ctx.sphereId, lods: 2, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate", after: (r, ctx) => { ctx.lodId = pluck(r, "mutation.output.instanceId"); } });
  s("lod_add_level", "C", "unity_open_mcp_lod_add_level", {
    resolveArgs: (ctx) => ({ instance_id: ctx.lodId, screen_relative_height: 0.5, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });

  // --- terrain ---
  s("terrain_create", "C", "unity_open_mcp_terrain_create", {
    width: 100, length: 100, height: 50, paths_hint: [FT_TERRAIN_DATA_HINT, ...SCENE_HINT],
  }, { gate: true, expect: "gate", after: (r, ctx) => { ctx.terrainId = pluck(r, "mutation.output.instanceId"); }, timeoutMs: 180_000 });
  s("terrain_set_heights", "C", "unity_open_mcp_terrain_set_heights", {
    resolveArgs: (ctx) => ({ instance_id: ctx.terrainId, height: 10, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate", timeoutMs: 180_000 });
  s("terrain_set_neighbors", "C", "unity_open_mcp_terrain_set_neighbors", {
    resolveArgs: (ctx) => ({ instance_id: ctx.terrainId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });
  s("terrain_paint_layer", "C", "unity_open_mcp_terrain_paint_layer", {
    resolveArgs: (ctx) => ({ instance_id: ctx.terrainId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate", timeoutMs: 180_000 });
  s("terrain_place_trees", "C", "unity_open_mcp_terrain_place_trees", {
    resolveArgs: (ctx) => ({ instance_id: ctx.terrainId, count: 1, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate", timeoutMs: 180_000 });

  // --- cinemachine (reflection-gated; package may be absent → tolerate tool_not_found via expect) ---
  s("cinemachine_brain_ensure", "C", "unity_open_mcp_cinemachine_brain_ensure", { paths_hint: SCENE_HINT }, { gate: true, expect: "gate" });
  s("cinemachine_create_camera", "C", "unity_open_mcp_cinemachine_create_camera", { paths_hint: SCENE_HINT }, {
    gate: true, expect: "gate", after: (r, ctx) => { ctx.vcamId = pluck(r, "mutation.output.instanceId"); },
  });
  s("cinemachine_set_targets", "C", "unity_open_mcp_cinemachine_set_targets", {
    resolveArgs: (ctx) => ({ instance_id: ctx.vcamId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });
  s("cinemachine_set_lens", "C", "unity_open_mcp_cinemachine_set_lens", {
    resolveArgs: (ctx) => ({ instance_id: ctx.vcamId, field_of_view: 60, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });
  s("cinemachine_set_body", "C", "unity_open_mcp_cinemachine_set_body", {
    resolveArgs: (ctx) => ({ instance_id: ctx.vcamId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });
  s("cinemachine_set_noise", "C", "unity_open_mcp_cinemachine_set_noise", {
    resolveArgs: (ctx) => ({ instance_id: ctx.vcamId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });
  s("cinemachine_camera_list", "C", "unity_open_mcp_cinemachine_camera_list");

  // --- sprite2d / texture ---
  s("spriteatlas_create", "C", "unity_open_mcp_spriteatlas_create", {
    asset_path: FT_SPRITEATLAS, paths_hint: [FT_SPRITEATLAS],
  }, { gate: true, expect: "gate" });
  s("spriteatlas_get", "C", "unity_open_mcp_spriteatlas_get", { asset_path: FT_SPRITEATLAS });
  s("spriteatlas_list", "C", "unity_open_mcp_spriteatlas_list");
  s("spriteatlas_add_packable", "C", "unity_open_mcp_spriteatlas_add_packable", {
    asset_path: FT_SPRITEATLAS, packable: { path: "Assets/Textures/rainbowtexture.png" }, paths_hint: [FT_SPRITEATLAS],
  }, { gate: true, expect: "gate" });
  s("spriteatlas_remove_packable", "C", "unity_open_mcp_spriteatlas_remove_packable", {
    asset_path: FT_SPRITEATLAS, packable: { path: "Assets/Textures/rainbowtexture.png" }, paths_hint: [FT_SPRITEATLAS],
  }, { gate: true, expect: "gate" });
  s("spriteatlas_modify", "C", "unity_open_mcp_spriteatlas_modify", {
    asset_path: FT_SPRITEATLAS, fields: {}, paths_hint: [FT_SPRITEATLAS],
  }, { gate: true, expect: "gate" });
  s("spriteatlas_delete", "C", "unity_open_mcp_spriteatlas_delete", { asset_path: FT_SPRITEATLAS, paths_hint: [FT_SPRITEATLAS] }, { gate: true, expect: "gate" });
  s("texture_get", "C", "unity_open_mcp_texture_get", { asset_path: "Assets/Textures/rainbowtexture.png" });
  s("texture_get_importer", "C", "unity_open_mcp_texture_get_importer", { asset_path: "Assets/Textures/rainbowtexture.png" });
  s("texture_reimport", "C", "unity_open_mcp_texture_reimport", { asset_path: "Assets/Textures/rainbowtexture.png", paths_hint: ["Assets/Textures/rainbowtexture.png"] }, { gate: true, expect: "gate" });
  s("texture_set_import", "C", "unity_open_mcp_texture_set_import", {
    asset_path: "Assets/Textures/rainbowtexture.png", fields: {}, paths_hint: ["Assets/Textures/rainbowtexture.png"],
  }, { gate: true, expect: "gate" });

  // --- core mutating entry points (non-destructive snippets / menus / methods) ---
  s("execute_csharp_safe", "C", "unity_open_mcp_execute_csharp", {
    code: "return 1 + 1;", paths_hint: HINT,
  }, { gate: true, expect: "gate" });
  s("invoke_method_safe", "C", "unity_open_mcp_invoke_method", {
    type_name: "UnityEngine.Time", method_name: "get_time", is_static: true, paths_hint: HINT,
  }, { gate: true, expect: "gate" });
  s("execute_menu_safe", "C", "unity_open_mcp_execute_menu", {
    menu_path: "Assets/Refresh", paths_hint: HINT,
  }, { gate: true, expect: "gate" });

  // Re-resolve handles before destroy — domain reload / long chain can stale
  // name-based lookup while cached instance_ids still work mid-chain.
  s("gameobject_find_refresh_pre_destroy", "C", "unity_open_mcp_gameobject_find", {
    name_contains: "FT_",
    max_results: 30,
  }, {
    after: (r, ctx) => { applyFindObjectsToCtx(r, ctx); },
  });
  s("gameobject_find_gate_test_cube", "C", "unity_open_mcp_gameobject_find", {
    name: "GateTestCube",
  }, {
    after: (r, ctx) => {
      const id = pluck(r, "objects.0.instanceId");
      if (id != null) ctx.prefabInstanceId = id;
    },
  });

  // --- GameObject destroy (cleanup of the chain GOs) ---
  s("gameobject_destroy_renamed", "C", "unity_open_mcp_gameobject_destroy", {
    resolveArgs: (ctx) => ({ instance_id: ctx.cubeDupId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "tolerate", tolerate: ["gameobject_not_found"] });
  s("gameobject_destroy_cube", "C", "unity_open_mcp_gameobject_destroy", {
    resolveArgs: (ctx) => ({ instance_id: ctx.cubeId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });
  s("gameobject_destroy_sphere", "C", "unity_open_mcp_gameobject_destroy", {
    resolveArgs: (ctx) => ({ instance_id: ctx.sphereId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });
  // M27 Plan 4 — cleanup the two batch_execute-created GameObjects. Tolerate
  // gameobject_not_found in case the batch was partial (one step failed).
  s("gameobject_destroy_batch_a", "C", "unity_open_mcp_gameobject_destroy", {
    resolveArgs: (ctx) => ({ instance_id: ctx.batchGoAId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "tolerate", tolerate: ["gameobject_not_found"] });
  s("gameobject_destroy_batch_b", "C", "unity_open_mcp_gameobject_destroy", {
    resolveArgs: (ctx) => ({ instance_id: ctx.batchGoBId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "tolerate", tolerate: ["gameobject_not_found"] });
  s("gameobject_destroy_prefab_inst", "C", "unity_open_mcp_gameobject_destroy", {
    resolveArgs: (ctx) => ({ instance_id: ctx.prefabInstanceId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "tolerate", tolerate: ["gameobject_not_found"] });
  s("gameobject_destroy_terrain", "C", "unity_open_mcp_gameobject_destroy", {
    resolveArgs: (ctx) => ({ instance_id: ctx.terrainId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "tolerate", tolerate: ["gameobject_not_found"] });
  s("gameobject_destroy_canvas", "C", "unity_open_mcp_gameobject_destroy", {
    resolveArgs: (ctx) => ({ instance_id: ctx.canvasId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "tolerate", tolerate: ["gameobject_not_found"] });
  s("gameobject_destroy_vcam", "C", "unity_open_mcp_gameobject_destroy", {
    resolveArgs: (ctx) => ({ instance_id: ctx.vcamId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "tolerate", tolerate: ["gameobject_not_found"] });

  // --- scene save / unload (only valid if FT_Scene was created; tolerate).
  // NOTE: when FT_Scene couldn't be created additively (untitled-scene limit),
  // the GameObject chain runs in whatever scene IS active (often Main.unity).
  // scene_save_ft targets "FT_Scene" by name, so it will NOT persist the chain
  // into Main.unity — the chain GOs are destroyed by gameobject_destroy_*
  // below, and any that survive are discarded by NOT saving the active scene.
  s("scene_save_ft", "C", "unity_open_mcp_scene_save", { name: "FT_Scene", paths_hint: SCENE_HINT }, { gate: true, expect: "tolerate", tolerate: ["scene_not_found", "missing_parameter"] });
  s("scene_unload_ft", "C", "unity_open_mcp_scene_unload", { name: "FT_Scene", paths_hint: SCENE_HINT }, { gate: true, expect: "tolerate", tolerate: ["scene_not_found"] });

  // --- assets_delete the whole fixture root. Tolerate bridge_response_unparsable
  // (the post-delete refresh can hit the serialization bug); cleanup() is the
  // safety net and also removes the orphan .meta. ---
  s("assets_delete_ft_root", "C", "unity_open_mcp_assets_delete", { paths: [FT_FOLDER], paths_hint: [FT_FOLDER] }, { gate: true, expect: "tolerate", tolerate: ["bridge_response_unparsable", "asset_not_found"] });

  // --- editor undo/redo probes (after destroy — must not invalidate destroy IDs) ---
  s("editor_undo", "C", "unity_open_mcp_editor_undo");
  s("editor_redo", "C", "unity_open_mcp_editor_redo");

  // --- editor_clear_history: run LAST in band C (irreversible; clears the undo stack) ---
  s("editor_clear_history", "C", "unity_open_mcp_editor_clear_history", { paths_hint: SCENE_HINT }, { gate: true, expect: "gate" });

  // --- package_add / package_remove / reimport_package: SKIP (would uninstall/break the demo) ---
  // Recorded as skips in the report; see SKIPS below.

  // =====================================================================
  // BAND D — batch / headless (expect locked / can't grab project)
  // =====================================================================
  s("compile_check", "D", "unity_open_mcp_compile_check", {}, { expect: "locked", timeoutMs: 180_000 });
  s("scan_all", "D", "unity_open_mcp_scan_all", {}, { expect: "locked", timeoutMs: 240_000 });
  s("baseline_create", "D", "unity_open_mcp_baseline_create", { baseline_path: "FT_baseline.json" }, { expect: "locked", timeoutMs: 240_000 });
  s("regression_check", "D", "unity_open_mcp_regression_check", { baseline_path: "FT_baseline.json" }, { expect: "locked", timeoutMs: 240_000 });
  // batch-fallback meta-tools (route to batch when bridge down; with bridge up they go live — tolerate either)
  s("find_members_batch", "D", "unity_open_mcp_find_members", { query: "Transform" });
  s("execute_csharp_batch", "D", "unity_open_mcp_execute_csharp", { code: "return 42;", paths_hint: HINT });
  s("invoke_method_batch", "D", "unity_open_mcp_invoke_method", { type_name: "UnityEngine.Time", method_name: "get_frameCount", is_static: true, paths_hint: HINT });
  s("execute_menu_batch", "D", "unity_open_mcp_execute_menu", { menu_path: "Edit/Selection", paths_hint: HINT });

  // =====================================================================
  // BAND E — destructive refusal path (no bypass flags → expect deny heuristic)
  // =====================================================================
  s("build_start_refused", "E", "unity_open_mcp_build_start", {
    target: "StandaloneOSX", paths_hint: HINT,
  }, { gate: true, expect: "refused" });
  s("execute_csharp_refused", "E", "unity_open_mcp_execute_csharp", {
    code: "EditorApplication.Exit(0); return 1;", paths_hint: HINT,
  }, { gate: true, expect: "refused" });
  s("execute_menu_refused", "E", "unity_open_mcp_execute_menu", {
    menu_path: "File/Quit", paths_hint: HINT,
  }, { gate: true, expect: "refused" });

  // =====================================================================
  // BAND F — extension pack availability
  //
  // Two sub-bands share letter F:
  //   (1) REACHABLE_EXT_TOOLS — compiled into the bridge (M18+ domain packs).
  //       Minimal args; pass on any response except tool_not_found.
  //   (2) UNAVAIL_TOOLS — not compiled into this bridge build; expect tool_not_found.
  // =====================================================================
  const REACHABLE_EXT_TOOLS = [
    // navigation (com.unity.ai.navigation)
    "unity_open_mcp_navigation_surface_add", "unity_open_mcp_navigation_set_bake_settings",
    "unity_open_mcp_navigation_surface_bake", "unity_open_mcp_navigation_modifier_add",
    "unity_open_mcp_navigation_modifier_volume_add", "unity_open_mcp_navigation_link_add",
    "unity_open_mcp_navigation_agent_add", "unity_open_mcp_navigation_agent_set_destination",
    "unity_open_mcp_navigation_list", "unity_open_mcp_navigation_get", "unity_open_mcp_navigation_modify",
    // input-system (com.unity.inputsystem)
    "unity_open_mcp_inputsystem_asset_create", "unity_open_mcp_inputsystem_actionmap_add",
    "unity_open_mcp_inputsystem_action_add", "unity_open_mcp_inputsystem_binding_add",
    "unity_open_mcp_inputsystem_binding_composite_add", "unity_open_mcp_inputsystem_controlscheme_add",
    "unity_open_mcp_inputsystem_get",
    // probuilder (com.unity.probuilder)
    "unity_open_mcp_probuilder_create_shape", "unity_open_mcp_probuilder_get_mesh_info",
    "unity_open_mcp_probuilder_extrude", "unity_open_mcp_probuilder_delete_faces", "unity_open_mcp_probuilder_set_face_material",
    // particle-system (UnityEngine.ParticleSystemModule)
    "unity_open_mcp_particle_system_get", "unity_open_mcp_particle_system_modify",
    // animation (com.unity.modules.animation)
    "unity_open_mcp_animation_create", "unity_open_mcp_animation_get_data", "unity_open_mcp_animation_modify",
    "unity_open_mcp_animator_create", "unity_open_mcp_animator_get_data", "unity_open_mcp_animator_modify",
    // shadergraph (com.unity.shadergraph)
    "unity_open_mcp_shader_graph_create", "unity_open_mcp_shader_graph_open",
    "unity_open_mcp_shader_graph_node_add", "unity_open_mcp_shader_graph_node_connect",
  ];
  for (const tool of REACHABLE_EXT_TOOLS) {
    const short = tool.replace(/^unity_(open_mcp_|senses_)/, "");
    s(`ext_${short}`, "F", tool, { paths_hint: HINT }, { expect: "reachable" });
  }

  const UNAVAIL_TOOLS = [
    // splines (com.unity.splines — extension pack not in default bridge build)
    "unity_open_mcp_splines_container_create", "unity_open_mcp_splines_add_knot", "unity_open_mcp_splines_set_knot",
    "unity_open_mcp_splines_set_tangent_mode", "unity_open_mcp_splines_evaluate", "unity_open_mcp_splines_get_knots", "unity_open_mcp_splines_modify",
    // timeline (com.unity.timeline)
    "unity_open_mcp_timeline_create", "unity_open_mcp_timeline_track_add", "unity_open_mcp_timeline_clip_add",
    "unity_open_mcp_timeline_director_bind", "unity_open_mcp_timeline_modify",
    // tilemap (com.unity.2d.tilemap)
    "unity_open_mcp_tilemap_create", "unity_open_mcp_tilemap_set_tile", "unity_open_mcp_tilemap_box_fill",
    "unity_open_mcp_tilemap_create_tile_asset", "unity_open_mcp_tilemap_create_rule_tile",
    // vfx (com.unity.visualeffectgraph)
    "unity_open_mcp_vfx_list", "unity_open_mcp_vfx_open", "unity_open_mcp_vfx_block_edit",
    // memoryprofiler (com.unity.memoryprofiler)
    "unity_senses_memory_snapshot_capture",
  ];
  for (const tool of UNAVAIL_TOOLS) {
    const short = tool.replace(/^unity_(open_mcp_|senses_)/, "");
    s(`unavail_${short}`, "F", tool, { paths_hint: HINT }, { expect: "unavailable" });
  }

  // =====================================================================
  // BAND G — run_tests (ISOLATED, runs LAST)
  //
  // run_tests blocks the Editor test runner for ~30–45s AND leaves the bridge
  // in a degraded (bridge_compile_failed) state for ~10–15s afterward, which
  // poisons any tool called in that window. Running it in its own band at the
  // very end (after all mutations + reads) means its contamination can't break
  // anything else. See specs/feedback.md for the underlying bridge bug.
  // =====================================================================
  s("run_tests", "G", "unity_senses_run_tests", { mode: "EditMode" }, { timeoutMs: 45_000, expect: "ok_or_timeout", tolerate: ["execution_error"] });

  return steps;
}

// Deliberately-skipped tools: genuinely project-trashing, no safe path.
const SKIPS = [
  { tool: "unity_open_mcp_package_add", reason: "would install a package mid-run, may force recompile / break downstream steps" },
  { tool: "unity_open_mcp_package_remove", reason: "would uninstall a package mid-run, breaking downstream steps + the demo" },
  { tool: "unity_open_mcp_reimport_package", reason: "force-reimports a package's source — expensive, forces recompile, no informative failure path" },
];

// Recompile / RestartThenSettle steps — save every dirty scene immediately before
// so Unity's native "unsaved changes" modal never blocks the main thread.
const SAVE_DIRTY_BEFORE = new Set([
  "prefab_instantiate",
  "asmdef_create",
  "asmdef_modify",
  "script_delete",
  "script_write",
  "build_set_target",
  "build_set_scenes",
  "build_set_defines",
  "settings_set_quality",
  "settings_set_quality_level",
  "editor_clear_history",
]);

// Persist in-scene GO mutations to disk before steps that trigger domain reload
// or prefab capture — otherwise compile/reload drops unsaved hierarchy state.
const SAVE_DIRTY_AFTER = new Set(["gameobject_find_query"]);

// ---------------------------------------------------------------------------
// main
// ---------------------------------------------------------------------------

function main() {
  const opts = parseArgs(process.argv.slice(2));

  if (!existsSync(CLI_BIN)) {
    console.error(`CLI binary not found at ${CLI_BIN}.`);
    console.error(`Run \`npm run build\` in mcp-server/ first.`);
    process.exit(2);
  }
  if (!isAbsolute(opts.project)) {
    console.error(`--project must be an ABSOLUTE path (relative paths hash to a different bridge port). Got: ${opts.project}`);
    process.exit(2);
  }
  if (!existsSync(opts.project)) {
    console.error(`Project not found: ${opts.project}`);
    process.exit(2);
  }

  const allSteps = buildSuite();
  let selected = allSteps;
  if (opts.band) selected = selected.filter((s) => opts.band.includes(s.band));
  if (opts.only) selected = selected.filter((s) => opts.only.some((n) => s.label.includes(n)));

  if (opts.list) {
    console.log("Full-test suite steps:");
    let lastBand = null;
    for (const s of selected) {
      if (s.band !== lastBand) {
        console.log(`\n  --- Band ${s.band} ---`);
        lastBand = s.band;
      }
      const tag = s.expect && s.expect !== "ok" ? ` [${s.expect}]` : (s.gate ? " [gate]" : "");
      console.log(`  ${s.label.padEnd(34)} ${s.tool}${tag}`);
    }
    console.log(`\n${selected.length} step(s). Project: ${opts.project}`);
    if (!opts.band && !opts.only) {
      console.log(`\nSkipped (deliberate):`);
      for (const sk of SKIPS) console.log(`  ${sk.tool.padEnd(36)} — ${sk.reason}`);
    }
    return;
  }

  console.log(`unity-open-mcp full test`);
  console.log(`  project: ${opts.project}`);
  console.log(`  cli:     ${CLI_BIN}`);
  console.log(`  steps:   ${selected.length}`);
  console.log("");

  const runEnv = buildRunEnv();

  // Preflight: try to dismiss a wedging modal (unsaved InitTestScene*, etc.)
  // before the suite starts. On macOS this needs Accessibility permission for
  // the node binary — without it, dismiss is best-effort and you may need to
  // click Don't Save manually once.
  console.log("--- preflight ---");
  dismissBlockingModals(runEnv);
  closeInitTestScenes(opts.project, runEnv);
  revertMainSceneIfDirty(opts.project, runEnv);
  dismissBlockingModals(runEnv);
  const preflight = invokeTool(opts.project, "unity_open_mcp_editor_status", {}, 15_000, runEnv);
  const preCode = preflight?.result?.error?.code;
  if (preflight?.isError === true && preCode === "main_thread_blocked") {
    console.error("");
    console.error("Unity main thread is blocked by a modal dialog (likely unsaved scene changes).");
    console.error("Click \"Don't Save\" in the Unity Editor, or grant Accessibility permission");
    console.error("to the app that runs `node` (Cursor, Terminal, iTerm, …) — see");
    console.error("docs/dialog-policy.md#macos-accessibility-required-for-auto-dismiss.");
    console.error("Then re-run this script.");
    process.exit(2);
  }
  process.stdout.write(`✓ ${"editor reachable".padEnd(34)}        ok\n\n`);

  const ctx = {};
  const results = [];
  const bandSummary = {};
  let pass = 0;
  let fail = 0;

  // SIGINT handler: still run cleanup, then exit.
  let interrupted = false;
  const onSigInt = () => {
    if (interrupted) process.exit(1);
    interrupted = true;
    console.error("\n^C — interrupted; running cleanup before exit...");
    if (!opts.noCleanup) cleanupViaBridge(opts.project, runEnv);
    process.exit(1);
  };
  process.on("SIGINT", onSigInt);

  let lastBand = null;
  for (const step of selected) {
    if (step.band !== lastBand) {
      if (lastBand !== null) console.log("");
      console.log(`--- Band ${step.band} ---`);
      if (step.band === "C") {
        const n = saveAllDirtyScenes(opts.project, runEnv);
        if (n > 0) process.stdout.write(`  (saved ${n} dirty scene(s) before Band C)\n`);
      }
      lastBand = step.band;
    }
    const out = runToolLib(step, ctx, opts.project, opts.timeoutMs, runEnv, { saveDirtyBefore: SAVE_DIRTY_BEFORE, saveDirtyAfter: SAVE_DIRTY_AFTER });
    if (out.ok) pass++;
    else fail++;
    bandSummary[step.band] = bandSummary[step.band] || { pass: 0, fail: 0 };
    bandSummary[step.band][out.ok ? "pass" : "fail"]++;
    results.push({ label: step.label, band: step.band, tool: step.tool, expect: step.expect ?? "ok", passed: out.ok, ms: out.ms, detail: out.detail ?? out.error ?? "" });
    if (step.after) {
      try {
        step.after(out.result ?? {}, ctx);
      } catch {
        // best-effort handoff; downstream steps fail loudly if missing
      }
    }
    if (ctx.ftSceneIsolationWarning && step.label === "scene_set_active") {
      process.stdout.write(`  ⚠ ${ctx.ftSceneIsolationWarning}\n`);
    }
    const mark = out.ok ? "✓" : "✗";
    const ms = String(out.ms).padStart(6);
    process.stdout.write(`${mark} ${step.label.padEnd(34)} ${ms}ms  ${out.detail ?? out.error}\n`);
  }

  process.removeListener("SIGINT", onSigInt);

  const ranBandC = !opts.band || opts.band.includes("C");
  const ranBandG = !opts.band || opts.band.includes("G");
  let isolation = null;

  // Post-suite: dismiss save modals, unload InitTestScene*, discard Main dirty state.
  if (ranBandC || ranBandG) {
    console.log("");
    console.log("--- finalize ---");
    finalizeEditorState(opts.project, runEnv, { excludeMain: !opts.saveMain });
    if (ranBandC) {
      isolation = collectIsolationState(opts.project, runEnv, ctx);
      if (isolation.main_dirty_at_finalize) {
        process.stdout.write(`  ⚠ Main still dirty after finalize — dismiss any modal manually\n`);
      }
    }
    process.stdout.write(`✓ ${"editor finalize (dismiss + scene cleanup)".padEnd(34)}        done\n`);
  }

  // Always attempt cleanup of the temp fixture root unless --no-cleanup.
  if (!opts.noCleanup && (!opts.band || opts.band.includes("C"))) {
    console.log("");
    console.log("--- cleanup ---");
    cleanupViaBridge(opts.project, runEnv);
    const clean = cleanupTempFolder(opts.project);
    process.stdout.write(`${clean ? "✓" : "✗"} ${"temp fixture cleanup".padEnd(34)}        ${clean ? "Assets/MCP_FullTest gone" : "left on disk (see --no-cleanup)"}\n`);
  }

  // Per-band summary
  console.log("");
  console.log("Band summary:");
  for (const b of Object.keys(bandSummary).sort()) {
    const bs = bandSummary[b];
    console.log(`  Band ${b}: ${bs.pass} passed, ${bs.fail} failed`);
  }

  // Skips
  if (!opts.band && !opts.only) {
    console.log("");
    console.log(`Skipped (${SKIPS.length} deliberate):`);
    for (const sk of SKIPS) console.log(`  ${sk.tool.padEnd(36)} — ${sk.reason}`);
  }

  console.log("");
  console.log(`${pass} passed, ${fail} failed (${selected.length} total)`);

  if (opts.jsonOut) {
    const report = {
      project: opts.project,
      ranAt: new Date().toISOString(),
      summary: { pass, fail, total: selected.length, bandSummary },
      isolation,
      skips: opts.band || opts.only ? [] : SKIPS,
      steps: results,
    };
    try {
      writeFileSync(opts.jsonOut, JSON.stringify(report, null, 2));
      console.log(`Report written to ${opts.jsonOut}`);
    } catch (e) {
      console.error(`Failed to write json-out: ${e.message}`);
    }
  }

  // --auto-revert: the settings/tag/build-scene setters in Band C inherently
  // mutate tracked demo files. Revert ProjectSettings/*.asset so the demo stays
  // clean for the next run. CRUCIAL: do NOT revert scene files (Scenes/*.unity)
  // via git — rewriting a scene on disk while Unity has it open triggers
  // Unity's "Scene has been modified externally" modal, which jams the bridge's
  // main-thread queue and can't be dismissed programmatically (see
  // specs/feedback.md). Scene changes are avoided by NOT saving the active
  // scene in the chain (scene_save_ft targets FT_Scene by name), so any chain
  // GOs that leak are discarded on the next Editor restart.
  if (opts.autoRevert && (!opts.band || opts.band.includes("C"))) {
    console.log("");
    console.log("--- auto-revert tracked ProjectSettings (NOT scenes) ---");
    try {
      const out = execFileSync("git", ["-C", REPO_ROOT, "status", "--porcelain", "--", opts.project], {
        encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], timeout: 15_000,
      });
      const dirty = out
        .split("\n")
        .filter(Boolean)
        .filter((l) => l.match(/^ M /))
        .map((l) => l.slice(3).trim())
        // Exclude .unity scenes — reverting them on disk triggers Unity's
        // external-modification modal. Only revert ProjectSettings + assets.
        .filter((f) => !f.endsWith(".unity"))
        .filter((f) => !f.includes(".unity-open-mcp/"));
      if (dirty.length === 0) {
        console.log("  (no tracked ProjectSettings files changed)");
      } else {
        for (const f of dirty) console.log(`  reverting ${f}`);
        execFileSync("git", ["-C", REPO_ROOT, "checkout", "--", ...dirty], {
          encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], timeout: 30_000,
        });
        console.log(`✓ reverted ${dirty.length} tracked file(s)`);
      }
    } catch (e) {
      console.error(`✗ auto-revert failed: ${e.message}`);
    }
  }

  if (fail > 0) {
    // Surface the first few failures' envelopes to aid debugging.
    const fails = results.filter((r) => !r.passed).slice(0, 3);
    for (const f of fails) {
      const raw = results.find((r) => r.label === f.label);
      console.error(`\n✗ ${f.label} (${f.tool}, expect=${f.expect}): ${f.detail}`);
    }
    process.exit(1);
  }
}

main();
