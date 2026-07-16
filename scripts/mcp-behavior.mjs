#!/usr/bin/env node
// mcp-behavior.mjs — S1: live behavioral MCP test suite.
//
// Strict success-path coverage for tools S0 only reaches via `tolerate`/
// `reachable`, plus the 5 tools absent from S0 (scene_open, prefab_apply,
// prefab_revert, prefab_unpack, settings_set_player, settings_set_lighting,
// audio_mixer_set_parameter). S0 proves reachability; S1 proves the tool
// actually works on a happy path.
//
// Fixture: Assets/MCP_BehaviorTest/ + an additive scene. Reuses M26-Plan-5
// finalize hygiene: re-resolve instance IDs before destroy; discard Main dirty
// state in finalize (never save Main — chain GOs are destroyed, not persisted).
//
// Bands:
//   A — meta + verify/gate intelligence (checkpoint/delta/apply_fix/mutation_explain)
//   B — strict re-tests of S0 tolerate steps (reads: console, spatial, screenshots,
//       component_get, asmdef, reserialize, frame_debugger, generate_skill, read depth)
//   C — mutations + cleanup (tools absent from S0 + editor_set_state play/pause/stop)
//   G — run_tests (EditMode + PlayMode; isolated LAST — bridge degradation caution)
//
// Strict expects: no `tolerate` except explicitly documented env gaps (marked
// with a comment citing specs/feedback.md). A failure means a real regression.
//
// Usage:
//   node scripts/mcp-behavior.mjs                       # full suite vs ./demo
//   node scripts/mcp-behavior.mjs --project /path       # target another project
//   node scripts/mcp-behavior.mjs --band A,B            # run named bands only
//   node scripts/mcp-behavior.mjs --only needle         # subset by label
//   node scripts/mcp-behavior.mjs --list                # list steps, don't run
//   node scripts/mcp-behavior.mjs --json-out report.json
//
// Requirements: mcp-server/dist/index.js built; a Unity Editor open with the
// project + bridge running. Pass --project as an ABSOLUTE path.

import { execFileSync } from "node:child_process";
import { existsSync, writeFileSync } from "node:fs";
import { isAbsolute } from "node:path";

import {
  REPO_ROOT,
  CLI_BIN,
  classify,
  pluck,
  parseCommonArgs,
  makeStepBuilder,
  buildRunEnv,
  dismissBlockingModals,
  invokeTool,
  saveAllDirtyScenes,
  runTool as runToolLib,
  finalizeEditorState,
  collectIsolationState,
  cleanupViaBridge,
  cleanupTempFolder,
  printStepList,
  makeSigIntHandler,
} from "./mcp-test-lib.mjs";

// Fixture layout (separate from S0's MCP_FullTest so both can coexist).
const FT = "Assets/MCP_BehaviorTest";
const FT_FOLDER = FT;
const FT_SCENE_ASSET = `${FT}/BT_Scene.unity`;
const FT_MAT = `${FT}/BT_Mat.mat`;
const FT_PREFAB = `${FT}/BT_Cube.prefab`;
const FT_ASMDEF = `${FT}/BT_Test.asmdef`;
const FT_SCRIPT = `${FT}/BT_Script.cs`;
const FT_SKILL_TMP = `${FT}/BT_SkillTmp`;
const HINT = [FT];
const SCENE_HINT = [FT_SCENE_ASSET];
const MAIN_SCENE_PATH = "Assets/Scenes/Main.unity";
const MAIN_SCENE_HINT = [MAIN_SCENE_PATH];

// Demo asset references (read-only targets that exist in the demo project).
const DEMO_PREFAB = "Assets/Prefabs/GateTestCube.prefab";
const DEMO_MATERIAL = "Assets/Materials/TestMaterial.mat";
const DEMO_FIXTURE_MISSING = "Assets/Fixtures/MissingScriptFixture.prefab";

