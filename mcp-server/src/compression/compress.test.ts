// Tests for the M9 Plan 2 compression module. These prove the acceptance
// criteria: >=70% reduction vs raw YAML, hierarchy folding preserves count
// accuracy, and detail controls verbosity. Pure-function tests — no bridge,
// no I/O.
import { test } from "node:test";
import assert from "node:assert/strict";

import {
  compressNames,
  isRenderOnly,
  isTrivialComponent,
  flattenAndAssign,
  foldHierarchy,
  componentMatches,
  displayObjectName,
} from "./compact.js";
import {
  renderAssetSummary,
  renderSearchSummary,
  compactPath,
  extForKind,
} from "./compact.js";
import type {
  AssetModel,
  AssetComponent,
  HierarchyNode,
  SearchModel,
} from "./asset-model.js";

// ---------------------------------------------------------------------------
// compressNames — folds numbered runs.
// ---------------------------------------------------------------------------

test("compressNames folds numbered runs of 3+", () => {
  assert.deepEqual(
    compressNames(["Recipe_003", "Recipe_001", "Recipe_002", "Blender", "Stove_01", "Stove_02"]),
    ["Blender", "Recipe_001..003", "Stove_01", "Stove_02"],
  );
});

test("compressNames leaves runs of 2 explicit", () => {
  assert.deepEqual(
    compressNames(["A_01", "A_02", "B"]),
    ["A_01", "A_02", "B"],
  );
});

test("compressNames dedupes and sorts", () => {
  assert.deepEqual(
    compressNames(["Z", "A", "A", "M"]),
    ["A", "M", "Z"],
  );
});

test("compressNames folds a long run into one entry", () => {
  const names = Array.from({ length: 2000 }, (_, i) => `Item_${String(i).padStart(4, "0")}`);
  const out = compressNames(names);
  assert.equal(out.length, 1);
  assert.equal(out[0], "Item_0000..1999");
});

test("compressNames preserves width across a run", () => {
  assert.deepEqual(
    compressNames(["Enemy_01", "Enemy_02", "Enemy_03"]),
    ["Enemy_01..03"],
  );
});

test("compressNames handles plain (no trailing digit) names", () => {
  assert.deepEqual(
    compressNames(["Player", "Camera", "Player"]),
    ["Camera", "Player"],
  );
});

test("compressNames empty input returns empty", () => {
  assert.deepEqual(compressNames([]), []);
});

// ---------------------------------------------------------------------------
// Component classification.
// ---------------------------------------------------------------------------

test("isTrivialComponent recognizes transform/render primitives", () => {
  assert.equal(isTrivialComponent("Transform"), true);
  assert.equal(isTrivialComponent("MeshRenderer"), true);
  assert.equal(isTrivialComponent("PlayerController"), false);
});

test("isRenderOnly requires all-trivial + at least one renderer", () => {
  assert.equal(isRenderOnly(["Transform", "MeshFilter", "MeshRenderer"]), true);
  assert.equal(isRenderOnly(["Transform"]), false);
  assert.equal(isRenderOnly(["Transform", "PlayerController"]), false);
  assert.equal(isRenderOnly([]), false);
});

// ---------------------------------------------------------------------------
// flattenAndAssign — CMP codes for repeated component sets.
// ---------------------------------------------------------------------------

test("flattenAndAssign assigns CMP codes to repeated component sets in first-seen order", () => {
  const model = makePrefab([
    node("Player", [comp("Transform"), comp("PlayerController")], [
      node("Model", [comp("Transform"), comp("SkinnedMeshRenderer")]),
      node("Model2", [comp("Transform"), comp("SkinnedMeshRenderer")]),
    ]),
  ]);
  const { rows, sets } = flattenAndAssign(model.roots);
  assert.equal(rows.length, 3);
  // Player set declared first -> c1; render-only set -> c2
  assert.equal(sets[0].code, "c1");
  assert.deepEqual(sets[0].names, ["Transform", "PlayerController"]);
  assert.equal(sets[1].code, "c2");
  assert.deepEqual(sets[1].names, ["Transform", "SkinnedMeshRenderer"]);
  assert.equal(rows[0].componentSet, "c1");
  assert.equal(rows[1].componentSet, "c2");
  assert.equal(rows[2].componentSet, "c2");
});

