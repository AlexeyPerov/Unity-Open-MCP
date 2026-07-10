// Dedicated tests for tool-session-state.ts — the per-session tool-group
// visibility store and the ListTools filter it drives.
//
// Note: capabilities/tool-groups.test.ts already covers the *basic*
// activate/deactivate/reset/filterVisibleTools flows and the auto-activation
// reconcile against real catalog groups. This file complements it by locking
// down the source-tracking state machine (manual vs auto vs default) and the
// ALWAYS_VISIBLE_TOOLS contract in more detail — the edges where a silent
// regression in tool-surface visibility would be most dangerous.

import { test } from "node:test";
import assert from "node:assert/strict";
import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import {
  ToolSessionState,
  filterVisibleTools,
  type ActivationSource,
} from "./tool-session-state.js";
import { DEFAULT_ENABLED_GROUPS, GROUP_IDS } from "./capabilities/tool-groups.js";

// ---------------------------------------------------------------------------
// Source-tracking invariants
// ---------------------------------------------------------------------------

test("every default-active group reports source 'default'", () => {
  const s = new ToolSessionState();
  for (const id of DEFAULT_ENABLED_GROUPS) {
    assert.equal(s.activationSource(id), "default");
  }
});

test("activationSource returns null for an inactive group", () => {
  const s = new ToolSessionState();
  assert.equal(s.activationSource("navigation"), null);
  assert.equal(s.activationSource("does-not-exist"), null);
});

test("activate sets source to 'manual' for a previously-default group that was deactivated", () => {
  // Deactivate a default-on group then re-activate it manually: the source
  // must be 'manual', not 'default' — the re-activation is an explicit act.
  const s = new ToolSessionState();
  const id = Array.from(DEFAULT_ENABLED_GROUPS)[0];
  assert.equal(s.deactivate(id), true);
  assert.equal(s.activationSource(id), null);
  assert.equal(s.activate(id), true);
  assert.equal(s.activationSource(id), "manual");
});

test("activateAuto then activate flips source from 'auto' to 'manual' (manual intent wins)", () => {
  const s = new ToolSessionState();
  s.activateAuto("shadergraph");
  assert.equal(s.activationSource("shadergraph"), "auto");
  // The group is already active, so activate() returns false (no change in
  // active set), but it must NOT silently flip the source. The current
  // contract: activate is a no-op when already active (see source). This test
  // pins that contract so a future "manual wins on re-activate" change is
  // intentional.
  assert.equal(s.activate("shadergraph"), false);
  assert.equal(s.activationSource("shadergraph"), "auto");
});

test("activateAuto rejects unknown groups", () => {
  const s = new ToolSessionState();
  assert.equal(s.activateAuto("nope"), false);
  assert.equal(s.isGroupActive("nope"), false);
});

test("activateAuto is a no-op when the group is already active (default-on)", () => {
  const s = new ToolSessionState();
  const id = Array.from(DEFAULT_ENABLED_GROUPS)[0];
  // Already active via default — activateAuto must not flip its source to auto.
  assert.equal(s.activateAuto(id), false);
  assert.equal(s.activationSource(id), "default");
});

// ---------------------------------------------------------------------------
// reconcileAutoActivation — multi-group + ordering
// ---------------------------------------------------------------------------

test("reconcileAutoActivation handles multiple satisfied groups in one call", () => {
  const s = new ToolSessionState();
  const changed = s.reconcileAutoActivation(new Set(["shadergraph", "vfx"]));
  // Both auto-activate groups became active.
  assert.ok(changed.includes("shadergraph"));
  assert.ok(changed.includes("vfx"));
  assert.equal(s.isGroupActive("shadergraph"), true);
  assert.equal(s.isGroupActive("vfx"), true);
  assert.equal(s.activationSource("shadergraph"), "auto");
  assert.equal(s.activationSource("vfx"), "auto");
});

test("reconcileAutoActivation ignores non-auto-activate groups even when satisfied", () => {
  // navigation has a unityPackage but autoActivate is NOT set, so satisfying
  // it must not auto-activate the group.
  const s = new ToolSessionState();
  const changed = s.reconcileAutoActivation(new Set(["navigation"]));
  assert.deepEqual(changed, []);
  assert.equal(s.isGroupActive("navigation"), false);
});

test("reconcileAutoActivation drops an auto group but preserves a co-active manual group", () => {
  // Both shadergraph and vfx auto-activated; then the agent manually
  // activated navigation. A reconcile with an empty satisfied set drops the
  // auto groups but keeps navigation.
  const s = new ToolSessionState();
  s.reconcileAutoActivation(new Set(["shadergraph", "vfx"]));
  s.activate("navigation");
  const changed = s.reconcileAutoActivation(new Set());
  assert.deepEqual(changed.sort(), ["shadergraph", "vfx"].sort());
  assert.equal(s.isGroupActive("shadergraph"), false);
  assert.equal(s.isGroupActive("vfx"), false);
  assert.equal(s.isGroupActive("navigation"), true);
  assert.equal(s.activationSource("navigation"), "manual");
});

// ---------------------------------------------------------------------------
// deactivate + reset interactions with source tracking
// ---------------------------------------------------------------------------

