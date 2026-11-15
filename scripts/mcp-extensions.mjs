#!/usr/bin/env node
// mcp-extensions.mjs — S4: embedded-domain chains.
//
// S0 Band F only proves each domain tool is registered (`reachable` /
// `unavailable`). S4 drives a create → mutate → read-back chain per embedded
// domain and checks that each step did its work: ids are read from the keys the
// tools really answer with, and a read-back step checks the mutation landed.
//
// Expectations follow the target project, not a fixed list. Before the chains
// run, `manage_tools list_groups` says which domain groups the running bridge
// compiled in, and Packages/packages-lock.json says which Unity packages are
// installed:
//   - compiled in            → the chain must pass (strict, with checks)
//   - not compiled, package  → every step must answer the bridge's real
//     not installed            "domain unavailable" answer, tool_not_found
//   - not compiled, package  → the chain still runs and fails: the package is
//     installed                there, so the domain should have compiled
//
// Fixture: Assets/MCP_ExtTest/ with an additive scene ET_Scene.unity that is
// made active before the chains run, so every scene object (hosts, shapes,
// containers) lands there and never in the project's own scenes. The scene is
// unloaded without saving and the folder deleted at the end (also on Ctrl+C).
//
// Bands (chains):
//   N  navigation:      host → surface_add → set_bake_settings → get → list
//   I  input-system:    asset_create → actionmap_add → action_add → binding_add → get
//   P  probuilder:      create_shape → extrude (by direction, by index) → get_mesh_info
//   A  particle-system: execute_csharp host → get → modify → get
//      animation:       clip create → modify → get_data; controller create → modify → get_data
//   S  shadergraph:     create → node_add ×2 → node_connect → open
//   X  splines:         container_create → add_knot ×2 → get_knots → evaluate
//      timeline:        create → track_add → clip_add → modify
//      tilemap:         create → create_tile_asset → set_tile
//      vfx:             list → open (no VFX asset fixture: open must refuse asset_not_found)
//      memoryprofiler:  snapshot_capture (temp path, outside the project)
// Band F (fixture setup) always runs, whatever --band / --only select.
//
// Usage:
//   node scripts/mcp-extensions.mjs                       # full suite vs ./demo
//   node scripts/mcp-extensions.mjs --project /path       # target another project
//   node scripts/mcp-extensions.mjs --band N,I,P,A,S,X    # run named bands only
//   node scripts/mcp-extensions.mjs --only needle         # subset by label
//   node scripts/mcp-extensions.mjs --list                # list steps, don't run
//   node scripts/mcp-extensions.mjs --check-args          # validate step args vs schemas, no Unity
//   node scripts/mcp-extensions.mjs --json-out report.json
//
// Requirements: mcp-server/dist/index.js built; a Unity Editor open with the
// project + bridge running and idle. Do not start S4 while the Editor is still
// recovering from S1's PlayMode test run; the preflight waits for an idle
// Editor, but a bridge that is down fails the run.

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { isAbsolute, resolve } from "node:path";

import {
  CLI_BIN,
  parseCommonArgs,
  makeStepBuilder,
  buildRunEnv,
  dismissBlockingModals,
  invokeTool,
  runTool as runToolLib,
  finalizeEditorState,
  cleanupViaBridge,
  cleanupTempFolder,
  printStepList,
  makeSigIntHandler,
  waitForEditorSettled,
  checkStepArgs,
  checkRefreshResult,
  toolOutput,
  PLACEHOLDER_CTX,
} from "./mcp-test-lib.mjs";

const FT = "Assets/MCP_ExtTest";
const FT_SCENE = `${FT}/ET_Scene.unity`;
const SCENE_HINT = [FT_SCENE];
const INPUT_ASSET = `${FT}/ET_Input.inputactions`;
const ANIM_CLIP = `${FT}/ET_Anim.anim`;
const ANIM_CONTROLLER = `${FT}/ET_Animator.controller`;
const SHADER_GRAPH = `${FT}/ET_Shader.shadergraph`;
const TIMELINE = `${FT}/ET_Timeline.playable`;
const TILE_ASSET = `${FT}/ET_Tile.asset`;
const VFX_ASSET = `${FT}/ET_Missing.vfx`;