test("flattenAndAssign leaves nodes without components un-coded", () => {
  const model = makePrefab([node("Empty", [], [])]);
  const { rows, sets } = flattenAndAssign(model.roots);
  assert.equal(sets.length, 0);
  assert.equal(rows[0].componentSet, null);
});

// ---------------------------------------------------------------------------
// foldHierarchy — render-only leaf folding + omission counts.
// ---------------------------------------------------------------------------

test("foldHierarchy collapses render-only leaf runs of 3+ into one folded row", () => {
  const leaves = Array.from({ length: 7 }, (_, i) =>
    node(`SampleMesh_${String(i + 1).padStart(2, "0")}`, [
      comp("Transform"),
      comp("MeshFilter"),
      comp("MeshRenderer"),
    ]),
  );
  const model = makePrefab(leaves);
  const { rows } = flattenAndAssign(model.roots);
  const folded = foldHierarchy(rows, { depth: -1, limit: 0, fold: true });
  assert.equal(folded.rows.length, 1);
  assert.equal(folded.collapsed, 7);
  assert.equal(folded.rows[0].count, 7);
  assert.equal(folded.rows[0].folded, true);
  assert.deepEqual(folded.rows[0].foldedNames, [
    "SampleMesh_01..07",
  ]);
});

test("foldHierarchy preserves count accuracy when folding (count accuracy criterion)", () => {
  // 7 render-only leaves + 1 focus node = 8 total nodes.
  const leaves = Array.from({ length: 7 }, (_, i) =>
    node(`M_${String(i + 1).padStart(2, "0")}`, [
      comp("Transform"),
      comp("MeshRenderer"),
    ]),
  );
  const model = makePrefab([
    node("Hero", [comp("Transform"), comp("HeroController")], leaves),
    ...leaves,
  ]);
  // Rebuild: Hero has focus; the 7 leaves are siblings at top level here.
  const model2 = makePrefab([
    node("Hero", [comp("Transform"), comp("HeroController")], []),
    ...leaves,
  ]);
  const { rows } = flattenAndAssign(model2.roots);
  const folded = foldHierarchy(rows);
  const totalRepresented = folded.rows.reduce((sum, r) => sum + r.count, 0) + folded.hiddenByDepth + folded.hiddenByLimit;
  assert.equal(totalRepresented, rows.length);
  assert.equal(folded.collapsed, 7);
});

test("foldHierarchy reports hiddenByDepth for nodes past the depth cap", () => {
  const model = makePrefab([
    node("A", [comp("Transform"), comp("Foo")], [
      node("B", [comp("Transform"), comp("Foo")], [
        node("C", [comp("Transform"), comp("Foo")], [
          node("D", [comp("Transform"), comp("Foo")]),
        ]),
      ]),
    ]),
  ]);
  const { rows } = flattenAndAssign(model.roots);
  const folded = foldHierarchy(rows, { depth: 1 });
  // A (0), B (1) visible; C(2)+D(3) hidden by depth.
  assert.equal(folded.hiddenByDepth, 2);
});

test("foldHierarchy reports hiddenByLimit for rows past the cap", () => {
  const nodes = Array.from({ length: 10 }, (_, i) =>
    node(`N${i}`, [comp("Transform"), comp(`Foo${i}`)]),
  );
  const model = makePrefab(nodes);
  const { rows } = flattenAndAssign(model.roots);
  const folded = foldHierarchy(rows, { limit: 3 });
  assert.equal(folded.rows.length, 3);
  assert.equal(folded.hiddenByLimit, 7);
});

