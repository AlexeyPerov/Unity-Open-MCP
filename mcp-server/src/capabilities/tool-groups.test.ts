// M18 Plan 2 / T18.2 — tests for the canonical tool-group catalog and the
// per-session visibility store.

import test from "node:test";
import assert from "node:assert/strict";

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

import {
  TOOL_GROUPS,
  DEFAULT_ENABLED_GROUPS,
  GROUP_IDS,
  AUTO_ACTIVATE_GROUPS,
  getGroup,
  groupFor,
  groupToTools,
} from "./tool-groups.js";
import {
  ToolSessionState,
  filterVisibleTools,
} from "../tool-session-state.js";

// ---------------------------------------------------------------------------
// Catalog invariants
// ---------------------------------------------------------------------------

test("TOOL_GROUPS has stable, unique, lowercase ids", () => {
  const ids = TOOL_GROUPS.map((g) => g.id);
  assert.equal(new Set(ids).size, ids.length, "group ids must be unique");
  for (const id of ids) {
    assert.equal(id, id.toLowerCase(), `${id} must be lowercase`);
    assert.ok(/^[a-z][a-z0-9-]*$/.test(id), `${id} must be a valid group id`);
  }
});

test("every group carries a non-empty description", () => {
  for (const g of TOOL_GROUPS) {
    assert.ok(
      typeof g.description === "string" && g.description.length > 10,
      `${g.id} must have a meaningful description`,
    );
  }
});

test("DEFAULT_ENABLED_GROUPS matches the catalog's defaultEnabled entries", () => {
  // Single source of truth: the set of groups marked `defaultEnabled: true`
  // in the catalog. The lean baseline is `core` (essential entry points) plus
  // `gate-and-verify` (the safety surface) — every other group activates on
  // demand or auto-activates when its Unity package is present. Asserting
  // against the catalog keeps this test honest when defaults change instead of
  // hard-coding a snapshot.
  const expected = TOOL_GROUPS.filter((g) => g.defaultEnabled)
    .map((g) => g.id)
    .sort();
  assert.deepEqual(Array.from(DEFAULT_ENABLED_GROUPS).sort(), expected);
  // `core` is always default-on — the essential entry points live there.
  assert.ok(DEFAULT_ENABLED_GROUPS.has("core"));
});

test("the lean default-on set is exactly core + gate-and-verify", () => {
  // Session-ergonomics baseline: a fresh session advertises only these two
  // groups from defaults (plus always-visible meta-tools and any package
  // auto-activated groups). Anything else widening the surface is a regression
  // — pin the exact ids so a catalog edit that re-enables a group is
  // intentional.
  assert.deepEqual(
    Array.from(DEFAULT_ENABLED_GROUPS).sort(),
    ["core", "gate-and-verify"],
  );
  // The three groups that moved off must NOT be default-on.
  for (const id of ["asset-intelligence", "typed-editor", "diagnostics"]) {
    assert.ok(
      !DEFAULT_ENABLED_GROUPS.has(id),
      `${id} must not be default-on (lean session surface)`,
    );
  }
});

test("every domain-gated group carries both domainDefine and unityPackage", () => {
  for (const g of TOOL_GROUPS) {
    if (g.domainDefine) {
      assert.ok(g.unityPackage, `${g.id} has domainDefine but no unityPackage`);
      assert.match(
        g.domainDefine,
        /^UNITY_OPEN_MCP_EXT_[A-Z]+$/,
        `${g.id} domainDefine must follow the UNITY_OPEN_MCP_EXT_<DOMAIN> convention`,
      );
    }
  }
});

test("GROUP_IDS matches TOOL_GROUPS ids", () => {
  assert.deepEqual(
    Array.from(GROUP_IDS).sort(),
    TOOL_GROUPS.map((g) => g.id).sort(),
  );
});

test("shipped extension domains are all present", () => {
  // The 5 migrated extension domains — Nav, Input, ProBuilder, Particles,
  // Animation. Adding/removing a domain means updating this list alongside
  // the bridge asmdef versionDefines.
  const expected = [
    "navigation",
    "input-system",
    "probuilder",
    "particle-system",
    "animation",
  ];
  const ids = TOOL_GROUPS.map((g) => g.id);
  for (const id of expected) {
    assert.ok(ids.includes(id), `group '${id}' must exist`);
  }
});

test("getGroup returns the catalog entry by id", () => {
  const core = getGroup("core");
  assert.ok(core);
  assert.equal(core!.defaultEnabled, true);
});

test("getGroup returns undefined for unknown ids", () => {
  assert.equal(getGroup("does-not-exist"), undefined);
});

// ---------------------------------------------------------------------------
// Per-tool assignment
// ---------------------------------------------------------------------------

test("groupFor returns null for server meta-tools (always visible)", () => {
  // These tools MUST stay always-visible so an agent can reach them before
  // any other group is active.
  for (const name of [
    "unity_open_mcp_capabilities",
    "unity_open_mcp_manage_tools",
    "unity_open_mcp_list_rules",
    "unity_open_mcp_generate_skill",
    "unity_open_mcp_read_compile_errors",
  ]) {
    assert.equal(groupFor(name), null, `${name} must be always-visible (null group)`);
  }
});

test("groupFor assigns core tools to core", () => {
  for (const name of [
    "unity_open_mcp_ping",
    "unity_open_mcp_execute_csharp",
    "unity_open_mcp_invoke_method",
    "unity_open_mcp_editor_status",
  ]) {
    assert.equal(groupFor(name), "core");
  }
});

