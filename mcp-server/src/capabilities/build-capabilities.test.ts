import test from "node:test";
import assert from "node:assert/strict";

import {
  buildCapabilities,
  PLANNED_TOOLS,
} from "./build-capabilities.js";
import {
  RULE_CATALOG,
  FIX_CATALOG,
  implementedRules,
  plannedRules,
  implementedFixes,
} from "./rule-catalog.js";
import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// ---------------------------------------------------------------------------
// Test fixtures — stand in for ALL_TOOLS / BATCH_TOOL_NAMES without importing
// production modules that have cross-file runtime imports (strip-types safe).
// ---------------------------------------------------------------------------

const FIXTURE_TOOLS: Tool[] = [
  {
    name: "unity_open_mcp_ping",
    description: "Bridge health check.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
  },
  {
    name: "unity_open_mcp_scan_paths",
    description: "Run verify rules scoped to paths.",
    inputSchema: { type: "object", required: ["paths"], properties: {} },
  },
  {
    name: "unity_open_mcp_list_assets",
    description: "List assets offline.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "unity_open_mcp_find_references",
    description: "Reverse dependency lookup.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "unity_open_mcp_capabilities",
    description: "Discover the full capability surface.",
    inputSchema: { type: "object", properties: {} },
  },
];

const FIXTURE_BATCH_NAMES: ReadonlySet<string> = new Set([
  "unity_open_mcp_scan_paths",
]);

const DEPS = {
  tools: FIXTURE_TOOLS,
  batchToolNames: FIXTURE_BATCH_NAMES,
  rules: RULE_CATALOG,
  fixes: FIX_CATALOG,
};

// ---------------------------------------------------------------------------
// Rule catalog
// ---------------------------------------------------------------------------

test("rule catalog contains all implemented rules", () => {
  const ids = RULE_CATALOG.filter((r) => r.implemented).map((r) => r.id);
  assert.ok(ids.includes("missing_references"));
  assert.ok(ids.includes("scene_prefab_health"));
  assert.ok(ids.includes("dependencies"));
});

test("implemented rules declare issue codes with severities", () => {
  for (const rule of implementedRules()) {
    assert.ok(
      rule.issues.length > 0,
      `${rule.id} should declare at least one issue code`,
    );
    for (const issue of rule.issues) {
      assert.ok(issue.code, "issue must have a code");
      assert.ok(
        issue.severity === "Error" || issue.severity === "Warning",
        `${rule.id}/${issue.code} severity must be Error or Warning`,
      );
      assert.ok(Array.isArray(issue.fixIds), "fixIds must be an array");
    }
  }
});

test("missing_script issue maps to remove_missing_script fix", () => {
  const rule = RULE_CATALOG.find((r) => r.id === "missing_references");
  assert.ok(rule);
  const issue = rule!.issues.find((i) => i.code === "missing_script");
  assert.ok(issue);
  assert.deepEqual(issue!.fixIds, ["remove_missing_script"]);
});

test("planned rules carry status and guidance, no hard errors", () => {
  const planned = plannedRules();
  assert.ok(planned.length >= 1, "should have at least one planned rule");
  for (const rule of planned) {
    assert.equal(rule.implemented, false);
    assert.equal(rule.status, "planned");
    assert.ok(
      typeof rule.guidance === "string" && rule.guidance.length > 0,
      `${rule.id} planned rule must carry guidance`,
    );
    assert.deepEqual(rule.issues, []);
  }
});

test("planned rule stubs from the bridge selector are all present", () => {
  const plannedIds = plannedRules().map((r) => r.id);
  const expected = [
    "asmdef_audit",
    "materials",
    "shader_analysis",
    "textures",
    "sprite_2d_analysis",
    "animation_analysis",
    "audio_analysis",
  ];
  for (const id of expected) {
    assert.ok(plannedIds.includes(id), `planned rule ${id} missing from catalog`);
  }
});

// ---------------------------------------------------------------------------
// Fix catalog
// ---------------------------------------------------------------------------