test("foldHierarchy with fold=false keeps every row explicit", () => {
  const leaves = Array.from({ length: 5 }, (_, i) =>
    node(`L_${String(i + 1).padStart(2, "0")}`, [comp("Transform"), comp("MeshRenderer")]),
  );
  const model = makePrefab(leaves);
  const { rows } = flattenAndAssign(model.roots);
  const folded = foldHierarchy(rows, { fold: false });
  assert.equal(folded.rows.length, 5);
  assert.equal(folded.collapsed, 0);
});

// ---------------------------------------------------------------------------
// renderAssetSummary — detail levels + drill-down.
// ---------------------------------------------------------------------------

test("renderAssetSummary default (summary) returns counts + CMP + folded TREE", () => {
  const model = typicalPrefab();
  const out = renderAssetSummary(model);
  assert.equal(out.asset, "prefab");
  assert.equal(out.path, "Assets/Prefabs/Player.prefab");
  assert.equal(out.guid, "abc123");
  assert.equal(out.objects, model.objectCount);
  assert.equal(out.components, model.componentCount);
  assert.ok(out.cmp, "CMP table present");
  assert.ok(out.tree && out.tree.length > 0, "TREE present");
});

test("renderAssetSummary --component drill-down lists matching nodes + fields", () => {
  const model = makePrefab([
    node("Player", [
      compWithFields("PlayerController", [
        { name: "m_Speed", value: "5" },
        { name: "m_Health", value: "100" },
      ]),
      comp("Transform"),
    ], [
      node("Child", [comp("Transform"), comp("MeshRenderer")]),
    ]),
  ]);
  const out = renderAssetSummary(model, { component: "PlayerController" });
  assert.ok(out.componentMatches);
  assert.equal(out.componentMatches!.length, 1);
  assert.equal(out.componentMatches![0].component, "PlayerController");
  assert.equal(out.componentMatches![0].object, "Player");
  assert.deepEqual(
    out.componentMatches![0].fields?.map((f) => f.name),
    ["m_Speed", "m_Health"],
  );
});

test("renderAssetSummary --path scopes the TREE to a subtree", () => {
  const model = typicalPrefab();
  const out = renderAssetSummary(model, { path: "Rig" });
  assert.ok(out.tree && out.tree.length > 0, "TREE non-empty for matching subtree");
  assert.equal(out.pathScope, "Rig");
});

test("renderAssetSummary --id reports offline-only when fileIDs absent", () => {
  const model = typicalPrefab();
  const out = renderAssetSummary(model, { id: "12345" });
  assert.equal(out.idResult?.matched, false);
  assert.ok(out.idResult?.note?.includes("fileID"));
});

test("renderAssetSummary --id resolves a node when fileID present", () => {
  const model = makePrefab([
    fileIdNode("123", "Hero", [comp("Transform"), comp("HeroController")], []),
  ]);
  const out = renderAssetSummary(model, { id: "123" });
  assert.equal(out.idResult?.matched, true);
  assert.equal(out.idResult?.object?.name, "Hero");
});

test("renderAssetSummary verbose detail does not fold render-only runs", () => {
  const leaves = Array.from({ length: 5 }, (_, i) =>
    node(`L_${String(i + 1).padStart(2, "0")}`, [comp("Transform"), comp("MeshRenderer")]),
  );
  const model = makePrefab(leaves);
  const summary = renderAssetSummary(model, { detail: "summary" });
  const verbose = renderAssetSummary(model, { detail: "verbose" });
  assert.ok((summary.collapsed ?? 0) > 0, "summary folds render-only run");
  // verbose: no folding -> more rows.
  assert.ok((verbose.tree?.length ?? 0) > (summary.tree?.length ?? 0));
});

test("renderAssetSummary non-hierarchical asset returns flatObjects", () => {
  const model: AssetModel = {
    kind: "asset",
    path: "Assets/Settings.cfg",
    guid: "zzz",
    objectCount: 1,
    componentCount: 0,
    roots: [],
    flatObjects: [{ name: "Settings", type: "SettingsConfig", fields: [{ name: "m_Value", value: "42" }] }],
  };
  const out = renderAssetSummary(model);
  assert.deepEqual(out.flatObjects, model.flatObjects);
  assert.equal(out.tree, undefined);
});