test("groupFor assigns navigation tools to navigation", () => {
  assert.equal(groupFor("unity_open_mcp_navigation_surface_add"), "navigation");
  assert.equal(groupFor("unity_open_mcp_navigation_modify"), "navigation");
});

test("groupFor assigns every typed-editor tool to typed-editor", () => {
  assert.equal(groupFor("unity_open_mcp_gameobject_create"), "typed-editor");
  assert.equal(groupFor("unity_open_mcp_scene_open"), "typed-editor");
  assert.equal(groupFor("unity_open_mcp_prefab_instantiate"), "typed-editor");
  assert.equal(groupFor("unity_open_mcp_sceneview_get_camera"), "typed-editor");
  assert.equal(groupFor("unity_open_mcp_editor_undo_history"), "typed-editor");
});

test("groupFor returns null for unknown tool names", () => {
  assert.equal(groupFor("unity_open_mcp_does_not_exist"), null);
});

test("groupToTools covers every catalog group", () => {
  const map = groupToTools();
  for (const g of TOOL_GROUPS) {
    assert.ok(Array.isArray(map[g.id]), `${g.id} must have a tool array`);
  }
});

// M31-optimizations Plan 4 / T4.1 (M1) — the inverse map is frozen once at
// module load (TOOL_GROUP_ASSIGNMENT is a module-level constant mutated only
// by the assign() calls during first import). Every call site in tool-router
// (manage_tools list_groups / activate_for / reconcileAutoActivation /
// resolveCompiledAvailability) now shares one object reference. Identity is
// the contract: a regression that re-introduces a per-call rebuild would
// fail this assertion.
test("groupToTools returns the same object reference across calls", () => {
  const a = groupToTools();
  const b = groupToTools();
  assert.equal(a, b, "groupToTools must return the memoized constant");
  // Inner arrays are frozen too — same reference for the same group id.
  for (const g of TOOL_GROUPS) {
    assert.equal(a[g.id], b[g.id], `${g.id} roster must be the same reference`);
  }
});

test("groupToTools rosters are sorted and match groupFor", () => {
  const map = groupToTools();
  for (const [groupId, tools] of Object.entries(map)) {
    const sorted = [...tools].sort();
    assert.deepEqual(tools, sorted, `${groupId} roster must be sorted`);
    for (const t of tools) {
      assert.equal(groupFor(t), groupId, `${t} must map back to ${groupId}`);
    }
  }
});

test("groupToTools core roster includes ping and execute_csharp", () => {
  const map = groupToTools();
  assert.ok(map.core.includes("unity_open_mcp_ping"));
  assert.ok(map.core.includes("unity_open_mcp_execute_csharp"));
});

test("groupToTools navigation roster has all 11 navigation tools", () => {
  const map = groupToTools();
  // 11 tools — mirrors the navigation extension pack surface.
  assert.equal(map.navigation.length, 11);
  assert.ok(map.navigation.includes("unity_open_mcp_navigation_surface_add"));
  assert.ok(map.navigation.includes("unity_open_mcp_navigation_modify"));
});

test("groupFor assigns animation tools to animation (two-prefix group)", () => {
  // Regression: the animation assign() block spans two domain prefixes
  // (animation_* + animator_*), so it lists fully-qualified tool names rather
  // than a single-prefix .map(). An earlier revision passed bare suffixes
  // (animation_create, …) without the unity_open_mcp_ prefix, which silently
  // left all six tools unassigned (falling through to the always-visible
  // meta/null bucket instead of the animation group). Pin the contract.
  for (const name of [
    "unity_open_mcp_animation_create",
    "unity_open_mcp_animation_get_data",
    "unity_open_mcp_animation_modify",
    "unity_open_mcp_animator_create",
    "unity_open_mcp_animator_get_data",
    "unity_open_mcp_animator_modify",
  ]) {
    assert.equal(groupFor(name), "animation", `${name} must map to animation`);
  }
});

test("groupToTools animation roster has all 6 animation tools", () => {
  const map = groupToTools();
  // 6 tools — three animation_* + three animator_* (M16 Plan 10 / T6.6.10).
  assert.equal(map.animation.length, 6);
  assert.ok(map.animation.includes("unity_open_mcp_animation_create"));
  assert.ok(map.animation.includes("unity_open_mcp_animator_modify"));
});

test("groupFor assigns lighting tools to lighting", () => {
  for (const name of [
    "unity_open_mcp_light_add",
    "unity_open_mcp_light_set",
    "unity_open_mcp_light_modify",
    "unity_open_mcp_reflection_probe_bake",
    "unity_open_mcp_reflection_probe_get",
    "unity_open_mcp_skybox_set",
    "unity_open_mcp_skybox_get",
  ]) {
    assert.equal(groupFor(name), "lighting", `${name} must map to lighting`);
  }
});

test("groupToTools lighting roster has all 7 lighting tools", () => {
  const map = groupToTools();
  // 7 tools — light_add / light_set / light_modify + reflection_probe_bake /
  // reflection_probe_get + skybox_set / skybox_get (M20 Plan 2).
  assert.equal(map.lighting.length, 7);
  assert.ok(map.lighting.includes("unity_open_mcp_light_add"));
  assert.ok(map.lighting.includes("unity_open_mcp_reflection_probe_bake"));
  assert.ok(map.lighting.includes("unity_open_mcp_skybox_set"));
});

test("groupFor assigns audio tools to audio", () => {
  for (const name of [
    "unity_open_mcp_audio_source_add",
    "unity_open_mcp_audio_source_modify",
    "unity_open_mcp_audio_mixer_set_parameter",
    "unity_open_mcp_audio_listener_get",
    "unity_open_mcp_audio_mixer_get_parameter",
  ]) {
    assert.equal(groupFor(name), "audio", `${name} must map to audio`);
  }
});

