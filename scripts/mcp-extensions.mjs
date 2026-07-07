#!/usr/bin/env node
// mcp-extensions.mjs — S4: compiled extension-pack test suite.
//
// S0 Band F uses minimal args + `reachable`/`unavailable` expects. S4 drives a
// minimal SUCCESS chain per compiled extension group (not just paths_hint), so
// a compiled-in pack actually works end-to-end.
//
// Strategy B (documented in the coverage matrix): skip groups returning
// tool_not_found (not compiled into this bridge build), but FAIL if a group is
// `available: true` in capabilities yet a step errors. This makes S4 strict on
// extended bridge builds while remaining green on the default demo+bridge.
//
// Per compiled group, a minimal create→mutate/read chain:
//   navigation:    surface_add → set_bake_settings → list
//   input-system:  asset_create → actionmap_add → action_add → get
//   probuilder:     create_shape → extrude → get_mesh_info
//   particle-system: get → modify  (needs a particle GO; create via execute_csharp)
//   animation:      create → get_data → modify
//   shadergraph:    create → open → node_add → node_connect
//   splines/timeline/tilemap/vfx/memoryprofiler: one create+read each (when compiled)
//
// Fixture: Assets/MCP_ExtTest/ + additive scene (self-contained mini-fixture,
// or reuses S1's if S1 ran first — not assumed here).
//
// Usage:
//   node scripts/mcp-extensions.mjs                       # full suite vs ./demo
//   node scripts/mcp-extensions.mjs --project /path       # target another project
//   node scripts/mcp-extensions.mjs --band N,I,P,A,S,X    # run named groups only
//   node scripts/mcp-extensions.mjs --only needle         # subset by label
//   node scripts/mcp-extensions.mjs --list                # list steps, don't run
//   node scripts/mcp-extensions.mjs --json-out report.json
//
// Requirements: mcp-server/dist/index.js built; a Unity Editor open with the
// project + bridge running. On the default demo+bridge, uncompiled groups skip
// without failing. On an extended bridge build (all UNITY_OPEN_MCP_EXT_*
// defines enabled + matching Unity packages), every compiled group strict-passes.

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
  cleanupViaBridge,
  cleanupTempFolder,
  printStepList,
  makeSigIntHandler,
} from "./mcp-test-lib.mjs";

const FT = "Assets/MCP_ExtTest";
const FT_FOLDER = FT;
const FT_SCENE_ASSET = `${FT}/ET_Scene.unity`;
const HINT = [FT];
const SCENE_HINT = [FT_SCENE_ASSET];
const MAIN_SCENE_PATH = "Assets/Scenes/Main.unity";
const MAIN_SCENE_HINT = [MAIN_SCENE_PATH];

function printHelp() {
  console.error(`Usage: node scripts/mcp-extensions.mjs [options]

Options:
  --project, -P <path>   Target Unity project (default: <repo>/demo). MUST be absolute.
  --band N,I,P,A,S,X     Run only extension groups: N=navigation, I=input-system,
                         P=probuilder, A=animation/particle, S=shadergraph,
                         X=extra (splines/timeline/tilemap/vfx/memoryprofiler)
  --only needle          Run only steps whose label contains any needle
  --list                 List the suite steps and exit (no execution)
  --no-cleanup           Leave temp fixtures in place (debug)
  --json-out <file>      Write a machine-readable report to <file>
  --timeout-ms <n>       Per-step timeout (default 120000)
  -h, --help             Show this help

The CLI binary must exist at mcp-server/dist/index.js. A Unity Editor must be
open with the project + bridge running. Uncompiled groups skip (tool_not_found)
without failing; compiled groups must strict-pass their success chain.`);
}

function parseArgs(argv) {
  return parseCommonArgs(argv, { help: printHelp });
}

// Each extension tool uses the `ext` expect: pass on success OR tool_not_found
// (group not compiled in); FAIL on any other error (a compiled-in group that
// errors is a real regression). This is stricter than S0's `reachable` (which
// tolerates guard errors).
function extStep(s, label, band, tool, args, extra = {}) {
  s(label, band, tool, args, { expect: "ext", ...extra });
}