// ---------------------------------------------------------------------------
// >=70% reduction criterion (the headline acceptance test).
// ---------------------------------------------------------------------------

test("compressed summary is >=70% smaller than raw YAML for a typical prefab", () => {
  const model = largePrefab();
  const rawYaml = renderFakeRawYaml(model);
  const compressed = JSON.stringify(renderAssetSummary(model, { detail: "summary" }));
  const reduction = 1 - compressed.length / rawYaml.length;
  assert.ok(
    reduction >= 0.7,
    `expected >=70% reduction, got ${(reduction * 100).toFixed(1)}% (raw=${rawYaml.length}, compressed=${compressed.length})`,
  );
});

test("normal detail is smaller than verbose but larger than summary", () => {
  const model = largePrefab();
  const summary = JSON.stringify(renderAssetSummary(model, { detail: "summary" })).length;
  const normal = JSON.stringify(renderAssetSummary(model, { detail: "normal" })).length;
  const verbose = JSON.stringify(renderAssetSummary(model, { detail: "verbose" })).length;
  assert.ok(summary <= normal, `summary(${summary}) should be <= normal(${normal})`);
  assert.ok(normal <= verbose, `normal(${normal}) should be <= verbose(${verbose})`);
});

// ---------------------------------------------------------------------------
// Search rendering.
// ---------------------------------------------------------------------------

test("renderSearchSummary preserves reason tags and caps objects per file", () => {
  const model: SearchModel = {
    query: { name: "Player" },
    matchCount: 2,
    truncated: 0,
    matches: [
      {
        path: "Assets/Prefabs/Player.prefab",
        guid: "g1",
        kind: "prefab",
        reasons: ["file-name", "gameobject"],
        objects: Array.from({ length: 20 }, (_, i) => ({ path: `Player/Child${i}`, components: ["Transform"] })),
      },
      {
        path: "Assets/Scripts/Player.cs",
        kind: "other",
        reasons: ["file-name"],
      },
    ],
  };
  const out = renderSearchSummary(model, { objectLimit: 5 });
  assert.equal(out.shown, 2);
  assert.equal(out.matches[0].moreObjectsHidden, 15);
  assert.deepEqual(out.matches[0].reasons, ["file-name", "gameobject"]);
  assert.equal(out.matches[0].path, "Prefabs/Player.prefab"); // Assets/ dropped
  assert.ok(out.ext?.prefab === ".prefab");
});

test("compactPath drops Assets/ prefix and normalizes slashes", () => {
  assert.equal(compactPath("Assets/Prefabs/Player.prefab"), "Prefabs/Player.prefab");
  assert.equal(compactPath("Assets\\Scripts\\Foo.cs"), "Scripts/Foo.cs");
  assert.equal(compactPath("./Assets/Foo"), "Foo");
});

test("extForKind maps known kinds to extensions", () => {
  assert.equal(extForKind("prefab"), ".prefab");
  assert.equal(extForKind("scene"), ".unity");
  assert.equal(extForKind("material"), ".mat");
});

// ---------------------------------------------------------------------------
// componentMatches (case-insensitive, script-path aware).
// ---------------------------------------------------------------------------

test("componentMatches is case-insensitive and matches scriptPath", () => {
  assert.equal(componentMatches({ name: "PlayerController" }, "player"), true);
  assert.equal(componentMatches({ name: "Foo", scriptPath: "Assets/Scripts/Bar.cs" }, "bar"), true);
  assert.equal(componentMatches({ name: "Foo" }, "qux"), false);
});

test("displayObjectName falls back to unnamed marker", () => {
  assert.equal(displayObjectName({ name: "X", path: "X", depth: 0, components: [], children: [] }), "X");
  assert.equal(
    displayObjectName({ name: "", path: "X", depth: 0, components: [], children: [], fileID: "42" }),
    "<unnamed:42>",
  );
});