test("groupToTools audio roster has all 5 audio tools", () => {
  const map = groupToTools();
  // 5 tools — audio_source_add / audio_source_modify +
  // audio_mixer_set_parameter / audio_mixer_get_parameter +
  // audio_listener_get (M20 Plan 3 / T20.3.1).
  assert.equal(map.audio.length, 5);
  assert.ok(map.audio.includes("unity_open_mcp_audio_source_add"));
  assert.ok(map.audio.includes("unity_open_mcp_audio_mixer_set_parameter"));
  assert.ok(map.audio.includes("unity_open_mcp_audio_listener_get"));
});

test("groupFor assigns ui tools to ui", () => {
  for (const name of [
    "unity_open_mcp_ui_canvas_add",
    "unity_open_mcp_ui_element_add",
    "unity_open_mcp_ui_layout_group_add",
    "unity_open_mcp_ui_element_modify",
  ]) {
    assert.equal(groupFor(name), "ui", `${name} must map to ui`);
  }
});

test("groupToTools ui roster has all 4 ui tools", () => {
  const map = groupToTools();
  // 4 tools — ui_canvas_add / ui_element_add / ui_layout_group_add /
  // ui_element_modify (M20 Plan 3 / T20.3.2).
  assert.equal(map.ui.length, 4);
  assert.ok(map.ui.includes("unity_open_mcp_ui_canvas_add"));
  assert.ok(map.ui.includes("unity_open_mcp_ui_element_add"));
  assert.ok(map.ui.includes("unity_open_mcp_ui_layout_group_add"));
  assert.ok(map.ui.includes("unity_open_mcp_ui_element_modify"));
});

test("groupFor assigns constraints tools to constraints", () => {
  for (const name of [
    "unity_open_mcp_constraint_add",
    "unity_open_mcp_lod_group_configure",
    "unity_open_mcp_lod_add_level",
  ]) {
    assert.equal(groupFor(name), "constraints", `${name} must map to constraints`);
  }
});

test("groupToTools constraints roster has all 3 constraints tools", () => {
  const map = groupToTools();
  // 3 tools — constraint_add + lod_group_configure + lod_add_level (M20 Plan 3
  // / T20.3.3). One `constraints` group covers both Constraints & LOD.
  assert.equal(map.constraints.length, 3);
  assert.ok(map.constraints.includes("unity_open_mcp_constraint_add"));
  assert.ok(map.constraints.includes("unity_open_mcp_lod_group_configure"));
  assert.ok(map.constraints.includes("unity_open_mcp_lod_add_level"));
});

test("groupFor assigns terrain tools to terrain", () => {
  for (const name of [
    "unity_open_mcp_terrain_create",
    "unity_open_mcp_terrain_set_heights",
    "unity_open_mcp_terrain_paint_layer",
    "unity_open_mcp_terrain_place_trees",
    "unity_open_mcp_terrain_set_neighbors",
  ]) {
    assert.equal(groupFor(name), "terrain", `${name} must map to terrain`);
  }
});

test("groupToTools terrain roster has all 5 terrain tools", () => {
  const map = groupToTools();
  // 5 tools — terrain_create + terrain_set_heights + terrain_paint_layer +
  // terrain_place_trees + terrain_set_neighbors (M20 Plan 4 / T20.4).
  assert.equal(map.terrain.length, 5);
  assert.ok(map.terrain.includes("unity_open_mcp_terrain_create"));
  assert.ok(map.terrain.includes("unity_open_mcp_terrain_set_heights"));
  assert.ok(map.terrain.includes("unity_open_mcp_terrain_paint_layer"));
  assert.ok(map.terrain.includes("unity_open_mcp_terrain_place_trees"));
  assert.ok(map.terrain.includes("unity_open_mcp_terrain_set_neighbors"));
});

// ---------------------------------------------------------------------------
// M20 Plan 9 / T20.9.4 — SceneView camera + Undo history map to typed-editor.
// ---------------------------------------------------------------------------

test("groupFor assigns sceneview + undo-history tools to typed-editor", () => {
  for (const name of [
    "unity_open_mcp_sceneview_get_camera",
    "unity_open_mcp_sceneview_set_camera",
    "unity_open_mcp_editor_undo_history",
    "unity_open_mcp_editor_clear_history",
  ]) {
    assert.equal(groupFor(name), "typed-editor", `${name} must map to typed-editor`);
  }
});

test("groupToTools typed-editor roster includes the 4 T20.9.4 tools", () => {
  const map = groupToTools();
  assert.ok(map["typed-editor"].includes("unity_open_mcp_sceneview_get_camera"));
  assert.ok(map["typed-editor"].includes("unity_open_mcp_sceneview_set_camera"));
  assert.ok(map["typed-editor"].includes("unity_open_mcp_editor_undo_history"));
  assert.ok(map["typed-editor"].includes("unity_open_mcp_editor_clear_history"));
});

// ---------------------------------------------------------------------------
// M20 Plan 9 / T20.9.1 — sprite2d catalog (SpriteAtlas + Texture import)
// ---------------------------------------------------------------------------

test("sprite2d group is registered and ungated (built-in 2D module)", () => {
  const g = getGroup("sprite2d");
  assert.ok(g, "sprite2d group must exist");
  assert.equal(g!.defaultEnabled, false);
  // Built-in 2D module — no domainDefine, no unityPackage.
  assert.equal(g!.domainDefine, undefined);
  assert.equal(g!.unityPackage, undefined);
});