test("remove_missing_script fix is registered and safe", () => {
  const fix = implementedFixes().find((f) => f.id === "remove_missing_script");
  assert.ok(fix);
  assert.equal(fix!.implemented, true);
  assert.equal(fix!.safe, true);
  assert.ok(fix!.rules.includes("missing_references"));
  assert.ok(fix!.issueCodes.includes("missing_script"));
});

// ---------------------------------------------------------------------------
// buildCapabilities — full surface
// ---------------------------------------------------------------------------

test("buildCapabilities returns tools, rules, and fixes in one call", () => {
  const caps = buildCapabilities(DEPS);
  assert.ok(caps.tools.length > 0);
  assert.ok(caps.rules.length > 0);
  assert.ok(caps.fixes.length > 0);
});

test("every registered tool appears as implemented with its schema", () => {
  const caps = buildCapabilities(DEPS);
  const implemented = caps.tools.filter((t) => t.implemented);
  assert.equal(implemented.length, FIXTURE_TOOLS.length);
  for (const tool of implemented) {
    assert.ok(tool.name, "tool must have a name");
    assert.ok(tool.inputSchema, `${tool.name} must carry inputSchema`);
    assert.equal(typeof tool.routePolicy, "string");
    assert.equal(typeof tool.batchCapable, "boolean");
    assert.equal(typeof tool.category, "string");
  }
});

test("capabilities tool itself is in the implemented surface", () => {
  const caps = buildCapabilities(DEPS);
  const found = caps.tools.find(
    (t) => t.name === "unity_open_mcp_capabilities",
  );
  assert.ok(found, "unity_open_mcp_capabilities must be discoverable");
  assert.equal(found!.implemented, true);
  assert.equal(found!.category, "capability-discovery");
});

test("batch capability flag reflects the injected allow-list", () => {
  const caps = buildCapabilities(DEPS);
  const scanPaths = caps.tools.find((t) => t.name === "unity_open_mcp_scan_paths");
  assert.ok(scanPaths);
  assert.equal(scanPaths!.batchCapable, true);

  const ping = caps.tools.find((t) => t.name === "unity_open_mcp_ping");
  assert.ok(ping);
  assert.equal(ping!.batchCapable, false);
});

test("route policy is assigned per tool", () => {
  const caps = buildCapabilities(DEPS);
  const listAssets = caps.tools.find((t) => t.name === "unity_open_mcp_list_assets");
  assert.equal(listAssets!.routePolicy, "offline");

  const findRefs = caps.tools.find((t) => t.name === "unity_open_mcp_find_references");
  assert.equal(findRefs!.routePolicy, "offline-first");

  const ping = caps.tools.find((t) => t.name === "unity_open_mcp_ping");
  assert.equal(ping!.routePolicy, "live");
});

test("planned tools surface with status planned and guidance", () => {
  const caps = buildCapabilities(DEPS);
  const planned = caps.tools.filter((t) => !t.implemented);
  assert.equal(planned.length, PLANNED_TOOLS.length);
  for (const tool of planned) {
    assert.equal(tool.status, "planned");
    assert.ok(
      typeof tool.guidance === "string" && tool.guidance.length > 0,
      `${tool.name} planned tool must carry guidance`,
    );
  }
});

test("counts reflect implemented vs planned split", () => {
  const caps = buildCapabilities(DEPS);
  assert.equal(caps.counts.toolsImplemented, FIXTURE_TOOLS.length);
  assert.equal(caps.counts.toolsPlanned, PLANNED_TOOLS.length);
  assert.equal(caps.counts.rulesImplemented, implementedRules().length);
  assert.equal(caps.counts.rulesPlanned, plannedRules().length);
  assert.equal(caps.counts.fixesImplemented, implementedFixes().length);
});

// ---------------------------------------------------------------------------
// Routing summary (T1.5.1)
// ---------------------------------------------------------------------------