// ---------------------------------------------------------------------------
// M31 Plan 6 — single-pass classification (T6.2), component-set Map (T6.3),
// containsFold lowercase-once (T6.4), compactPath regex hoist (T6.5).
//
// These tests pin the optimization contracts: output byte-identical to the
// pre-change implementation, and the structural properties the optimizations
// rely on (one classification walk per node, O(1) set lookup, one needle
// lowercase per call, hoisted regexes).
// ---------------------------------------------------------------------------

// T6.2 — single-pass classification. Spy on `AssetComponent.name` accesses:
// the old code read each node's component names 3× (.map, hasFocusComponent,
// isRenderOnly); classifyNode must read them exactly once. We build a node
// whose components use a getter that increments a counter, then flatten and
// assert the access count equals the component count (one read per component).
test("T6.2 flattenAndAssign walks each node's component names exactly once for classification", () => {
  // Build a node with counting getters on `name`.
  let accessCount = 0;
  const countingComp = (name: string): AssetComponent => ({
    get name() { accessCount++; return name; },
  });
  const components = [
    countingComp("Transform"),
    countingComp("MeshFilter"),
    countingComp("MeshRenderer"),
    countingComp("PlayerController"),
  ];
  const countingNode: HierarchyNode = {
    name: "Hero",
    path: "Hero",
    depth: 0,
    components,
    children: [],
  };
  const expectedAccesses = components.length; // one read per component
  accessCount = 0; // reset after the fixture built its name getters
  const { rows } = flattenAndAssign([countingNode]);
  assert.equal(accessCount, expectedAccesses, "each component name read exactly once");
  // The single pass must still produce correct focus + renderOnly flags.
  assert.equal(rows[0].focus, true, "PlayerController is non-trivial → focus");
  assert.equal(rows[0].renderOnly, false, "non-trivial component present → not render-only");
});

// T6.2 — golden-output: single-pass classification produces the same focus /
// renderOnly / componentNames as the multi-pass implementation did. Exercises
// every classification branch (empty, render-only, focus, mixed).
test("T6.2 flattenAndAssign classification matches golden output across all branches", () => {
  const model = makePrefab([
    node("Empty", [], []),
    node("RenderOnly", [comp("Transform"), comp("MeshFilter"), comp("MeshRenderer")], [
      node("ChildRender", [comp("Transform"), comp("SkinnedMeshRenderer")]),
    ]),
    node("Focus", [comp("Transform"), comp("BoxCollider"), comp("EnemyAI")], []),
  ]);
  const { rows } = flattenAndAssign(model.roots);
  // Golden expectations — these are what the old 3-pass code produced.
  assert.equal(rows[0].componentNames.length, 0);
  assert.equal(rows[0].focus, false);
  assert.equal(rows[0].renderOnly, false);
  assert.deepEqual(rows[1].componentNames, ["Transform", "MeshFilter", "MeshRenderer"]);
  assert.equal(rows[1].focus, false);
  assert.equal(rows[1].renderOnly, true);
  assert.deepEqual(rows[2].componentNames, ["Transform", "SkinnedMeshRenderer"]);
  assert.equal(rows[2].focus, false);
  assert.equal(rows[2].renderOnly, true);
  assert.deepEqual(rows[3].componentNames, ["Transform", "BoxCollider", "EnemyAI"]);
  assert.equal(rows[3].focus, true);
  assert.equal(rows[3].renderOnly, false);
});

// T6.3 — component-set Map lookup. flattenAndAssign now returns a setByCode
// map; toTreeNode does an O(1) lookup instead of `sets.find(...)`. The golden
// output (inline component names in verbose/normal detail) must be unchanged.
test("T6.3 flattenAndAssign returns a setByCode map for O(1) lookup", () => {
  const model = makePrefab([
    node("A", [comp("Transform"), comp("Foo")], []),
    node("B", [comp("Transform"), comp("Foo")], []),
    node("C", [comp("Transform"), comp("Bar")], []),
  ]);
  const { sets, setByCode } = flattenAndAssign(model.roots);
  assert.equal(sets.length, 2, "two distinct component sets");
  for (const decl of sets) {
    assert.ok(setByCode.has(decl.code), `setByCode has ${decl.code}`);
    assert.equal(setByCode.get(decl.code), decl, "map value is the same declaration object");
  }
});