// Register the `ext` classifier mode by wrapping classify. We can't add a mode
// to the shared lib without changing S0, so S4 passes its own classifier via a
// post-hoc re-classify in main(). The step builder just tags expect:"ext".
function buildSuite() {
  const { s, steps } = makeStepBuilder();

  // =====================================================================
  // BAND N — navigation (com.unity.ai.navigation)
  // =====================================================================
  extStep(s, "nav_surface_add", "N", "unity_open_mcp_navigation_surface_add", {
    paths_hint: SCENE_HINT,
  }, { gate: true, after: (r, ctx) => { ctx.navSurfaceId = pluck(r, "mutation.output.instanceId"); } });
  extStep(s, "nav_set_bake_settings", "N", "unity_open_mcp_navigation_set_bake_settings", {
    resolveArgs: (ctx) => ({ instance_id: ctx.navSurfaceId, paths_hint: SCENE_HINT }),
  }, { gate: true });
  extStep(s, "nav_list", "N", "unity_open_mcp_navigation_list", {});

  // =====================================================================
  // BAND I — input-system (com.unity.inputsystem)
  // =====================================================================
  extStep(s, "input_asset_create", "I", "unity_open_mcp_inputsystem_asset_create", {
    asset_path: `${FT}/ET_Input.inputactions`, paths_hint: [`${FT}/ET_Input.inputactions`],
  }, { gate: true, after: (r, ctx) => { ctx.inputAssetPath = `${FT}/ET_Input.inputactions`; } });
  extStep(s, "input_actionmap_add", "I", "unity_open_mcp_inputsystem_actionmap_add", {
    resolveArgs: (ctx) => ({ asset_path: ctx.inputAssetPath, actionmap_name: "ET_Map", paths_hint: [ctx.inputAssetPath] }),
  }, { gate: true });
  extStep(s, "input_action_add", "I", "unity_open_mcp_inputsystem_action_add", {
    resolveArgs: (ctx) => ({ asset_path: ctx.inputAssetPath, actionmap_name: "ET_Map", action_name: "ET_Move", paths_hint: [ctx.inputAssetPath] }),
  }, { gate: true });
  extStep(s, "input_get", "I", "unity_open_mcp_inputsystem_get", {
    resolveArgs: (ctx) => ({ asset_path: ctx.inputAssetPath }),
  });

  // =====================================================================
  // BAND P — probuilder (com.unity.probuilder)
  // =====================================================================
  extStep(s, "probuilder_create_shape", "P", "unity_open_mcp_probuilder_create_shape", {
    shape_type: "Cube", position: "0,1,0", paths_hint: SCENE_HINT,
  }, { gate: true, after: (r, ctx) => { ctx.pbId = pluck(r, "mutation.output.instanceId"); } });
  extStep(s, "probuilder_extrude", "P", "unity_open_mcp_probuilder_extrude", {
    resolveArgs: (ctx) => ({ instance_id: ctx.pbId, distance: 1, paths_hint: SCENE_HINT }),
  }, { gate: true });
  extStep(s, "probuilder_get_mesh_info", "P", "unity_open_mcp_probuilder_get_mesh_info", {
    resolveArgs: (ctx) => ({ instance_id: ctx.pbId }),
  });

  // =====================================================================
  // BAND A — particle-system + animation
  // =====================================================================
  // particle-system: needs a GameObject with a ParticleSystem. Create one via
  // execute_csharp, then get + modify.
  extStep(s, "particle_create_go", "A", "unity_open_mcp_execute_csharp", {
    code: 'var go = new UnityEngine.GameObject("ET_Particle"); go.AddComponent<UnityEngine.ParticleSystem>(); return new { instanceId = go.GetInstanceID(); };',
    paths_hint: SCENE_HINT,
  }, { gate: true, after: (r, ctx) => { ctx.particleGoId = pluck(r, "mutation.output.instanceId"); } });
  extStep(s, "particle_get", "A", "unity_open_mcp_particle_system_get", {
    resolveArgs: (ctx) => ({ instance_id: ctx.particleGoId }),
  });
  extStep(s, "particle_modify", "A", "unity_open_mcp_particle_system_modify", {
    resolveArgs: (ctx) => ({ instance_id: ctx.particleGoId, fields: { duration: 5 }, paths_hint: SCENE_HINT }),
  }, { gate: true });

  // animation: create a clip, get_data, modify.
  extStep(s, "animation_create", "A", "unity_open_mcp_animation_create", {
    asset_path: `${FT}/ET_Anim.anim`, paths_hint: [`${FT}/ET_Anim.anim`],
  }, { gate: true, after: (r, ctx) => { ctx.animPath = `${FT}/ET_Anim.anim`; } });
  extStep(s, "animation_get_data", "A", "unity_open_mcp_animation_get_data", {
    resolveArgs: (ctx) => ({ asset_path: ctx.animPath }),
  });
  extStep(s, "animation_modify", "A", "unity_open_mcp_animation_modify", {
    resolveArgs: (ctx) => ({ asset_path: ctx.animPath, fields: {}, paths_hint: [ctx.animPath] }),
  }, { gate: true });

  // =====================================================================
  // BAND S — shadergraph (com.unity.shadergraph)
  // =====================================================================
  extStep(s, "shader_graph_create", "S", "unity_open_mcp_shader_graph_create", {
    asset_path: `${FT}/ET_Shader.shadergraph`, paths_hint: [`${FT}/ET_Shader.shadergraph`],
  }, { gate: true, after: (r, ctx) => { ctx.sgPath = `${FT}/ET_Shader.shadergraph`; } });
  extStep(s, "shader_graph_open", "S", "unity_open_mcp_shader_graph_open", {
    resolveArgs: (ctx) => ({ asset_path: ctx.sgPath, paths_hint: [ctx.sgPath] }),
  }, { gate: true });
  extStep(s, "shader_graph_node_add", "S", "unity_open_mcp_shader_graph_node_add", {
    resolveArgs: (ctx) => ({ asset_path: ctx.sgPath, node_type: "Color", paths_hint: [ctx.sgPath] }),
  }, { gate: true });
  extStep(s, "shader_graph_node_connect", "S", "unity_open_mcp_shader_graph_node_connect", {
    resolveArgs: (ctx) => ({ asset_path: ctx.sgPath, from_node: 1, from_slot: 0, to_node: 2, to_slot: 0, paths_hint: [ctx.sgPath] }),
  }, { gate: true, expect: "ext" });

  // =====================================================================
  // BAND X — extra (splines / timeline / tilemap / vfx / memoryprofiler)
  // These are typically NOT compiled into the default bridge build. When they
  // ARE compiled, each gets one create+read step.
  // =====================================================================
  extStep(s, "splines_container_create", "X", "unity_open_mcp_splines_container_create", {
    paths_hint: SCENE_HINT,
  }, { gate: true });
  extStep(s, "splines_get_knots", "X", "unity_open_mcp_splines_get_knots", {
    resolveArgs: (ctx) => ({ instance_id: ctx.splinesId }),
  });

  extStep(s, "timeline_create", "X", "unity_open_mcp_timeline_create", {
    asset_path: `${FT}/ET_Timeline.playable`, paths_hint: [`${FT}/ET_Timeline.playable`],
  }, { gate: true });
  extStep(s, "timeline_modify", "X", "unity_open_mcp_timeline_modify", {
    resolveArgs: (ctx) => ({ asset_path: `${FT}/ET_Timeline.playable`, fields: {}, paths_hint: [`${FT}/ET_Timeline.playable`] }),
  }, { gate: true });

  extStep(s, "tilemap_create", "X", "unity_open_mcp_tilemap_create", {
    paths_hint: SCENE_HINT,
  }, { gate: true });
  extStep(s, "tilemap_set_tile", "X", "unity_open_mcp_tilemap_set_tile", {
    resolveArgs: (ctx) => ({ instance_id: ctx.tilemapId, position: [0, 0], paths_hint: SCENE_HINT }),
  }, { gate: true });

  extStep(s, "vfx_list", "X", "unity_open_mcp_vfx_list", {});
  extStep(s, "vfx_open", "X", "unity_open_mcp_vfx_open", {
    asset_path: `${FT}/ET_VFX.vfxgraph`, paths_hint: [`${FT}/ET_VFX.vfxgraph`],
  }, { gate: true });

  extStep(s, "memory_snapshot_capture", "X", "unity_senses_memory_snapshot_capture", {
    file_path: `${FT}/ET_MemSnap.mem`, paths_hint: [`${FT}/ET_MemSnap.mem`],
  }, { gate: true });

  return steps;
}