function printHelp() {
  console.error(`Usage: node scripts/mcp-extensions.mjs [options]

Options:
  --project, -P <path>   Target Unity project (default: <repo>/demo). MUST be absolute.
  --band N,I,P,A,S,X     Run only these chains: N=navigation, I=input-system,
                         P=probuilder, A=particle-system + animation,
                         S=shadergraph, X=splines/timeline/tilemap/vfx/memoryprofiler
                         (the fixture band F always runs)
  --only needle          Run only steps whose label contains any needle
  --list                 List the suite steps and exit (no execution)
  --check-args           Validate every step's arguments against the built
                         tool schemas and exit (no Unity needed)
  --no-cleanup           Leave the fixture folder and scene in place (debug)
  --json-out <file>      Write a machine-readable report to <file>
  --timeout-ms <n>       Per-step timeout (default 120000)
  -h, --help             Show this help

A domain group the bridge compiled in must pass its chain; a group whose Unity
package is not installed must answer tool_not_found on every step.`);
}

function parseArgs(argv) {
  return parseCommonArgs(argv, {
    help: printHelp,
    defaults: { extra: { checkArgs: false } },
    extra: (a, opts, _argv, i) => {
      if (a === "--check-args") { opts.checkArgs = true; return i; }
      console.error(`Unknown argument: ${a}`);
      process.exit(2);
    },
  });
}

// A check that fails with `message` unless `predicate(output)` holds.
function expectOut(predicate, message) {
  return (r, ctx) => {
    const o = toolOutput(r);
    let ok = false;
    try { ok = !!predicate(o ?? {}, ctx); } catch { ok = false; }
    return ok ? null : `${message} (got ${JSON.stringify(o ?? null).slice(0, 240)})`;
  };
}

