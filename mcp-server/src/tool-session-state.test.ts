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
  FD_SAMPLE_RING_CAPACITY,
  type ActivationSource,
} from "./tool-session-state.js";
// M31 Plan 6 / T6.1 — type-only import for the fdSample fixture builder; the
// ring tests in this file pin the bulk-splice overflow contract.
import type { FdSample } from "./process-diagnostics.js";
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
  "unity_open_mcp_ping",
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
      // execute_csharp is a core tool — with core deactivated it is hidden,
      // demonstrating that only ALWAYS_VISIBLE_TOOLS survive the teardown.
      // (ping is core too but is always-visible — covered above.)
      "unity_open_mcp_execute_csharp",
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
    // execute_csharp is a core tool (NOT always-visible) — the injected
    // resolver sends it to a deactivated group, so it is hidden. ping is
    // always-visible, so it survives regardless of the resolver.
    tools("unity_open_mcp_execute_csharp", "unity_open_mcp_ping", "unity_open_mcp_manage_tools"),
    state,
    () => "deactivated-group",
  );
  // execute_csharp resolves to a deactivated group → hidden; ping and
  // manage_tools are always-visible (the ALWAYS_VISIBLE check runs before the
  // resolver).
  assert.deepEqual(
    filtered.map((t) => t.name),
    ["unity_open_mcp_ping", "unity_open_mcp_manage_tools"],
  );
});

test("filterVisibleTools returns an empty array for an empty input", () => {
  const state = new ToolSessionState();
  assert.deepEqual(filterVisibleTools([], state), []);
});

// T6.3 — ping is a health-check tool that must survive group deactivation. An
// agent that just tore down the core group (e.g. to slim its tool surface)
// still needs to re-probe the bridge before re-activating. ping is assigned to
// the `core` group in the catalog, but ALWAYS_VISIBLE_TOOLS wins, so it stays
// reachable here.
test("ping stays visible after manage_tools(deactivate, core)", () => {
  const state = new ToolSessionState();
  assert.ok(state.deactivate("core"), "core should be deactivatable");
  const filtered = filterVisibleTools(
    tools("unity_open_mcp_ping", "unity_open_mcp_execute_csharp"),
    state,
  );
  assert.deepEqual(
    filtered.map((t) => t.name),
    ["unity_open_mcp_ping"],
    "ping survives core deactivation; execute_csharp (core, not always-visible) is hidden",
  );
});

// ---------------------------------------------------------------------------
// M31 Plan 6 / T6.1 — fdSamples ring bulk-splice overflow
// ---------------------------------------------------------------------------

/**
 * Build a minimal FdSample for the ring tests. Only `ts` varies so we can
 * assert eviction order; `pid` and `count` are constants the ring math does
 * not inspect here.
 */
function fdSample(ts: number): FdSample {
  return { ts, pid: 12345, count: 100 };
}

test("fdSamples ring: under-capacity push keeps every sample", () => {
  const s = new ToolSessionState();
  for (let i = 0; i < 5; i++) s.recordFdSample(fdSample(i));
  const snap = s.fdSamplesSnapshot();
  assert.equal(snap.length, 5, "5 pushes under capacity → 5 samples");
  assert.deepEqual(
    snap.map((x) => x.ts),
    [0, 1, 2, 3, 4],
    "samples are oldest-first, no eviction",
  );
});

test("fdSamples ring: bulk-splice overflow drops the oldest excess in one go", () => {
  // Capacity is FD_SAMPLE_RING_CAPACITY (=20). Pushing 25 must drop the
  // oldest 5 and keep the newest 20. The old shift()-per-excess loop and the
  // new single splice() must both produce this exact shape — this test pins
  // the contract so a future rewrite cannot regress the eviction order.
  const s = new ToolSessionState();
  for (let i = 0; i < 25; i++) s.recordFdSample(fdSample(i));
  const snap = s.fdSamplesSnapshot();
  assert.equal(snap.length, FD_SAMPLE_RING_CAPACITY, "ring is exactly at capacity");
  // Oldest 5 (ts 0..4) evicted; newest 20 (ts 5..24) retained, oldest-first.
  assert.deepEqual(
    snap.map((x) => x.ts),
    Array.from({ length: FD_SAMPLE_RING_CAPACITY }, (_, k) => 5 + k),
    "overflow drops the oldest excess; newest samples retained in order",
  );
});

test("fdSamples ring: a single push past capacity drops exactly one", () => {
  // Fill to capacity, then push exactly one more. Only ts=0 should be gone.
  const s = new ToolSessionState();
  for (let i = 0; i < FD_SAMPLE_RING_CAPACITY; i++) s.recordFdSample(fdSample(i));
  s.recordFdSample(fdSample(999));
  const snap = s.fdSamplesSnapshot();
  assert.equal(snap.length, FD_SAMPLE_RING_CAPACITY);
  assert.equal(snap[0].ts, 1, "oldest (ts=0) evicted");
  assert.equal(snap[snap.length - 1].ts, 999, "newest (ts=999) retained");
});

test("fdSamples ring: clearFdSamples empties the ring without touching groups", () => {
  const s = new ToolSessionState();
  s.recordFdSample(fdSample(1));
  s.activate("navigation");
  s.clearFdSamples();
  assert.equal(s.fdSamplesSnapshot().length, 0, "ring cleared");
  assert.equal(s.isGroupActive("navigation"), true, "group state untouched");
});

test("fdSamples ring: reset clears the ring alongside group state", () => {
  const s = new ToolSessionState();
  s.recordFdSample(fdSample(1));
  s.activate("navigation");
  s.reset();
  assert.equal(s.fdSamplesSnapshot().length, 0, "reset clears the ring");
  assert.equal(s.isGroupActive("navigation"), false, "reset clears opt-in groups");
});
