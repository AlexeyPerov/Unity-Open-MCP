// Tests for the capabilities output-profile + paging view.
//
// specs/feedback.md 2026-08-24 — `capabilities` is the "call this first" tool
// but returned ~513 KB in one shot. Pins:
//  1. `compact` drops the prose (tool inputSchema/description, rule
//     description, group description/usageHint) and folds group rosters.
//  2. `balanced` keeps descriptions and still drops inputSchema.
//  3. `full` is byte-identical to the unfolded result for tools/rules/groups.
//  4. Sizes shrink monotonically: full > balanced > compact.
//  5. Paging walks `tools[]` with a resumable cursor and exact count math.
//  6. A cursor minted for another tool resets to the first page.
//  7. `profile` + `profileHint` are echoed so an agent can expand.
//
// Pure-function tests over the real catalog (ALL_TOOLS) — no I/O.

import test from "node:test";
import assert from "node:assert/strict";

import { buildCapabilities, type CapabilitiesResult } from "./build-capabilities.js";
import { RULE_CATALOG, FIX_CATALOG } from "./rule-catalog.js";
import { ALL_TOOLS } from "../tools/index.js";
import { BATCH_TOOL_NAMES } from "../batch-spawn.js";
import { viewCapabilities, CAPABILITIES_CURSOR_KEY } from "./capabilities-view.js";

function build(kind?: "tools" | "rules" | "fixes"): CapabilitiesResult {
  return buildCapabilities(
    {
      tools: ALL_TOOLS,
      batchToolNames: BATCH_TOOL_NAMES,
      rules: RULE_CATALOG,
      fixes: FIX_CATALOG,
    },
    { kind },
  );
}

const size = (value: unknown): number => JSON.stringify(value).length;

// ---------------------------------------------------------------------------
// Profile folding
// ---------------------------------------------------------------------------

test("compact drops per-tool inputSchema and description", () => {
  const view = viewCapabilities(build(), { profile: "compact" });
  const tools = view.tools as Record<string, unknown>[];
  assert.ok(tools.length > 250, "the full tool catalog should be present");
  for (const tool of tools) {
    assert.equal("inputSchema" in tool, false, `${String(tool.name)} kept inputSchema`);
    assert.equal("description" in tool, false, `${String(tool.name)} kept description`);
    // The identity + routing metadata an agent branches on must survive.
    assert.equal(typeof tool.name, "string");
    assert.equal(typeof tool.implemented, "boolean");
    assert.equal(typeof tool.batchCapable, "boolean");
    assert.equal(typeof tool.routePolicy, "string");
    assert.equal(typeof tool.lifecycle, "string");
    assert.ok("group" in tool, "group must survive the fold — it replaces the group roster");
  }
});

test("compact folds group rosters to toolCount and drops group prose", () => {
  const view = viewCapabilities(build(), { profile: "compact" });
  const groups = view.toolGroups as Record<string, unknown>[];
  assert.ok(groups.length > 0);
  for (const group of groups) {
    assert.equal("tools" in group, false, `${String(group.id)} kept its roster`);
    assert.equal("description" in group, false);
    assert.equal("usageHint" in group, false);
    assert.equal(typeof group.toolCount, "number");
    assert.equal(typeof group.id, "string");
  }
});

test("compact drops rule descriptions but keeps applicability", () => {
  const view = viewCapabilities(build(), { profile: "compact" });
  const rules = view.rules as Record<string, unknown>[];
  assert.ok(rules.length > 0);
  for (const rule of rules) {
    assert.equal("description" in rule, false, `${String(rule.id)} kept description`);
    assert.equal(typeof rule.id, "string");
    assert.ok(Array.isArray(rule.applicableAssetKinds));
  }
});

test("balanced keeps descriptions and still drops inputSchema", () => {
  const view = viewCapabilities(build(), { profile: "balanced" });
  const tools = view.tools as Record<string, unknown>[];
  for (const tool of tools) {
    assert.equal("inputSchema" in tool, false, `${String(tool.name)} kept inputSchema`);
    assert.equal(typeof tool.description, "string");
  }
  // Rules and groups are unfolded at balanced.
  const rules = view.rules as Record<string, unknown>[];
  assert.equal(typeof rules[0].description, "string");
  const groups = view.toolGroups as Record<string, unknown>[];
  assert.ok(Array.isArray(groups[0].tools));
});

test("full is the unfolded shape", () => {
  const result = build();
  const view = viewCapabilities(result, { profile: "full" });
  assert.equal(JSON.stringify(view.tools), JSON.stringify(result.tools));
  assert.equal(JSON.stringify(view.rules), JSON.stringify(result.rules));
  assert.equal(JSON.stringify(view.toolGroups), JSON.stringify(result.toolGroups));
});