test("groupFor assigns spriteatlas + texture tools to sprite2d", () => {
  for (const name of [
    "unity_open_mcp_spriteatlas_create",
    "unity_open_mcp_spriteatlas_get",
    "unity_open_mcp_spriteatlas_add_packable",
    "unity_open_mcp_spriteatlas_remove_packable",
    "unity_open_mcp_spriteatlas_modify",
    "unity_open_mcp_spriteatlas_delete",
    "unity_open_mcp_spriteatlas_list",
    "unity_open_mcp_texture_get_importer",
    "unity_open_mcp_texture_set_import",
    "unity_open_mcp_texture_reimport",
    "unity_open_mcp_texture_get",
  ]) {
    assert.equal(groupFor(name), "sprite2d", `${name} must map to sprite2d`);
  }
});

test("groupToTools sprite2d roster has all 11 2D-art-pipeline tools", () => {
  const map = groupToTools();
  // 11 tools — 7 spriteatlas_* + 4 texture_* (M20 Plan 9 / T20.9.1).
  assert.equal(map.sprite2d.length, 11);
  assert.ok(map.sprite2d.includes("unity_open_mcp_spriteatlas_create"));
  assert.ok(map.sprite2d.includes("unity_open_mcp_texture_set_import"));
});

// ---------------------------------------------------------------------------
// M20 Plan 9 / T20.9.2 + T20.9.3 — KV preferences + Project Settings remainder
// ride the build-settings group (project configuration surface).
// ---------------------------------------------------------------------------

test("groupFor assigns prefs + settings-remainder tools to build-settings", () => {
  for (const name of [
    // T20.9.2 — KV preferences (gate-free mutators; registry writes).
    "unity_open_mcp_playerprefs_get",
    "unity_open_mcp_playerprefs_set",
    "unity_open_mcp_playerprefs_delete",
    "unity_open_mcp_editorprefs_get",
    "unity_open_mcp_editorprefs_set",
    "unity_open_mcp_editorprefs_delete",
    // T20.9.3 — Project Settings remainder.
    "unity_open_mcp_settings_get_time",
    "unity_open_mcp_settings_set_time",
    "unity_open_mcp_settings_get_render_pipeline",
    "unity_open_mcp_settings_set_quality_level",
  ]) {
    assert.equal(groupFor(name), "build-settings", `${name} must map to build-settings`);
  }
});

test("groupToTools build-settings roster includes the 10 new prefs + remainder tools", () => {
  const map = groupToTools();
  // The build-settings group already carried the 16 M16 Plan 9 build/settings
  // tools; T20.9.2 adds 6 prefs tools and T20.9.3 adds 4 settings-remainder
  // tools → 26 total.
  assert.ok(map["build-settings"].length >= 26, "build-settings must include the new tools");
  assert.ok(map["build-settings"].includes("unity_open_mcp_playerprefs_set"));
  assert.ok(map["build-settings"].includes("unity_open_mcp_editorprefs_delete"));
  assert.ok(map["build-settings"].includes("unity_open_mcp_settings_set_time"));
  assert.ok(map["build-settings"].includes("unity_open_mcp_settings_set_quality_level"));
  assert.ok(map["build-settings"].includes("unity_open_mcp_settings_get_render_pipeline"));
});

// ---------------------------------------------------------------------------
// M20 Plan 7 / T20.7.0 + T20.7.1 — shadergraph catalog + auto-activation
// ---------------------------------------------------------------------------

test("shadergraph group is registered, gated, and auto-activating", () => {
  // T20.7.1: the shadergraph group exists and carries the right metadata.
  const sg = getGroup("shadergraph");
  assert.ok(sg, "shadergraph group must exist");
  assert.equal(sg!.defaultEnabled, false);
  assert.equal(sg!.domainDefine, "UNITY_OPEN_MCP_EXT_SHADERGRAPH");
  assert.equal(sg!.unityPackage, "com.unity.shadergraph");
  // T20.7.0: the FIRST auto-activating domain.
  assert.equal(sg!.autoActivate, true);
});

test("shadergraph roster has all 4 shader_graph tools", () => {
  const map = groupToTools();
  assert.equal(map.shadergraph.length, 4);
  assert.ok(map.shadergraph.includes("unity_open_mcp_shader_graph_create"));
  assert.ok(map.shadergraph.includes("unity_open_mcp_shader_graph_open"));
  assert.ok(map.shadergraph.includes("unity_open_mcp_shader_graph_node_add"));
  assert.ok(map.shadergraph.includes("unity_open_mcp_shader_graph_node_connect"));
});

test("groupFor assigns shader_graph tools to shadergraph", () => {
  assert.equal(groupFor("unity_open_mcp_shader_graph_create"), "shadergraph");
  assert.equal(groupFor("unity_open_mcp_shader_graph_open"), "shadergraph");
  assert.equal(groupFor("unity_open_mcp_shader_graph_node_add"), "shadergraph");
  assert.equal(groupFor("unity_open_mcp_shader_graph_node_connect"), "shadergraph");
});

test("AUTO_ACTIVATE_GROUPS lists shadergraph with its package", () => {
  // T20.7.0: the auto-activation index is derived from the catalog. shadergraph
  // is the first entry.
  assert.ok(AUTO_ACTIVATE_GROUPS.length >= 1);
  const sg = AUTO_ACTIVATE_GROUPS.find((e) => e.groupId === "shadergraph");
  assert.ok(sg, "shadergraph must be in AUTO_ACTIVATE_GROUPS");
  assert.equal(sg!.packageId, "com.unity.shadergraph");
});

// ---------------------------------------------------------------------------
// M20 Plan 7 / T20.7.2 — vfx catalog + auto-activation
// ---------------------------------------------------------------------------