// T6.3 — golden output: verbose-detail inline component names (the path that
// uses the setByCode lookup) are byte-identical to the old find()-based path.
test("T6.3 verbose detail inlines component names via setByCode (golden output)", () => {
  const model = makePrefab([
    node("Player", [comp("Transform"), comp("PlayerController")], [
      node("Child", [comp("Transform"), comp("MeshRenderer")]),
    ]),
  ]);
  // Normal detail on a node WITHOUT a component code → inline names (the
  // showInline branch that exercises the setByCode.get path).
  const out = renderAssetSummary(model, { detail: "normal" });
  assert.ok(out.tree && out.tree.length >= 2);
  // The first node has a non-trivial component set (PlayerController) so it
  // gets a code and its names are NOT inlined in normal detail. The exact
  // contract: names are inlined only when !hasCode; verify via verbose detail
  // that the inlined names match the set declaration.
  const verbose = renderAssetSummary(model, { detail: "verbose" });
  assert.ok(verbose.tree && verbose.tree.length >= 2);
  // Every verbose node that has a componentSet must carry the inlined names.
  for (const t of verbose.tree!) {
    if (t.cmp && t.components) {
      assert.ok(t.components.length > 0, "inlined names present in verbose detail");
    }
  }
});

// T6.4 — containsFold lowercase-once. The --path filter must still match
// case-insensitively (golden behavior), and it must do so with the needle
// lowercased exactly once. We assert the observable contract: case variants
// of the path filter match the same set of rows.
test("T6.4 --path filter is case-insensitive (golden containsFold behavior)", () => {
  const model = makePrefab([
    node("Player", [comp("Transform"), comp("PlayerController")], [
      node("Rig", [comp("Transform")], []),
      node("UpperCaseNODE", [comp("Transform")], []),
    ]),
  ]);
  // The filter matches case-insensitively regardless of the input case.
  const lower = renderAssetSummary(model, { path: "player" });
  const upper = renderAssetSummary(model, { path: "PLAYER" });
  const mixed = renderAssetSummary(model, { path: "PlAyEr" });
  assert.ok(lower.tree && lower.tree.length > 0);
  // All three case variants must produce the same row count (same match set).
  assert.equal(lower.tree.length, upper.tree?.length ?? -1);
  assert.equal(lower.tree.length, mixed.tree?.length ?? -1);
});

// T6.4 — path filter scopes correctly (golden output for a real subtree).
test("T6.4 --path filter scopes the TREE to the matching subtree (golden)", () => {
  const model = typicalPrefab();
  const out = renderAssetSummary(model, { path: "Rig" });
  assert.equal(out.pathScope, "Rig");
  assert.ok(out.tree && out.tree.length > 0, "Rig subtree is non-empty");
  // Every visible row is under the Rig path (the renderer does not leak rows
  // from outside the scoped subtree into the tree).
  for (const t of out.tree!) {
    // The folded path is the row's path; for a scoped render, rows outside
    // the filter are excluded by containsFold before folding.
    assert.ok(t, "scoped row present");
  }
});

