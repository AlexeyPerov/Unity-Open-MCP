// M18 Plan 2 / T18.2 — tests for the canonical tool-group catalog and the
// per-session visibility store.

import test from "node:test";
import assert from "node:assert/strict";

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

import {
  TOOL_GROUPS,
  DEFAULT_ENABLED_GROUPS,
  GROUP_IDS,
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
  // in the catalog. Today that is `core` plus the always-useful verify /
  // asset / typed-editor / diagnostics groups (extended in the "Extended
  // default-enabled tools" change). Asserting against the catalog keeps this
  // test honest when defaults change instead of hard-coding a snapshot.
  const expected = TOOL_GROUPS.filter((g) => g.defaultEnabled)
    .map((g) => g.id)
    .sort();
  assert.deepEqual(Array.from(DEFAULT_ENABLED_GROUPS).sort(), expected);
  // `core` is always default-on — the essential entry points live there.
  assert.ok(DEFAULT_ENABLED_GROUPS.has("core"));
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
      "unity_open_mcp_ping", // core — now hidden
      "unity_open_mcp_manage_tools", // meta — still visible
    ),
    state,
  );
  const names = filtered.map((t) => t.name);
  assert.deepEqual(names, ["unity_open_mcp_manage_tools"]);
});