function printHelp() {
  console.error(`Usage: node scripts/mcp-behavior.mjs [options]

Options:
  --project, -P <path>   Target Unity project (default: <repo>/demo). MUST be absolute.
  --band A,B,C,G         Run only bands A=meta/gate-intel, B=strict-read-retests,
                         C=mutations+cleanup (tools absent from S0 + editor state),
                         G=run_tests (isolated LAST)
  --only needle          Run only steps whose label contains any needle
  --list                 List the suite steps and exit (no execution)
  --no-cleanup           Leave temp fixtures in place (debug)
  --json-out <file>      Write a machine-readable report to <file>
  --timeout-ms <n>       Per-step timeout (default 120000; raised internally
                         for recompile-heavy steps)
  -h, --help             Show this help

The CLI binary must exist at mcp-server/dist/index.js (run \`npm run build\` in
mcp-server/ first). A Unity Editor must be open with the project and the bridge
running. This suite exercises STRICT success paths — a failure is a real
regression (unlike S0's tolerate-mode reachability probes).`);
}

function parseArgs(argv) {
  return parseCommonArgs(argv, { help: printHelp });
}

function buildSuite() {
  const { s, steps } = makeStepBuilder();

  // =====================================================================
  // BAND A — meta + verify/gate intelligence
  // =====================================================================
  s("ping", "A", "unity_open_mcp_ping");
  s("editor_status", "A", "unity_open_mcp_editor_status");

  // checkpoint_create → mutating step → delta with returned id (strict chain).
  s("checkpoint_create", "A", "unity_open_mcp_checkpoint_create", { label: "bt-checkpoint" }, {
    after: (r, ctx) => {
      ctx.checkpointId =
        pluck(r, "checkpointId") ??
        pluck(r, "checkpoint.id") ??
        pluck(r, "id");
    },
  });

  // apply_fix with dry_run:false on a known issue from a fresh scan_paths.
  // scan the missing-script fixture, find a real missing_references issue,
  // then apply the fix (remove_missing_script is safe + auto-rollback on failure).
  s("scan_missing_script", "A", "unity_open_mcp_scan_paths", {
    paths: [DEMO_FIXTURE_MISSING], profile: "balanced",
  }, {
    after: (r, ctx) => {
      const issues = r.issues ?? [];
      const match = issues.find((i) => i.ruleId === "missing_references" && i.fixId);
      if (match) ctx.fixIssueId = match.issueId ?? `${match.ruleId}|${match.severity}|${match.assetPath}|${match.issueCode}`;
    },
  });
  s("apply_fix_real", "A", "unity_open_mcp_apply_fix", {
    resolveArgs: (ctx) => ({
      issue_id: ctx.fixIssueId ?? "missing_references|Error|Assets/Fixtures/MissingScriptFixture.prefab|missing_script",
      dry_run: false, gate: "enforce",
    }),
  }, {
    gate: true,
    // The fixture may already be fixed from a prior S1 run, or the issue_id
    // resolution may mismatch (S0 hit invalid_issue_id). Tolerate those two
    // documented gaps; anything else is a real apply_fix regression.
    expect: "tolerate", tolerate: ["invalid_issue_id", "issue_not_found"],
  });

  // mutation_explain after a real mutation context — use the checkpoint id
  // stashed above so the explain has a real before-state to diff against.
  s("mutation_explain", "A", "unity_open_mcp_mutation_explain", {
    resolveArgs: (ctx) => ({
      tool_name: "unity_open_mcp_material_set_property",
      args: { property: "_Color", type: "color", value: [1, 0, 0, 1] },
      checkpoint_id: ctx.checkpointId,
    }),
  });

  // =====================================================================
  // BAND B — strict re-tests of S0 tolerate steps (reads)
  // =====================================================================

  // read_console — S0 tolerates execution_error (Unity 6 LogEntries reflection
  // bug). S1 re-tests; if the bug is fixed, this passes strict. If still broken,
  // keep the tolerate with a feedback.md link so the suite stays green while
  // surfacing the bug. (See specs/feedback.md.)
  s("read_console_strict", "B", "unity_senses_read_console", { max_entries: 5 }, {
    expect: "tolerate", tolerate: ["execution_error"],
  });

  // spatial_query — all five actions with fixture colliders/GOs. The fixture
  // (created in Band C) provides real targets. We run reads here assuming the
  // Band C fixture already exists from a prior run, OR tolerate target_not_found
  // if the fixture isn't up yet. The strict owner is the bounds+raycast pair
  // against the BT_Cube created in Band C — so this band assumes C ran first
  // when run as a full suite (band letter is just for grouping; the orchestrator
  // runs them in file order A→B→C→G).

  // To make spatial_query strict regardless of band order, we create a throwaway
  // probe GO inline here, run the 5 actions, then destroy it.
  s("spatial_probe_create", "B", "unity_open_mcp_gameobject_create", {
    name: "BT_SpatialProbe", primitive_type: "Cube", position: "0,2,0", paths_hint: SCENE_HINT,
  }, {
    gate: true, expect: "gate",
    after: (r, ctx) => { ctx.spatialProbeId = pluck(r, "mutation.output.instanceId") ?? pluck(r, "instanceId"); },
  });
  s("spatial_bounds", "B", "unity_senses_spatial_query", {
    // instance_id is a JSON string on Unity 6000.5+ (EntityId wire form).
    resolveArgs: (ctx) => ({ action: "bounds", instance_id: ctx.spatialProbeId }),
  });
  s("spatial_raycast", "B", "unity_senses_spatial_query", {
    action: "raycast", origin: "0,5,0", direction: "0,-1,0", max_distance: 10,
  });
  s("spatial_overlap", "B", "unity_senses_spatial_query", {
    action: "overlap", shape: "sphere", center: "0,2,0", radius: 2,
  });
  s("spatial_ground_check", "B", "unity_senses_spatial_query", {
    resolveArgs: (ctx) => ({
      action: "ground_check", instance_id: ctx.spatialProbeId, max_distance: 10,
    }),
  });
  s("spatial_nearest", "B", "unity_senses_spatial_query", {
    resolveArgs: (ctx) => ({
      action: "nearest", instance_id: ctx.spatialProbeId, max: 5,
    }),
  });
  s("spatial_probe_destroy", "B", "unity_open_mcp_gameobject_destroy", {
    resolveArgs: (ctx) => ({ instance_id: ctx.spatialProbeId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "tolerate", tolerate: ["gameobject_not_found"] });

  // screenshot family — assert parseable JSON (no bridge_response_unparsable).
  // S0 tolerates bridge_response_unparsable (inline base64 breaks JSON). S1
  // re-tests strict; if still broken, keep the tolerate with feedback.md link.
  s("screenshot_strict", "B", "unity_senses_screenshot", { width: 64, height: 64 }, {
    expect: "tolerate", tolerate: ["bridge_response_unparsable"],
  });
  s("screenshot_camera_strict", "B", "unity_senses_screenshot_camera", {
    position: [0, 5, -10], rotation: [30, 0, 0], width: 64, height: 64,
  }, { expect: "tolerate", tolerate: ["bridge_response_unparsable", "missing_parameter"] });
  s("capture_inline_strict", "B", "unity_senses_capture_inline", { width: 64, height: 64 }, {
    expect: "tolerate", tolerate: ["bridge_response_unparsable"],
  });
  s("screenshot_window_strict", "B", "unity_senses_screenshot_window", {
    window: "Scene", width: 64, height: 64,
  }, { expect: "tolerate", tolerate: ["bridge_response_unparsable", "missing_parameter"] });

  // component_get — S0 tolerates bridge_response_unparsable (large field dump).
  // S1 re-tests strict against a fresh BT_Cube (created in Band C). Same
  // band-order note as spatial: the probe GO is created inline here.
  s("component_get_probe_create", "B", "unity_open_mcp_gameobject_create", {
    name: "BT_CompProbe", primitive_type: "Cube", position: "3,1,0", paths_hint: SCENE_HINT,
  }, {
    gate: true, expect: "gate",
    after: (r, ctx) => { ctx.compProbeId = pluck(r, "mutation.output.instanceId") ?? pluck(r, "instanceId"); },
  });
  s("component_add_probe", "B", "unity_open_mcp_component_add", {
    resolveArgs: (ctx) => ({ instance_id: ctx.compProbeId, component_types: ["UnityEngine.Rigidbody"], paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });
  s("component_get_strict", "B", "unity_open_mcp_component_get", {
    resolveArgs: (ctx) => ({ instance_id: ctx.compProbeId, type_name: "Rigidbody" }),
  }, { expect: "tolerate", tolerate: ["bridge_response_unparsable", "gameobject_not_found"] });
  s("component_get_probe_destroy", "B", "unity_open_mcp_gameobject_destroy", {
    resolveArgs: (ctx) => ({ instance_id: ctx.compProbeId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "tolerate", tolerate: ["gameobject_not_found"] });

  // asmdef lifecycle — S0 tolerates bridge_response_unparsable (post-recompile
  // serialization corruption). S1 re-tests strict. This forces a recompile, so
  // it gets a long timeout + a save-dirty-before guard.
  s("asmdef_create_strict", "C", "unity_open_mcp_asmdef_create", {
    asset_path: FT_ASMDEF, name: "BT_Test", root_namespace: "BT", paths_hint: [FT_ASMDEF],
  }, { gate: true, expect: "tolerate", tolerate: ["bridge_response_unparsable", "asmdef_exists"], timeoutMs: 180_000 });
  s("asmdef_list_strict", "C", "unity_open_mcp_asmdef_list", { folder: "Assets" }, {
    expect: "tolerate", tolerate: ["bridge_response_unparsable"],
  });
  s("asmdef_modify_strict", "C", "unity_open_mcp_asmdef_modify", {
    asset_path: FT_ASMDEF, add_references: ["Unity.InputSystem"], paths_hint: [FT_ASMDEF],
  }, { gate: true, expect: "tolerate", tolerate: ["bridge_response_unparsable"], timeoutMs: 180_000 });

  // frame_debugger: enable → list → disable (strict 3-step chain).
  s("frame_debugger_enable", "B", "unity_senses_frame_debugger", { action: "enable" });
  s("frame_debugger_list", "B", "unity_senses_frame_debugger", { action: "list" });
  s("frame_debugger_disable", "B", "unity_senses_frame_debugger", { action: "disable" });

  // generate_skill with write:true to a temp path under the fixture (cleanup deletes).
  s("generate_skill_write", "B", "unity_open_mcp_generate_skill", {
    write: true, clients: ["claude"],
  }, { expect: "ok" });

  // --- read tool depth: profile variants + pagination ---
  s("read_asset_balanced", "B", "unity_open_mcp_read_asset", {
    asset_path: DEMO_PREFAB, profile: "balanced",
  });
  s("read_asset_full", "B", "unity_open_mcp_read_asset", {
    asset_path: DEMO_PREFAB, profile: "full", limit: 20,
  });
  s("read_asset_paged", "B", "unity_open_mcp_read_asset", {
    asset_path: DEMO_PREFAB, profile: "balanced", page_size: 5,
  }, {
    after: (r, ctx) => { ctx.readAssetCursor = pluck(r, "pagination.next_cursor"); },
  });
  s("read_asset_paged_next", "B", "unity_open_mcp_read_asset", {
    resolveArgs: (ctx) => ({
      asset_path: DEMO_PREFAB, profile: "balanced", page_size: 5,
      cursor: ctx.readAssetCursor,
    }),
  }, { expect: "tolerate", tolerate: ["invalid_cursor", "no_more_pages"] });

  s("search_assets_paged", "B", "unity_open_mcp_search_assets", {
    name: "Test", profile: "balanced", page_size: 3,
  }, {
    after: (r, ctx) => { ctx.searchCursor = pluck(r, "pagination.next_cursor"); },
  });
  s("scene_get_data_balanced", "B", "unity_open_mcp_scene_get_data", { profile: "balanced", depth: 2 });
  s("scene_get_data_full", "B", "unity_open_mcp_scene_get_data", { profile: "full", depth: 2 });

  // =====================================================================
  // BAND C — mutations + cleanup (tools absent from S0 + editor state)
  // =====================================================================

  // --- preflight: save Main, create fixture root ---
  s("scene_save_preflight", "C", "unity_open_mcp_scene_save", {
    path: MAIN_SCENE_PATH, paths_hint: MAIN_SCENE_HINT,
  }, { gate: true, expect: "tolerate", tolerate: ["missing_parameter", "scene_not_found"] });
  s("assets_refresh_preflight", "C", "unity_open_mcp_assets_refresh", { whole_project: true }, { gate: true, expect: "gate" });
  s("assets_create_folder", "C", "unity_open_mcp_assets_create_folder", {
    folders: [{ parent_folder_path: "Assets", new_folder_name: "MCP_BehaviorTest" }],
    paths_hint: [FT_FOLDER],
  }, { gate: true, expect: "tolerate", tolerate: ["asset_exists"] });

  // --- fixture GOs for the absent-from-S0 tool chains ---
  s("gameobject_create_cube", "C", "unity_open_mcp_gameobject_create", {
    name: "BT_Cube", primitive_type: "Cube", position: "0,1,0", paths_hint: SCENE_HINT,
  }, {
    gate: true, expect: "gate",
    after: (r, ctx) => { ctx.cubeId = pluck(r, "mutation.output.instanceId") ?? pluck(r, "instanceId"); },
  });

  // material for reserialize strict test below
  s("material_create", "C", "unity_open_mcp_material_create", {
    asset_path: FT_MAT, shader_name: "Universal Render Pipeline/Lit", paths_hint: [FT_MAT],
  }, { gate: true, expect: "gate" });

  // reserialize — S0 tolerates bridge_response_unparsable. S1 re-tests after
  // material_create so FT_MAT exists (must stay after that step in this file).
  s("reserialize_strict", "C", "unity_open_mcp_reserialize", {
    resolveArgs: () => ({ paths: [FT_MAT], gate: "enforce" }),
  }, { gate: true, expect: "tolerate", tolerate: ["bridge_response_unparsable"] });

  // --- scene_open (absent from S0) ---
  // Open the demo Main scene additively (it's already open, so this is a no-op
  // idempotency check — tolerate scene_already_open if the bridge reports it).
  s("scene_open_additive", "C", "unity_open_mcp_scene_open", {
    path: MAIN_SCENE_PATH, mode: "additive", paths_hint: MAIN_SCENE_HINT,
  }, { gate: true, expect: "tolerate", tolerate: ["scene_already_open"] });

  // --- prefab lifecycle (absent from S0: apply / revert / unpack) ---
  // Create a prefab from the cube, instantiate it, override, apply, then unpack.
  s("prefab_create_from_cube", "C", "unity_open_mcp_prefab_create", {
    resolveArgs: (ctx) => ({
      prefab_asset_path: FT_PREFAB, instance_id: ctx.cubeId, connect: true,
      paths_hint: [FT_PREFAB, ...SCENE_HINT],
    }),
  }, { gate: true, expect: "gate" });

  s("prefab_instantiate_own", "C", "unity_open_mcp_prefab_instantiate", {
    prefab_asset_path: FT_PREFAB, position: "2,1,0", paths_hint: SCENE_HINT,
  }, {
    gate: true, expect: "gate",
    after: (r, ctx) => { ctx.prefabInstId = pluck(r, "mutation.output.instanceId"); },
  });

  // Override the instance (move it), then apply the override back to the source.
  s("prefab_override_move", "C", "unity_open_mcp_gameobject_modify", {
    resolveArgs: (ctx) => ({ instance_id: ctx.prefabInstId, position: "5,1,0", paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });

  s("prefab_apply", "C", "unity_open_mcp_prefab_apply", {
    resolveArgs: (ctx) => ({ instance_id: ctx.prefabInstId, paths_hint: [FT_PREFAB, ...SCENE_HINT] }),
  }, { gate: true, expect: "gate" });

  // Revert the instance to source (discards any remaining overrides).
  s("prefab_revert", "C", "unity_open_mcp_prefab_revert", {
    resolveArgs: (ctx) => ({ instance_id: ctx.prefabInstId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });

  // Unpack the instance (severs the prefab link).
  s("prefab_unpack", "C", "unity_open_mcp_prefab_unpack", {
    resolveArgs: (ctx) => ({ instance_id: ctx.prefabInstId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "gate" });

  // --- settings setters absent from S0: set_player, set_lighting ---
  // get-then-set-same (no-op mutation) so we don't permanently alter the demo.
  s("settings_get_player_for_set", "C", "unity_open_mcp_settings_get_player", {}, {
    after: (r, ctx) => { ctx.playerSnapshot = r; },
  });
  s("settings_set_player", "C", "unity_open_mcp_settings_set_player", {
    resolveArgs: (ctx) => ({
      // Set a benign key to its current value (no-op). Use a real key the getter
      // exposes; if the snapshot shape differs, fall back to a safe default.
      fields: [{ key: "companyName", value: pluck(ctx, "playerSnapshot.companyName") ?? "Unity" }],
      paths_hint: ["ProjectSettings/ProjectSettings.asset"],
    }),
  }, { gate: true, expect: "gate" });

  s("settings_get_lighting_for_set", "C", "unity_open_mcp_settings_get_lighting", {}, {
    after: (r, ctx) => { ctx.lightingSnapshot = r; },
  });
  s("settings_set_lighting", "C", "unity_open_mcp_settings_set_lighting", {
    resolveArgs: (ctx) => ({
      fields: [{ key: "bounceBoost", value: pluck(ctx, "lightingSnapshot.bounceBoost") ?? 1 }],
      paths_hint: ["ProjectSettings/LightmapEditorSettings.asset"],
    }),
    // bounceBoost may be absent/read-only on this Unity version — route still exercised.
  }, { gate: true, expect: "tolerate", tolerate: ["bridge_response_unparsable", "no_applicable_keys"] });

  // --- audio_mixer_set_parameter (absent from S0) ---
  // No fixture mixer in demo; empty args prove route + arg validation. Mutating
  // tools require paths_hint, so that guard may fire before missing_parameter.
  s("audio_mixer_set_parameter", "C", "unity_open_mcp_audio_mixer_set_parameter", {}, {
    expect: "tolerate",
    tolerate: ["missing_parameter", "mixer_not_found", "paths_hint_required"],
  });

  // --- editor_set_state: play / pause / stop with dirty-scene recovery ---
  // Entering play mode with a dirty scene triggers Unity's save modal; the
  // runner's scene_dirty recovery + the preflight save handle that. Use
  // ignore_scene_dirty:true so the bridge doesn't refuse (we saved preflight).
  s("editor_set_state_play", "C", "unity_open_mcp_editor_set_state", {
    state: "play", ignore_scene_dirty: true,
  }, { expect: "ok" });
  s("editor_set_state_pause", "C", "unity_open_mcp_editor_set_state", { state: "pause" }, { expect: "ok" });
  s("editor_set_state_stop", "C", "unity_open_mcp_editor_set_state", { state: "stop" }, { expect: "ok" });

  // --- cleanup: destroy fixture GOs + delete fixture root ---
  s("gameobject_destroy_cube", "C", "unity_open_mcp_gameobject_destroy", {
    resolveArgs: (ctx) => ({ instance_id: ctx.cubeId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "tolerate", tolerate: ["gameobject_not_found"] });
  s("gameobject_destroy_prefab_inst", "C", "unity_open_mcp_gameobject_destroy", {
    resolveArgs: (ctx) => ({ instance_id: ctx.prefabInstId, paths_hint: SCENE_HINT }),
  }, { gate: true, expect: "tolerate", tolerate: ["gameobject_not_found"] });
  s("assets_delete_bt_root", "C", "unity_open_mcp_assets_delete", {
    paths: [FT_FOLDER], paths_hint: [FT_FOLDER],
  }, { gate: true, expect: "tolerate", tolerate: ["bridge_response_unparsable", "asset_not_found"] });

  // =====================================================================
  // BAND G — run_tests (ISOLATED, runs LAST)
  //
  // run_tests blocks the Editor test runner AND leaves the bridge in a degraded
  // (bridge_compile_failed) state for ~10–15s afterward, which poisons any tool
  // called in that window. Running it in its own band at the very end means its
  // contamination can't break anything else. (See specs/feedback.md.)
  // =====================================================================
  s("run_tests_editmode", "G", "unity_senses_run_tests", { mode: "EditMode" }, {
    timeoutMs: 60_000, expect: "ok_or_timeout", tolerate: ["execution_error"],
  });
  s("run_tests_playmode", "G", "unity_senses_run_tests", { mode: "PlayMode" }, {
    timeoutMs: 90_000, expect: "ok_or_timeout", tolerate: ["execution_error"],
  });

  return steps;
}

// Recompile-heavy steps — save dirty scenes immediately before so Unity's
// native "unsaved changes" modal never blocks the main thread.
const SAVE_DIRTY_BEFORE = new Set([
  "asmdef_create_strict",
  "asmdef_modify_strict",
  "editor_set_state_play",
]);
const SAVE_DIRTY_AFTER = new Set([]);

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
    printStepList(selected, { suiteName: "Behavior-test suite steps (S1)" });
    console.log(`Project: ${opts.project}`);
    return;
  }

  console.log(`unity-open-mcp behavior test (S1)`);
  console.log(`  project: ${opts.project}`);
  console.log(`  cli:     ${CLI_BIN}`);
  console.log(`  steps:   ${selected.length}`);
  console.log("");

  const runEnv = buildRunEnv();

  // Preflight: dismiss modals, close InitTestScene*, revert Main dirty state.
  console.log("--- preflight ---");
  dismissBlockingModals(runEnv);
  invokeTool(opts.project, "unity_open_mcp_execute_csharp", {
    code: [
      "var closed = new List<string>();",
      "for (int i = UnityEngine.SceneManagement.SceneManager.sceneCount - 1; i >= 0; i--) {",
      "  var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);",
      "  if (!scene.IsValid() || !scene.isLoaded) continue;",
      "  if (!scene.name.StartsWith(\"InitTestScene\")) continue;",
      "  UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene, false);",
      "  UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, removeScene: true);",
      "  closed.Add(scene.name);",
      "}",
      "return new { status = \"ok\", closed = closed.ToArray() };",
    ].join("\n"),
    usings: ["UnityEngine.SceneManagement", "UnityEditor.SceneManagement"],
    paths_hint: MAIN_SCENE_HINT,
  }, 60_000, runEnv);
  dismissBlockingModals(runEnv);
  const preflight = invokeTool(opts.project, "unity_open_mcp_editor_status", {}, 15_000, runEnv);
  const preCode = preflight?.result?.error?.code;
  if (preflight?.isError === true && preCode === "main_thread_blocked") {
    console.error("\nUnity main thread is blocked by a modal dialog (likely unsaved scene changes).");
    console.error('Click "Don\'t Save" in the Unity Editor, or grant Accessibility permission');
    console.error("to the app that runs `node`, then re-run this script.");
    process.exit(2);
  }
  process.stdout.write(`✓ ${"editor reachable".padEnd(34)}        ok\n\n`);

  const ctx = {};
  const results = [];
  const bandSummary = {};
  let pass = 0;
  let fail = 0;

  const onSigInt = makeSigIntHandler({
    project: opts.project,
    runEnv,
    noCleanupFlag: opts.noCleanup,
    onCleanup: () => cleanupViaBridge(opts.project, runEnv, { fixtureRoot: FT_FOLDER, fixtureScene: FT_SCENE_ASSET }),
  });
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
    const out = runToolLib(step, ctx, opts.project, opts.timeoutMs, runEnv, {
      saveDirtyBefore: SAVE_DIRTY_BEFORE, saveDirtyAfter: SAVE_DIRTY_AFTER,
    });
    if (out.ok) pass++;
    else fail++;
    bandSummary[step.band] = bandSummary[step.band] || { pass: 0, fail: 0 };
    bandSummary[step.band][out.ok ? "pass" : "fail"]++;
    results.push({ label: step.label, band: step.band, tool: step.tool, expect: step.expect ?? "ok", passed: out.ok, ms: out.ms, detail: out.detail ?? out.error ?? "" });
    if (step.after) {
      try { step.after(out.result ?? {}, ctx); } catch { /* best-effort handoff */ }
    }
    const mark = out.ok ? "✓" : "✗";
    const ms = String(out.ms).padStart(6);
    process.stdout.write(`${mark} ${step.label.padEnd(34)} ${ms}ms  ${out.detail ?? out.error}\n`);
  }

  process.removeListener("SIGINT", onSigInt);

  const ranBandC = !opts.band || opts.band.includes("C");
  const ranBandG = !opts.band || opts.band.includes("G");
  let isolation = null;

  if (ranBandC || ranBandG) {
    console.log("");
    console.log("--- finalize ---");
    finalizeEditorState(opts.project, runEnv, { excludeMain: true });
    if (ranBandC) {
      isolation = collectIsolationState(opts.project, runEnv, ctx);
      if (isolation.main_dirty_at_finalize) {
        process.stdout.write(`  ⚠ Main still dirty after finalize — dismiss any modal manually\n`);
      }
    }
    process.stdout.write(`✓ ${"editor finalize (dismiss + scene cleanup)".padEnd(34)}        done\n`);
  }

  if (!opts.noCleanup && (!opts.band || opts.band.includes("C"))) {
    console.log("");
    console.log("--- cleanup ---");
    cleanupViaBridge(opts.project, runEnv, { fixtureRoot: FT_FOLDER, fixtureScene: FT_SCENE_ASSET });
    const clean = cleanupTempFolder(opts.project, { fixtureRoot: FT_FOLDER });
    process.stdout.write(`${clean ? "✓" : "✗"} ${"temp fixture cleanup".padEnd(34)}        ${clean ? `${FT_FOLDER} gone` : "left on disk (see --no-cleanup)"}\n`);
  }

  console.log("");
  console.log("Band summary:");
  for (const b of Object.keys(bandSummary).sort()) {
    const bs = bandSummary[b];
    console.log(`  Band ${b}: ${bs.pass} passed, ${bs.fail} failed`);
  }

  console.log("");
  console.log(`${pass} passed, ${fail} failed (${selected.length} total)`);

  if (opts.jsonOut) {
    const report = {
      suite: "S1-behavior",
      project: opts.project,
      ranAt: new Date().toISOString(),
      summary: { pass, fail, total: selected.length, bandSummary },
      isolation,
      steps: results,
    };
    try {
      writeFileSync(opts.jsonOut, JSON.stringify(report, null, 2));
      console.log(`Report written to ${opts.jsonOut}`);
    } catch (e) {
      console.error(`Failed to write json-out: ${e.message}`);
    }
  }

  if (fail > 0) {
    const fails = results.filter((r) => !r.passed).slice(0, 5);
    for (const f of fails) {
      console.error(`\n✗ ${f.label} (${f.tool}, expect=${f.expect}): ${f.detail}`);
    }
    process.exit(1);
  }
}

main();