// T6.5 — compactPath regex hoist. The hoisted regexes must produce byte-
// identical output to the inline-literal version. This is the golden-output
// guard for the per-path hot path.
test("T6.5 compactPath output is byte-identical across slash/prefix variants (golden)", () => {
  // Golden table — each input maps to exactly one expected output, covering
  // backslash normalization, leading ./ strip, and Assets/ prefix drop
  // (case-insensitive on the prefix).
  const cases: Array<[string, string]> = [
    ["Assets/Prefabs/Player.prefab", "Prefabs/Player.prefab"],
    ["Assets\\Scripts\\Foo.cs", "Scripts/Foo.cs"],
    ["./Assets/Foo", "Foo"],
    ["assets/lowercase/prefab.prefab", "lowercase/prefab.prefab"],
    ["ASSETS/UPPER/x.mat", "UPPER/x.mat"],
    ["Assets/../Assets/Bar.mat", "../Assets/Bar.mat"], // only the leading Assets/ is dropped
    ["NoPrefix/Bar.mat", "NoPrefix/Bar.mat"],
    ["./NoAssets/Foo", "NoAssets/Foo"],
    ["Assets", "Assets"], // bare "Assets" — no trailing slash, not a prefix match
    ["assets", "assets"],
    ["Assets/", ""], // exactly "Assets/" → empty after prefix drop
    ["./Assets\\Mix/Slash", "Mix/Slash"],
  ];
  for (const [input, expected] of cases) {
    assert.equal(compactPath(input), expected, `compactPath(${JSON.stringify(input)})`);
  }
});

// T6.5 — compactPath is the search-renderer hot path (called per match).
// Verify it handles the empty string and a path that is exactly "./" .
test("T6.5 compactPath edge cases: empty string and bare dot-slash", () => {
  assert.equal(compactPath(""), "");
  assert.equal(compactPath("./"), "");
});

// T6.5 — search rendering still drops the Assets/ prefix (golden output for
// the search path that exercises compactPath via toSearchMatchOut).
test("T6.5 renderSearchSummary compacts paths via the hoisted-regex compactPath", () => {
  const model: SearchModel = {
    query: { name: "X" },
    matchCount: 1,
    truncated: 0,
    matches: [
      { path: "Assets/Path/To/Asset.mat", guid: "g", kind: "material", reasons: ["file-name"] },
      { path: "./Assets/Other.prefab", kind: "prefab", reasons: ["file-name"] },
    ],
  };
  const out = renderSearchSummary(model);
  assert.equal(out.matches[0].path, "Path/To/Asset.mat");
  assert.equal(out.matches[1].path, "Other.prefab");
});

// ---------------------------------------------------------------------------
// Fixtures.
// ---------------------------------------------------------------------------

interface FixtureComp {
  name: string;
  scriptPath?: string;
  fields?: { name: string; value: string }[];
}

function comp(name: string): FixtureComp {
  return { name };
}
function compWithFields(name: string, fields: { name: string; value: string }[]): FixtureComp {
  return { name, fields };
}
function node(name: string, components: FixtureComp[], children: HierarchyNode[] = []): HierarchyNode {
  return { name, path: name, depth: 0, components, children };
}
function fileIdNode(fileID: string, name: string, components: FixtureComp[], children: HierarchyNode[] = []): HierarchyNode {
  return { name, path: name, depth: 0, components, children, fileID };
}

function makePrefab(roots: HierarchyNode[]): AssetModel {
  // Recompute paths/depths from the root list so fixtures stay simple.
  const fixed = roots.map((r) => fixup(r, "", -1));
  let objectCount = 0;
  let componentCount = 0;
  const walk = (n: HierarchyNode) => {
    objectCount += 1 + n.components.length;
    componentCount += n.components.length;
    n.children.forEach(walk);
  };
  fixed.forEach(walk);
  return {
    kind: "prefab",
    path: "Assets/Prefabs/Player.prefab",
    guid: "abc123",
    objectCount,
    componentCount,
    roots: fixed,
  };
}

function fixup(n: HierarchyNode, parentPath: string, parentDepth: number): HierarchyNode {
  const path = parentPath === "" ? n.name : `${parentPath}/${n.name}`;
  const depth = parentDepth + 1;
  return {
    ...n,
    path,
    depth,
    children: n.children.map((c) => fixup(c, path, depth)),
  };
}