function buildSuite() {
  const { s, steps } = makeStepBuilder();

  // =====================================================================
  // BAND F — fixture: folder + additive scene, made active
  // =====================================================================
  s("fixture_refresh", "F", "unity_open_mcp_assets_refresh", { whole_project: true }, { gate: true, check: checkRefreshResult });
  s("fixture_folder", "F", "unity_open_mcp_assets_create_folder", {
    folders: [{ parent_folder_path: "Assets", new_folder_name: "MCP_ExtTest" }],
    paths_hint: [FT],
  }, { gate: true, check: expectOut((o) => o.created?.includes(FT), `${FT} was not created`) });
  s("fixture_scene", "F", "unity_open_mcp_scene_create", {
    asset_path: FT_SCENE, setup: "empty", mode: "additive", paths_hint: SCENE_HINT,
  }, {
    gate: true,
    check: expectOut((o) => o.scene?.path === FT_SCENE && o.scene?.isActive === true,
      "the fixture scene is not the active scene, so chain objects would land in a project scene"),
  });

  // =====================================================================
  // BAND N — navigation (com.unity.ai.navigation)
  // =====================================================================
  const nav = { domain: "navigation" };
  s("nav_host_create", "N", "unity_open_mcp_gameobject_create", {
    name: "ET_NavHost", primitive_type: "Plane", paths_hint: SCENE_HINT,
  }, {
    ...nav, gate: true,
    check: expectOut((o) => o.instanceId && o.scene === FT_SCENE, "no host id in the fixture scene"),
    after: (r, ctx) => { ctx.navHostId = toolOutput(r)?.instanceId; },
  });
  s("nav_surface_add", "N", "unity_open_mcp_navigation_surface_add", {
    resolveArgs: (ctx) => ({ instance_id: ctx.navHostId, paths_hint: SCENE_HINT }),
  }, { ...nav, gate: true, check: expectOut((o) => o.surface?.added === true, "surface.added is not true") });
  s("nav_set_bake_settings", "N", "unity_open_mcp_navigation_set_bake_settings", {
    resolveArgs: (ctx) => ({ instance_id: ctx.navHostId, collect_objects: "Volume", collection_extent: "5,2,5", paths_hint: SCENE_HINT }),
  }, { ...nav, gate: true, check: expectOut((o) => o.collectObjects === "Volume", "collectObjects is not Volume") });
  s("nav_get", "N", "unity_open_mcp_navigation_get", {
    resolveArgs: (ctx) => ({ instance_id: ctx.navHostId }),
  }, {
    ...nav,
    check: expectOut((o) => o.target?.components?.some((c) => c.type === "NavMeshSurface" && c.collectObjects === "Volume"),
      "the host's NavMeshSurface does not read back with collectObjects Volume"),
  });
  s("nav_list", "N", "unity_open_mcp_navigation_list", {}, {
    ...nav, check: expectOut((o) => o.components?.surfaces?.some((x) => x.name === "ET_NavHost"), "ET_NavHost is not listed"),
  });

  // =====================================================================
  // BAND I — input-system (com.unity.inputsystem)
  // =====================================================================
  const input = { domain: "input-system", gate: true };
  s("input_asset_create", "I", "unity_open_mcp_inputsystem_asset_create", {
    asset_path: INPUT_ASSET, paths_hint: [INPUT_ASSET],
  }, { ...input, check: expectOut((o) => o.assetPath === INPUT_ASSET, "assetPath differs") });
  s("input_actionmap_add", "I", "unity_open_mcp_inputsystem_actionmap_add", {
    asset_path: INPUT_ASSET, map_name: "ET_Map", paths_hint: [INPUT_ASSET],
  }, { ...input, check: expectOut((o) => o.actionMapCount === 1, "actionMapCount is not 1") });
  s("input_action_add", "I", "unity_open_mcp_inputsystem_action_add", {
    asset_path: INPUT_ASSET, map_name: "ET_Map", action_name: "ET_Jump", action_type: "Button", paths_hint: [INPUT_ASSET],
  }, { ...input, check: expectOut((o) => o.actionName === "ET_Jump", "action not added") });
  s("input_binding_add", "I", "unity_open_mcp_inputsystem_binding_add", {
    asset_path: INPUT_ASSET, map_name: "ET_Map", action_name: "ET_Jump", path: "<Keyboard>/space", paths_hint: [INPUT_ASSET],
  }, { ...input, check: expectOut((o) => o.bindingCount === 1, "bindingCount is not 1") });
  s("input_get", "I", "unity_open_mcp_inputsystem_get", { asset_path: INPUT_ASSET }, {
    domain: "input-system",
    check: expectOut((o) => o.actionMaps?.[0]?.actions?.[0]?.bindings?.[0]?.path === "<Keyboard>/space",
      "the binding does not read back"),
  });

  // =====================================================================
  // BAND P — probuilder (com.unity.probuilder)
  // =====================================================================
  const pb = { domain: "probuilder" };
  s("probuilder_create_shape", "P", "unity_open_mcp_probuilder_create_shape", {
    shape_type: "Cube", name: "ET_PbCube", position: "3,1,0", paths_hint: SCENE_HINT,
  }, {
    ...pb, gate: true,
    check: expectOut((o) => o.shape?.instanceId && o.shape.faceCount === 6, "no shape id / 6 faces"),
    after: (r, ctx) => { ctx.pbId = toolOutput(r)?.shape?.instanceId; },
  });
  s("probuilder_extrude_direction", "P", "unity_open_mcp_probuilder_extrude", {
    resolveArgs: (ctx) => ({ instance_id: ctx.pbId, face_direction: "Up", distance: 0.5, paths_hint: SCENE_HINT }),
  }, { ...pb, gate: true, check: expectOut((o) => o.extrude?.totalFaceCount === 10, "totalFaceCount is not 10") });
  s("probuilder_extrude_indices", "P", "unity_open_mcp_probuilder_extrude", {
    resolveArgs: (ctx) => ({ instance_id: ctx.pbId, face_indices: [0], distance: 0.25, paths_hint: SCENE_HINT }),
  }, {
    ...pb, gate: true,
    check: expectOut((o) => o.extrude?.selectionMethod === "by index" && o.extrude?.totalFaceCount === 14,
      "face_indices extrude did not run by index to 14 faces"),
  });
  s("probuilder_get_mesh_info", "P", "unity_open_mcp_probuilder_get_mesh_info", {
    resolveArgs: (ctx) => ({ instance_id: ctx.pbId }),
  }, { ...pb, check: expectOut((o) => o.mesh?.faceCount === 14 && o.mesh?.faceDirections?.up, "mesh does not read back with 14 faces") });

  // =====================================================================
  // BAND A — particle-system (built-in module) + animation (built-in module)
  // =====================================================================
  const ps = { domain: "particle-system" };
  // The host comes from a snippet on purpose: S4 also covers execute_csharp
  // returning a scene object (answered as { objectId, type, name, path }).
  s("particle_create_go", "A", "unity_open_mcp_execute_csharp", {
    code: 'var go = new UnityEngine.GameObject("ET_Particle"); go.AddComponent<UnityEngine.ParticleSystem>(); return go;',
    paths_hint: SCENE_HINT,
  }, {
    ...ps, gate: true,
    check: expectOut((o) => o.objectId && o.name === "ET_Particle", "the snippet did not return the host"),
    after: (r, ctx) => { ctx.particleId = toolOutput(r)?.objectId; },
  });
  s("particle_get", "A", "unity_open_mcp_particle_system_get", {
    resolveArgs: (ctx) => ({ instance_id: ctx.particleId }),
  }, { ...ps, check: expectOut((o) => o.particleSystem?.main?.loop === true, "main.loop does not read back as the default true") });
  s("particle_modify", "A", "unity_open_mcp_particle_system_modify", {
    resolveArgs: (ctx) => ({ instance_id: ctx.particleId, module: "main", fields_json: '{"loop":false}', paths_hint: SCENE_HINT }),
  }, {
    ...ps, gate: true,
    check: expectOut((o) => o.modify?.appliedFields?.includes("loop") && (o.modify?.errors ?? []).length === 0,
      "loop was not applied cleanly"),
  });
  s("particle_get_after", "A", "unity_open_mcp_particle_system_get", {
    resolveArgs: (ctx) => ({ instance_id: ctx.particleId }),
  }, { ...ps, check: expectOut((o) => o.particleSystem?.main?.loop === false, "main.loop did not change") });

  const anim = { domain: "animation" };
  s("animation_create", "A", "unity_open_mcp_animation_create", {
    asset_paths: [ANIM_CLIP], paths_hint: [ANIM_CLIP],
  }, { ...anim, gate: true, check: expectOut((o) => o.create?.createdPaths?.includes(ANIM_CLIP), "the clip was not created") });
  s("animation_modify", "A", "unity_open_mcp_animation_modify", {
    asset_path: ANIM_CLIP,
    modifications_json: JSON.stringify([
      { type: "SetFrameRate", frameRate: 30 },
      { type: "SetCurve", componentType: "UnityEngine.Transform", propertyName: "localPosition.y", keyframes: [{ time: 0, value: 0 }, { time: 1, value: 2 }] },
    ]),
    paths_hint: [ANIM_CLIP],
  }, { ...anim, gate: true, check: expectOut((o) => o.modify?.errorCount === 0 && o.modify?.applied?.length === 2, "modifications not all applied") });
  s("animation_get_data", "A", "unity_open_mcp_animation_get_data", { asset_path: ANIM_CLIP }, {
    ...anim,
    check: expectOut((o) => o.clip?.frameRate === 30 && o.clip?.curveBindings?.some((b) => b.propertyName === "m_LocalPosition.y"),
      "frame rate / curve do not read back"),
  });
  s("animator_create", "A", "unity_open_mcp_animator_create", {
    asset_paths: [ANIM_CONTROLLER], paths_hint: [ANIM_CONTROLLER],
  }, { ...anim, gate: true, check: expectOut((o) => o.create?.createdPaths?.includes(ANIM_CONTROLLER), "the controller was not created") });
  s("animator_modify", "A", "unity_open_mcp_animator_modify", {
    asset_path: ANIM_CONTROLLER,
    modifications_json: JSON.stringify([
      { type: "AddParameter", parameterName: "Speed", parameterType: "Float" },
      { type: "AddState", layerName: "Base Layer", stateName: "Idle", motionAssetPath: ANIM_CLIP },
    ]),
    paths_hint: [ANIM_CONTROLLER],
  }, { ...anim, gate: true, check: expectOut((o) => o.modify?.errorCount === 0 && o.modify?.applied?.length === 2, "modifications not all applied") });
  s("animator_get_data", "A", "unity_open_mcp_animator_get_data", { asset_path: ANIM_CONTROLLER }, {
    ...anim,
    check: expectOut((o) => o.controller?.parameters?.some((p) => p.name === "Speed") &&
      o.controller?.layers?.[0]?.states?.some((st) => st.name === "Idle"), "parameter / state do not read back"),
  });

  // =====================================================================
  // BAND S — shadergraph (com.unity.shadergraph)
  // =====================================================================
  const sg = { domain: "shadergraph" };
  const slotOf = (o, direction) => o.node?.slots?.find((x) => x.direction === direction)?.id;
  s("shader_graph_create", "S", "unity_open_mcp_shader_graph_create", {
    asset_path: SHADER_GRAPH, shader_type: "Unlit", paths_hint: [SHADER_GRAPH],
  }, { ...sg, gate: true, check: expectOut((o) => o.shaderGraph?.created === true, "the graph was not created") });
  s("shader_graph_node_add_color", "S", "unity_open_mcp_shader_graph_node_add", {
    asset_path: SHADER_GRAPH, node_type: "Color", position: "-400,0", paths_hint: [SHADER_GRAPH],
  }, {
    ...sg, gate: true,
    check: expectOut((o) => o.node?.nodeId && slotOf(o, "output") !== undefined, "no node id / output slot"),
    after: (r, ctx) => { const o = toolOutput(r) ?? {}; ctx.sgColorNodeId = o.node?.nodeId; ctx.sgColorOutSlot = slotOf(o, "output"); },
  });
  s("shader_graph_node_add_multiply", "S", "unity_open_mcp_shader_graph_node_add", {
    asset_path: SHADER_GRAPH, node_type: "Multiply", position: "-200,0", paths_hint: [SHADER_GRAPH],
  }, {
    ...sg, gate: true,
    check: expectOut((o) => o.node?.nodeId && slotOf(o, "input") !== undefined, "no node id / input slot"),
    after: (r, ctx) => { const o = toolOutput(r) ?? {}; ctx.sgMultiplyNodeId = o.node?.nodeId; ctx.sgMultiplyInSlot = slotOf(o, "input"); },
  });
  s("shader_graph_node_connect", "S", "unity_open_mcp_shader_graph_node_connect", {
    resolveArgs: (ctx) => ({
      asset_path: SHADER_GRAPH,
      source_node_id: ctx.sgColorNodeId, source_slot: ctx.sgColorOutSlot ?? 0,
      destination_node_id: ctx.sgMultiplyNodeId, destination_slot: ctx.sgMultiplyInSlot ?? 0,
      paths_hint: [SHADER_GRAPH],
    }),
  }, { ...sg, gate: true, check: expectOut((o) => o.edge?.added === true, "edge.added is not true") });
  s("shader_graph_open", "S", "unity_open_mcp_shader_graph_open", { asset_path: SHADER_GRAPH }, {
    ...sg,
    check: expectOut((o, ctx) => o.shaderGraph?.window?.action === "opened" && o.shaderGraph.window.open === true &&
      o.shaderGraph.edgeCount === 1 &&
      o.shaderGraph.edges?.[0]?.outputNodeId === ctx.sgColorNodeId &&
      o.shaderGraph.nodes?.some((n) => n.id === ctx.sgMultiplyNodeId), "no window opened, or the edge / nodes do not read back"),
  });
  // An edit under the open window reloads it (no "changed on disk" dialog).
  s("shader_graph_node_add_open_window", "S", "unity_open_mcp_shader_graph_node_add", {
    asset_path: SHADER_GRAPH, node_type: "Time", position: "-400,200", paths_hint: [SHADER_GRAPH],
  }, { ...sg, gate: true, check: expectOut((o) => o.node?.nodeId, "no node id") });
  // The teardown deletes the graph: an open window would ask in a modal dialog.
  s("shader_graph_close_window", "S", "unity_open_mcp_shader_graph_open", { asset_path: SHADER_GRAPH, window: "close" }, {
    ...sg,
    check: expectOut((o) => o.shaderGraph?.window?.action === "closed" && o.shaderGraph.window.open === false &&
      o.shaderGraph.nodeCount >= 7, "the window did not close, or the added node does not read back"),
  });

  // =====================================================================
  // BAND X — splines / timeline / tilemap / vfx / memoryprofiler
  // =====================================================================
  const spl = { domain: "splines" };
  s("splines_container_create", "X", "unity_open_mcp_splines_container_create", {
    name: "ET_Spline", paths_hint: SCENE_HINT,
  }, {
    ...spl, gate: true,
    check: expectOut((o) => o.container?.instanceId && o.container?.container?.path === FT_SCENE, "no container id in the fixture scene"),
    after: (r, ctx) => { ctx.splineId = toolOutput(r)?.container?.instanceId; },
  });
  s("splines_add_knot_a", "X", "unity_open_mcp_splines_add_knot", {
    resolveArgs: (ctx) => ({ instance_id: ctx.splineId, position: "0,0,0", paths_hint: SCENE_HINT }),
  }, { ...spl, gate: true, check: expectOut((o) => o.knot?.index === 0, "knot index is not 0") });
  s("splines_add_knot_b", "X", "unity_open_mcp_splines_add_knot", {
    resolveArgs: (ctx) => ({ instance_id: ctx.splineId, position: "2,0,1", paths_hint: SCENE_HINT }),
  }, { ...spl, gate: true, check: expectOut((o) => o.knot?.index === 1, "knot index is not 1") });
  s("splines_get_knots", "X", "unity_open_mcp_splines_get_knots", {
    resolveArgs: (ctx) => ({ instance_id: ctx.splineId }),
  }, { ...spl, check: expectOut((o) => o.knotCount === 2 && o.knots?.[1]?.position?.[0] === 2, "the knots do not read back") });
  s("splines_evaluate", "X", "unity_open_mcp_splines_evaluate", {
    resolveArgs: (ctx) => ({ instance_id: ctx.splineId, t: 0.5 }),
  }, { ...spl, check: expectOut((o) => Math.abs((o.position?.[0] ?? 0) - 1) < 0.01, "the midpoint is not at x=1") });

  const tl = { domain: "timeline" };
  s("timeline_create", "X", "unity_open_mcp_timeline_create", {
    asset_path: TIMELINE, paths_hint: [TIMELINE],
  }, { ...tl, gate: true, check: expectOut((o) => o.timeline?.assetPath === TIMELINE, "the timeline was not created") });
  s("timeline_track_add", "X", "unity_open_mcp_timeline_track_add", {
    asset_path: TIMELINE, track_type: "Activation", track_name: "ET_Track", paths_hint: [TIMELINE],
  }, { ...tl, gate: true, check: expectOut((o) => o.track?.added === true && o.track.rootIndex === 0, "the track was not added") });
  s("timeline_clip_add", "X", "unity_open_mcp_timeline_clip_add", {
    asset_path: TIMELINE, track_index: 0, clip_name: "ET_Clip", start_time: 0.5, duration: 1, paths_hint: [TIMELINE],
  }, { ...tl, gate: true, check: expectOut((o) => o.clip?.added === true && o.clip.start === 0.5 && o.clip.duration === 1, "the clip timing does not match") });
  s("timeline_modify", "X", "unity_open_mcp_timeline_modify", {
    asset_path: TIMELINE, track_index: 0, fields_json: JSON.stringify([{ field: "m_Muted", value: true, type: "bool" }]), paths_hint: [TIMELINE],
  }, { ...tl, gate: true, check: expectOut((o) => o.applied?.some((a) => a.field === "m_Muted") && (o.errors ?? []).length === 0, "m_Muted was not applied") });

  const tm = { domain: "tilemap" };
  s("tilemap_create", "X", "unity_open_mcp_tilemap_create", {
    grid_name: "ET_Grid", tilemap_name: "ET_Tilemap", paths_hint: SCENE_HINT,
  }, {
    ...tm, gate: true,
    after: (r, ctx) => { const o = toolOutput(r) ?? {}; ctx.tilemapId = o.tilemap?.instanceId ?? o.instanceId; },
  });
  s("tilemap_create_tile_asset", "X", "unity_open_mcp_tilemap_create_tile_asset", {
    asset_path: TILE_ASSET, paths_hint: [TILE_ASSET],
  }, { ...tm, gate: true });
  s("tilemap_set_tile", "X", "unity_open_mcp_tilemap_set_tile", {
    resolveArgs: (ctx) => ({ instance_id: ctx.tilemapId, tile_asset_path: TILE_ASSET, x: 0, y: 0, paths_hint: SCENE_HINT }),
  }, { ...tm, gate: true });

  const vfx = { domain: "vfx" };
  s("vfx_list", "X", "unity_open_mcp_vfx_list", {}, vfx);
  // No VFX Graph asset fixture: open on a missing asset must refuse cleanly.
  s("vfx_open", "X", "unity_open_mcp_vfx_open", { asset_path: VFX_ASSET }, { ...vfx, expect: "tolerate", tolerate: ["asset_not_found"] });

  // No output_path: the snapshot goes to a temp path outside the project.
  s("memory_snapshot_capture", "X", "unity_senses_memory_snapshot_capture", {}, { domain: "memoryprofiler", timeoutMs: 120_000 });

  return steps;
}

