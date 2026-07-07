import test from "node:test";
import assert from "node:assert/strict";

import {
  buildCapabilities,
  PLANNED_TOOLS,
  type BuildCapabilitiesDeps,
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
  {
    name: "unity_open_mcp_list_rules",
    description: "List every verify rule (implemented + planned).",
    inputSchema: { type: "object", properties: {} },
  },
  // M27 Plan 4 — batch_execute fixture. The capabilities builder must classify
  // it batchCapable:false (NOT in BATCH_TOOL_NAMES — live-only sequential invoke)
  // and surface its scene-dirty lifecycle. Lives in the `core` category.
  {
    name: "unity_open_mcp_batch_execute",
    description: "Run many typed tools sequentially inside the open Editor.",
    inputSchema: {
      type: "object",
      required: ["commands", "paths_hint"],
      properties: {},
    },
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

// T2.4 — broken-GUID fixes now have a provider.

test("missing_guid issue maps to relink_broken_guid fix", () => {
  const rule = RULE_CATALOG.find((r) => r.id === "missing_references");
  assert.ok(rule);
  const issue = rule!.issues.find((i) => i.code === "missing_guid");
  assert.ok(issue, "missing_guid must be a declared issue code");
  assert.deepEqual(issue!.fixIds, ["relink_broken_guid"]);
});

test("broken_dependency issue maps to relink_broken_guid fix", () => {
  const rule = RULE_CATALOG.find((r) => r.id === "dependencies");
  assert.ok(rule);
  const issue = rule!.issues.find((i) => i.code === "broken_dependency");
  assert.ok(issue);
  assert.deepEqual(issue!.fixIds, ["relink_broken_guid"]);
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
  // M25 Plan 1 — asmdef_audit, materials, shader_analysis, animation_analysis
  // flipped from planned to implemented. The remaining selector stubs are the
  // wave-2 long tail (textures, sprite_2d_analysis, audio_analysis).
  const plannedIds = plannedRules().map((r) => r.id);
  const expected = [
    "textures",
    "sprite_2d_analysis",
    "audio_analysis",
  ];
  for (const id of expected) {
    assert.ok(plannedIds.includes(id), `planned rule ${id} missing from catalog`);
  }
  // Wave-1 rules must NOT be planned anymore.
  const implementedIds = implementedRules().map((r) => r.id);
  for (const id of ["asmdef_audit", "materials", "shader_analysis", "animation_analysis"]) {
    assert.ok(implementedIds.includes(id), `${id} must be implemented after M25 Plan 1`);
    assert.ok(!plannedIds.includes(id), `${id} must not be planned after M25 Plan 1`);
  }
});

// M25 Plan 1 — wave-1 rule families ported. Each must declare its issue codes
// with the right severities so the catalog mirrors the C# issue mappers.

test("asmdef_audit rule is implemented with broken_asmdef_reference and friends", () => {
  const rule = RULE_CATALOG.find((r) => r.id === "asmdef_audit");
  assert.ok(rule, "asmdef_audit rule must be in the catalog");
  assert.equal(rule!.implemented, true);
  assert.equal(rule!.status, "implemented");
  const codes = rule!.issues.map((i) => i.code);
  assert.ok(codes.includes("broken_asmdef_reference"));
  assert.ok(codes.includes("asmdef_missing_name"));
  assert.ok(codes.includes("malformed_asmdef"));
});

test("project_health rule is implemented and reuses orphan_meta + duplicate_guid codes", () => {
  const rule = RULE_CATALOG.find((r) => r.id === "project_health");
  assert.ok(rule, "project_health rule must be in the catalog");
  assert.equal(rule!.implemented, true);
  assert.equal(rule!.status, "implemented");
  const codes = rule!.issues.map((i) => i.code);
  assert.ok(codes.includes("orphan_meta"));
  assert.ok(codes.includes("duplicate_guid"));
  assert.ok(codes.includes("missing_project_setting"));
  // All project_health codes are full-scan-only (whole-project checks).
  for (const issue of rule!.issues) {
    assert.equal(issue.fullScanOnly, true, `${issue.code} must be fullScanOnly`);
  }
});

test("materials rule is implemented with missing_shader and missing_texture codes", () => {
  const rule = RULE_CATALOG.find((r) => r.id === "materials");
  assert.ok(rule, "materials rule must be in the catalog");
  assert.equal(rule!.implemented, true);
  assert.equal(rule!.status, "implemented");
  const codes = rule!.issues.map((i) => i.code);
  assert.ok(codes.includes("missing_shader"));
  assert.ok(codes.includes("missing_texture"));
  // The planned reassign fixes link to the materials rule codes.
  const shaderIssue = rule!.issues.find((i) => i.code === "missing_shader")!;
  assert.ok(shaderIssue.fixIds.includes("reassign_missing_shader"));
  const texIssue = rule!.issues.find((i) => i.code === "missing_texture")!;
  assert.ok(texIssue.fixIds.includes("reassign_missing_texture"));
});

test("animation_analysis rule is implemented with missing_clip and empty_clip codes", () => {
  const rule = RULE_CATALOG.find((r) => r.id === "animation_analysis");
  assert.ok(rule, "animation_analysis rule must be in the catalog");
  assert.equal(rule!.implemented, true);
  assert.equal(rule!.status, "implemented");
  const codes = rule!.issues.map((i) => i.code);
  assert.ok(codes.includes("missing_clip"));
  assert.ok(codes.includes("empty_clip"));
});

test("shader_analysis rule is implemented with shader_compile_error code", () => {
  const rule = RULE_CATALOG.find((r) => r.id === "shader_analysis");
  assert.ok(rule, "shader_analysis rule must be in the catalog");
  assert.equal(rule!.implemented, true);
  assert.equal(rule!.status, "implemented");
  const codes = rule!.issues.map((i) => i.code);
  assert.ok(codes.includes("shader_compile_error"));
  assert.ok(codes.includes("missing_shader_asset"));
});

// ---------------------------------------------------------------------------
// M25 Plan 3 — explainability. Every implemented-rule issue descriptor must
// carry a stable machine-readable rootCause + a clean remediation playbook.
// These mirror the C# IssueExplainability taxonomy
// (packages/verify/Editor/Core/IssueExplainability.cs) and are emitted per-issue
// in scan_paths / validate_edit responses.
// ---------------------------------------------------------------------------

const STABLE_ROOT_CAUSES = new Set([
  "missing_guid_reference",
  "missing_fileid_reference",
  "missing_script_class",
  "missing_dependency",
  "orphaned_meta",
  "duplicate_guid",
  "structural_complexity",
  "configuration_mismatch",
  "resource_missing",
  "build_blocker",
]);

// Forbidden tokens in user-visible remediation copy (AGENTS.md
// §No internal references). None should ever leak into remediation text.
const FORBIDDEN_INTERNAL_TOKENS = [
  "M25", "M24", "M1", "M4", "M9", "M12", "M18", "M22",
  "execution-plan", "specs/", "backlog-", "Plan 1", "Plan 2", "Plan 3",
];

test("every implemented-rule issue carries a stable rootCause", () => {
  for (const rule of implementedRules()) {
    for (const issue of rule.issues) {
      assert.ok(
        typeof issue.rootCause === "string" && issue.rootCause.length > 0,
        `${rule.id}/${issue.code} must declare a rootCause`,
      );
      assert.ok(
        STABLE_ROOT_CAUSES.has(issue.rootCause!),
        `${rule.id}/${issue.code} rootCause '${issue.rootCause}' is not in the stable taxonomy`,
      );
    }
  }
});

test("every implemented-rule issue carries remediation guidance", () => {
  for (const rule of implementedRules()) {
    for (const issue of rule.issues) {
      assert.ok(
        typeof issue.remediation === "string" && issue.remediation.length > 0,
        `${rule.id}/${issue.code} must declare remediation guidance`,
      );
    }
  }
});

test("remediation copy is clean of internal IDs", () => {
  for (const rule of implementedRules()) {
    for (const issue of rule.issues) {
      for (const token of FORBIDDEN_INTERNAL_TOKENS) {
        assert.ok(
          !(issue.remediation ?? "").includes(token),
          `${rule.id}/${issue.code} remediation leaks internal token '${token}'`,
        );
      }
    }
  }
});

test("capabilities surfaces rootCause and remediation on issue descriptors", () => {
  // buildCapabilities passes the catalog through, so the capabilities surface
  // carries the explainability fields verbatim.
  const caps = buildCapabilities(DEPS, { kind: "rules" });
  const missingRef = caps.rules.find((r) => r.id === "missing_references");
  assert.ok(missingRef);
  const missingScript = missingRef!.issues.find((i) => i.code === "missing_script");
  assert.ok(missingScript);
  assert.equal(missingScript!.rootCause, "missing_script_class");
  assert.ok(
    typeof missingScript!.remediation === "string" && missingScript!.remediation!.length > 0,
  );
});

test("rootCause differentiates missing-guid vs duplicate-guid classes", () => {
  // Two issue classes that a naive "broken reference" label would conflate —
  // the taxonomy splits them so an agent branches recovery programmatically.
  const missingRef = RULE_CATALOG.find((r) => r.id === "missing_references")!;
  const missingGuid = missingRef.issues.find((i) => i.code === "missing_guid")!;
  assert.equal(missingGuid.rootCause, "missing_guid_reference");

  const projectHealth = RULE_CATALOG.find((r) => r.id === "project_health")!;
  const dupGuid = projectHealth.issues.find((i) => i.code === "duplicate_guid")!;
  assert.equal(dupGuid.rootCause, "duplicate_guid");

  assert.notEqual(missingGuid.rootCause, dupGuid.rootCause);
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

// T2.4 — relink_broken_guid is the unsafe provider for broken GUIDs.

test("relink_broken_guid fix is registered and unsafe", () => {
  const fix = implementedFixes().find((f) => f.id === "relink_broken_guid");
  assert.ok(fix, "relink_broken_guid must be in the implemented fix surface");
  assert.equal(fix!.implemented, true);
  assert.equal(fix!.safe, false, "relink mutates references — must be unsafe");
  assert.ok(fix!.rules.includes("missing_references"));
  assert.ok(fix!.rules.includes("dependencies"));
  assert.ok(fix!.issueCodes.includes("missing_guid"));
  assert.ok(fix!.issueCodes.includes("broken_dependency"));
});

test("no fix providers remain in the planned state", () => {
  // M25 Plan 2 — the last two planned fix providers (reassign_missing_texture
  // / reassign_missing_shader) shipped as real C# IFixProviders. Every catalog
  // fix is now implemented; if a planned fix is re-introduced, this guard
  // forces the author to update it deliberately.
  const plannedFixes = FIX_CATALOG.filter((f) => !f.implemented);
  assert.equal(
    plannedFixes.length,
    0,
    `expected zero planned fixes; found: ${plannedFixes.map((f) => f.id).join(", ")}`,
  );
});

// M25 Plan 2 — the two materials fix providers shipped (registered in the
// verify package's FixProviderRegistry, linked to the materials rule).

test("reassign_missing_texture fix is implemented and linked to materials", () => {
  const fix = implementedFixes().find((f) => f.id === "reassign_missing_texture");
  assert.ok(fix, "reassign_missing_texture must be in the implemented fix surface");
  assert.equal(fix!.implemented, true);
  assert.equal(fix!.safe, false, "a wrong texture silently changes the material's look");
  assert.ok(fix!.rules.includes("materials"));
  assert.ok(fix!.issueCodes.includes("missing_texture"));
});

test("reassign_missing_shader fix is implemented and linked to materials", () => {
  const fix = implementedFixes().find((f) => f.id === "reassign_missing_shader");
  assert.ok(fix, "reassign_missing_shader must be in the implemented fix surface");
  assert.equal(fix!.implemented, true);
  assert.equal(fix!.safe, false, "a wrong shader silently changes rendering");
  assert.ok(fix!.rules.includes("materials"));
  assert.ok(fix!.issueCodes.includes("missing_shader"));
});

// M24 Plan 2 / T24.2 — orphan_meta + duplicate_guid fixes now have an emitting
// rule (offline_integrity) and are implemented. Previously planned; the
// offline scanIntegrityOffline scanner is the producer.

test("remove_orphan_meta fix is implemented and linked to offline_integrity", () => {
  const fix = implementedFixes().find((f) => f.id === "remove_orphan_meta");
  assert.ok(fix, "remove_orphan_meta must be in the implemented fix surface");
  assert.equal(fix!.implemented, true);
  assert.equal(fix!.safe, true, "deleting a detached .meta loses no asset data");
  assert.ok(fix!.rules.includes("offline_integrity"));
  assert.ok(fix!.issueCodes.includes("orphan_meta"));
});

test("fix_duplicate_guid fix is implemented and linked to offline_integrity", () => {
  const fix = implementedFixes().find((f) => f.id === "fix_duplicate_guid");
  assert.ok(fix, "fix_duplicate_guid must be in the implemented fix surface");
  assert.equal(fix!.implemented, true);
  assert.equal(fix!.safe, false, "re-GUIDing silently rewires the asset graph");
  assert.ok(fix!.rules.includes("offline_integrity"));
  assert.ok(fix!.issueCodes.includes("duplicate_guid"));
});

test("offline_integrity rule is implemented with orphan_meta and duplicate_guid codes", () => {
  const rule = RULE_CATALOG.find((r) => r.id === "offline_integrity");
  assert.ok(rule, "offline_integrity rule must be in the catalog");
  assert.equal(rule!.implemented, true);
  assert.equal(rule!.status, "implemented");
  const codes = rule!.issues.map((i) => i.code);
  assert.ok(codes.includes("orphan_meta"));
  assert.ok(codes.includes("duplicate_guid"));
  assert.ok(codes.includes("missing_reference"));
  assert.ok(codes.includes("missing_script_reference"));
  // All offline_integrity codes are full-scan-only (they need the whole tree).
  for (const issue of rule!.issues) {
    assert.equal(issue.fullScanOnly, true, `${issue.code} must be fullScanOnly`);
  }
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

test("list_rules tool is in the implemented surface under capability-discovery", () => {
  const caps = buildCapabilities(DEPS);
  const found = caps.tools.find((t) => t.name === "unity_open_mcp_list_rules");
  assert.ok(found, "unity_open_mcp_list_rules must be discoverable via capabilities");
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

  // M27 Plan 4 — batch_execute must be batchCapable:false (NOT headless). It is
  // live-only: one HTTP round trip runs many typed tools sequentially inside
  // the open Editor. It must NOT appear in BATCH_TOOL_NAMES (no spawn fallback).
  const batchExecute = caps.tools.find(
    (t) => t.name === "unity_open_mcp_batch_execute",
  );
  assert.ok(batchExecute, "unity_open_mcp_batch_execute must be discoverable");
  assert.equal(
    batchExecute!.batchCapable,
    false,
    "batch_execute must NOT be headless batchCapable — it is live-only",
  );
  assert.equal(batchExecute!.routePolicy, "live");
  assert.equal(batchExecute!.category, "core");
});

test("batch_execute lifecycle is scene-dirty with a note", () => {
  const caps = buildCapabilities(DEPS);
  const batchExecute = caps.tools.find(
    (t) => t.name === "unity_open_mcp_batch_execute",
  );
  assert.ok(batchExecute);
  assert.equal(batchExecute!.lifecycle, "scene-dirty");
  assert.ok(
    typeof batchExecute!.lifecycleNote === "string" &&
      batchExecute!.lifecycleNote!.length > 0,
    "batch_execute must carry a lifecycle note explaining the batch gate + undo group",
  );
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
// M18 Plan 2 / T18.2.3 — toolGroups block (compiled-state only)
// ---------------------------------------------------------------------------

test("capabilities include a toolGroups block", () => {
  const caps = buildCapabilities(DEPS);
  assert.ok(Array.isArray(caps.toolGroups));
  assert.ok(caps.toolGroups.length > 0, "toolGroups must be non-empty");
});

test("toolGroups block reports the core group as default-enabled", () => {
  const caps = buildCapabilities(DEPS);
  const core = caps.toolGroups.find((g) => g.id === "core");
  assert.ok(core, "core group must be present");
  assert.equal(core!.defaultEnabled, true);
  assert.equal(core!.available, true, "core has no domainDefine — always compiled in");
  assert.equal(core!.domainDefine, null);
});

test("toolGroups block reports the default-enabled count from the catalog", () => {
  // The default-enabled set is the catalog's `defaultEnabled: true` entries
  // (extended beyond `core` in the "Extended default-enabled tools" change).
  // Derive the expectation from the built capabilities so this test tracks
  // the catalog instead of a stale hard-coded count.
  const caps = buildCapabilities(DEPS);
  const defaultCount = caps.toolGroups.filter((g) => g.defaultEnabled).length;
  assert.ok(defaultCount >= 1, "at least core must be default-enabled");
  assert.equal(caps.counts.toolGroupsDefaultEnabled, defaultCount);
  assert.equal(caps.counts.toolGroupsTotal, caps.toolGroups.length);
});

test("every implemented tool carries a group assignment (null or string)", () => {
  const caps = buildCapabilities(DEPS);
  for (const t of caps.tools.filter((t) => t.implemented)) {
    assert.ok(
      t.group === null || typeof t.group === "string",
      `${t.name} group must be null or a string`,
    );
  }
});

test("toolGroups lists compiled-in tool names per group", () => {
  const caps = buildCapabilities(DEPS);
  // FIXTURE_TOOLS has ping (core) + capabilities/list_rules (null/meta).
  const core = caps.toolGroups.find((g) => g.id === "core");
  assert.ok(core);
  assert.ok(core!.tools.includes("unity_open_mcp_ping"));
  assert.ok(core!.toolCount >= 1);
});

test("domain-gated group reports available=null when bridge inventory is omitted", () => {
  // Local capability call with no bridge probe — availability unknown.
  const caps = buildCapabilities(DEPS);
  const nav = caps.toolGroups.find((g) => g.id === "navigation");
  assert.ok(nav);
  assert.equal(nav!.available, null);
  assert.equal(nav!.domainDefine, "UNITY_OPEN_MCP_EXT_NAVIGATION");
  assert.equal(nav!.unityPackage, "com.unity.ai.navigation");
  assert.ok(nav!.availableReason !== null);
  // specs/feedback.md 2026-07-03 — top-level bridgeReachable mirrors the
  // omitted-inventory state so a caller can tell the null availability is a
  // reachability artifact, not a compile-state verdict.
  assert.equal(caps.bridgeReachable, false);
});

// specs/feedback.md 2026-07-03 — bridgeReachable flag distinguishes
// "group genuinely not compiled in" from "I can't tell because the bridge is
// down". When the bridge is reachable, bridgeReachable is true; when omitted
// (offline), it is false and every domain-gated group is available:null.
test("bridgeReachable is true when the bridge inventory is provided", () => {
  const caps = buildCapabilities({
    ...DEPS,
    availableBridgeTools: new Set<string>(["unity_open_mcp_ping"]),
  });
  assert.equal(caps.bridgeReachable, true);
});

test("available:false reason mentions both the package and the bridge build config", () => {
  // When the bridge IS reachable but a domain group's tools are absent, the
  // reason must point at BOTH possible causes (Unity package not installed OR
  // the bridge binary not built with the extension pack) — otherwise an
  // operator whose package IS installed loops on a misleading "install the
  // package" hint when the real fix is rebuilding the bridge.
  const caps = buildCapabilities({
    ...DEPS,
    tools: [
      ...FIXTURE_TOOLS,
      {
        name: "unity_open_mcp_navigation_surface_add",
        description: "NavMesh surface add (fixture).",
        inputSchema: { type: "object", properties: {} },
      },
    ],
    availableBridgeTools: new Set<string>(["unity_open_mcp_ping"]),
  });
  assert.equal(caps.bridgeReachable, true);
  const nav = caps.toolGroups.find((g) => g.id === "navigation");
  assert.ok(nav);
  assert.equal(nav!.available, false);
  assert.ok(nav!.availableReason);
  assert.match(nav!.availableReason!, /com\.unity\.ai\.navigation/);
  assert.match(
    nav!.availableReason!,
    /built without|extension pack/i,
    "reason must mention the bridge build configuration, not only the package",
  );
});

test("domain-gated group reports available=true when bridge inventory includes its tools", () => {
  // The fixture must include a navigation tool so the group has a non-empty
  // roster to test against the bridge inventory. Without it, buildOneGroup
  // correctly returns available=false (no tools compiled in).
  const depsWithNav: BuildCapabilitiesDeps = {
    tools: [
      ...FIXTURE_TOOLS,
      {
        name: "unity_open_mcp_navigation_surface_add",
        description: "NavMesh surface add (fixture).",
        inputSchema: { type: "object", properties: {} },
      },
    ],
    batchToolNames: FIXTURE_BATCH_NAMES,
    rules: RULE_CATALOG,
    fixes: FIX_CATALOG,
    availableBridgeTools: new Set<string>([
      "unity_open_mcp_ping",
      "unity_open_mcp_navigation_surface_add",
    ]),
  };
  const caps = buildCapabilities(depsWithNav);
  const nav = caps.toolGroups.find((g) => g.id === "navigation");
  assert.ok(nav);
  assert.equal(nav!.available, true);
  assert.equal(nav!.availableReason, null);
});

test("domain-gated group reports available=false when bridge inventory omits its tools", () => {
  const depsWithNav: BuildCapabilitiesDeps = {
    tools: [
      ...FIXTURE_TOOLS,
      {
        name: "unity_open_mcp_navigation_surface_add",
        description: "NavMesh surface add (fixture).",
        inputSchema: { type: "object", properties: {} },
      },
    ],
    batchToolNames: FIXTURE_BATCH_NAMES,
    rules: RULE_CATALOG,
    fixes: FIX_CATALOG,
    availableBridgeTools: new Set<string>(["unity_open_mcp_ping"]),
  };
  const caps = buildCapabilities(depsWithNav);
  const nav = caps.toolGroups.find((g) => g.id === "navigation");
  assert.ok(nav);
  assert.equal(nav!.available, false);
  assert.match(nav!.availableReason!, /com\.unity\.ai\.navigation/);
});

// ---------------------------------------------------------------------------
// M20 Plan 7 / T20.7.0 — auto-activation metadata on group capabilities
// ---------------------------------------------------------------------------

test("every group carries autoActivate + packageDependency fields", () => {
  const caps = buildCapabilities(DEPS);
  for (const g of caps.toolGroups) {
    assert.equal(
      typeof g.autoActivate,
      "boolean",
      `${g.id} autoActivate must be a boolean`,
    );
    // packageDependency is non-null only when autoActivate is true.
    if (g.autoActivate) {
      assert.ok(
        typeof g.packageDependency === "string" && g.packageDependency.length > 0,
        `${g.id} autoActivate=true must carry a non-empty packageDependency`,
      );
    } else {
      assert.equal(
        g.packageDependency,
        null,
        `${g.id} autoActivate=false must have packageDependency=null`,
      );
    }
  }
});

test("navigation is NOT auto-activating (manual only — additive invariant)", () => {
  const caps = buildCapabilities(DEPS);
  const nav = caps.toolGroups.find((g) => g.id === "navigation");
  assert.ok(nav);
  assert.equal(nav!.autoActivate, false);
  assert.equal(nav!.packageDependency, null);
});

test("auto-activating group surfaces packageDependency and a distinct usageHint", () => {
  // Build with the shadergraph group present in the tool list so it has a
  // non-empty roster to report against.
  const depsWithSg: BuildCapabilitiesDeps = {
    tools: [
      ...FIXTURE_TOOLS,
      {
        name: "unity_open_mcp_shader_graph_create",
        description: "Shader Graph create (fixture).",
        inputSchema: { type: "object", properties: {} },
      },
    ],
    batchToolNames: FIXTURE_BATCH_NAMES,
    rules: RULE_CATALOG,
    fixes: FIX_CATALOG,
  };
  const caps = buildCapabilities(depsWithSg);
  const sg = caps.toolGroups.find((g) => g.id === "shadergraph");
  assert.ok(sg, "shadergraph group must appear in capabilities");
  assert.equal(sg!.autoActivate, true);
  assert.equal(sg!.packageDependency, "com.unity.shadergraph");
  // The auto-activation usageHint mentions the package + that no manual call
  // is required, and still references manage_tools (for deactivate).
  assert.match(sg!.usageHint, /com\.unity\.shadergraph/);
  assert.match(sg!.usageHint, /Auto-activates/);
  assert.match(sg!.usageHint, /unity_open_mcp_manage_tools/);
});

test("vfx + memoryprofiler groups surface auto-activation metadata", () => {
  // T20.7.2 / T20.7.3 — the vfx and memoryprofiler groups are also
  // auto-activating. Build with their tools present so each has a non-empty
  // roster.
  const depsWithDomains: BuildCapabilitiesDeps = {
    tools: [
      ...FIXTURE_TOOLS,
      {
        name: "unity_open_mcp_vfx_list",
        description: "VFX list (fixture).",
        inputSchema: { type: "object", properties: {} },
      },
      {
        name: "unity_senses_memory_snapshot_capture",
        description: "Memory snapshot capture (fixture).",
        inputSchema: { type: "object", properties: {} },
      },
    ],
    batchToolNames: FIXTURE_BATCH_NAMES,
    rules: RULE_CATALOG,
    fixes: FIX_CATALOG,
  };
  const caps = buildCapabilities(depsWithDomains);

  const vfx = caps.toolGroups.find((g) => g.id === "vfx");
  assert.ok(vfx, "vfx group must appear in capabilities");
  assert.equal(vfx!.autoActivate, true);
  assert.equal(vfx!.packageDependency, "com.unity.visualeffectgraph");
  assert.match(vfx!.usageHint, /com\.unity\.visualeffectgraph/);
  assert.match(vfx!.usageHint, /Auto-activates/);

  const mp = caps.toolGroups.find((g) => g.id === "memoryprofiler");
  assert.ok(mp, "memoryprofiler group must appear in capabilities");
  assert.equal(mp!.autoActivate, true);
  assert.equal(mp!.packageDependency, "com.unity.memoryprofiler");
  assert.match(mp!.usageHint, /com\.unity\.memoryprofiler/);
  assert.match(mp!.usageHint, /Auto-activates/);

  // The memory snapshot capture tool is sense-prefixed (unity_senses_*) but
  // belongs to the memoryprofiler group/category, NOT agent-senses.
  const tool = caps.tools.find((t) => t.name === "unity_senses_memory_snapshot_capture");
  assert.ok(tool, "memory snapshot capture tool must appear in tools");
  assert.equal(tool!.category, "memoryprofiler");
  assert.equal(tool!.group, "memoryprofiler");
});

test("non-default-enabled groups carry a usageHint pointing at manage_tools", () => {
  const caps = buildCapabilities(DEPS);
  for (const g of caps.toolGroups) {
    if (!g.defaultEnabled) {
      assert.match(
        g.usageHint,
        /unity_open_mcp_manage_tools/,
        `${g.id} usageHint must mention manage_tools`,
      );
    }
  }
});

test("toolGroups block is returned even with kind=rules filter", () => {
  // Independent of the kind filter — an agent asking for rules still sees
  // the group catalog.
  const caps = buildCapabilities(DEPS, { kind: "rules" });
  assert.ok(caps.toolGroups.length > 0);
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

test("routing summary names the batch env vars; no meta-tool is batch-blocked (M26 Plan 3)", () => {
  const caps = buildCapabilities(DEPS);
  const r = caps.routing;
  assert.ok(r.batchRequirements.includes("UNITY_PATH"));
  assert.ok(r.batchRequirements.includes("UNITY_PROJECT_PATH"));
  // M26 Plan 3 — all four meta-tools (find_members, execute_csharp,
  // invoke_method, execute_menu) are now batch-capable, so the batch-blocked
  // list is empty. execute_menu is gated by a batch-viable allow-list inside
  // the C# entry point, but the tool itself is batch-capable.
  assert.equal(r.batchBlocked.length, 0, "no meta-tool should be batch-blocked after M26 Plan 3");
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
// M22 Plan 1 / T22.1.5 — cost hints block
// ---------------------------------------------------------------------------

test("capabilities include a costHints block", () => {
  const caps = buildCapabilities(DEPS);
  assert.ok(caps.costHints, "costHints block must be present");
  assert.ok(Array.isArray(caps.costHints.tools));
  assert.ok(caps.costHints.tools.length > 0, "costHints.tools must be non-empty");
  assert.ok(
    typeof caps.costHints.recommendedPageSize === "object" &&
      caps.costHints.recommendedPageSize !== null,
  );
  assert.ok(Array.isArray(caps.costHints.recommendedToolChains));
  assert.ok(
    typeof caps.costHints.guidance === "string" && caps.costHints.guidance.length > 0,
  );
});

test("costHints block covers the documented heavy-tool roster", () => {
  const caps = buildCapabilities(DEPS);
  const hinted = new Set(caps.costHints.tools.map((h) => h.tool));
  for (const tool of [
    "unity_open_mcp_read_asset",
    "unity_open_mcp_search_assets",
    "unity_open_mcp_scene_get_data",
    "unity_open_mcp_find_references",
    "unity_open_mcp_validate_edit",
    "unity_open_mcp_scan_paths",
  ]) {
    assert.ok(hinted.has(tool), `${tool} must appear in costHints`);
  }
});

test("costHints block is returned even with a kind filter", () => {
  // Independent of the kind filter — agents asking for rules/fixes still
  // benefit from the cost narrative (same rationale as routing).
  const caps = buildCapabilities(DEPS, { kind: "rules" });
  assert.ok(caps.costHints);
  assert.ok(caps.costHints.tools.length > 0);
});

// ---------------------------------------------------------------------------
// Lifecycle policy block (5-class taxonomy + per-tool declarations)
// ---------------------------------------------------------------------------

// A fixture that includes a tool from each lifecycle class so the per-tool
// `lifecycle` field can be asserted without importing the full ALL_TOOLS list.
const LIFECYCLE_FIXTURE_TOOLS: Tool[] = [
  ...FIXTURE_TOOLS,
  {
    name: "unity_open_mcp_execute_csharp",
    description: "Execute C# (fixture).",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "unity_open_mcp_compile_check",
    description: "Compile check (fixture).",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "unity_open_mcp_gameobject_modify",
    description: "GameObject modify (fixture).",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "unity_open_mcp_build_start",
    description: "Build start (fixture).",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "unity_senses_run_tests",
    description: "Run tests (fixture).",
    inputSchema: { type: "object", properties: {} },
  },
];
const LIFECYCLE_DEPS = {
  tools: LIFECYCLE_FIXTURE_TOOLS,
  batchToolNames: FIXTURE_BATCH_NAMES,
  rules: RULE_CATALOG,
  fixes: FIX_CATALOG,
};

test("capabilities include a lifecycleBlock with the 5-class taxonomy", () => {
  const caps = buildCapabilities(LIFECYCLE_DEPS);
  assert.ok(caps.lifecycleBlock, "lifecycleBlock must be present");
  assert.equal(caps.lifecycleBlock.classes.length, 5);
  const ids = caps.lifecycleBlock.classes.map((c) => c.class);
  assert.deepEqual(ids, [
    "none",
    "compile-reload",
    "modal-dialog",
    "scene-dirty",
    "process-stale",
  ]);
  assert.ok(
    typeof caps.lifecycleBlock.guidance === "string" &&
      caps.lifecycleBlock.guidance.length > 0,
  );
});

test("every implemented tool carries a lifecycle class + lifecycleNote field", () => {
  const caps = buildCapabilities(LIFECYCLE_DEPS);
  for (const t of caps.tools.filter((t) => t.implemented)) {
    assert.equal(typeof t.lifecycle, "string", `${t.name} lifecycle missing`);
    assert.ok(
      t.lifecycleNote === null || typeof t.lifecycleNote === "string",
      `${t.name} lifecycleNote must be null or string`,
    );
  }
});

test("lifecycle class is assigned per tool from the taxonomy", () => {
  const caps = buildCapabilities(LIFECYCLE_DEPS);
  const byName = new Map(caps.tools.map((t) => [t.name, t]));

  assert.equal(byName.get("unity_open_mcp_ping")!.lifecycle, "none");
  assert.equal(
    byName.get("unity_open_mcp_execute_csharp")!.lifecycle,
    "compile-reload",
  );
  assert.equal(
    byName.get("unity_open_mcp_compile_check")!.lifecycle,
    "compile-reload",
  );
  assert.equal(
    byName.get("unity_open_mcp_gameobject_modify")!.lifecycle,
    "scene-dirty",
  );
  assert.equal(
    byName.get("unity_open_mcp_build_start")!.lifecycle,
    "modal-dialog",
  );
  assert.equal(
    byName.get("unity_senses_run_tests")!.lifecycle,
    "process-stale",
  );
});

test("compile_check carries its batch-only lifecycleNote in capabilities", () => {
  const caps = buildCapabilities(LIFECYCLE_DEPS);
  const compileCheck = caps.tools.find(
    (t) => t.name === "unity_open_mcp_compile_check",
  );
  assert.ok(compileCheck);
  assert.match(
    compileCheck!.lifecycleNote!,
    /editor_instance_locked/,
    "compile_check lifecycleNote must mention editor_instance_locked",
  );
});

test("lifecycleBlock is returned even with a kind filter", () => {
  // Independent of the kind filter — agents asking for rules/fixes still
  // benefit from the recovery narrative (same rationale as routing/costHints).
  const caps = buildCapabilities(LIFECYCLE_DEPS, { kind: "rules" });
  assert.ok(caps.lifecycleBlock);
  assert.equal(caps.lifecycleBlock.classes.length, 5);
});

test("planned tools default to lifecycle none with null note", () => {
  const caps = buildCapabilities(DEPS);
  for (const t of caps.tools.filter((t) => !t.implemented)) {
    assert.equal(t.lifecycle, "none", `${t.name} planned should default to none`);
    assert.equal(t.lifecycleNote, null);
  }
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