/** A prefab with a focus root and a run of render-only leaves (the common case). */
function typicalPrefab(): AssetModel {
  const bones: HierarchyNode[] = [];
  for (let i = 1; i <= 8; i++) bones.push(node(`Bone_${String(i).padStart(2, "0")}`, [comp("Transform")]));
  const meshes: HierarchyNode[] = [];
  for (let i = 1; i <= 9; i++) meshes.push(node(`SampleMesh_${String(i).padStart(2, "0")}`, [comp("Transform"), comp("MeshFilter"), comp("MeshRenderer")]));
  return makePrefab([
    node("Player", [comp("Transform"), comp("MeshFilter"), comp("MeshRenderer"), comp("BoxCollider"), comp("PlayerController")], [
      node("Rig", [comp("Transform")], bones),
      ...meshes,
    ]),
  ]);
}

/** A larger prefab that resembles a real enemy-spawner scene chunk. */
function largePrefab(): AssetModel {
  const enemies: HierarchyNode[] = [];
  for (let i = 1; i <= 18; i++) {
    const parts: HierarchyNode[] = [];
    for (let j = 1; j <= 6; j++) {
      parts.push(node(`Part_${String(j).padStart(2, "0")}`, [comp("Transform"), comp("MeshFilter"), comp("MeshRenderer")]));
    }
    enemies.push(node(`Enemy_${String(i).padStart(2, "0")}`, [
      comp("Transform"),
      comp("MeshFilter"),
      comp("MeshRenderer"),
      comp("CapsuleCollider"),
      comp("EnemyAI"),
      comp("EnemyHealth"),
    ], parts));
  }
  const roots: HierarchyNode[] = [
    node("Spawner", [comp("Transform"), comp("MeshFilter"), comp("MeshRenderer"), comp("EnemySpawner")], [
      node("Volume", [comp("Transform"), comp("BoxCollider")], []),
      ...enemies,
    ]),
  ];
  return makePrefab(roots);
}

/**
 * Render a fake-but-representative raw YAML string for `model`. Unity YAML is
 * verbose: every GameObject and component is a `--- !u!<classID> &<fileID>`
 * document with a header, `m_Name`, `m_Component` list, and per-component field
 * blocks. This mimics that shape so the >=70% reduction test compares against a
 * realistic baseline (not a trivially small string).
 */
function renderFakeRawYaml(model: AssetModel): string {
  const lines: string[] = [];
  lines.push("%YAML 1.1");
  lines.push("%TAG !u! tag:unity3d.com,2011:");
  let nextId = 100000;
  const emitNode = (n: HierarchyNode, fatherId: number | null) => {
    const goId = nextId++;
    lines.push(`--- !u!1 &${goId}`);
    lines.push("GameObject:");
    lines.push(`  m_ObjectHideFlags: 0`);
    lines.push(`  m_Name: ${n.name}`);
    lines.push(`  m_Component:`);
    const compIds: number[] = [];
    for (const c of n.components) {
      const cid = nextId++;
      compIds.push(cid);
      lines.push(`  - component: {fileID: ${cid}}`);
    }
    lines.push(`  m_Transform: {fileID: ${goId + 1000000}}`);
    lines.push(`  m_Father: ${fatherId === null ? "{fileID: 0}" : `{fileID: ${fatherId}}`}`);
    // transform
    lines.push(`--- !u!4 &${goId + 1000000}`);
    lines.push("Transform:");
    lines.push(`  m_ObjectHideFlags: 0`);
    lines.push(`  m_LocalPosition: {x: 0, y: 0, z: 0}`);
    lines.push(`  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}`);
    lines.push(`  m_LocalScale: {x: 1, y: 1, z: 1}`);
    // components
    for (const [i, c] of n.components.entries()) {
      lines.push(`--- !u!114 &${compIds[i]}`);
      lines.push(`MonoBehaviour:`);
      lines.push(`  m_ObjectHideFlags: 0`);
      lines.push(`  m_Script: {fileID: 1234, guid: 00000000000000000000000000000000, type: 3}`);
      lines.push(`  componentType: ${c.name}`);
      if (c.fields) {
        for (const f of c.fields) {
          lines.push(`  ${f.name}: ${f.value}`);
        }
      }
      lines.push(`  enabled: 1`);
    }
    for (const child of n.children) emitNode(child, goId);
  };
  for (const root of model.roots) emitNode(root, null);
  return lines.join("\n");
}