// ---------------------------------------------------------------------------
// domain availability
// ---------------------------------------------------------------------------

function installedPackages(project) {
  const names = new Set();
  for (const file of ["packages-lock.json", "manifest.json"]) {
    try {
      const json = JSON.parse(readFileSync(resolve(project, "Packages", file), "utf8"));
      for (const name of Object.keys(json.dependencies ?? {})) names.add(name);
    } catch { /* missing file: fall through */ }
  }
  return names;
}

/** domain id → { mode: "strict" | "absent", note } from list_groups + the project's packages. */
function resolveDomains(project, runEnv, domains) {
  const listed = invokeTool(project, "unity_open_mcp_manage_tools", { action: "list_groups" }, 30_000, runEnv);
  const groups = listed?.result?.groups;
  if (!Array.isArray(groups)) return null;
  const packages = installedPackages(project);
  const plan = new Map();
  for (const id of domains) {
    const group = groups.find((g) => g.id === id);
    if (!group) { plan.set(id, { mode: "strict", note: "group unknown to the server" }); continue; }
    const pkg = group.unityPackage;
    const builtIn = !pkg || !pkg.startsWith("com.") || pkg.startsWith("com.unity.modules.");
    const installed = builtIn || packages.has(pkg);
    if (group.available !== false) {
      plan.set(id, { mode: "strict", note: `compiled in${pkg ? ` (${pkg})` : ""}` });
    } else if (!installed) {
      plan.set(id, { mode: "absent", note: `not compiled in: ${pkg} is not installed → expects tool_not_found` });
    } else {
      plan.set(id, { mode: "strict", note: `NOT compiled in although ${pkg} is installed → chain must fail` });
    }
  }
  return plan;
}