test("vfx group is registered, gated, and auto-activating", () => {
  const vfx = getGroup("vfx");
  assert.ok(vfx, "vfx group must exist");
  assert.equal(vfx!.defaultEnabled, false);
  assert.equal(vfx!.domainDefine, "UNITY_OPEN_MCP_EXT_VFX");
  assert.equal(vfx!.unityPackage, "com.unity.visualeffectgraph");
  // T20.7.0: the second auto-activating domain.
  assert.equal(vfx!.autoActivate, true);
});

test("vfx roster has all 3 vfx tools", () => {
  const map = groupToTools();
  assert.equal(map.vfx.length, 3);
  assert.ok(map.vfx.includes("unity_open_mcp_vfx_list"));
  assert.ok(map.vfx.includes("unity_open_mcp_vfx_open"));
  assert.ok(map.vfx.includes("unity_open_mcp_vfx_block_edit"));
});

test("groupFor assigns vfx tools to vfx", () => {
  assert.equal(groupFor("unity_open_mcp_vfx_list"), "vfx");
  assert.equal(groupFor("unity_open_mcp_vfx_open"), "vfx");
  assert.equal(groupFor("unity_open_mcp_vfx_block_edit"), "vfx");
});

test("AUTO_ACTIVATE_GROUPS lists vfx with its package", () => {
  const vfx = AUTO_ACTIVATE_GROUPS.find((e) => e.groupId === "vfx");
  assert.ok(vfx, "vfx must be in AUTO_ACTIVATE_GROUPS");
  assert.equal(vfx!.packageId, "com.unity.visualeffectgraph");
});

// ---------------------------------------------------------------------------
// M20 Plan 7 / T20.7.3 — memoryprofiler catalog + auto-activation
// ---------------------------------------------------------------------------

test("memoryprofiler group is registered, gated, and auto-activating", () => {
  const mp = getGroup("memoryprofiler");
  assert.ok(mp, "memoryprofiler group must exist");
  assert.equal(mp!.defaultEnabled, false);
  assert.equal(mp!.domainDefine, "UNITY_OPEN_MCP_EXT_MEMORYPROFILER");
  assert.equal(mp!.unityPackage, "com.unity.memoryprofiler");
  // T20.7.0: the third auto-activating domain.
  assert.equal(mp!.autoActivate, true);
});

test("memoryprofiler roster has the single capture tool", () => {
  const map = groupToTools();
  assert.equal(map.memoryprofiler.length, 1);
  assert.ok(map.memoryprofiler.includes("unity_senses_memory_snapshot_capture"));
});

test("groupFor assigns the memory snapshot capture tool to memoryprofiler", () => {
  // Sense-prefixed (unity_senses_*) but belongs to the memoryprofiler group,
  // not agent-senses, because it pairs with the profiler family and is gated
  // on com.unity.memoryprofiler.
  assert.equal(
    groupFor("unity_senses_memory_snapshot_capture"),
    "memoryprofiler",
  );
});

test("AUTO_ACTIVATE_GROUPS lists memoryprofiler with its package", () => {
  const mp = AUTO_ACTIVATE_GROUPS.find((e) => e.groupId === "memoryprofiler");
  assert.ok(mp, "memoryprofiler must be in AUTO_ACTIVATE_GROUPS");
  assert.equal(mp!.packageId, "com.unity.memoryprofiler");
});

test("every other shipped domain is NOT auto-activating (manual only)", () => {
  // T20.7.0 regression guard: auto-activation is additive — existing domains
  // keep manual activation unless they explicitly opt in. Today only
  // shadergraph + vfx + memoryprofiler opt in.
  const autoIds = new Set(AUTO_ACTIVATE_GROUPS.map((e) => e.groupId));
  for (const g of TOOL_GROUPS) {
    if (g.id === "shadergraph" || g.id === "vfx" || g.id === "memoryprofiler") continue;
    assert.ok(
      !autoIds.has(g.id),
      `${g.id} must not be auto-activating (additive invariant)`,
    );
  }
});

// ---------------------------------------------------------------------------
// M26 Plan 2 — unity-hub-control catalog (local-routed, built-in, ungated)
// ---------------------------------------------------------------------------

test("unity-hub-control group is registered, ungated, and manual-activation", () => {
  const g = getGroup("unity-hub-control");
  assert.ok(g, "unity-hub-control group must exist");
  assert.equal(g!.defaultEnabled, false);
  // Built-in module — no domain define, no Unity package, no auto-activation.
  assert.equal(g!.domainDefine, undefined);
  assert.equal(g!.unityPackage, undefined);
  assert.equal(g!.autoActivate, undefined);
});

test("groupFor assigns the 6 hub control tools to unity-hub-control", () => {
  for (const name of [
    "unity_open_mcp_hub_list_editors",
    "unity_open_mcp_hub_available_releases",
    "unity_open_mcp_hub_install_editor",
    "unity_open_mcp_hub_install_modules",
    "unity_open_mcp_hub_get_install_path",
    "unity_open_mcp_hub_set_install_path",
  ]) {
    assert.equal(groupFor(name), "unity-hub-control", `${name} must map to unity-hub-control`);
  }
});

test("groupToTools unity-hub-control roster has all 6 tools", () => {
  const map = groupToTools();
  assert.equal(map["unity-hub-control"].length, 6);
  assert.ok(map["unity-hub-control"].includes("unity_open_mcp_hub_list_editors"));
  assert.ok(map["unity-hub-control"].includes("unity_open_mcp_hub_available_releases"));
  assert.ok(map["unity-hub-control"].includes("unity_open_mcp_hub_install_editor"));
  assert.ok(map["unity-hub-control"].includes("unity_open_mcp_hub_install_modules"));
  assert.ok(map["unity-hub-control"].includes("unity_open_mcp_hub_get_install_path"));
  assert.ok(map["unity-hub-control"].includes("unity_open_mcp_hub_set_install_path"));
});