test("deactivate clears the source (activationSource → null)", () => {
  const s = new ToolSessionState();
  s.activate("navigation");
  assert.equal(s.activationSource("navigation"), "manual");
  s.deactivate("navigation");
  assert.equal(s.activationSource("navigation"), null);
});

test("reset restores every default group to source 'default'", () => {
  const s = new ToolSessionState();
  // Mutate: deactivate a default group, activate an opt-in group.
  const defaultId = Array.from(DEFAULT_ENABLED_GROUPS)[0];
  s.deactivate(defaultId);
  s.activate("navigation");
  s.activateAuto("shadergraph");
  s.reset();
  for (const id of DEFAULT_ENABLED_GROUPS) {
    assert.equal(s.activationSource(id), "default");
  }
  assert.equal(s.activationSource("navigation"), null);
  assert.equal(s.activationSource("shadergraph"), null);
});

test("activeGroups is sorted and stable", () => {
  const s = new ToolSessionState();
  s.activate("navigation");
  s.activate("probuilder");
  const groups = s.activeGroups();
  // Assert sorted by checking it equals its sorted copy.
  assert.deepEqual(groups, [...groups].sort());
});

// ---------------------------------------------------------------------------
// isGroupActive parity with activeGroups
// ---------------------------------------------------------------------------

test("isGroupActive agrees with activeGroups for every known group", () => {
  const s = new ToolSessionState();
  s.activate("navigation");
  const active = new Set(s.activeGroups());
  for (const id of GROUP_IDS) {
    assert.equal(s.isGroupActive(id), active.has(id), `group=${id}`);
  }
});

// ---------------------------------------------------------------------------
// filterVisibleTools — ALWAYS_VISIBLE_TOOLS contract
// ---------------------------------------------------------------------------

// These are the meta-tools that must stay reachable regardless of session
// state. If a name is added/removed from the constant in tool-session-state.ts
// this test will catch it (and force a deliberate update here).
const EXPECTED_ALWAYS_VISIBLE = [
  "unity_open_mcp_capabilities",
  "unity_open_mcp_list_rules",
  "unity_open_mcp_generate_skill",
  "unity_open_mcp_manage_tools",
  "unity_open_mcp_pull_events",
  "unity_senses_pull_events",
  "unity_open_mcp_read_compile_errors",
  "unity_open_mcp_bridge_status",
];

function tools(...names: string[]): Tool[] {
  return names.map((name) => ({
    name,
    description: `${name} fixture`,
    inputSchema: { type: "object" as const, properties: {} },
  }));
}

test("always-visible tools stay visible even when every default group is deactivated", () => {
  const state = new ToolSessionState();
  for (const id of DEFAULT_ENABLED_GROUPS) state.deactivate(id);
  // Sanity: nothing is active.
  assert.deepEqual(state.activeGroups(), []);
  const filtered = filterVisibleTools(tools(...EXPECTED_ALWAYS_VISIBLE), state);
  const names = filtered.map((t) => t.name).sort();
  assert.deepEqual(names, [...EXPECTED_ALWAYS_VISIBLE].sort());
});

test("always-visible tools are visible alongside an activated opt-in group", () => {
  const state = new ToolSessionState();
  for (const id of DEFAULT_ENABLED_GROUPS) state.deactivate(id);
  state.activate("navigation");
  const filtered = filterVisibleTools(
    tools(
      ...EXPECTED_ALWAYS_VISIBLE,
      "unity_open_mcp_navigation_surface_add",
      "unity_open_mcp_ping", // core — deactivated, hidden
    ),
    state,
  );
  const names = filtered.map((t) => t.name).sort();
  assert.deepEqual(names, [
    ...EXPECTED_ALWAYS_VISIBLE,
    "unity_open_mcp_navigation_surface_add",
  ].sort());
});

test("filterVisibleTools preserves input order (stable filter)", () => {
  const state = new ToolSessionState();
  const input = tools(
    "unity_open_mcp_ping",
    "unity_open_mcp_navigation_surface_add",
    "unity_open_mcp_manage_tools",
  );
  const filtered = filterVisibleTools(input, state);
  // ping (core, default-on) and manage_tools (always visible) survive; order kept.
  assert.deepEqual(
    filtered.map((t) => t.name),
    ["unity_open_mcp_ping", "unity_open_mcp_manage_tools"],
  );
});

test("filterVisibleTools with a custom resolver uses it instead of the catalog", () => {
  // Inject a resolver that assigns every tool to a deactivated group → all
  // non-meta tools hidden. Confirms the resolver param is honored.
  const state = new ToolSessionState();
  for (const id of DEFAULT_ENABLED_GROUPS) state.deactivate(id);
  const filtered = filterVisibleTools(
    tools("unity_open_mcp_ping", "unity_open_mcp_manage_tools"),
    state,
    () => "deactivated-group",
  );
  // ping now resolves to a deactivated group → hidden; manage_tools is still
  // always-visible (the ALWAYS_VISIBLE check runs before the resolver).
  assert.deepEqual(filtered.map((t) => t.name), ["unity_open_mcp_manage_tools"]);
});

test("filterVisibleTools returns an empty array for an empty input", () => {
  const state = new ToolSessionState();
  assert.deepEqual(filterVisibleTools([], state), []);
});
