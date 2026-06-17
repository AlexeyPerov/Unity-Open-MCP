import test from "node:test";
import assert from "node:assert/strict";

import { listRules } from "./list-rules.js";
import {
  RULE_CATALOG,
  FIX_CATALOG,
  implementedRules,
  plannedRules,
} from "./rule-catalog.js";

const DEPS = { rules: RULE_CATALOG, fixes: FIX_CATALOG };

// ---------------------------------------------------------------------------
// default shape — full catalog
// ---------------------------------------------------------------------------

test("listRules returns every rule with derived defaultSeverity + fixIds", () => {
  const res = listRules(DEPS);
  assert.equal(res.counts.total, RULE_CATALOG.length);
  assert.equal(
    res.counts.implemented,
    implementedRules().length,
  );
  assert.equal(res.counts.planned, plannedRules().length);

  for (const rule of res.rules) {
    assert.ok(rule.defaultSeverity === "Error" || rule.defaultSeverity === "Warning");
    assert.ok(Array.isArray(rule.availableFixIds));
  }
});

test("listRules filters surface only when a filter is passed", () => {
  assert.equal(listRules(DEPS).filters, undefined, "no filter -> no filter block");
  const withFilter = listRules(DEPS, { assetKind: "prefab" });
  assert.ok(withFilter.filters);
  assert.equal(withFilter.filters!.assetKind, "prefab");
});

// ---------------------------------------------------------------------------
// defaultSeverity derivation — worst severity wins
// ---------------------------------------------------------------------------

test("missing_references defaultSeverity is Error (it can emit missing_script)", () => {
  const res = listRules(DEPS);
  const mr = res.rules.find((r) => r.id === "missing_references");
  assert.ok(mr);
  assert.equal(mr!.defaultSeverity, "Error");
});

test("dependencies defaultSeverity is Error (broken_dependency is Error)", () => {
  const res = listRules(DEPS);
  const dep = res.rules.find((r) => r.id === "dependencies");
  assert.ok(dep);
  assert.equal(dep!.defaultSeverity, "Error");
});

test("planned rules default to Warning severity", () => {
  // Planned rules have empty issues -> defaultSeverity resolves to Warning.
  const res = listRules(DEPS);
  for (const rule of res.rules.filter((r) => !r.implemented)) {
    assert.equal(
      rule.defaultSeverity,
      "Warning",
      `${rule.id} planned rule should default to Warning`,
    );
  }
});

// ---------------------------------------------------------------------------
// availableFixIds — collected across issues, deduplicated
// ---------------------------------------------------------------------------

test("missing_references availableFixIds includes remove_missing_script + relink_broken_guid", () => {
  const res = listRules(DEPS);
  const mr = res.rules.find((r) => r.id === "missing_references");
  assert.ok(mr);
  assert.ok(mr!.availableFixIds.includes("remove_missing_script"));
  assert.ok(mr!.availableFixIds.includes("relink_broken_guid"));
  // No duplicate fix IDs even if multiple issues point to the same fix.
  const unique = new Set(mr!.availableFixIds);
  assert.equal(unique.size, mr!.availableFixIds.length);
});

test("dependencies availableFixIds includes relink_broken_guid", () => {
  const res = listRules(DEPS);
  const dep = res.rules.find((r) => r.id === "dependencies");
  assert.ok(dep);
  assert.ok(dep!.availableFixIds.includes("relink_broken_guid"));
});

test("planned rules have empty availableFixIds", () => {
  const res = listRules(DEPS);
  for (const rule of res.rules.filter((r) => !r.implemented)) {
    assert.deepEqual(rule.availableFixIds, [], `${rule.id} planned rule has no fixes`);
  }
});

// ---------------------------------------------------------------------------
// filters — asset_kind / extension / implemented_only
// ---------------------------------------------------------------------------

test("asset_kind=prefab narrows to rules that apply to prefabs", () => {
  const res = listRules(DEPS, { assetKind: "prefab" });
  const ids = res.rules.map((r) => r.id);
  assert.ok(ids.includes("missing_references"));
  assert.ok(ids.includes("scene_prefab_health"));
  assert.ok(ids.includes("dependencies"));
  // materials applies to material, not prefab -> excluded.
  assert.ok(!ids.includes("materials"));
  assert.ok(res.filters?.assetKind === "prefab");
});

test("extension=.mat narrows to material/shader rules", () => {
  const res = listRules(DEPS, { extension: ".mat" });
  const ids = res.rules.map((r) => r.id);
  assert.ok(ids.includes("materials"));
  assert.ok(ids.includes("shader_analysis"));
  assert.ok(ids.includes("dependencies"), "dependencies lists .mat");
  assert.ok(!ids.includes("textures"));
});

test("extension accepts bare form without leading dot", () => {
  const withDot = listRules(DEPS, { extension: ".prefab" });
  const bare = listRules(DEPS, { extension: "prefab" });
  assert.deepEqual(
    withDot.rules.map((r) => r.id).sort(),
    bare.rules.map((r) => r.id).sort(),
  );
});

test("implemented_only drops planned rules", () => {
  const res = listRules(DEPS, { implementedOnly: true });
  for (const r of res.rules) assert.equal(r.implemented, true);
  assert.equal(res.counts.planned, 0);
});

test("implemented_only + asset_kind combine", () => {
  const res = listRules(DEPS, {
    implementedOnly: true,
    assetKind: "prefab",
  });
  for (const r of res.rules) assert.equal(r.implemented, true);
  for (const r of res.rules) assert.ok(r.applicableAssetKinds.includes("prefab"));
});

// ---------------------------------------------------------------------------
// planned rules still carry guidance for agent self-correction
// ---------------------------------------------------------------------------

test("planned rules carry guidance in the list output", () => {
  const res = listRules(DEPS);
  for (const rule of res.rules.filter((r) => !r.implemented)) {
    assert.equal(rule.status, "planned");
    assert.ok(
      typeof rule.guidance === "string" && rule.guidance.length > 0,
      `${rule.id} planned rule must carry guidance`,
    );
  }
});