/** An absent domain's step: the call must reach the bridge (schema-valid
 *  placeholder args) and answer tool_not_found. */
function asAbsent(step) {
  return {
    ...step,
    expect: "unavailable",
    check: undefined,
    after: undefined,
    retryWhile: undefined,
    resolveArgs: step.resolveArgs ? () => step.resolveArgs(PLACEHOLDER_CTX) : undefined,
  };
}

// ---------------------------------------------------------------------------
// fixture teardown
// ---------------------------------------------------------------------------

function teardown(project, runEnv) {
  // Unload the fixture scene without saving (its objects die with it), then
  // the shared finalize (InitTestScene*, Main dirty state) and the folder.
  // Deleting the folder makes Unity recompile, so wait for an idle Editor.
  invokeTool(project, "unity_open_mcp_scene_unload", { asset_path: FT_SCENE, paths_hint: SCENE_HINT }, 30_000, runEnv);
  // A Shader Graph window left open on the fixture graph (a chain that failed
  // before its close step) would ask in a modal dialog when the folder goes.
  invokeTool(project, "unity_open_mcp_shader_graph_open", { asset_path: SHADER_GRAPH, window: "close" }, 30_000, runEnv);
  finalizeEditorState(project, runEnv, { excludeMain: true });
  cleanupViaBridge(project, runEnv, { fixtureRoot: FT, fixtureScene: FT_SCENE });
  waitForEditorSettled(project, runEnv);
  return cleanupTempFolder(project, { fixtureRoot: FT });
}