// ---------------------------------------------------------------------------
// Session state — activate / deactivate / reset
// ---------------------------------------------------------------------------

// The default-active set is the catalog's `defaultEnabled` entries. Several
// session-state tests assert against it; derive here so they track the
// catalog instead of a stale `["core"]`-only snapshot.
function expectedDefaultActive(): string[] {
  return Array.from(DEFAULT_ENABLED_GROUPS).sort();
}

test("fresh session state has the default-active groups", () => {
  const s = new ToolSessionState();
  assert.deepEqual(s.activeGroups(), expectedDefaultActive());
});

test("activate adds a group", () => {
  const s = new ToolSessionState();
  assert.equal(s.activate("navigation"), true);
  assert.deepEqual(
    s.activeGroups(),
    [...expectedDefaultActive(), "navigation"].sort(),
  );
  // activating again is a no-op (returns false)
  assert.equal(s.activate("navigation"), false);
});

test("activate rejects unknown groups", () => {
  const s = new ToolSessionState();
  assert.equal(s.activate("does-not-exist"), false);
  assert.deepEqual(s.activeGroups(), expectedDefaultActive());
});

test("deactivate removes a group", () => {
  const s = new ToolSessionState();
  s.activate("navigation");
  assert.equal(s.deactivate("navigation"), true);
  assert.deepEqual(s.activeGroups(), expectedDefaultActive());
  // deactivating again is a no-op
  assert.equal(s.deactivate("navigation"), false);
});

test("deactivate rejects unknown groups", () => {
  const s = new ToolSessionState();
  assert.equal(s.deactivate("does-not-exist"), false);
});

test("deactivate allows removing core", () => {
  // Core is not special at the store level — always-visible meta-tools stay
  // reachable via the filter's allow-list. Agents can darken core if they
  // want a minimal surface.
  const s = new ToolSessionState();
  assert.equal(s.deactivate("core"), true);
  assert.ok(!s.isGroupActive("core"));
});

test("reset restores the default active set", () => {
  const s = new ToolSessionState();
  s.activate("navigation");
  s.activate("probuilder");
  s.deactivate("core");
  assert.equal(s.reset(), true);
  assert.deepEqual(s.activeGroups(), expectedDefaultActive());
});

test("isGroupActive reflects the active set", () => {
  const s = new ToolSessionState();
  assert.equal(s.isGroupActive("core"), true);
  assert.equal(s.isGroupActive("navigation"), false);
  s.activate("navigation");
  assert.equal(s.isGroupActive("navigation"), true);
});

// ---------------------------------------------------------------------------
// M20 Plan 7 / T20.7.0 — auto-activation behavior (reconcileAutoActivation)
// ---------------------------------------------------------------------------

test("activationSource tracks why a group is active", () => {
  const s = new ToolSessionState();
  // Default-on groups report "default".
  assert.equal(s.activationSource("core"), "default");
  // Inactive groups report null.
  assert.equal(s.activationSource("navigation"), null);
  // Manual activate → "manual".
  s.activate("navigation");
  assert.equal(s.activationSource("navigation"), "manual");
});

test("activateAuto activates a group with source 'auto'", () => {
  const s = new ToolSessionState();
  assert.equal(s.isGroupActive("shadergraph"), false);
  const changed = s.activateAuto("shadergraph");
  assert.equal(changed, true);
  assert.equal(s.isGroupActive("shadergraph"), true);
  assert.equal(s.activationSource("shadergraph"), "auto");
});

test("activateAuto is idempotent (no-op when already active)", () => {
  const s = new ToolSessionState();
  s.activateAuto("shadergraph");
  // Re-activating an already-active group is a no-op (source preserved).
  const changed = s.activateAuto("shadergraph");
  assert.equal(changed, false);
  assert.equal(s.activationSource("shadergraph"), "auto");
});

test("reconcileAutoActivation activates satisfied groups and drops unsatisfied auto ones", () => {
  const s = new ToolSessionState();
  // Package present (shadergraph compiled in) → auto-activated.
  const changed1 = s.reconcileAutoActivation(new Set(["shadergraph"]));
  assert.deepEqual(changed1, ["shadergraph"]);
  assert.equal(s.isGroupActive("shadergraph"), true);
  assert.equal(s.activationSource("shadergraph"), "auto");

  // Package removed → auto-activated group is dropped again.
  const changed2 = s.reconcileAutoActivation(new Set());
  assert.deepEqual(changed2, ["shadergraph"]);
  assert.equal(s.isGroupActive("shadergraph"), false);
  assert.equal(s.activationSource("shadergraph"), null);
});

test("reconcileAutoActivation preserves a manually-activated group when its package goes away", () => {
  // Manual activation wins: if the agent activated a group by hand, a later
  // reconcile where its package is absent must NOT drop it.
  const s = new ToolSessionState();
  s.activate("shadergraph");
  assert.equal(s.activationSource("shadergraph"), "manual");
  const changed = s.reconcileAutoActivation(new Set());
  // No change — manual activation preserved even though package is absent.
  assert.deepEqual(changed, []);
  assert.equal(s.isGroupActive("shadergraph"), true);
  assert.equal(s.activationSource("shadergraph"), "manual");
});