test("payload shrinks monotonically compact < balanced < full", () => {
  const compact = size(viewCapabilities(build(), { profile: "compact" }));
  const balanced = size(viewCapabilities(build(), { profile: "balanced" }));
  const full = size(viewCapabilities(build(), { profile: "full" }));
  assert.ok(compact < balanced, `compact ${compact} should be < balanced ${balanced}`);
  assert.ok(balanced < full, `balanced ${balanced} should be < full ${full}`);
  // The point of the change: the default profile is a fraction of the
  // unfolded catalog. A regression that re-inlines the schemas fails here.
  assert.ok(
    compact * 3 < full,
    `compact ${compact} should be well under a third of full ${full}`,
  );
});

// ---------------------------------------------------------------------------
// Paging
// ---------------------------------------------------------------------------

test("page_size bounds the tools list and mints a resumable cursor", () => {
  const total = build().tools.length;
  const first = viewCapabilities(build(), { profile: "compact", page_size: 40 });
  assert.equal((first.tools as unknown[]).length, 40);
  assert.equal(first.pagination.page_size, 40);
  assert.equal(first.pagination.cursor, null);
  assert.equal(first.pagination.next_cursor, `${CAPABILITIES_CURSOR_KEY}:40`);
  assert.equal(first.pagination.truncated, total - 40);

  const second = viewCapabilities(build(), {
    profile: "compact",
    page_size: 40,
    cursor: first.pagination.next_cursor!,
  });
  assert.equal((second.tools as unknown[]).length, 40);
  assert.equal(second.pagination.cursor, `${CAPABILITIES_CURSOR_KEY}:40`);
  assert.equal(second.pagination.next_cursor, `${CAPABILITIES_CURSOR_KEY}:80`);
  // No overlap between consecutive pages.
  const firstNames = (first.tools as { name: string }[]).map((t) => t.name);
  const secondNames = (second.tools as { name: string }[]).map((t) => t.name);
  assert.equal(firstNames.some((n) => secondNames.includes(n)), false);
});

test("walking every page yields the whole catalog exactly once", () => {
  const seen: string[] = [];
  let cursor: string | undefined;
  for (let guard = 0; guard < 100; guard++) {
    const view = viewCapabilities(build(), { profile: "compact", page_size: 50, cursor });
    seen.push(...(view.tools as { name: string }[]).map((t) => t.name));
    if (view.pagination.next_cursor === null) break;
    cursor = view.pagination.next_cursor;
  }
  const expected = build().tools.map((t) => t.name);
  assert.deepEqual(seen, expected);
  assert.equal(new Set(seen).size, seen.length, "no tool should repeat across pages");
});

test("omitting page_size returns every tool with a terminal pagination block", () => {
  const view = viewCapabilities(build(), { profile: "compact" });
  assert.equal((view.tools as unknown[]).length, build().tools.length);
  assert.equal(view.pagination.next_cursor, null);
  assert.equal(view.pagination.truncated, 0);
  assert.equal(view.pagination.page_size, 0);
});

test("a cursor minted for another tool resets to the first page", () => {
  const view = viewCapabilities(build(), {
    profile: "compact",
    page_size: 10,
    cursor: "search_assets:120",
  });
  const names = (view.tools as { name: string }[]).map((t) => t.name);
  assert.deepEqual(names, build().tools.slice(0, 10).map((t) => t.name));
});

test("kind:'rules' leaves the tools page empty and pagination terminal", () => {
  const view = viewCapabilities(build("rules"), { profile: "compact", page_size: 40 });
  assert.deepEqual(view.tools, []);
  assert.equal(view.pagination.next_cursor, null);
  assert.ok((view.rules as unknown[]).length > 0);
});

// ---------------------------------------------------------------------------
// Self-describing envelope
// ---------------------------------------------------------------------------

test("the response echoes the profile and names what was folded", () => {
  for (const profile of ["compact", "balanced", "full"] as const) {
    const view = viewCapabilities(build(), { profile });
    assert.equal(view.profile, profile);
    assert.equal(typeof view.profileHint, "string");
    assert.ok(view.profileHint.length > 40, "the hint must be actionable, not a label");
  }
  // The compact hint has to name the way out, or an agent cannot expand.
  const compact = viewCapabilities(build(), { profile: "compact" });
  assert.match(compact.profileHint, /balanced/);
  assert.match(compact.profileHint, /full/);
  assert.match(compact.profileHint, /page_size/);
});

test("non-tool blocks are passed through untouched by every profile", () => {
  const result = build();
  for (const profile of ["compact", "balanced", "full"] as const) {
    const view = viewCapabilities(result, { profile });
    assert.deepEqual(view.counts, result.counts);
    assert.deepEqual(view.routing, result.routing);
    assert.deepEqual(view.costHints, result.costHints);
    assert.deepEqual(view.lifecycleBlock, result.lifecycleBlock);
    assert.deepEqual(view.fixes, result.fixes);
    assert.equal(view.bridgeReachable, result.bridgeReachable);
  }
});