// ---------------------------------------------------------------------------
// main
// ---------------------------------------------------------------------------

async function main() {
  const opts = parseArgs(process.argv.slice(2));

  if (!existsSync(CLI_BIN)) {
    console.error(`CLI binary not found at ${CLI_BIN}.`);
    console.error(`Run \`npm run build\` in mcp-server/ first.`);
    process.exit(2);
  }

  const allSteps = buildSuite();
  if (opts.checkArgs) {
    const problems = await checkStepArgs(allSteps);
    for (const p of problems) console.log(`✗ ${p}`);
    console.log(problems.length
      ? `\n${problems.length} of ${allSteps.length} step(s) send arguments their tool's schema rejects.`
      : `All ${allSteps.length} step(s) send schema-valid arguments.`);
    process.exit(problems.length ? 1 : 0);
  }
  if (!isAbsolute(opts.project)) {
    console.error(`--project must be an ABSOLUTE path. Got: ${opts.project}`);
    process.exit(2);
  }
  if (!existsSync(opts.project)) {
    console.error(`Project not found: ${opts.project}`);
    process.exit(2);
  }

  let selected = allSteps;
  if (opts.band) selected = selected.filter((st) => st.band === "F" || opts.band.includes(st.band));
  if (opts.only) selected = selected.filter((st) => st.band === "F" || opts.only.some((n) => st.label.includes(n)));

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

  console.log("--- preflight ---");
  dismissBlockingModals(runEnv);
  const preflight = invokeTool(opts.project, "unity_open_mcp_editor_status", {}, 15_000, runEnv);
  if (!preflight || preflight.isError === true) {
    const code = preflight?.result?.error?.code ?? "no response";
    console.error(`\nThe Editor bridge is not usable (${code}). Open the project, let it settle, and re-run.`);
    process.exit(2);
  }
  if (!waitForEditorSettled(opts.project, runEnv)) {
    console.error("\nThe Editor did not reach idle (compile / reload / play mode). Let it settle and re-run.");
    process.exit(2);
  }
  // A fixture left by an interrupted run would make create_folder rename.
  if (existsSync(resolve(opts.project, FT))) {
    teardown(opts.project, runEnv);
    process.stdout.write(`✓ ${"stale fixture removed".padEnd(34)}        ${FT}\n`);
  }

  const domains = [...new Set(selected.map((st) => st.domain).filter(Boolean))];
  const plan = resolveDomains(opts.project, runEnv, domains);
  if (!plan) {
    console.error("\nmanage_tools list_groups did not answer; cannot tell which domains are compiled in.");
    process.exit(2);
  }
  for (const [id, p] of plan) process.stdout.write(`  ${id.padEnd(16)} ${p.note}\n`);
  console.log("");

  const ctx = {};
  const results = [];
  const bandSummary = {};
  let pass = 0;
  let fail = 0;

  const onSigInt = makeSigIntHandler({
    project: opts.project,
    runEnv,
    noCleanupFlag: opts.noCleanup,
    onCleanup: () => teardown(opts.project, runEnv),
  });
  process.on("SIGINT", onSigInt);

  let lastBand = null;
  for (const original of selected) {
    if (original.band !== lastBand) {
      if (lastBand !== null) console.log("");
      console.log(`--- Band ${original.band} ---`);
      lastBand = original.band;
    }
    const absent = original.domain && plan.get(original.domain)?.mode === "absent";
    const step = absent ? asAbsent(original) : original;
    const out = runToolLib(step, ctx, opts.project, opts.timeoutMs, runEnv);
    if (out.ok) pass++; else fail++;
    bandSummary[step.band] = bandSummary[step.band] || { pass: 0, fail: 0 };
    bandSummary[step.band][out.ok ? "pass" : "fail"]++;
    const detail = `${out.detail ?? out.error ?? ""}${absent ? " — domain unavailable, as expected" : ""}`;
    results.push({ label: step.label, band: step.band, domain: step.domain ?? null, tool: step.tool, expect: step.expect ?? "ok", passed: out.ok, ms: out.ms, detail });
    if (step.after) {
      try { step.after(out.result ?? {}, ctx); } catch { /* downstream checks report a missing id */ }
    }
    process.stdout.write(`${out.ok ? "✓" : "✗"} ${step.label.padEnd(34)} ${String(out.ms).padStart(6)}ms  ${detail}\n`);
  }

  process.removeListener("SIGINT", onSigInt);

  console.log("");
  console.log("--- cleanup ---");
  if (!opts.noCleanup) {
    const clean = teardown(opts.project, runEnv);
    process.stdout.write(`${clean ? "✓" : "✗"} ${"fixture scene + folder".padEnd(34)}        ${clean ? `${FT} gone` : "left on disk"}\n`);
  } else {
    process.stdout.write(`- ${"fixture left in place".padEnd(34)}        --no-cleanup\n`);
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
      suite: "S4-extensions",
      project: opts.project,
      ranAt: new Date().toISOString(),
      domains: Object.fromEntries(plan),
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
    for (const f of results.filter((r) => !r.passed).slice(0, 5)) {
      console.error(`\n✗ ${f.label} (${f.tool}): ${f.detail}`);
    }
    process.exit(1);
  }
}

main();