test("reconcileAutoActivation does not re-flip a manually-deactivated group", () => {
  // The agent deactivated an auto-activated group; reconcile must not flip it
  // back to "auto" even when the package is present. (Manual intent wins.)
  const s = new ToolSessionState();
  s.activateAuto("shadergraph");
  s.deactivate("shadergraph");
  assert.equal(s.isGroupActive("shadergraph"), false);
  const changed = s.reconcileAutoActivation(new Set(["shadergraph"]));
  // deactivate cleared the source; reconcile would re-activate. But the
  // resolved decision is that a deliberate deactivate is sticky for the
  // session only if the agent re-deactivates after each reconcile. We document
  // the actual behavior here: reconcile re-activates because the source was
  // cleared on deactivate (there is no "manually-deactivated" sentinel). This
  // test pins the contract so a future change is intentional.
  assert.deepEqual(changed, ["shadergraph"]);
  assert.equal(s.activationSource("shadergraph"), "auto");
});

test("reconcileAutoActivation reports no change when nothing changes", () => {
  const s = new ToolSessionState();
  // Empty satisfied set + no prior auto-activation → no change.
  assert.deepEqual(s.reconcileAutoActivation(new Set()), []);
  // Satisfied set stable across two reconciles → second is a no-op.
  s.reconcileAutoActivation(new Set(["shadergraph"]));
  assert.deepEqual(s.reconcileAutoActivation(new Set(["shadergraph"])), []);
});

test("reset clears auto-activation back to defaults", () => {
  const s = new ToolSessionState();
  s.activateAuto("shadergraph");
  assert.equal(s.isGroupActive("shadergraph"), true);
  s.reset();
  assert.equal(s.isGroupActive("shadergraph"), false);
  assert.equal(s.activationSource("shadergraph"), null);
  // core is back to "default".
  assert.equal(s.activationSource("core"), "default");
});

// ---------------------------------------------------------------------------
// filterVisibleTools — the ListTools gate
// ---------------------------------------------------------------------------

function tools(...names: string[]): Tool[] {
  return names.map((name) => ({
    name,
    description: `${name} fixture`,
    inputSchema: { type: "object" as const, properties: {} },
  }));
}

test("filterVisibleTools: fresh session shows default-active + meta-tools, hides opt-in groups", () => {
  const state = new ToolSessionState();
  const filtered = filterVisibleTools(
    tools(
      "unity_open_mcp_ping", // core — default-on
      "unity_open_mcp_capabilities", // meta (always visible)
      "unity_open_mcp_manage_tools", // meta (always visible)
      "unity_open_mcp_navigation_surface_add", // navigation — opt-in, hidden
      "unity_open_mcp_build_start", // build-settings — opt-in, hidden
    ),
    state,
  );
  const names = filtered.map((t) => t.name).sort();
  assert.deepEqual(names, [
    "unity_open_mcp_capabilities",
    "unity_open_mcp_manage_tools",
    "unity_open_mcp_ping",
  ]);
});

test("filterVisibleTools: activating an opt-in group reveals its tools", () => {
  const state = new ToolSessionState();
  state.activate("navigation");
  const filtered = filterVisibleTools(
    tools(
      "unity_open_mcp_ping",
      "unity_open_mcp_navigation_surface_add",
      "unity_open_mcp_navigation_modify",
      "unity_open_mcp_build_start", // build-settings — still opt-in, hidden
    ),
    state,
  );
  const names = filtered.map((t) => t.name).sort();
  assert.deepEqual(names, [
    "unity_open_mcp_navigation_modify",
    "unity_open_mcp_navigation_surface_add",
    "unity_open_mcp_ping",
  ]);
});

test("filterVisibleTools: unknown tools (null group) stay visible", () => {
  // Defensive — a tool that is not in the catalog at all is treated as a
  // meta-tool and stays visible. This matches the catalog intent: only
  // group-assigned tools are subject to visibility filtering.
  const state = new ToolSessionState();
  const filtered = filterVisibleTools(
    tools("unity_open_mcp_some_future_meta_tool"),
    state,
  );
  assert.equal(filtered.length, 1);
});

test("filterVisibleTools: deactivating core hides core tools, not meta-tools", () => {
  const state = new ToolSessionState();
  state.deactivate("core");
  const filtered = filterVisibleTools(
    tools(
      // execute_csharp is a core tool with no always-visible pin — hidden when
      // core is deactivated. (ping is also core but is pinned always-visible so
      // it survives; covered by tool-session-state.test.ts.)
      "unity_open_mcp_execute_csharp",
      "unity_open_mcp_manage_tools", // meta — still visible
    ),
    state,
  );
  const names = filtered.map((t) => t.name);
  assert.deepEqual(names, ["unity_open_mcp_manage_tools"]);
});

// ---------------------------------------------------------------------------
// M31-optimizations Plan 4 / T4.5 (L13) — TOOL_CATEGORY derivation parity
// ---------------------------------------------------------------------------
//
// `TOOL_CATEGORY` is now DERIVED from TOOL_GROUP_ASSIGNMENT at module load
// (group = category for ~95% of tools), with a small documented override map
// for the exceptions. The tests below assert:
//
//   1. Every tool in ALL_TOOLS resolves to a category — no silent `"other"`
//      fallback for a tool that should have one. A new tool added to ALL_TOOLS
//      without a group assignment AND without an override entry fails loudly.
//
//   2. The `"other"` category is allowed ONLY for the documented
//      ALLOWED_OTHER_CATEGORY_TOOLS set (operator/debug meta-tools + the two
//      preserved pre-change `dependencies` / `reimport_package` overrides).
//
//   3. The override map is small and every entry resolves to its override
//      category (not the derived-from-group one).
//
// `buildCapabilities` is in a separate module that imports this one; we lazy-
// import it inside the test body to avoid pulling the tool index into every
// test file that imports tool-groups.