test("capabilities include a top-level routing summary", () => {
  const caps = buildCapabilities(DEPS);
  assert.ok(caps.routing, "routing summary must be present");
  assert.equal(typeof caps.routing.liveDefault, "boolean");
  assert.equal(typeof caps.routing.batchFallback, "boolean");
  assert.ok(Array.isArray(caps.routing.batchRequirements));
  assert.ok(Array.isArray(caps.routing.batchBlocked));
  assert.ok(Array.isArray(caps.routing.liveOnlyCategories));
});

test("routing summary names the batch env vars and the blocked meta-tools", () => {
  const caps = buildCapabilities(DEPS);
  const r = caps.routing;
  assert.ok(r.batchRequirements.includes("UNITY_PATH"));
  assert.ok(r.batchRequirements.includes("UNITY_PROJECT_PATH"));
  const blockedNames = r.batchBlocked.map((b) => b.tool);
  assert.ok(blockedNames.includes("unity_open_mcp_execute_csharp"));
  assert.ok(blockedNames.includes("unity_open_mcp_invoke_method"));
  assert.ok(blockedNames.includes("unity_open_mcp_execute_menu"));
  for (const b of r.batchBlocked) {
    assert.ok(
      typeof b.reason === "string" && b.reason.length > 0,
      `${b.tool} blocked entry must carry a reason`,
    );
  }
});

test("routing summary marks agent-senses as live-only", () => {
  const caps = buildCapabilities(DEPS);
  assert.ok(
    caps.routing.liveOnlyCategories.includes("agent-senses"),
    "agent-senses category must be flagged live-only",
  );
});

test("routing summary is returned even with a kind filter", () => {
  // An agent asking only for rules still benefits from the routing
  // narrative — the summary is independent of the kind filter.
  const caps = buildCapabilities(DEPS, { kind: "rules" });
  assert.ok(caps.routing);
  assert.ok(caps.routing.batchRequirements.includes("UNITY_PROJECT_PATH"));
});

// ---------------------------------------------------------------------------
// buildCapabilities — filters
// ---------------------------------------------------------------------------

test("kind=rules returns only rules", () => {
  const caps = buildCapabilities(DEPS, { kind: "rules" });
  assert.equal(caps.tools.length, 0);
  assert.ok(caps.rules.length > 0);
  assert.equal(caps.fixes.length, 0);
});

test("kind=tools returns only tools", () => {
  const caps = buildCapabilities(DEPS, { kind: "tools" });
  assert.ok(caps.tools.length > 0);
  assert.equal(caps.rules.length, 0);
  assert.equal(caps.fixes.length, 0);
});

test("kind=fixes returns only fixes", () => {
  const caps = buildCapabilities(DEPS, { kind: "fixes" });
  assert.equal(caps.tools.length, 0);
  assert.equal(caps.rules.length, 0);
  assert.ok(caps.fixes.length > 0);
});

test("includePlanned=false drops planned items", () => {
  const caps = buildCapabilities(DEPS, { includePlanned: false });
  for (const t of caps.tools) assert.equal(t.implemented, true);
  for (const r of caps.rules) assert.equal(r.implemented, true);
  for (const f of caps.fixes) assert.equal(f.implemented, true);
  assert.equal(caps.counts.toolsPlanned, 0);
  assert.equal(caps.counts.rulesPlanned, 0);
});

// ---------------------------------------------------------------------------
// Structural invariants
// ---------------------------------------------------------------------------

test("no duplicate tool names in the catalog", () => {
  const caps = buildCapabilities(DEPS);
  const names = caps.tools.map((t) => t.name);
  assert.equal(new Set(names).size, names.length);
});

test("rule catalog has no duplicate ids", () => {
  const ids = RULE_CATALOG.map((r) => r.id);
  assert.equal(new Set(ids).size, ids.length);
});

test("fix catalog has no duplicate ids", () => {
  const ids = FIX_CATALOG.map((f) => f.id);
  assert.equal(new Set(ids).size, ids.length);
});