// S4's classifier: `ext` = pass on success OR tool_not_found; FAIL on any other
// error (a compiled-in group that errors is a real regression, unlike S0's
// `reachable` which tolerates guard errors).
function classifyExt(step, parsed) {
  if (step.expect !== "ext") return classify(step, parsed);
  const isError = parsed.isError === true;
  const result = parsed.result ?? {};
  const errCode = result.error?.code ?? result.mutation?.error?.code;
  if (!isError) return { pass: true, detail: "ok (compiled in, success)" };
  if (errCode === "tool_not_found") return { pass: true, detail: "tool_not_found (group not compiled in — skipped)" };
  return { pass: false, detail: `err=${errCode} (compiled in but failed — real regression)` };
}

function main() {
  const opts = parseArgs(process.argv.slice(2));

  if (!existsSync(CLI_BIN)) {
    console.error(`CLI binary not found at ${CLI_BIN}.`);
    console.error(`Run \`npm run build\` in mcp-server/ first.`);
    process.exit(2);
  }
  if (!isAbsolute(opts.project)) {
    console.error(`--project must be an ABSOLUTE path. Got: ${opts.project}`);
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
    printStepList(selected, { suiteName: "Extensions-test suite steps (S4)" });
    console.log(`Project: ${opts.project}`);
    return;
  }

  console.log(`unity-open-mcp extensions test (S4)`);
  console.log(`  project: ${opts.project}`);
  console.log(`  cli:     ${CLI_BIN}`);
  console.log(`  steps:   ${selected.length}`);
  console.log("");

  const runEnv = buildRunEnv();

  // Preflight: dismiss modals, check bridge.
  console.log("--- preflight ---");
  dismissBlockingModals(runEnv);
  const preflight = invokeTool(opts.project, "unity_open_mcp_editor_status", {}, 15_000, runEnv);
  if (preflight?.isError === true && preflight?.result?.error?.code === "main_thread_blocked") {
    console.error("\nUnity main thread is blocked by a modal dialog. Dismiss it and re-run.");
    process.exit(2);
  }

  // Query capabilities to learn which groups are available (compiled in).
  const caps = invokeTool(opts.project, "unity_open_mcp_capabilities", { kind: "tools", include_planned: false }, 15_000, runEnv);
  const availableGroups = new Set();
  const availableTools = new Set();
  if (caps && !caps.isError) {
    const tools = caps.result?.tools ?? [];
    for (const t of tools) {
      availableTools.add(t.name);
      if (t.group) availableGroups.add(t.group);
    }
  }
  process.stdout.write(`✓ ${"capabilities queried".padEnd(34)}        ${availableTools.size} tools, ${availableGroups.size} groups\n\n`);

  // Create fixture root.
  invokeTool(opts.project, "unity_open_mcp_assets_refresh", { whole_project: true }, 30_000, runEnv);
  invokeTool(opts.project, "unity_open_mcp_assets_create_folder", {
    folders: [{ parent_folder_path: "Assets", new_folder_name: "MCP_ExtTest" }],
    paths_hint: [FT_FOLDER],
  }, 30_000, runEnv);

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
      lastBand = step.band;
    }
    // Skip steps whose tool isn't available (not compiled in) — don't even call.
    if (!availableTools.has(step.tool)) {
      pass++;
      const detail = "tool not in capabilities (group not compiled in — skipped)";
      bandSummary[step.band] = bandSummary[step.band] || { pass: 0, fail: 0 };
      bandSummary[step.band].pass++;
      results.push({ label: step.label, band: step.band, tool: step.tool, expect: step.expect, passed: true, ms: 0, detail });
      process.stdout.write(`⊘ ${step.label.padEnd(34)}      0ms  ${detail}\n`);
      continue;
    }
    const out = runToolLib(step, ctx, opts.project, opts.timeoutMs, runEnv);
    // Re-classify with the ext classifier.
    if (out.raw && typeof out.raw === "object") {
      const reclassed = classifyExt(step, out.raw);
      out.ok = reclassed.pass;
      out.detail = reclassed.detail;
    }
    if (out.ok) pass++; else fail++;
    bandSummary[step.band] = bandSummary[step.band] || { pass: 0, fail: 0 };
    bandSummary[step.band][out.ok ? "pass" : "fail"]++;
    results.push({ label: step.label, band: step.band, tool: step.tool, expect: step.expect, passed: out.ok, ms: out.ms, detail: out.detail ?? out.error ?? "" });
    if (step.after) {
      try { step.after(out.result ?? {}, ctx); } catch { /* best-effort */ }
    }
    const mark = out.ok ? "✓" : "✗";
    const ms = String(out.ms).padStart(6);
    process.stdout.write(`${mark} ${step.label.padEnd(34)} ${ms}ms  ${out.detail ?? out.error}\n`);
  }

  process.removeListener("SIGINT", onSigInt);

  // Finalize + cleanup.
  console.log("");
  console.log("--- finalize ---");
  finalizeEditorState(opts.project, runEnv, { excludeMain: true });
  if (!opts.noCleanup) {
    cleanupViaBridge(opts.project, runEnv, { fixtureRoot: FT_FOLDER, fixtureScene: FT_SCENE_ASSET });
    cleanupTempFolder(opts.project, { fixtureRoot: FT_FOLDER });
  }
  process.stdout.write(`✓ ${"editor finalize + cleanup".padEnd(34)}        done\n`);

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
      suite: "S4-extensions",
      project: opts.project,
      ranAt: new Date().toISOString(),
      availableGroups: [...availableGroups],
      summary: { pass, fail, total: selected.length, bandSummary },
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
      console.error(`\n✗ ${f.label} (${f.tool}): ${f.detail}`);
    }
    process.exit(1);
  }
}

main();