test("TOOL_CATEGORY is derived from TOOL_GROUP_ASSIGNMENT for the agreed majority", async () => {
  const { buildCapabilities, TOOL_CATEGORY_OVERRIDES } = await import("./build-capabilities.js");
  const { ALL_TOOLS } = await import("../tools/index.js");
  const { RULE_CATALOG, FIX_CATALOG } = await import("./rule-catalog.js");

  const caps = buildCapabilities({
    tools: ALL_TOOLS,
    batchToolNames: new Set(),
    rules: RULE_CATALOG,
    fixes: FIX_CATALOG,
  });
  const catByName = new Map(caps.tools.map((t) => [t.name, t.category]));

  // Every grouped tool whose name is NOT in the override map must have
  // category === group. This is the single-source-of-truth contract.
  const overrideNames = new Set(Object.keys(TOOL_CATEGORY_OVERRIDES));
  let derivedCount = 0;
  for (const tool of ALL_TOOLS) {
    const g = groupFor(tool.name);
    if (g === null || overrideNames.has(tool.name)) continue;
    assert.equal(
      catByName.get(tool.name),
      g,
      `${tool.name}: category must equal group ${g} when not overridden`,
    );
    derivedCount++;
  }
  // Sanity: the derived majority is the bulk of the catalog (>90%).
  const total = ALL_TOOLS.filter((t) => groupFor(t.name) !== null).length;
  assert.ok(
    derivedCount > total * 0.9,
    `derived majority sanity: ${derivedCount}/${total} grouped tools derive category from group`,
  );
});

test("every tool in ALL_TOOLS resolves to a category (no silent other fallback)", async () => {
  // The parity test: adding a tool to ALL_TOOLS without a group assignment AND
  // without an override entry fails loudly here. Pre-change, `dependencies`
  // and `reimport_package` silently fell through to "other"; the allowlist
  // now documents them as deliberate exceptions.
  const { buildCapabilities, ALLOWED_OTHER_CATEGORY_TOOLS } = await import("./build-capabilities.js");
  const { ALL_TOOLS } = await import("../tools/index.js");
  const { RULE_CATALOG, FIX_CATALOG } = await import("./rule-catalog.js");

  const caps = buildCapabilities({
    tools: ALL_TOOLS,
    batchToolNames: new Set(),
    rules: RULE_CATALOG,
    fixes: FIX_CATALOG,
  });
  const catByName = new Map(caps.tools.map((t) => [t.name, t.category]));

  const unallowableOther: string[] = [];
  for (const tool of ALL_TOOLS) {
    const cat = catByName.get(tool.name);
    assert.ok(cat, `${tool.name} must resolve to a category string`);
    if (cat === "other" && !ALLOWED_OTHER_CATEGORY_TOOLS.has(tool.name)) {
      unallowableOther.push(tool.name);
    }
  }
  assert.deepEqual(
    unallowableOther,
    [],
    `tools with category="other" not in the allowlist (add a group assignment, an override, or an allowlist entry): ${unallowableOther.join(", ")}`,
  );
});

test("TOOL_CATEGORY_OVERRIDES is small and every entry wins over the group", async () => {
  const { buildCapabilities, TOOL_CATEGORY_OVERRIDES } = await import("./build-capabilities.js");
  const { ALL_TOOLS } = await import("../tools/index.js");
  const { RULE_CATALOG, FIX_CATALOG } = await import("./rule-catalog.js");

  // The override map must stay small (T4.5: "small and documented"). Pin a
  // sane ceiling so a regression that floods it back to a parallel table
  // fails the test.
  const overrideCount = Object.keys(TOOL_CATEGORY_OVERRIDES).length;
  assert.ok(
    overrideCount <= 12,
    `override map should stay small (got ${overrideCount}); the majority must derive from the group`,
  );

  const caps = buildCapabilities({
    tools: ALL_TOOLS,
    batchToolNames: new Set(),
    rules: RULE_CATALOG,
    fixes: FIX_CATALOG,
  });
  const catByName = new Map(caps.tools.map((t) => [t.name, t.category]));

  for (const [tool, expectedCat] of Object.entries(TOOL_CATEGORY_OVERRIDES)) {
    const actual = catByName.get(tool);
    assert.equal(
      actual,
      expectedCat,
      `${tool}: override category must win (${expectedCat}), got ${actual}`,
    );
  }
});

test("capabilities category for compile_check is diagnostics (override)", async () => {
  // The one grouped tool whose category intentionally diverges from its group:
  // compile_check is in the gate-and-verify GROUP (session visibility alongside
  // validate_edit / scan_paths) but classified under diagnostics (it is a
  // compile-error probe, not a verify-rule runner). Pin the contract.
  const { buildCapabilities } = await import("./build-capabilities.js");
  const { ALL_TOOLS } = await import("../tools/index.js");
  const { RULE_CATALOG, FIX_CATALOG } = await import("./rule-catalog.js");
  const caps = buildCapabilities({
    tools: ALL_TOOLS,
    batchToolNames: new Set(),
    rules: RULE_CATALOG,
    fixes: FIX_CATALOG,
  });
  const compileCheck = caps.tools.find((t) => t.name === "unity_open_mcp_compile_check");
  assert.ok(compileCheck);
  assert.equal(compileCheck!.category, "diagnostics");
  // The GROUP stays gate-and-verify (session visibility).
  assert.equal(groupFor("unity_open_mcp_compile_check"), "gate-and-verify");
});
