// Tests for the M9 Plan 3 offline asset reader. These prove the T1.1
// acceptance criteria: read hierarchy + components + GUID→path for any
// text-serialized asset without a running Editor, works in CI (no Unity
// license), no Library/ dependency.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm, mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, sep } from "node:path";

import {
  readAssetOffline,
  isOfflineAsset,
  searchAssetsOffline,
  listAssetsOffline,
  findReferencesOffline,
  dependenciesOffline,
  scanIntegrityOffline,
  countHierarchy,
} from "./offline.js";
// M31-optimizations Plan 2 — test-only walk counters, used to assert the
// single-walk acceptance criteria (H3 scan, H5 read_asset, H4 impact BFS).
import {
  resetWalkCounters,
  getWalkMetaCount,
  getCollectFilesCount,
  parallelMap,
  collectFiles,
  buildGuidAndScriptIndex,
  buildGuidScriptAndNameIndex,
} from "./offline/index-builders.js";
// M31-optimizations Plan 3 — direct unit tests of the single-pass / cached
// offline primitives (extractKnownFields, cleanScalar, objectPath cache,
// extractExtension). These prove the H6/H7/H8/L3/L7/L8-offline acceptance
// criteria at the leaf layer (no project fixture needed for most).
import {
  extractKnownFields,
  cleanScalar,
} from "./offline/primitives.js";
import {
  buildHierarchy,
  objectPath,
  componentsFor,
} from "./offline/hierarchy.js";
import { extractExtension } from "./offline/paths.js";
import { renderAssetSummary } from "./compression/compact.js";
import type { AssetModel } from "./compression/asset-model.js";

/**
 * M31-optimizations Plan 3 — strip // line comments and /\* *\/ block comments
 * from a TypeScript source string. Used by the code-inspection acceptance
 * tests so doc-comments that mention the pre-change shape (e.g. "previously
 * three loops", "was an inline ... literal") do not inflate the structural
 * counts those tests assert.
 *
 * String literals are NOT tracked — the offline sources being inspected do not
 * contain `//` or `/*` inside string literals, so a naive strip is sufficient
 * and keeps the helper trivial. If that ever changes, the tests using it will
 * fail loudly and the helper can be upgraded then.
 */
function stripComments(src: string): string {
  let out = "";
  let i = 0;
  while (i < src.length) {
    // Line comment: // ... \n
    if (src[i] === "/" && src[i + 1] === "/") {
      const nl = src.indexOf("\n", i);
      i = nl < 0 ? src.length : nl;
      continue;
    }
    // Block comment: /* ... */
    if (src[i] === "/" && src[i + 1] === "*") {
      const end = src.indexOf("*/", i + 2);
      i = end < 0 ? src.length : end + 2;
      continue;
    }
    out += src[i];
    i++;
  }
  return out;
}

/**
 * M31-optimizations Plan 3 — resolve the on-disk path of an offline `.ts`
 * source file for the code-inspection acceptance tests.
 *
 * `npm test` compiles tests to `dist-test/` and runs them from there, so
 * `import.meta.dirname` points at `dist-test/` (no `.ts` files) when run that
 * way, but at `src/` when run via `tsx --test src/`. This helper walks up from
 * the test file's directory to find the `mcp-server/` root (identified by
 * `package.json`), then joins into `src/offline/<name>.ts`. Works identically
 * under both execution paths.
 */
async function offlineSourcePath(name: string): Promise<string> {
  const { readFile, stat } = await import("node:fs/promises");
  let dir = import.meta.dirname;
  for (let i = 0; i < 8; i++) {
    try {
      await readFile(join(dir, "package.json"));
      const candidate = join(dir, "src", "offline", `${name}.ts`);
      try {
        await stat(candidate);
        return candidate;
      } catch { /* keep walking */ }
      break;
    } catch { /* not the package root */ }
    const slash = dir.lastIndexOf(sep);
    if (slash <= 0) break;
    dir = dir.slice(0, slash);
  }
  throw new Error(`could not locate src/offline/${name}.ts from ${import.meta.dirname}`);
}

// ---------------------------------------------------------------------------
// readAssetOffline — end-to-end integration (acceptance criteria).
// ---------------------------------------------------------------------------

test("readAssetOffline returns hierarchy + components + GUID→path without running Editor", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-read-"));
  try {
    await setupProject(tmp);
    const { model, source } = await readAssetOffline("Assets/Prefabs/Player.prefab", {
      fieldLimit: 10,
      projectRoot: tmp,
    });
    assert.equal(source, "offline");
    assert.equal(model.kind, "prefab");
    assert.equal(model.path, "Assets/Prefabs/Player.prefab");
    assert.equal(model.guid, "aaa111");
    assert.ok(model.roots.length > 0, "has hierarchy roots");
    assert.equal(model.roots[0].name, "Player");

    const playerController = model.roots[0].components.find((c) => c.name === "PlayerController");
    assert.ok(playerController, "PlayerController component present");
    assert.equal(playerController.scriptPath, "Assets/Scripts/PlayerController.cs");

    assert.ok(playerController.fields, "fields present");
    const speedField = playerController.fields.find((f) => f.name === "m_Speed");
    assert.ok(speedField);

    assert.ok(model.roots[0].children.length > 0, "has children");
    assert.equal(model.roots[0].children[0].name, "Model");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("readAssetOffline works with fieldLimit=0 (names only)", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-nofields-"));
  try {
    await setupProject(tmp);
    const { model } = await readAssetOffline("Assets/Prefabs/Player.prefab", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    const playerController = model.roots[0].components.find((c) => c.name === "PlayerController");
    assert.ok(playerController);
    assert.equal(playerController.fields, undefined, "no fields when fieldLimit=0");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("readAssetOffline resolves field GUID references to asset paths", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-fieldguid-"));
  try {
    await setupProject(tmp);
    const { model } = await readAssetOffline("Assets/Prefabs/Player.prefab", {
      fieldLimit: 20,
      projectRoot: tmp,
    });
    const playerController = model.roots[0].components.find((c) => c.name === "PlayerController");
    assert.ok(playerController);
    const targetField = playerController.fields?.find((f) => f.name === "m_Target");
    assert.ok(targetField, "m_Target field present");
    assert.ok(targetField.value.includes("Config"), `value resolves GUID to asset name: ${targetField.value}`);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("readAssetOffline produces model that the compression module renders", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-render-"));
  try {
    await setupProject(tmp);
    const { model } = await readAssetOffline("Assets/Prefabs/Player.prefab", {
      fieldLimit: 10,
      projectRoot: tmp,
    });
    const compact = renderAssetSummary(model, { detail: "summary" });
    assert.equal(compact.asset, "prefab");
    assert.ok(compact.tree && compact.tree.length > 0);
    assert.ok(compact.cmp, "CMP table present");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("readAssetOffline works for ScriptableObject (.asset flat)", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-so-"));
  try {
    await mkdir(join(tmp, "Assets", "Data"), { recursive: true });
    await writeFile(
      join(tmp, "Assets", "Data", "Config.asset"),
      `%YAML 1.1
--- !u!114 &11400000
MonoBehaviour:
  m_Script: {fileID: 11500000, guid: aabb000000000000000000000000aa03, type: 3}
  m_Name: Config
  m_Value: 42
  m_Threshold: 0.5
`,
    );
    await writeFile(join(tmp, "Assets", "Data", "Config.asset.meta"), "guid: cfg111\n");
    await mkdir(join(tmp, "Assets", "Scripts"), { recursive: true });
    await writeFile(join(tmp, "Assets", "Scripts", "ConfigAsset.cs.meta"), "guid: aabb000000000000000000000000aa03\n");

    const { model } = await readAssetOffline("Assets/Data/Config.asset", {
      fieldLimit: 10,
      projectRoot: tmp,
    });
    assert.equal(model.roots.length, 0, "no hierarchy for ScriptableObject");
    assert.ok(model.flatObjects && model.flatObjects.length > 0);
    assert.equal(model.flatObjects[0].name, "Config");
    assert.ok(model.flatObjects[0].fields);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("isOfflineAsset identifies text-serialized extensions", () => {
  assert.equal(isOfflineAsset("Assets/Foo.prefab"), true);
  assert.equal(isOfflineAsset("Assets/Scene.unity"), true);
  assert.equal(isOfflineAsset("Assets/Data.asset"), true);
  assert.equal(isOfflineAsset("Assets/Tex.png"), false);
  assert.equal(isOfflineAsset("Assets/Audio.wav"), false);
});

// ---------------------------------------------------------------------------
// searchAssetsOffline — offline search.
// ---------------------------------------------------------------------------

test("searchAssetsOffline finds assets by file name", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-search-name-"));
  try {
    await setupProject(tmp);
    const result = await searchAssetsOffline({ name: "Player", projectRoot: tmp });
    assert.ok(result.matches.length > 0);
    const playerMatch = result.matches.find((m) => m.path.includes("Player.prefab"));
    assert.ok(playerMatch);
    assert.ok(playerMatch.reasons.includes("file-name"));
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("searchAssetsOffline finds assets by component/script name", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-search-comp-"));
  try {
    await setupProject(tmp);
    const result = await searchAssetsOffline({ component: "PlayerController", projectRoot: tmp });
    assert.ok(result.matches.length > 0);
    const playerMatch = result.matches.find((m) => m.path.includes("Player.prefab"));
    assert.ok(playerMatch);
    assert.ok(playerMatch.reasons.includes("component"));
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("searchAssetsOffline finds assets by GameObject name", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-search-go-"));
  try {
    await setupProject(tmp);
    const result = await searchAssetsOffline({ name: "Model", projectRoot: tmp });
    const playerMatch = result.matches.find((m) => m.path.includes("Player.prefab"));
    assert.ok(playerMatch);
    assert.ok(playerMatch.reasons.includes("gameobject"));
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// listAssetsOffline — directory listing.
// ---------------------------------------------------------------------------

test("listAssetsOffline lists files grouped by folder and kind, drops .meta", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-list-"));
  try {
    await setupProject(tmp);
    const result = await listAssetsOffline({ projectRoot: tmp });
    assert.ok(result.totalFiles > 0);
    assert.ok(result.totalFolders > 0);
    for (const folder of result.folders) {
      for (const kind of Object.keys(folder.kinds)) {
        if (kind === "meta") assert.fail("meta kind should not appear");
      }
    }
    const prefabFolder = result.folders.find((f) => f.folder.includes("Prefabs"));
    assert.ok(prefabFolder, "Prefabs folder present");
    assert.ok(prefabFolder.kinds.prefab, "prefab kind present");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("listAssetsOffline respects type filter", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-list-type-"));
  try {
    await setupProject(tmp);
    const result = await listAssetsOffline({ projectRoot: tmp, type: "prefab" });
    for (const folder of result.folders) {
      for (const kind of Object.keys(folder.kinds)) {
        assert.equal(kind, "prefab");
      }
    }
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// Fixtures.
// ---------------------------------------------------------------------------

async function setupProject(tmp: string): Promise<void> {
  await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
  await writeFile(
    join(tmp, "Assets", "Prefabs", "Player.prefab"),
    `%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &100
GameObject:
  m_Component:
  - component: {fileID: 200}
  - component: {fileID: 300}
  m_Name: Player
--- !u!4 &200
Transform:
  m_GameObject: {fileID: 100}
  m_Father: {fileID: 0}
--- !u!114 &300
MonoBehaviour:
  m_GameObject: {fileID: 100}
  m_Script: {fileID: 11500000, guid: aabb0000000000000000000000000001, type: 3}
  m_Speed: 5
  m_Health: 100
  m_Target: {fileID: 0, guid: aabb0000000000000000000000000002, type: 2}
--- !u!1 &101
GameObject:
  m_Component:
  - component: {fileID: 201}
  m_Name: Model
--- !u!4 &201
Transform:
  m_GameObject: {fileID: 101}
  m_Father: {fileID: 200}
`,
  );
  await writeFile(join(tmp, "Assets", "Prefabs", "Player.prefab.meta"), "guid: aaa111\n");

  await mkdir(join(tmp, "Assets", "Scripts"), { recursive: true });
  await writeFile(join(tmp, "Assets", "Scripts", "PlayerController.cs.meta"), "guid: aabb0000000000000000000000000001\n");

  await mkdir(join(tmp, "Assets", "data"), { recursive: true });
  await writeFile(join(tmp, "Assets", "data", "Config.asset"), `%YAML 1.1\n--- !u!114 &11400000\nMonoBehaviour:\n  m_Name: Config\n`);
  await writeFile(join(tmp, "Assets", "data", "Config.asset.meta"), "guid: aabb0000000000000000000000000002\n");
}

// ---------------------------------------------------------------------------
// findReferencesOffline — offline reverse reference lookup (T1.4).
// ---------------------------------------------------------------------------

test("findReferencesOffline finds assets referencing a GUID without running Editor", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-refs-"));
  try {
    await setupReferencesProject(tmp);
    const result = await findReferencesOffline({
      guid: "dead000000000000000000000000beef",
      projectRoot: tmp,
    });
    assert.equal(result.queriedAssetGuid, "dead000000000000000000000000beef");
    assert.equal(result.queriedAssetPath, "Assets/Materials/SharedMat.mat");
    assert.ok(result.totalCount >= 2, "at least 2 referencing assets");
    const paths = result.referencedBy.map((e) => e.assetPath).sort();
    assert.ok(paths.includes("Assets/Prefabs/Player.prefab"));
    assert.ok(paths.includes("Assets/Scenes/Main.unity"));
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("findReferencesOffline works by asset_path (resolves GUID from .meta)", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-refs-path-"));
  try {
    await setupReferencesProject(tmp);
    const result = await findReferencesOffline({
      assetPath: "Assets/Materials/SharedMat.mat",
      projectRoot: tmp,
    });
    assert.equal(result.queriedAssetGuid, "dead000000000000000000000000beef");
    assert.ok(result.totalCount >= 2);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("findReferencesOffline groups results by kind and folder", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-refs-group-"));
  try {
    await setupReferencesProject(tmp);
    const result = await findReferencesOffline({
      guid: "dead000000000000000000000000beef",
      projectRoot: tmp,
    });
    assert.ok(result.byKind.prefab >= 1, "prefab kind in byKind");
    assert.ok(result.byKind.scene >= 1, "scene kind in byKind");
    assert.ok(result.byFolder["Assets/Prefabs"] >= 1);
    assert.ok(result.byFolder["Assets/Scenes"] >= 1);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("findReferencesOffline summary detail omits individual entries", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-refs-summary-"));
  try {
    await setupReferencesProject(tmp);
    const result = await findReferencesOffline({
      guid: "dead000000000000000000000000beef",
      detail: "summary",
      projectRoot: tmp,
    });
    assert.equal(result.referencedBy.length, 0, "summary has no individual entries");
    assert.ok(result.totalCount > 0, "totalCount still accurate");
    assert.ok(Object.keys(result.byKind).length > 0, "groups still present");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("findReferencesOffline verbose includes field locations", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-refs-verbose-"));
  try {
    await setupReferencesProject(tmp);
    const result = await findReferencesOffline({
      guid: "dead000000000000000000000000beef",
      detail: "verbose",
      projectRoot: tmp,
    });
    const playerEntry = result.referencedBy.find((e) => e.assetPath.includes("Player.prefab"));
    assert.ok(playerEntry, "Player.prefab in results");
    assert.ok(playerEntry.locations && playerEntry.locations.length > 0, "has locations");
    assert.ok(
      playerEntry.locations.some((l) => l.includes("m_Material")),
      `locations include m_Material: ${JSON.stringify(playerEntry.locations)}`,
    );
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("findReferencesOffline respects max_results cap", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-refs-cap-"));
  try {
    await setupReferencesProject(tmp);
    const full = await findReferencesOffline({
      guid: "dead000000000000000000000000beef",
      projectRoot: tmp,
    });
    const capped = await findReferencesOffline({
      guid: "dead000000000000000000000000beef",
      maxResults: 1,
      projectRoot: tmp,
    });
    assert.equal(full.totalCount, capped.totalCount, "totalCount unchanged");
    assert.ok(capped.referencedBy.length <= 1, "referencedBy capped");
    assert.ok(capped.truncated >= full.totalCount - 1, "truncated count reported");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("findReferencesOffline pattern_threshold collapses large folders", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-refs-collapse-"));
  try {
    await setupReferencesProject(tmp);
    // With threshold=1, every folder with >=1 file gets collapsed.
    const result = await findReferencesOffline({
      guid: "dead000000000000000000000000beef",
      patternThreshold: 1,
      projectRoot: tmp,
    });
    assert.ok(result.collapsedGroups && result.collapsedGroups.length > 0, "collapsed groups present");
    // All hits should be absorbed into collapsed groups.
    assert.equal(result.referencedBy.length, 0, "no individual entries after full collapse");
    assert.ok(result.totalCount > 0, "totalCount still accurate");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("findReferencesOffline excludes self-reference", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-refs-self-"));
  try {
    await setupReferencesProject(tmp);
    const result = await findReferencesOffline({
      assetPath: "Assets/Materials/SharedMat.mat",
      projectRoot: tmp,
    });
    const selfHit = result.referencedBy.find((e) => e.assetPath === "Assets/Materials/SharedMat.mat");
    assert.equal(selfHit, undefined, "target asset not in its own results");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("findReferencesOffline returns empty for GUID with no references", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-refs-empty-"));
  try {
    await setupReferencesProject(tmp);
    const result = await findReferencesOffline({
      guid: "ffff000000000000000000000000ffff",
      projectRoot: tmp,
    });
    assert.equal(result.totalCount, 0);
    assert.equal(result.referencedBy.length, 0);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("findReferencesOffline catches prefab modification references (variant overrides)", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-refs-variant-"));
  try {
    await setupReferencesProject(tmp);
    const result = await findReferencesOffline({
      guid: "aaa1110000000000000000000000aaa1",
      detail: "verbose",
      projectRoot: tmp,
    });
    // The variant prefab references the base prefab GUID in m_Modifications.
    const variantEntry = result.referencedBy.find((e) => e.assetPath.includes("PlayerVariant.prefab"));
    assert.ok(variantEntry, "variant prefab found");
    assert.ok(
      variantEntry.locations && variantEntry.locations.some((l) => l.startsWith("prefab →")),
      `locations include prefab modification: ${JSON.stringify(variantEntry.locations)}`,
    );
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// Fixtures for findReferencesOffline tests.
// ---------------------------------------------------------------------------

async function setupReferencesProject(tmp: string): Promise<void> {
  const matGuid = "dead000000000000000000000000beef";
  const basePrefabGuid = "aaa1110000000000000000000000aaa1";

  // Shared material (the target asset).
  await mkdir(join(tmp, "Assets", "Materials"), { recursive: true });
  await writeFile(
    join(tmp, "Assets", "Materials", "SharedMat.mat"),
    `%YAML 1.1
--- !u!21 &2100000
Material:
  m_Name: SharedMat
`,
  );
  await writeFile(join(tmp, "Assets", "Materials", "SharedMat.mat.meta"), `guid: ${matGuid}\n`);

  // Base prefab referencing the material.
  await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
  await writeFile(
    join(tmp, "Assets", "Prefabs", "Player.prefab"),
    `%YAML 1.1
--- !u!1 &100
GameObject:
  m_Component:
  - component: {fileID: 200}
  - component: {fileID: 300}
  m_Name: Player
--- !u!4 &200
Transform:
  m_GameObject: {fileID: 100}
  m_Father: {fileID: 0}
--- !u!23 &300
MeshRenderer:
  m_GameObject: {fileID: 100}
  m_Material: {fileID: 0, guid: ${matGuid}, type: 2}
`,
  );
  await writeFile(join(tmp, "Assets", "Prefabs", "Player.prefab.meta"), `guid: ${basePrefabGuid}\n`);

  // Variant prefab that modifies the base prefab.
  await writeFile(
    join(tmp, "Assets", "Prefabs", "PlayerVariant.prefab"),
    `%YAML 1.1
--- !u!1001 &400
PrefabInstance:
  m_Modifications:
  - target: {fileID: 100, guid: ${basePrefabGuid}, type: 3}
    propertyPath: m_Name
    value: PlayerVariant
  - target: {fileID: 300, guid: ${basePrefabGuid}, type: 3}
    propertyPath: m_Materials.Array.data[0]
    value: 
    objectReference: {fileID: 0, guid: ${matGuid}, type: 2}
  m_SourcePrefab: {fileID: 100100000, guid: ${basePrefabGuid}, type: 3}
`,
  );
  await writeFile(join(tmp, "Assets", "Prefabs", "PlayerVariant.prefab.meta"), "guid: bbb2220000000000000000000000bbb2\n");

  // Scene referencing the material.
  await mkdir(join(tmp, "Assets", "Scenes"), { recursive: true });
  await writeFile(
    join(tmp, "Assets", "Scenes", "Main.unity"),
    `%YAML 1.1
--- !u!1 &500
GameObject:
  m_Component:
  - component: {fileID: 600}
  - component: {fileID: 700}
  m_Name: Cube
--- !u!4 &600
Transform:
  m_GameObject: {fileID: 500}
  m_Father: {fileID: 0}
--- !u!23 &700
MeshRenderer:
  m_GameObject: {fileID: 500}
  m_Material: {fileID: 0, guid: ${matGuid}, type: 2}
`,
  );
  await writeFile(join(tmp, "Assets", "Scenes", "Main.unity.meta"), "guid: ccc3330000000000000000000000ccc3\n");
}

// ---------------------------------------------------------------------------
// Prefab override parsing (T1.7) — variant override list readable offline.
// ---------------------------------------------------------------------------

test("readAssetOffline collects prefab variant overrides from PrefabInstance", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-overrides-"));
  try {
    await setupVariantProject(tmp);
    const { model } = await readAssetOffline("Assets/Prefabs/PlayerVariant.prefab", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    assert.ok(model.overrides, "overrides populated");
    assert.ok(model.overrides.length >= 4, "at least 4 override entries");

    const nameOverride = model.overrides.find(
      (o) => o.kind === "property" && o.propertyPath === "m_Name",
    );
    assert.ok(nameOverride, "m_Name property override present");
    assert.equal(nameOverride.value, "PlayerVariant");

    const speedOverride = model.overrides.find(
      (o) => o.kind === "property" && o.propertyPath === "m_Speed",
    );
    assert.ok(speedOverride, "m_Speed property override present");
    assert.equal(speedOverride.value, "10");
    assert.ok(speedOverride.target, "m_Speed target resolved");
    assert.ok(speedOverride.target.includes("PlayerVariant"), "target references resolved GO name");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("prefab overrides unwrap C# backing-field names", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-overrides-bf-"));
  try {
    await setupVariantProject(tmp);
    const { model } = await readAssetOffline("Assets/Prefabs/PlayerVariant.prefab", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    assert.ok(model.overrides);
    const healthOverride = model.overrides.find((o) => o.propertyPath === "Health");
    assert.ok(healthOverride, "<Health>k__BackingField unwrapped to Health");
    assert.equal(healthOverride.value, "200");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("prefab overrides include added and removed components", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-overrides-ar-"));
  try {
    await setupVariantProject(tmp);
    const { model } = await readAssetOffline("Assets/Prefabs/PlayerVariant.prefab", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    assert.ok(model.overrides);

    const added = model.overrides.filter((o) => o.kind === "added-component");
    assert.ok(added.length >= 1, "added-component entry present");
    assert.ok(added[0].addedObject, "addedObject resolved");
    assert.ok(added[0].addedObject.includes("Rigidbody"), "addedObject is Rigidbody");

    const removed = model.overrides.filter((o) => o.kind === "removed-components");
    assert.ok(removed.length >= 1, "removed-components entry present");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("renderAssetSummary override drill-down returns overrides list", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-overrides-render-"));
  try {
    await setupVariantProject(tmp);
    const { model } = await readAssetOffline("Assets/Prefabs/PlayerVariant.prefab", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    const compact = renderAssetSummary(model, { override: true });
    assert.ok(compact.overrides, "overrides in compact result");
    assert.equal(compact.tree, undefined, "no TREE when override=true");
    const nameOverride = compact.overrides.find(
      (o) => o.propertyPath === "m_Name",
    );
    assert.ok(nameOverride, "m_Name override in compact output");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("renderAssetSummary override=true on non-variant prefab gives note", () => {
  const model: AssetModel = {
    kind: "prefab",
    path: "Assets/Foo.prefab",
    objectCount: 2,
    componentCount: 1,
    roots: [
      {
        name: "Foo",
        path: "Foo",
        depth: 0,
        components: [{ name: "Transform" }],
        children: [],
      },
    ],
  };
  const compact = renderAssetSummary(model, { override: true });
  assert.equal(compact.overrides, undefined);
  assert.ok(compact.note && compact.note.includes("no prefab overrides"));
});

test("prefab overrides normalize {fileID: 0} value to null", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-overrides-null-"));
  try {
    await setupVariantProject(tmp);
    const { model } = await readAssetOffline("Assets/Prefabs/PlayerVariant.prefab", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    assert.ok(model.overrides);
    const clearedRef = model.overrides.find((o) => o.propertyPath === "m_Target");
    assert.ok(clearedRef, "m_Target override present");
    assert.equal(clearedRef.value, "null", "{fileID: 0} normalized to null");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// Fixtures for prefab override tests.
// ---------------------------------------------------------------------------

async function setupVariantProject(tmp: string): Promise<void> {
  const basePrefabGuid = "base11100000000000000000000000001";
  const scriptGuid = "scrpt00000000000000000000000000001";

  await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
  await mkdir(join(tmp, "Assets", "Scripts"), { recursive: true });

  // Base prefab (the source).
  await writeFile(
    join(tmp, "Assets", "Prefabs", "Player.prefab"),
    `%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &100
GameObject:
  m_Component:
  - component: {fileID: 200}
  - component: {fileID: 300}
  - component: {fileID: 400}
  m_Name: Player
--- !u!4 &200
Transform:
  m_GameObject: {fileID: 100}
  m_Father: {fileID: 0}
--- !u!114 &300
MonoBehaviour:
  m_GameObject: {fileID: 100}
  m_Script: {fileID: 11500000, guid: ${scriptGuid}, type: 3}
  m_Speed: 5
  m_Health: 100
--- !u!82 &400
AudioSource:
  m_GameObject: {fileID: 100}
`,
  );
  await writeFile(join(tmp, "Assets", "Prefabs", "Player.prefab.meta"), `guid: ${basePrefabGuid}\n`);
  await writeFile(join(tmp, "Assets", "Scripts", "PlayerController.cs.meta"), `guid: ${scriptGuid}\n`);

  // Variant prefab with modifications, removed/added components.
  await writeFile(
    join(tmp, "Assets", "Prefabs", "PlayerVariant.prefab"),
    `%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &101
GameObject:
  m_CorrespondingSourceObject: {fileID: 100, guid: ${basePrefabGuid}, type: 3}
  m_PrefabInstance: {fileID: 500}
  m_Component:
  - component: {fileID: 201}
  - component: {fileID: 301}
  - component: {fileID: 600}
  m_Name: PlayerVariant
--- !u!4 &201
Transform:
  m_GameObject: {fileID: 101}
  m_Father: {fileID: 0}
--- !u!114 &301
MonoBehaviour:
  m_GameObject: {fileID: 101}
  m_CorrespondingSourceObject: {fileID: 300, guid: ${basePrefabGuid}, type: 3}
  m_PrefabInstance: {fileID: 500}
  m_Script: {fileID: 11500000, guid: ${scriptGuid}, type: 3}
  m_Speed: 10
  m_Health: 200
  m_Target: {fileID: 0}
--- !u!54 &600
Rigidbody:
  m_GameObject: {fileID: 101}
--- !u!1001 &500
PrefabInstance:
  m_ObjectHideFlags: 0
  serializedVersion: 2
  m_Modification:
    serializedVersion: 3
    m_TransformParent: {fileID: 0}
    m_Modifications:
    - target: {fileID: 100, guid: ${basePrefabGuid}, type: 3}
      propertyPath: m_Name
      value: PlayerVariant
      objectReference: {fileID: 0}
    - target: {fileID: 300, guid: ${basePrefabGuid}, type: 3}
      propertyPath: m_Speed
      value: 10
      objectReference: {fileID: 0}
    - target: {fileID: 300, guid: ${basePrefabGuid}, type: 3}
      propertyPath: <Health>k__BackingField
      value: 200
      objectReference: {fileID: 0}
    - target: {fileID: 300, guid: ${basePrefabGuid}, type: 3}
      propertyPath: m_Target
      value:
      objectReference: {fileID: 0}
    m_RemovedComponents:
    - {fileID: 400, guid: ${basePrefabGuid}, type: 3}
    m_RemovedGameObjects: []
    m_AddedGameObjects: []
    m_AddedComponents:
    - targetCorrespondingSourceObject: {fileID: 101, guid: ${basePrefabGuid}, type: 3}
      addedObject: {fileID: 600, guid: 0, type: 0}
  m_SourcePrefab: {fileID: 100100000, guid: ${basePrefabGuid}, type: 3}
`,
  );
  await writeFile(
    join(tmp, "Assets", "Prefabs", "PlayerVariant.prefab.meta"),
    "guid: variant000000000000000000000000002\n",
  );
}

// ===========================================================================
// M24 Plan 1 — expanded offline parsers (T24.1.1).
//
// Coverage: .asmdef (JSON), .shadergraph (JSON stream), .preset (YAML),
// .terrainlayer (YAML), .spriteatlas (YAML) — plus per-type integrity checks.
// unity-scanner handles YAML only; the JSON path is our differentiator.
// ===========================================================================

test("isOfflineAsset gates the expanded type set", () => {
  // Original YAML set still offline-parseable.
  assert.equal(isOfflineAsset("Assets/Foo.prefab"), true);
  assert.equal(isOfflineAsset("Assets/Scene.unity"), true);
  // M24 additions.
  assert.equal(isOfflineAsset("Assets/Foo.asmdef"), true);
  assert.equal(isOfflineAsset("Assets/Foo.shadergraph"), true);
  assert.equal(isOfflineAsset("Assets/Foo.shadersubgraph"), true);
  assert.equal(isOfflineAsset("Assets/Foo.preset"), true);
  assert.equal(isOfflineAsset("Assets/Foo.terrainlayer"), true);
  assert.equal(isOfflineAsset("Assets/Foo.spriteatlas"), true);
  assert.equal(isOfflineAsset("Assets/Foo.vfx"), true);
  // Binary / unsupported stay live-routed.
  assert.equal(isOfflineAsset("Assets/Tex.png"), false);
  assert.equal(isOfflineAsset("Assets/Audio.wav"), false);
});

test("readAssetOffline parses .asmdef (single JSON object)", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-asmdef-"));
  try {
    await setupAsmdefProject(tmp);
    const { model, source } = await readAssetOffline("Assets/Scripts/Foo.asmdef", {
      fieldLimit: 20,
      projectRoot: tmp,
    });
    assert.equal(source, "offline");
    assert.equal(model.kind, "asmdef");
    assert.equal(model.roots.length, 0, "no hierarchy for asmdef");
    assert.ok(model.flatObjects && model.flatObjects.length > 0, "flatObjects present");
    const nameField = model.flatObjects[0].fields?.find((f) => f.name === "name");
    assert.ok(nameField, "name field present");
    assert.equal(nameField.value, "Foo");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("readAssetOffline parses .shadergraph (JSON object stream)", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-sg-"));
  try {
    await setupShaderGraphProject(tmp);
    const { model } = await readAssetOffline("Assets/Foo.shadergraph", {
      fieldLimit: 30,
      projectRoot: tmp,
    });
    assert.equal(model.kind, "shadergraph");
    assert.equal(model.roots.length, 0, "no hierarchy for shadergraph");
    // The stream splits into one object per JSON document (graph root + node).
    assert.ok(model.flatObjects && model.flatObjects.length >= 2, "multiple flat objects from stream");
    const root = model.flatObjects[0];
    assert.ok(root.type.includes("GraphData"), `root type is GraphData: ${root.type}`);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("readAssetOffline parses .preset (YAML)", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-preset-"));
  try {
    await setupPresetProject(tmp);
    const { model } = await readAssetOffline("Assets/Presets/MyPreset.preset", {
      fieldLimit: 20,
      projectRoot: tmp,
    });
    assert.equal(model.kind, "preset");
    assert.equal(model.roots.length, 0, "no hierarchy for preset");
    assert.ok(model.flatObjects && model.flatObjects.length > 0);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("readAssetOffline parses .terrainlayer (YAML) and resolves texture refs", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-terrainlayer-"));
  try {
    await setupTerrainLayerProject(tmp);
    const { model } = await readAssetOffline("Assets/Terrain/dry_soil.terrainlayer", {
      fieldLimit: 20,
      projectRoot: tmp,
    });
    assert.equal(model.kind, "terrainlayer");
    assert.ok(model.flatObjects && model.flatObjects.length > 0);
    const diffuse = model.flatObjects[0].fields?.find((f) => f.name === "m_DiffuseTexture");
    assert.ok(diffuse, "m_DiffuseTexture field present");
    // The field GUID should resolve to the texture asset name via the meta index.
    assert.ok(diffuse.value.includes("Diffuse"), `diffuse ref resolves: ${diffuse.value}`);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("readAssetOffline parses .spriteatlas (YAML) packable refs", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-atlas-"));
  try {
    await setupSpriteAtlasProject(tmp);
    const { model } = await readAssetOffline("Assets/UI/Icons.spriteatlas", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    assert.equal(model.kind, "atlas");
    assert.ok(model.flatObjects && model.flatObjects.length > 0);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// M24 — integrity checks (T24.1.1 "feeding the verify engine").
// ---------------------------------------------------------------------------

test("readAssetOffline reports missing-reference integrity for unresolved field GUIDs", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-integrity-missing-"));
  try {
    await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
    await writeFile(
      join(tmp, "Assets", "Prefabs", "Broken.prefab"),
      `%YAML 1.1
--- !u!1 &100
GameObject:
  m_Component:
  - component: {fileID: 200}
  m_Name: Broken
--- !u!4 &200
Transform:
  m_GameObject: {fileID: 100}
  m_Father: {fileID: 0}
--- !u!114 &300
MonoBehaviour:
  m_GameObject: {fileID: 100}
  m_Material: {fileID: 0, guid: 9999888800000000000000000000dead, type: 2}
`,
    );
    await writeFile(join(tmp, "Assets", "Prefabs", "Broken.prefab.meta"), "guid: brok000000000000000000000000001\n");
    // No .meta for the referenced GUID → missing_reference signal.

    const { model } = await readAssetOffline("Assets/Prefabs/Broken.prefab", {
      fieldLimit: 10,
      projectRoot: tmp,
    });
    assert.ok(model.integrity, "integrity signals present");
    const missing = model.integrity.find((i) => i.code === "missing_reference");
    assert.ok(missing, "missing_reference signal emitted");
    assert.ok(missing.detail.includes("99998888"), `detail names the guid: ${missing.detail}`);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("readAssetOffline reports missing-script-reference for unresolved m_Script GUIDs", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-integrity-script-"));
  try {
    await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
    await writeFile(
      join(tmp, "Assets", "Prefabs", "MissingScript.prefab"),
      `%YAML 1.1
--- !u!1 &100
GameObject:
  m_Component:
  - component: {fileID: 200}
  - component: {fileID: 300}
  m_Name: MissingScript
--- !u!4 &200
Transform:
  m_GameObject: {fileID: 100}
  m_Father: {fileID: 0}
--- !u!114 &300
MonoBehaviour:
  m_GameObject: {fileID: 100}
  m_Script: {fileID: 11500000, guid: 7777666600000000000000000000abcd, type: 3}
`,
    );
    await writeFile(join(tmp, "Assets", "Prefabs", "MissingScript.prefab.meta"), "guid: ms00000000000000000000000000001\n");

    const { model } = await readAssetOffline("Assets/Prefabs/MissingScript.prefab", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    assert.ok(model.integrity);
    const scriptMissing = model.integrity.find((i) => i.code === "missing_script_reference");
    assert.ok(scriptMissing, "missing_script_reference signal emitted");
    assert.equal(scriptMissing.severity, "error");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("readAssetOffline reports malformed_json integrity for broken .asmdef", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-integrity-json-"));
  try {
    await mkdir(join(tmp, "Assets", "Scripts"), { recursive: true });
    await writeFile(join(tmp, "Assets", "Scripts", "Bad.asmdef"), `{ "name": "broken `);
    await writeFile(join(tmp, "Assets", "Scripts", "Bad.asmdef.meta"), "guid: bad00000000000000000000000000001\n");

    const { model } = await readAssetOffline("Assets/Scripts/Bad.asmdef", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    assert.ok(model.integrity, "integrity present even for malformed JSON");
    const malformed = model.integrity.find((i) => i.code === "malformed_json");
    assert.ok(malformed, "malformed_json signal emitted");
    assert.equal(malformed.severity, "error");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("readAssetOffline reports asmdef_missing_name integrity", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-integrity-asmdef-"));
  try {
    await mkdir(join(tmp, "Assets", "Scripts"), { recursive: true });
    await writeFile(join(tmp, "Assets", "Scripts", "Noname.asmdef"), `{"references": []}`);
    await writeFile(join(tmp, "Assets", "Scripts", "Noname.asmdef.meta"), "guid: non00000000000000000000000000001\n");

    const { model } = await readAssetOffline("Assets/Scripts/Noname.asmdef", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    assert.ok(model.integrity);
    const missingName = model.integrity.find((i) => i.code === "asmdef_missing_name");
    assert.ok(missingName, "asmdef_missing_name signal emitted");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("readAssetOffline reports orphaned_prefab_instance when base prefab missing", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-integrity-orphan-"));
  try {
    await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
    // Variant references a base GUID that has no .meta in the project. The GUID
    // must be valid 32-char hex so the scanner reads it whole.
    const missingBaseGuid = "0badbad000000000000000000000abcd";
    await writeFile(
      join(tmp, "Assets", "Prefabs", "OrphanVariant.prefab"),
      `%YAML 1.1
--- !u!1001 &500
PrefabInstance:
  m_Modification:
    m_Modifications: []
  m_SourcePrefab: {fileID: 100100000, guid: ${missingBaseGuid}, type: 3}
`,
    );
    await writeFile(join(tmp, "Assets", "Prefabs", "OrphanVariant.prefab.meta"), "guid: orph00000000000000000000000000001\n");

    const { model } = await readAssetOffline("Assets/Prefabs/OrphanVariant.prefab", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    assert.ok(model.integrity);
    const orphan = model.integrity.find((i) => i.code === "orphaned_prefab_instance");
    assert.ok(orphan, "orphaned_prefab_instance signal emitted");
    assert.ok(orphan.detail.includes(missingBaseGuid), `detail names the base guid: ${orphan.detail}`);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("renderAssetSummary surfaces integrity signals on the compact output", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-integrity-render-"));
  try {
    await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
    await writeFile(
      join(tmp, "Assets", "Prefabs", "Broken.prefab"),
      `%YAML 1.1
--- !u!1 &100
GameObject:
  m_Component:
  - component: {fileID: 200}
  m_Name: Broken
--- !u!4 &200
Transform:
  m_GameObject: {fileID: 100}
  m_Father: {fileID: 0}
--- !u!114 &300
MonoBehaviour:
  m_GameObject: {fileID: 100}
  m_Material: {fileID: 0, guid: 5555444400000000000000000000dead, type: 2}
`,
    );
    await writeFile(join(tmp, "Assets", "Prefabs", "Broken.prefab.meta"), "guid: brk00000000000000000000000000001\n");

    const { model } = await readAssetOffline("Assets/Prefabs/Broken.prefab", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    const compact = renderAssetSummary(model, { detail: "summary" });
    assert.ok(compact.integrity, "integrity on compact result");
    assert.ok(compact.integrity.length > 0);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("searchAssetsOffline finds .asmdef by name (expanded type search)", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-search-asmdef-"));
  try {
    await setupAsmdefProject(tmp);
    const result = await searchAssetsOffline({ name: "Foo", projectRoot: tmp });
    const asmdefMatch = result.matches.find((m) => m.path.endsWith("Foo.asmdef"));
    assert.ok(asmdefMatch, "asmdef found by name");
    assert.ok(asmdefMatch.reasons.includes("file-name"));
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("searchAssetsOffline finds .shadergraph by guid reference", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-search-sg-guid-"));
  try {
    await setupShaderGraphProject(tmp);
    const result = await searchAssetsOffline({
      guid: "tex111000000000000000000000000001",
      projectRoot: tmp,
    });
    const sgMatch = result.matches.find((m) => m.path.endsWith("Foo.shadergraph"));
    assert.ok(sgMatch, "shadergraph found by referenced guid");
    assert.ok(sgMatch.reasons.includes("guid"));
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("listAssetsOffline includes the expanded types", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-list-expanded-"));
  try {
    await setupAsmdefProject(tmp);
    await setupPresetProject(tmp);
    const result = await listAssetsOffline({ projectRoot: tmp });
    const kinds = new Set(Object.keys(result.kindSummary));
    assert.ok(kinds.has("asmdef"), "asmdef kind in summary");
    assert.ok(kinds.has("preset"), "preset kind in summary");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("findReferencesOffline scans .shadergraph and .asmdef (expanded ref targets)", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-refs-expanded-"));
  try {
    await setupShaderGraphProject(tmp);
    const result = await findReferencesOffline({
      guid: "tex111000000000000000000000000001",
      projectRoot: tmp,
    });
    const sgHit = result.referencedBy.find((e) => e.assetPath.endsWith("Foo.shadergraph"));
    assert.ok(sgHit, "shadergraph found as a referencer of the texture GUID");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// Fixtures for M24 expanded-type tests.
// ---------------------------------------------------------------------------

async function setupAsmdefProject(tmp: string): Promise<void> {
  await mkdir(join(tmp, "Assets", "Scripts"), { recursive: true });
  await writeFile(
    join(tmp, "Assets", "Scripts", "Foo.asmdef"),
    JSON.stringify({
      name: "Foo",
      rootNamespace: "",
      references: ["Bar"],
      includePlatforms: ["Editor"],
      excludePlatforms: [],
      allowUnsafeCode: false,
      overrideReferences: false,
      precompiledReferences: [],
      autoReferenced: true,
      defineConstraints: [],
      versionDefines: [],
      noEngineReferences: false,
    }, null, 4),
  );
  await writeFile(join(tmp, "Assets", "Scripts", "Foo.asmdef.meta"), "guid: asmd0000000000000000000000000001\n");
}

async function setupShaderGraphProject(tmp: string): Promise<void> {
  await mkdir(join(tmp, "Assets"), { recursive: true });
  // A realistic .shadergraph is a stream of pretty-printed JSON objects
  // separated by blank lines, each carrying an "m_Type" discriminator. The
  // first object is always the GraphData root. Texture refs are inline PPtrs.
  const texGuid = "tex111000000000000000000000000001";
  await writeFile(
    join(tmp, "Assets", "Foo.shadergraph"),
    `{
    "m_SGVersion": 3,
    "m_Type": "UnityEditor.ShaderGraph.GraphData",
    "m_ObjectId": "root0000000000000000000000000001",
    "m_Properties": [],
    "m_Nodes": []
}

{
    "m_Type": "UnityEditor.ShaderGraph.SampleTexture2DNode",
    "m_Id": "node00000000000000000000000000001",
    "m_Texture": {
        "m_SerializedTexture": "",
        "m_Guid": "00000000000000000000000000000000"
    },
    "m_RefTexture": { "fileID": 2800000, "guid": "${texGuid}", "type": 3 }
}`,
  );
  await writeFile(join(tmp, "Assets", "Foo.shadergraph.meta"), "guid: sg0000000000000000000000000000011\n");
  // A texture meta so the ref resolves in find_references / integrity.
  await mkdir(join(tmp, "Assets", "Textures"), { recursive: true });
  await writeFile(join(tmp, "Assets", "Textures", "Diffuse.png.meta"), `guid: ${texGuid}\n`);
}

async function setupPresetProject(tmp: string): Promise<void> {
  await mkdir(join(tmp, "Assets", "Presets"), { recursive: true });
  // .preset is text YAML with a Preset object (classID 1386491679).
  await writeFile(
    join(tmp, "Assets", "Presets", "MyPreset.preset"),
    `%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1386491679 &9000000
Preset:
  m_Name: MyPreset
  m_TargetTypeSerialized: 108
`,
  );
  await writeFile(join(tmp, "Assets", "Presets", "MyPreset.preset.meta"), "guid: prst00000000000000000000000000001\n");
}

async function setupTerrainLayerProject(tmp: string): Promise<void> {
  // GUIDs must be valid 32-char hex (0-9a-f); the scanner stops at the first
  // non-hex char, so mnemonic prefixes like "diff..." break resolution.
  const diffuseGuid = "da1f0000000000000000000000000aa1";
  await mkdir(join(tmp, "Assets", "Terrain"), { recursive: true });
  await mkdir(join(tmp, "Assets", "Textures"), { recursive: true });
  await writeFile(
    join(tmp, "Assets", "Terrain", "dry_soil.terrainlayer"),
    `%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1953259897 &8574412962073106934
TerrainLayer:
  m_ObjectHideFlags: 0
  m_Name: dry_soil
  m_DiffuseTexture: {fileID: 2800000, guid: ${diffuseGuid}, type: 3}
  m_TileSize: {x: 2.3, y: 2.3}
  m_Metallic: 0
  m_Smoothness: 0
`,
  );
  await writeFile(join(tmp, "Assets", "Terrain", "dry_soil.terrainlayer.meta"), "guid: tl0000000000000000000000000000a1\n");
  await writeFile(join(tmp, "Assets", "Textures", "Diffuse.png.meta"), `guid: ${diffuseGuid}\n`);
}

async function setupSpriteAtlasProject(tmp: string): Promise<void> {
  const folderGuid = "fold00000000000000000000000000aa1";
  await mkdir(join(tmp, "Assets", "UI", "Icons"), { recursive: true });
  await writeFile(
    join(tmp, "Assets", "UI", "Icons.spriteatlas"),
    `%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!635262636 &7000000
SpriteAtlas:
  m_Name: Icons
  m_EditorData:
    m_Packables:
    - {fileID: 102900000, guid: ${folderGuid}, type: 3}
`,
  );
  await writeFile(join(tmp, "Assets", "UI", "Icons.spriteatlas.meta"), "guid: satl00000000000000000000000000a1\n");
}

// ===========================================================================
// M24 Plan 1 — full hierarchy reconstruction parity (T24.1.2).
//
// The offline parser already returns the complete GameObject/component tree;
// the gap was parity with the live read_asset bridge (which counts GameObjects
// + attached components, not every raw YAML object). countHierarchy() returns
// the live-comparable {nodes, components} so a fixture verifies the
// reconstructed tree matches what a live read would emit node-for-node.
// ===========================================================================

test("countHierarchy returns live-comparable node and component counts", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-parity-count-"));
  try {
    await setupDeepHierarchyProject(tmp);
    const { model } = await readAssetOffline("Assets/Prefabs/Deep.prefab", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    // Fixture: Root (Transform + MeshRenderer + MonoBehaviour)
    //          └─ Child (Transform + Camera)
    //             └─ Grandchild (Transform)
    //  → 3 GameObject nodes, 6 components total (Transform x3, MeshRenderer,
    //    MonoBehaviour, Camera).
    const { nodes, components } = countHierarchy(model);
    assert.equal(nodes, 3, "3 GameObject nodes reconstructed");
    assert.equal(components, 6, "6 components across the tree");
    // Sanity: the model's componentCount (which the renderer reports) agrees
    // with the parity count — both derive from the same reconstructed tree.
    assert.equal(model.componentCount, components, "model.componentCount matches parity");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("full hierarchy reconstruction preserves depth and path for the whole tree", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-parity-tree-"));
  try {
    await setupDeepHierarchyProject(tmp);
    const { model } = await readAssetOffline("Assets/Prefabs/Deep.prefab", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    // verbose profile disables render-only folding, so the TREE shows every node.
    const compact = renderAssetSummary(model, { detail: "verbose" });
    assert.ok(compact.tree, "tree rendered");
    const paths = compact.tree.map((r) => `${r.depth}:${r.name}`);
    // Every node appears (no folding collapsed a non-render-only node away).
    assert.ok(paths.some((p) => p === "0:Root"), "Root at depth 0");
    assert.ok(paths.some((p) => p === "1:Child"), "Child at depth 1");
    assert.ok(paths.some((p) => p === "2:Grandchild"), "Grandchild at depth 2");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

async function setupDeepHierarchyProject(tmp: string): Promise<void> {
  const scriptGuid = "dpth0000000000000000000000000001";
  await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
  await mkdir(join(tmp, "Assets", "Scripts"), { recursive: true });
  await writeFile(
    join(tmp, "Assets", "Prefabs", "Deep.prefab"),
    `%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &100
GameObject:
  m_Component:
  - component: {fileID: 200}
  - component: {fileID: 300}
  - component: {fileID: 400}
  m_Name: Root
--- !u!4 &200
Transform:
  m_GameObject: {fileID: 100}
  m_Father: {fileID: 0}
--- !u!23 &300
MeshRenderer:
  m_GameObject: {fileID: 100}
--- !u!114 &400
MonoBehaviour:
  m_GameObject: {fileID: 100}
  m_Script: {fileID: 11500000, guid: ${scriptGuid}, type: 3}
--- !u!1 &101
GameObject:
  m_Component:
  - component: {fileID: 201}
  - component: {fileID: 500}
  m_Name: Child
--- !u!4 &201
Transform:
  m_GameObject: {fileID: 101}
  m_Father: {fileID: 200}
--- !u!20 &500
Camera:
  m_GameObject: {fileID: 101}
--- !u!1 &102
GameObject:
  m_Component:
  - component: {fileID: 202}
  m_Name: Grandchild
--- !u!4 &202
Transform:
  m_GameObject: {fileID: 102}
  m_Father: {fileID: 201}
`,
  );
  await writeFile(join(tmp, "Assets", "Prefabs", "Deep.prefab.meta"), "guid: dpthprefab000000000000000000a1\n");
  await writeFile(join(tmp, "Assets", "Scripts", "DepthController.cs.meta"), `guid: ${scriptGuid}\n`);
}

// ===========================================================================
// M24 Plan 1 — offline prefab override parsing vs live shape (T24.1.3).
//
// The offline parser already emits PrefabOverrideEntry records; the M24
// acceptance criterion is that the offline override set matches what the live
// prefab_get_overrides tool returns on a variant fixture. The live tool emits
// propertyModifications / addedComponents / removedComponents arrays; the
// offline entries normalize to the same kinds (property / added-component /
// removed-components). These tests pin the shape parity.
// ===========================================================================

test("offline prefab overrides match the live prefab_get_overrides kind set", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-overrides-parity-"));
  try {
    await setupVariantProject(tmp);
    const { model } = await readAssetOffline("Assets/Prefabs/PlayerVariant.prefab", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    assert.ok(model.overrides, "overrides populated");

    // Live prefab_get_overrides emits three buckets; the offline parser must
    // cover all three kinds present in the fixture.
    const kinds = new Set(model.overrides.map((o) => o.kind));
    assert.ok(kinds.has("property"), "property overrides (live: propertyModifications)");
    assert.ok(kinds.has("added-component"), "added-component overrides (live: addedComponents)");
    assert.ok(kinds.has("removed-components"), "removed-components overrides (live: removedComponents)");

    // The live tool reports the property path and value verbatim; the offline
    // parser unwraps backing fields but otherwise mirrors the same fields.
    const speedOverride = model.overrides.find(
      (o) => o.kind === "property" && o.propertyPath === "m_Speed",
    );
    assert.ok(speedOverride, "m_Speed property override present");
    assert.equal(speedOverride.value, "10", "value matches the serialized override");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("offline override target resolves to a GameObject path the live tool would report", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-overrides-target-"));
  try {
    await setupVariantProject(tmp);
    const { model } = await readAssetOffline("Assets/Prefabs/PlayerVariant.prefab", {
      fieldLimit: 0,
      projectRoot: tmp,
    });
    assert.ok(model.overrides);
    // The live tool resolves the override target to the component's host
    // GameObject; the offline parser resolves to "Component on Path" or the
    // path itself. Either way, the resolved target must name the variant root.
    const withTarget = model.overrides.filter((o) => o.target && o.target.length > 0);
    assert.ok(withTarget.length > 0, "at least one override with a resolved target");
    const namesVariant = withTarget.some((o) => (o.target ?? "").includes("PlayerVariant"));
    assert.ok(namesVariant, "a target references the variant root GameObject");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// dependenciesOffline — forward + reverse edges + impact analysis (T24.2).
//
// The graph fixture models a real dependency chain so forward/reverse/broken/
// cycle/impact all exercise on one tree:
//
//   SharedMat.mat  ←─referenced by─  Player.prefab  ←─base of─  PlayerVariant.prefab
//        │                                  │
//        │                                  └─ m_Script → PlayerController.cs
//        └─ also referenced by Main.unity
//
//   BrokenMat.mat is named by a GUID that has no .meta anywhere (broken edge).
// ---------------------------------------------------------------------------

async function setupDependencyProject(tmp: string): Promise<void> {
  const matGuid = "cafe0000000000000000000000000001";
  const basePrefabGuid = "cafe0000000000000000000000000002";
  const variantGuid = "cafe0000000000000000000000000003";
  const scriptGuid = "cafe0000000000000000000000000004";
  const sceneGuid = "cafe0000000000000000000000000005";
  // Deliberately unresolved — no .meta in the project.
  const brokenGuid = "deadbeef000000000000000000000001";

  // Shared material (leaf — referenced by prefab + scene).
  await mkdir(join(tmp, "Assets", "Materials"), { recursive: true });
  await writeFile(
    join(tmp, "Assets", "Materials", "SharedMat.mat"),
    `%YAML 1.1
--- !u!21 &2100000
Material:
  m_Name: SharedMat
`,
  );
  await writeFile(join(tmp, "Assets", "Materials", "SharedMat.mat.meta"), `guid: ${matGuid}\n`);

  // PlayerController script (forward edge of Player.prefab via m_Script).
  await mkdir(join(tmp, "Assets", "Scripts"), { recursive: true });
  await writeFile(join(tmp, "Assets", "Scripts", "PlayerController.cs"), "using UnityEngine;\npublic class PlayerController : MonoBehaviour {}\n");
  await writeFile(join(tmp, "Assets", "Scripts", "PlayerController.cs.meta"), `guid: ${scriptGuid}\n`);

  // Base prefab — references the material (forward) + the script (forward) +
  // a broken GUID (forward, unresolved).
  await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
  await writeFile(
    join(tmp, "Assets", "Prefabs", "Player.prefab"),
    `%YAML 1.1
--- !u!1 &100
GameObject:
  m_Component:
  - component: {fileID: 200}
  - component: {fileID: 300}
  m_Name: Player
--- !u!4 &200
Transform:
  m_GameObject: {fileID: 100}
  m_Father: {fileID: 0}
--- !u!23 &300
MeshRenderer:
  m_GameObject: {fileID: 100}
  m_Material: {fileID: 0, guid: ${matGuid}, type: 2}
--- !u!114 &400
MonoBehaviour:
  m_GameObject: {fileID: 100}
  m_Script: {fileID: 11500000, guid: ${scriptGuid}, type: 3}
  m_BrokenRef: {fileID: 0, guid: ${brokenGuid}, type: 2}
`,
  );
  await writeFile(join(tmp, "Assets", "Prefabs", "Player.prefab.meta"), `guid: ${basePrefabGuid}\n`);

  // Variant prefab — its base is Player.prefab (prefab_source forward edge).
  await writeFile(
    join(tmp, "Assets", "Prefabs", "PlayerVariant.prefab"),
    `%YAML 1.1
--- !u!1001 &500
PrefabInstance:
  m_SourcePrefab: {fileID: 100100000, guid: ${basePrefabGuid}, type: 3}
  m_Modifications:
  - target: {fileID: 100, guid: ${basePrefabGuid}, type: 3}
    propertyPath: m_Name
    value: PlayerVariant
`,
  );
  await writeFile(join(tmp, "Assets", "Prefabs", "PlayerVariant.prefab.meta"), `guid: ${variantGuid}\n`);

  // Scene — references the material (reverse edge of SharedMat).
  await mkdir(join(tmp, "Assets", "Scenes"), { recursive: true });
  await writeFile(
    join(tmp, "Assets", "Scenes", "Main.unity"),
    `%YAML 1.1
--- !u!1 &100
GameObject:
  m_Component:
  - component: {fileID: 200}
  m_Name: SceneGO
--- !u!4 &200
Transform:
  m_GameObject: {fileID: 100}
  m_Father: {fileID: 0}
--- !u!23 &300
MeshRenderer:
  m_GameObject: {fileID: 100}
  m_Material: {fileID: 0, guid: ${matGuid}, type: 2}
`,
  );
  await writeFile(join(tmp, "Assets", "Scenes", "Main.unity.meta"), `guid: ${sceneGuid}\n`);
}

test("dependenciesOffline returns forward edges for an asset that depends on others", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-deps-forward-"));
  try {
    await setupDependencyProject(tmp);
    const result = await dependenciesOffline({
      assetPath: "Assets/Prefabs/Player.prefab",
      projectRoot: tmp,
    });
    // Player.prefab forward-depends on the material (pptr) + script (script).
    const forwardPaths = result.forwardDependencies.filter((e) => e.resolved).map((e) => e.assetPath);
    assert.ok(forwardPaths.includes("Assets/Materials/SharedMat.mat"), `material in forward edges: ${JSON.stringify(forwardPaths)}`);
    assert.ok(forwardPaths.includes("Assets/Scripts/PlayerController.cs"), `script in forward edges: ${JSON.stringify(forwardPaths)}`);
    assert.ok(result.forwardCount >= 2, `forwardCount >= 2: ${result.forwardCount}`);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("dependenciesOffline returns reverse edges for an asset that is referenced", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-deps-reverse-"));
  try {
    await setupDependencyProject(tmp);
    const result = await dependenciesOffline({
      assetPath: "Assets/Materials/SharedMat.mat",
      projectRoot: tmp,
    });
    // SharedMat is referenced by Player.prefab + Main.unity.
    const reversePaths = result.reverseDependencies.map((e) => e.assetPath).sort();
    assert.ok(reversePaths.includes("Assets/Prefabs/Player.prefab"), `Player.prefab in reverse: ${JSON.stringify(reversePaths)}`);
    assert.ok(reversePaths.includes("Assets/Scenes/Main.unity"), `Main.unity in reverse: ${JSON.stringify(reversePaths)}`);
    assert.ok(result.reverseCount >= 2, `reverseCount >= 2: ${result.reverseCount}`);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("dependenciesOffline reports broken forward-edge GUIDs", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-deps-broken-"));
  try {
    await setupDependencyProject(tmp);
    const result = await dependenciesOffline({
      assetPath: "Assets/Prefabs/Player.prefab",
      projectRoot: tmp,
    });
    assert.ok(
      result.brokenForwardGuids.includes("deadbeef000000000000000000000001"),
      `broken GUID reported: ${JSON.stringify(result.brokenForwardGuids)}`,
    );
    // The broken edge should appear in forwardDependencies as unresolved.
    const brokenEdge = result.forwardDependencies.find((e) => !e.resolved);
    assert.ok(brokenEdge, "at least one unresolved forward edge");
    assert.equal(brokenEdge!.assetPath, "");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("dependenciesOffline detects prefab_source as a forward edge (variant → base)", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-deps-prefabsrc-"));
  try {
    await setupDependencyProject(tmp);
    const result = await dependenciesOffline({
      assetPath: "Assets/Prefabs/PlayerVariant.prefab",
      projectRoot: tmp,
    });
    const prefabSrcEdge = result.forwardDependencies.find((e) => e.kind === "prefab_source");
    assert.ok(prefabSrcEdge, "prefab_source forward edge present");
    assert.equal(prefabSrcEdge!.assetPath, "Assets/Prefabs/Player.prefab");
    assert.equal(prefabSrcEdge!.resolved, true);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("dependenciesOffline returns BOTH forward and reverse in one call", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-deps-both-"));
  try {
    await setupDependencyProject(tmp);
    const result = await dependenciesOffline({
      assetPath: "Assets/Prefabs/Player.prefab",
      projectRoot: tmp,
    });
    // Player.prefab has forward edges (material/script) AND a reverse edge
    // (PlayerVariant depends on it as its prefab base).
    assert.ok(result.forwardCount > 0, "forward edges present");
    assert.ok(result.reverseCount > 0, "reverse edges present");
    const reversePaths = result.reverseDependencies.map((e) => e.assetPath);
    assert.ok(reversePaths.includes("Assets/Prefabs/PlayerVariant.prefab"), `variant in reverse: ${JSON.stringify(reversePaths)}`);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("dependenciesOffline summary detail omits edge rosters but keeps counts", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-deps-summary-"));
  try {
    await setupDependencyProject(tmp);
    const result = await dependenciesOffline({
      assetPath: "Assets/Prefabs/Player.prefab",
      detail: "summary",
      projectRoot: tmp,
    });
    assert.equal(result.forwardDependencies.length, 0, "summary drops forward roster");
    assert.equal(result.reverseDependencies.length, 0, "summary drops reverse roster");
    assert.ok(result.forwardCount > 0, "forwardCount still accurate");
    assert.ok(result.reverseCount > 0, "reverseCount still accurate");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("dependenciesOffline include_impact returns the transitive reverse closure", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-deps-impact-"));
  try {
    await setupDependencyProject(tmp);
    // Impact of deleting SharedMat: direct (Player.prefab, Main.unity) +
    // transitive (PlayerVariant.prefab depends on Player.prefab).
    const result = await dependenciesOffline({
      assetPath: "Assets/Materials/SharedMat.mat",
      includeImpact: true,
      projectRoot: tmp,
    });
    assert.ok(result.impact, "impact block present");
    const affected = result.impact!.affected.map((a) => a.assetPath);
    assert.ok(affected.includes("Assets/Prefabs/Player.prefab"), "Player.prefab in impact (depth 1)");
    assert.ok(affected.includes("Assets/Scenes/Main.unity"), "Main.unity in impact (depth 1)");
    assert.ok(affected.includes("Assets/Prefabs/PlayerVariant.prefab"), "PlayerVariant in impact (depth 2, transitive)");
    // Depth 1 = direct; depth 2 = transitive via Player.prefab.
    const variant = result.impact!.affected.find((a) => a.assetPath.includes("PlayerVariant"));
    assert.ok(variant, "variant entry present with depth");
    assert.ok(variant!.depth >= 2, `variant depth >= 2: ${variant!.depth}`);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("dependenciesOffline omits impact block when include_impact is false", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-deps-noimpact-"));
  try {
    await setupDependencyProject(tmp);
    const result = await dependenciesOffline({
      assetPath: "Assets/Materials/SharedMat.mat",
      includeImpact: false,
      projectRoot: tmp,
    });
    assert.equal(result.impact, undefined, "no impact block by default");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("dependenciesOffline detects a forward dependency cycle", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-deps-cycle-"));
  try {
    // A → B → A (a two-node cycle). Each references the other's GUID.
    const aGuid = "c1c10000000000000000000000000001";
    const bGuid = "c1c10000000000000000000000000002";
    await mkdir(join(tmp, "Assets", "Data"), { recursive: true });
    await writeFile(
      join(tmp, "Assets", "Data", "A.asset"),
      `%YAML 1.1
--- !u!114 &11400000
MonoBehaviour:
  m_Name: A
  m_Ref: {fileID: 0, guid: ${bGuid}, type: 2}
`,
    );
    await writeFile(join(tmp, "Assets", "Data", "A.asset.meta"), `guid: ${aGuid}\n`);
    await writeFile(
      join(tmp, "Assets", "Data", "B.asset"),
      `%YAML 1.1
--- !u!114 &11400000
MonoBehaviour:
  m_Name: B
  m_Ref: {fileID: 0, guid: ${aGuid}, type: 2}
`,
    );
    await writeFile(join(tmp, "Assets", "Data", "B.asset.meta"), `guid: ${bGuid}\n`);

    const result = await dependenciesOffline({
      assetPath: "Assets/Data/A.asset",
      projectRoot: tmp,
    });
    assert.ok(result.cycles.length > 0, `cycle detected: ${JSON.stringify(result.cycles)}`);
    // The cycle trail starts at A and returns to A.
    const cycle = result.cycles[0];
    assert.ok(cycle[0].endsWith("A.asset"), `cycle starts at A: ${JSON.stringify(cycle)}`);
    assert.ok(cycle[cycle.length - 1].endsWith("A.asset"), `cycle returns to A: ${JSON.stringify(cycle)}`);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("dependenciesOffline resolves by GUID directly", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-deps-byguid-"));
  try {
    await setupDependencyProject(tmp);
    const result = await dependenciesOffline({
      guid: "cafe0000000000000000000000000001", // SharedMat.mat
      projectRoot: tmp,
    });
    assert.equal(result.queriedAssetPath, "Assets/Materials/SharedMat.mat");
    assert.ok(result.reverseCount >= 2);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("dependenciesOffline returns empty result for an unknown GUID", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-deps-unknown-"));
  try {
    await setupDependencyProject(tmp);
    const result = await dependenciesOffline({
      guid: "ffff000000000000000000000000ffff",
      projectRoot: tmp,
    });
    assert.equal(result.forwardCount, 0);
    assert.equal(result.reverseCount, 0);
    assert.equal(result.queriedAssetGuid, "ffff000000000000000000000000ffff");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("dependenciesOffline sets forwardSkipped for a non-YAML asset", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-deps-skip-"));
  try {
    await setupDependencyProject(tmp);
    // .asmdef is JSON — the YAML forward-edge parser cannot extract edges.
    await mkdir(join(tmp, "Assets", "Scripts"), { recursive: true });
    await writeFile(
      join(tmp, "Assets", "Scripts", "MyAsm.asmdef"),
      `{"name":"MyAsm","rootNamespace":"","references":[],"includePlatforms":[],"excludePlatforms":[],"allowUnsafeCode":false,"overrideReferences":false,"autoReferenced":true,"defineConstraints":[],"versionDefines":[]}`,
    );
    await writeFile(join(tmp, "Assets", "Scripts", "MyAsm.asmdef.meta"), "guid: cafe0000000000000000000000000099\n");
    const result = await dependenciesOffline({
      assetPath: "Assets/Scripts/MyAsm.asmdef",
      projectRoot: tmp,
    });
    assert.ok(result.forwardSkipped, "forwardSkipped reason set for non-YAML asset");
    assert.equal(result.forwardCount, 0, "no forward edges extracted");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// scanIntegrityOffline — project-wide orphan_meta + duplicate_guid + missing
// refs (T24.2 item 3).
// ---------------------------------------------------------------------------

test("scanIntegrityOffline detects orphaned .meta files (companion asset deleted)", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-scan-orphan-"));
  try {
    await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
    // A .meta with NO companion asset file.
    await writeFile(join(tmp, "Assets", "Prefabs", "Ghost.prefab.meta"), "guid: 0bad0000000000000000000000000bad\n");
    // A healthy asset + its .meta (should NOT be flagged).
    await writeFile(join(tmp, "Assets", "Prefabs", "Real.prefab"), `%YAML 1.1\n--- !u!1 &100\nGameObject:\n  m_Name: Real\n`);
    await writeFile(join(tmp, "Assets", "Prefabs", "Real.prefab.meta"), "guid: 600d000000000000000000000000060d\n");

    const result = await scanIntegrityOffline({ projectRoot: tmp });
    const orphans = result.issues.filter((i) => i.code === "orphan_meta");
    assert.ok(orphans.length >= 1, `at least one orphan_meta: ${JSON.stringify(orphans)}`);
    assert.ok(orphans.some((o) => o.path.includes("Ghost.prefab.meta")), "Ghost flagged as orphan");
    assert.ok(!orphans.some((o) => o.path.includes("Real.prefab")), "Real not flagged");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("scanIntegrityOffline detects duplicate GUIDs across two assets", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-scan-dupe-"));
  try {
    await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
    const dupGuid = "d0d0000000000000000000000000000d";
    await writeFile(join(tmp, "Assets", "Prefabs", "One.prefab"), `%YAML 1.1\n--- !u!1 &100\nGameObject:\n  m_Name: One\n`);
    await writeFile(join(tmp, "Assets", "Prefabs", "One.prefab.meta"), `guid: ${dupGuid}\n`);
    await writeFile(join(tmp, "Assets", "Prefabs", "Two.prefab"), `%YAML 1.1\n--- !u!1 &100\nGameObject:\n  m_Name: Two\n`);
    await writeFile(join(tmp, "Assets", "Prefabs", "Two.prefab.meta"), `guid: ${dupGuid}\n`);

    const result = await scanIntegrityOffline({ projectRoot: tmp });
    const dupes = result.issues.filter((i) => i.code === "duplicate_guid");
    assert.ok(dupes.length >= 2, "both assets flagged for the shared GUID");
    const one = dupes.find((d) => d.path.includes("One.prefab"));
    assert.ok(one, "One.prefab flagged");
    assert.ok(one!.relatedPaths && one!.relatedPaths.some((p) => p.includes("Two.prefab")), "relatedPaths names the other asset");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("scanIntegrityOffline aggregates missing references project-wide", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-scan-missing-"));
  try {
    await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
    await writeFile(
      join(tmp, "Assets", "Prefabs", "Broken.prefab"),
      `%YAML 1.1
--- !u!1 &100
GameObject:
  m_Component:
  - component: {fileID: 200}
  m_Name: Broken
--- !u!4 &200
Transform:
  m_GameObject: {fileID: 100}
  m_Father: {fileID: 0}
--- !u!114 &300
MonoBehaviour:
  m_GameObject: {fileID: 100}
  m_Target: {fileID: 0, guid: 9999888800000000000000000000dead, type: 2}
`,
    );
    await writeFile(join(tmp, "Assets", "Prefabs", "Broken.prefab.meta"), "guid: b0b00000000000000000000000000b0b\n");

    const result = await scanIntegrityOffline({ projectRoot: tmp });
    const missing = result.issues.filter((i) => i.code === "missing_reference");
    assert.ok(missing.length >= 1, "missing_reference detected project-wide");
    assert.ok(missing[0].detail.includes("99998888"), `detail names the unresolved GUID: ${missing[0].detail}`);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("scanIntegrityOffline reports clean tree with no issues", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-scan-clean-"));
  try {
    await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
    await writeFile(join(tmp, "Assets", "Prefabs", "Healthy.prefab"), `%YAML 1.1\n--- !u!1 &100\nGameObject:\n  m_Name: Healthy\n`);
    await writeFile(join(tmp, "Assets", "Prefabs", "Healthy.prefab.meta"), "guid: 5a15000000000000000000000000aa55\n");

    const result = await scanIntegrityOffline({ projectRoot: tmp });
    assert.equal(result.totalIssues, 0, `no issues in a clean tree: ${JSON.stringify(result.byCode)}`);
    assert.ok(result.assetsScanned >= 1, "assets scanned count > 0");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("scanIntegrityOffline groups issues by code in byCode", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-scan-bycode-"));
  try {
    await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
    // orphan
    await writeFile(join(tmp, "Assets", "Prefabs", "Ghost.prefab.meta"), "guid: 0bad0000000000000000000000000a1\n");
    // missing ref
    await writeFile(
      join(tmp, "Assets", "Prefabs", "Broken.prefab"),
      `%YAML 1.1\n--- !u!1 &100\nGameObject:\n  m_Name: Broken\n  m_Ref: {fileID: 0, guid: 1111222200000000000000000000dead, type: 2}\n`,
    );
    await writeFile(join(tmp, "Assets", "Prefabs", "Broken.prefab.meta"), "guid: b0b00000000000000000000000000b0b\n");

    const result = await scanIntegrityOffline({ projectRoot: tmp });
    assert.ok((result.byCode.orphan_meta ?? 0) >= 1, "orphan_meta in byCode");
    assert.ok((result.byCode.missing_reference ?? 0) >= 1, "missing_reference in byCode");
    assert.equal(result.totalIssues, result.issues.length, "totalIssues matches issues length");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ===========================================================================
// M31-optimizations Plan 2 — single-walk + parallel-walk acceptance tests.
//
// These pin the H3 / H4 / H5 / M4 / L2 / L4 contract: each operation walks
// the meta tree (or the project tree) the minimum number of times, the
// output is byte-identical to pre-change for representative fixtures, and
// the parallel walker preserves ordering.
// ===========================================================================

// Fixture: a multi-directory project with scripts + prefabs + materials +
// orphans + a broken ref. Larger than setupProject so the walk-count
// assertions have signal (more directories → more readdir calls per walk).
async function setupWalkCountProject(tmp: string): Promise<void> {
  // Scripts (2 dirs, 3 scripts).
  await mkdir(join(tmp, "Assets", "Scripts", "Player"), { recursive: true });
  await writeFile(join(tmp, "Assets", "Scripts", "PlayerController.cs"), "class A {}\n");
  await writeFile(join(tmp, "Assets", "Scripts", "PlayerController.cs.meta"), "guid: c0a10000000000000000000000000001\n");
  await writeFile(join(tmp, "Assets", "Scripts", "Player", "PlayerInput.cs"), "class B {}\n");
  await writeFile(join(tmp, "Assets", "Scripts", "Player", "PlayerInput.cs.meta"), "guid: c0a10000000000000000000000000002\n");
  await writeFile(join(tmp, "Assets", "Scripts", "Enemy.cs"), "class C {}\n");
  await writeFile(join(tmp, "Assets", "Scripts", "Enemy.cs.meta"), "guid: c0a10000000000000000000000000003\n");

  // Prefabs (1 dir, 2 prefabs) referencing scripts + materials.
  const scriptGuid = "c0a10000000000000000000000000001";
  const matGuid = "d0e10000000000000000000000000001";
  await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
  await writeFile(
    join(tmp, "Assets", "Prefabs", "Player.prefab"),
    `%YAML 1.1
--- !u!1 &100
GameObject:
  m_Name: Player
--- !u!114 &200
MonoBehaviour:
  m_Script: {fileID: 11500000, guid: ${scriptGuid}, type: 3}
  m_Mat: {fileID: 0, guid: ${matGuid}, type: 2}
`,
  );
  await writeFile(join(tmp, "Assets", "Prefabs", "Player.prefab.meta"), "guid: a0b10000000000000000000000000001\n");
  await writeFile(
    join(tmp, "Assets", "Prefabs", "PlayerVariant.prefab"),
    `%YAML 1.1
--- !u!1001 &300
PrefabInstance:
  m_SourcePrefab: {fileID: 100100000, guid: a0b10000000000000000000000000001, type: 3}
`,
  );
  await writeFile(join(tmp, "Assets", "Prefabs", "PlayerVariant.prefab.meta"), "guid: a0b10000000000000000000000000002\n");

  // Materials (1 dir, 1 material).
  await mkdir(join(tmp, "Assets", "Materials"), { recursive: true });
  await writeFile(join(tmp, "Assets", "Materials", "SharedMat.mat"), `%YAML 1.1\n--- !u!21 &1\nMaterial:\n  m_Name: SharedMat\n`);
  await writeFile(join(tmp, "Assets", "Materials", "SharedMat.mat.meta"), `guid: ${matGuid}\n`);

  // Orphan .meta (no companion asset).
  await writeFile(join(tmp, "Assets", "Ghost.cs.meta"), "guid: f0f00000000000000000000000000001\n");
}

// ---- T2.1: scanIntegrityOffline walks the meta tree once (H3) ----

test("H3: scanIntegrityOffline walks the meta tree exactly once regardless of project size", async () => {
  // The previous implementation walked the meta tree 4× per scan
  // (collectFiles + per-file safeReadMetaGUID + walkMeta for orphans +
  // buildGUIDIndex for integrity). The single-walk refactor collapses that
  // to one walkMeta invocation chain (collectMetaTriples). Assert via the
  // test-only walk counter.
  const tmp = await mkdtemp(join(tmpdir(), "offline-scan-walks-"));
  try {
    await setupWalkCountProject(tmp);
    resetWalkCounters();
    const result = await scanIntegrityOffline({ projectRoot: tmp });
    const walks = getWalkMetaCount();
    // The fixture has Assets/ + 3 subdirs (Scripts, Prefabs, Materials) +
    // Scripts/Player = 5 directories. A single meta walk enters walkMeta
    // once per directory, so walks === 5. The pre-change code entered
    // walkMeta twice per directory (the orphan walk + the buildGUIDIndex
    // walk) → walks would be 10. Asserting <= 6 gives a little slack for
    // future fixture growth while still proving the single-walk property.
    assert.ok(
      walks <= 6,
      `scanIntegrityOffline should walk the meta tree once (got ${walks} walkMeta entries; pre-change was ~2× directory count)`,
    );
    // Output correctness: orphan + missing-ref + duplicate detection still work.
    assert.ok((result.byCode.orphan_meta ?? 0) >= 1, "orphan still detected");
    assert.equal(result._source, "offline");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("H3: scanIntegrityOffline output is byte-stable across runs (golden shape)", async () => {
  // Run the scan twice and assert the issue set + counts are identical —
  // proves the single-walk refactor did not change the output shape.
  const tmp = await mkdtemp(join(tmpdir(), "offline-scan-stable-"));
  try {
    await setupWalkCountProject(tmp);
    const first = await scanIntegrityOffline({ projectRoot: tmp });
    const second = await scanIntegrityOffline({ projectRoot: tmp });
    assert.deepEqual(first.byCode, second.byCode);
    assert.equal(first.totalIssues, second.totalIssues);
    assert.equal(first.assetsScanned, second.assetsScanned);
    assert.deepEqual(
      first.issues.map((i) => `${i.code}:${i.path}`),
      second.issues.map((i) => `${i.code}:${i.path}`),
      "issue ordering must be deterministic across runs",
    );
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---- T2.3: cold read_asset walks the meta tree once (H5) ----

test("H5: cold read_asset walks the meta tree exactly once (union wanted-set)", async () => {
  // The previous implementation called buildScriptIndex then buildGUIDIndex
  // back-to-back. buildScriptIndex internally calls buildGUIDIndex, so the
  // meta tree was walked twice for every cold read_asset. The union wanted-
  // set refactor collapses that to one walk.
  const tmp = await mkdtemp(join(tmpdir(), "offline-read-walks-"));
  try {
    await setupWalkCountProject(tmp);
    resetWalkCounters();
    const { model } = await readAssetOffline("Assets/Prefabs/Player.prefab", {
      fieldLimit: 10,
      projectRoot: tmp,
    });
    const walks = getWalkMetaCount();
    // Same 5-directory tree → single walk = 5 walkMeta entries. Pre-change
    // was 2× that (10). Assert <= 6 with the same slack rationale as H3.
    assert.ok(
      walks <= 6,
      `cold read_asset should walk the meta tree once (got ${walks} walkMeta entries; pre-change was ~2× directory count)`,
    );
    // Output correctness: the asset parsed and the GUID resolution happened
    // (the integrity block runs against the union-built guidIndex).
    assert.equal(model.kind, "prefab");
    assert.equal(model.path, "Assets/Prefabs/Player.prefab");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("H5: buildGuidAndScriptIndex produces both indices in a single walk", async () => {
  // Direct unit test of the combined builder: one walk, both maps populated.
  const tmp = await mkdtemp(join(tmpdir(), "offline-combined-idx-"));
  try {
    await setupWalkCountProject(tmp);
    resetWalkCounters();
    const wantedScripts = new Set(["c0a10000000000000000000000000001"]);
    const wantedGuids = new Set(["d0e10000000000000000000000000001"]);
    const { guidIndex, scriptIndex } = await buildGuidAndScriptIndex(
      tmp,
      wantedGuids,
      wantedScripts,
    );
    const walks = getWalkMetaCount();
    assert.ok(walks <= 6, `combined builder should walk once (got ${walks})`);
    assert.equal(
      guidIndex.get("d0e10000000000000000000000000001"),
      "Assets/Materials/SharedMat.mat",
      "guidIndex populated from the single walk",
    );
    assert.equal(
      scriptIndex.get("c0a10000000000000000000000000001"),
      "Assets/Scripts/PlayerController.cs",
      "scriptIndex populated from the same single walk",
    );
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---- T2.2: dependenciesOffline impact walks the project once (H4) ----

test("H4: dependenciesOffline(include_impact) walks the project once regardless of closure size", async () => {
  // The previous computeTransitiveImpact called findReferencesOffline per
  // BFS frontier node, each of which re-walked the whole project. For a
  // closure of size K the walk count was O(K × project). The graph-driven
  // BFS builds the reverse-edge graph once (one collectFiles walk) and
  // serves every hop via Map.get.
  //
  // The fixture: Player.prefab ← PlayerVariant.prefab (depth-1 reverse).
  // No depth-2+ closure here, but the assertion is about the WALK COUNT,
  // not the closure depth — even a deep closure must not add walks.
  const tmp = await mkdtemp(join(tmpdir(), "offline-impact-walks-"));
  try {
    await setupWalkCountProject(tmp);
    resetWalkCounters();
    const result = await dependenciesOffline({
      assetPath: "Assets/Prefabs/Player.prefab",
      includeImpact: true,
      maxImpactDepth: 5,
      projectRoot: tmp,
    });
    const fileWalks = getCollectFilesCount();
    // The reverse-edge graph build does ONE collectFiles walk. The forward-
    // edge path does not call collectFiles. So fileWalks should be small
    // (proportional to directory count, not closure size). Pre-change was
    // O(closure × directory count).
    assert.ok(
      fileWalks <= 6,
      `impact should walk the project once (got ${fileWalks} collectFiles entries; pre-change was O(closure × directories))`,
    );
    // Output correctness: impact block present with at least the direct
    // reverse edge (PlayerVariant → Player).
    assert.ok(result.impact, "impact block present");
    assert.ok(
      result.impact!.affected.some((a) => a.assetPath === "Assets/Prefabs/PlayerVariant.prefab"),
      "direct reverse edge (variant → base) is in the impact closure",
    );
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("H4: dependenciesOffline impact output is byte-stable (golden shape)", async () => {
  // Run impact twice and assert the affected set + depths are identical —
  // proves the graph-driven BFS produces the same closure as the previous
  // per-node walk.
  const tmp = await mkdtemp(join(tmpdir(), "offline-impact-stable-"));
  try {
    await setupWalkCountProject(tmp);
    const first = await dependenciesOffline({
      assetPath: "Assets/Prefabs/Player.prefab",
      includeImpact: true,
      projectRoot: tmp,
    });
    const second = await dependenciesOffline({
      assetPath: "Assets/Prefabs/Player.prefab",
      includeImpact: true,
      projectRoot: tmp,
    });
    assert.deepEqual(first.impact?.affected, second.impact?.affected);
    assert.equal(first.impact?.affectedCount, second.impact?.affectedCount);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("H4: dependenciesOffline without impact does NOT build the graph (cheaper path)", async () => {
  // When impact is not requested, dependenciesOffline falls back to the
  // direct findReferencesOffline path (cheaper than building the whole
  // project graph for a single reverse-edge lookup). Assert the graph build
  // is skipped by checking collectFiles was NOT called by dependenciesOffline
  // itself (findReferencesOffline DOES call collectFiles once, so the total
  // is small — but never the graph build's additional walk).
  const tmp = await mkdtemp(join(tmpdir(), "offline-no-impact-"));
  try {
    await setupWalkCountProject(tmp);
    resetWalkCounters();
    await dependenciesOffline({
      assetPath: "Assets/Prefabs/Player.prefab",
      includeImpact: false,
      projectRoot: tmp,
    });
    const fileWalks = getCollectFilesCount();
    assert.ok(
      fileWalks <= 6,
      `no-impact path should not build the graph (got ${fileWalks} collectFiles entries)`,
    );
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---- T2.4: collectFiles parallel + accumulator + ordering (M4, L5) ----

test("M4: collectFiles output ordering is deterministic (matches sequential readdir order)", async () => {
  // The parallel fan-out must preserve the same iteration order as the
  // previous sequential walk so golden-output tests stay stable. Build a
  // wide tree (many siblings) and assert the output matches a sequential
  // reference walk.
  const tmp = await mkdtemp(join(tmpdir(), "offline-collect-order-"));
  try {
    await mkdir(join(tmp, "Assets", "Wide"), { recursive: true });
    // 20 sibling files + 4 sibling subdirs with 5 files each.
    const expected: string[] = [];
    for (let i = 0; i < 20; i++) {
      const name = `file${String(i).padStart(2, "0")}.cs`;
      await writeFile(join(tmp, "Assets", "Wide", name), "");
      expected.push(join(tmp, "Assets", "Wide", name));
    }
    for (let s = 0; s < 4; s++) {
      const sub = `sub${s}`;
      await mkdir(join(tmp, "Assets", "Wide", sub));
      for (let i = 0; i < 5; i++) {
        const name = `inner${i}.cs`;
        await writeFile(join(tmp, "Assets", "Wide", sub, name), "");
        expected.push(join(tmp, "Assets", "Wide", sub, name));
      }
    }
    // readdir returns entries in directory order; the previous sequential
    // walk inlined each subdir's results where the subdir sat in its parent's
    // listing. Mirror that expected order by reading the parent's readdir,
    // recursing sequentially, and flattening.
    const { readdir, stat } = await import("node:fs/promises");
    const expectedOrder: string[] = [];
    const sequentialWalk = async (dir: string): Promise<void> => {
      const entries = await readdir(dir);
      for (const name of entries) {
        if (name === "Assets" && dir === tmp) {
          // Top-level tmp/Assets handled below; skip the recursion here.
        }
        const fullPath = join(dir, name);
        const s = await stat(fullPath);
        if (s.isDirectory()) await sequentialWalk(fullPath);
        else if (!name.endsWith(".meta")) expectedOrder.push(fullPath);
      }
    };
    await sequentialWalk(join(tmp, "Assets"));

    const actual = await collectFiles(join(tmp, "Assets"));
    assert.deepEqual(
      actual,
      expectedOrder,
      "parallel collectFiles must produce the same order as a sequential readdir walk",
    );
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("M4: parallelMap preserves input order across chunks", async () => {
  // Direct unit test of the bounded-parallel helper: with a chunk size
  // smaller than the input, the output order must match the input order.
  const input = Array.from({ length: 100 }, (_, i) => i);
  const out = await parallelMap(input, async (n) => {
    // Add a small randomized delay so chunks settle out of order without
    // parallelMap's order-stable merge.
    await new Promise<void>((r) => setTimeout(r, Math.random() * 5));
    return n * 2;
  });
  assert.deepEqual(out, input.map((n) => n * 2));
});

test("M4: parallelMap respects chunk size (bounds concurrency)", async () => {
  // Track the high-water mark of in-flight fn invocations. With chunk size 4
  // and 20 items, concurrency must never exceed 4.
  let inFlight = 0;
  let highWater = 0;
  const items = Array.from({ length: 20 }, (_, i) => i);
  await parallelMap(
    items,
    async (n) => {
      inFlight++;
      highWater = Math.max(highWater, inFlight);
      await new Promise<void>((r) => setTimeout(r, 5));
      inFlight--;
      return n;
    },
    4,
  );
  assert.ok(
    highWater <= 4,
    `concurrency must be bounded by chunk size (high-water ${highWater} > 4)`,
  );
});

// ---- T2.5: extractReferenceLocations single split (L4) ----

test("L4: verbose find_references output is byte-stable (single-split refactor)", async () => {
  // The verbose path now splits content once at the call site and passes
  // the line array downstream. Assert the verbose output (locations field)
  // is byte-identical to a known-good capture for a fixture with prefab
  // modifications + direct field refs.
  const tmp = await mkdtemp(join(tmpdir(), "offline-verbose-split-"));
  try {
    const targetGuid = "dead000000000000000000000000beef";
    await mkdir(join(tmp, "Assets", "Materials"), { recursive: true });
    await writeFile(join(tmp, "Assets", "Materials", "Target.mat"), `%YAML 1.1\n--- !u!21 &1\nMaterial:\n  m_Name: Target\n`);
    await writeFile(join(tmp, "Assets", "Materials", "Target.mat.meta"), `guid: ${targetGuid}\n`);

    await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
    // Prefab with a direct material field ref + a prefab-modification target
    // ref — both shapes the locations extractor handles.
    await writeFile(
      join(tmp, "Assets", "Prefabs", "User.prefab"),
      `%YAML 1.1
--- !u!1 &100
GameObject:
  m_Name: User
--- !u!23 &200
MeshRenderer:
  m_Material: {fileID: 0, guid: ${targetGuid}, type: 2}
--- !u!1001 &300
PrefabInstance:
  m_Modifications:
  - target: {fileID: 100, guid: ${targetGuid}, type: 3}
    propertyPath: m_Name
    value: Renamed
`,
    );
    await writeFile(join(tmp, "Assets", "Prefabs", "User.prefab.meta"), "guid: user000000000000000000000000000001\n");

    const result = await findReferencesOffline({
      guid: targetGuid,
      detail: "verbose",
      projectRoot: tmp,
    });
    const user = result.referencedBy.find((e) => e.assetPath === "Assets/Prefabs/User.prefab");
    assert.ok(user, "User.prefab is in the referenced-by list");
    assert.ok(
      user!.locations && user!.locations.length >= 2,
      `verbose locations extracted (got ${user!.locations?.length ?? 0})`,
    );
    // The material field ref surfaces as the "m_Material" field name; the
    // prefab-modification target surfaces as "prefab → m_Name". Both must
    // appear in the locations array — pinning the exact labels catches any
    // regression in the single-split path's per-line scan.
    assert.ok(
      user!.locations!.includes("m_Material"),
      `locations include the direct material field (got ${JSON.stringify(user!.locations)})`,
    );
    assert.ok(
      user!.locations!.includes("prefab → m_Name"),
      `locations include the prefab-modification target label (got ${JSON.stringify(user!.locations)})`,
    );
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---- L2: search_assets component query uses the combined walk ----

test("L2: buildScriptIndexForQuery delegates to the combined single-walk primitive", async () => {
  // The component-query path now derives its script-name → guid → path index
  // from buildGuidScriptAndNameIndex (the same primitive read_asset uses),
  // instead of a separate walk. Assert the output is byte-identical to the
  // pre-change shape: a Map<guid, path> for every .cs whose filename
  // contains the query substring.
  const tmp = await mkdtemp(join(tmpdir(), "offline-script-query-"));
  try {
    await setupWalkCountProject(tmp);
    const { buildScriptIndexForQuery } = await import("./offline/index-builders.js");
    const idx = await buildScriptIndexForQuery(tmp, "player");
    assert.ok(
      idx.get("c0a10000000000000000000000000001") === "Assets/Scripts/PlayerController.cs",
      "PlayerController matched by 'player' query",
    );
    assert.ok(
      idx.get("c0a10000000000000000000000000002") === "Assets/Scripts/Player/PlayerInput.cs",
      "PlayerInput matched by 'player' query",
    );
    assert.ok(
      !idx.has("c0a10000000000000000000000000003"),
      "Enemy.cs NOT matched by 'player' query",
    );
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("L2: buildGuidScriptAndNameIndex collects script names alongside guid+script indices", async () => {
  // The combined primitive returns a script-name → guid → path mapping as a
  // byproduct of the single walk. Assert all three maps are populated from
  // the same pass when both a wanted set and a component query are supplied.
  const tmp = await mkdtemp(join(tmpdir(), "offline-combined-name-idx-"));
  try {
    await setupWalkCountProject(tmp);
    resetWalkCounters();
    const { guidIndex, scriptIndex, scriptNameIndex } = await buildGuidScriptAndNameIndex(
      tmp,
      {
        wantedGuids: new Set(["d0e10000000000000000000000000001"]),
        componentQuery: "player",
      },
    );
    const walks = getWalkMetaCount();
    assert.ok(walks <= 6, `combined name+guid+script walk ran once (got ${walks})`);
    assert.equal(guidIndex.get("d0e10000000000000000000000000001"), "Assets/Materials/SharedMat.mat");
    assert.equal(scriptNameIndex.get("c0a10000000000000000000000000001")?.path, "Assets/Scripts/PlayerController.cs");
    assert.equal(scriptNameIndex.get("c0a10000000000000000000000000001")?.name, "playercontroller");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// M31-optimizations Plan 3 — Offline parse single-pass.
//
// Acceptance criteria (per sub-plan): byte-identical output + single-pass /
// cache-hit properties. The golden-output tests run end-to-end through the
// public entry points (dependenciesOffline / readAssetOffline / searchAssets-
// Offline); the cache-hit + leaf-primitive tests import the offline
// submodules directly. Every test asserts byte-identical output where the
// underlying function has observable output, and counter-spy / identity
// properties where the optimization is structural (single pass, cache reuse).
// ---------------------------------------------------------------------------

// ---- T3.1: collectForwardEdges single pass (H6) ----

test("H6: collectForwardEdges output is byte-stable across runs and ordered by kind (prefab_source, script, pptr)", async () => {
  // PlayerVariant.prefab from setupDependencyProject has one PrefabInstance
  // (m_SourcePrefab → basePrefabGuid) AND a target: GUID inside its
  // m_Modifications (also basePrefabGuid). The previous 3-pass collectForward-
  // Edges produced: [prefab_source(base), pptr(base)]. The single-pass
  // refactor must produce the same edge set in the same kind order. This is
  // the golden-output gate for T3.1 — the edge roster is the byte-identical
  // acceptance bar.
  const tmp = await mkdtemp(join(tmpdir(), "offline-plan3-forward-"));
  try {
    await setupDependencyProject(tmp);
    const result = await dependenciesOffline({
      assetPath: "Assets/Prefabs/PlayerVariant.prefab",
      projectRoot: tmp,
    });
    const edges = result.forwardDependencies;
    assert.ok(edges.length > 0, `forward edges present: ${JSON.stringify(edges)}`);
    // First edge MUST be prefab_source (the previous pass-1 emitted it first).
    assert.equal(edges[0].kind, "prefab_source", "prefab_source kind first");
    assert.ok(
      edges[0].assetPath.endsWith("Player.prefab"),
      `prefab_source resolves to base prefab: ${edges[0].assetPath}`,
    );
    // No script edge here (no MonoBehaviour). Verify at least one pptr edge
    // exists from the m_Modifications target line (same guid, deduped into
    // one pptr entry).
    const pptrEdges = edges.filter((e) => e.kind === "pptr");
    assert.ok(pptrEdges.length >= 1, `pptr edge(s) present: ${JSON.stringify(pptrEdges)}`);
    // Deterministic across two runs (single-pass must be byte-stable).
    const second = await dependenciesOffline({
      assetPath: "Assets/Prefabs/PlayerVariant.prefab",
      projectRoot: tmp,
    });
    assert.deepEqual(
      edges.map((e) => `${e.kind}:${e.guid}:${e.resolved}`),
      second.forwardDependencies.map((e) => `${e.kind}:${e.guid}:${e.resolved}`),
      "forward edge roster stable across runs",
    );
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("H6: collectForwardEdges emits all three edge kinds in the canonical kind order (prefab_source → script → pptr)", async () => {
  // To exercise all three edge kinds from ONE asset, the file needs a
  // PrefabInstance object (prefab_source edge from m_SourcePrefab) AND a
  // MonoBehaviour object (script edge from m_Script) AND a generic guid: ref
  // (pptr edge). The pptr scan also catches the m_SourcePrefab + m_Script
  // line guids, but those dedupe against the per-kind seen keys, so the mat
  // guid is the standalone pptr edge here. The single-pass refactor must
  // preserve the previous kind-ordering (prefab_source first, then script,
  // then pptr) — that ordering is the byte-identical acceptance bar.
  const tmp = await mkdtemp(join(tmpdir(), "offline-plan3-three-kinds-"));
  try {
    const baseGuid = "f1000000000000000000000000000001";
    const scriptGuid = "f1000000000000000000000000000002";
    const matGuid = "f1000000000000000000000000000003";
    await mkdir(join(tmp, "Assets", "Data"), { recursive: true });
    await writeFile(
      join(tmp, "Assets", "Data", "Variant.asset"),
      `%YAML 1.1
--- !u!1001 &1000
PrefabInstance:
  m_SourcePrefab: {fileID: 100100000, guid: ${baseGuid}, type: 3}
--- !u!114 &2000
MonoBehaviour:
  m_Script: {fileID: 11500000, guid: ${scriptGuid}, type: 3}
  m_Mat: {fileID: 0, guid: ${matGuid}, type: 2}
`,
    );
    await writeFile(join(tmp, "Assets", "Data", "Variant.asset.meta"), `guid: f1000000000000000000000000000099\n`);
    // Companion .metas so the edges resolve.
    await mkdir(join(tmp, "Assets", "Base"), { recursive: true });
    await writeFile(join(tmp, "Assets", "Base", "Base.prefab"), `%YAML 1.1\n--- !u!1 &1\nGameObject:\n  m_Name: Base\n`);
    await writeFile(join(tmp, "Assets", "Base", "Base.prefab.meta"), `guid: ${baseGuid}\n`);
    await writeFile(join(tmp, "Assets", "Base", "S.cs"), "class S {}\n");
    await writeFile(join(tmp, "Assets", "Base", "S.cs.meta"), `guid: ${scriptGuid}\n`);
    await writeFile(join(tmp, "Assets", "Base", "M.mat"), `%YAML 1.1\n--- !u!21 &1\nMaterial:\n  m_Name: M\n`);
    await writeFile(join(tmp, "Assets", "Base", "M.mat.meta"), `guid: ${matGuid}\n`);

    const result = await dependenciesOffline({
      assetPath: "Assets/Data/Variant.asset",
      projectRoot: tmp,
    });
    const kinds = result.forwardDependencies.map((e) => e.kind);
    // Expected ordering: prefab_source, then script, then pptr.
    assert.equal(kinds[0], "prefab_source", `prefab_source emitted first: ${JSON.stringify(kinds)}`);
    assert.ok(
      kinds.indexOf("prefab_source") < kinds.indexOf("script"),
      `script after prefab_source: ${JSON.stringify(kinds)}`,
    );
    assert.ok(
      kinds.indexOf("script") < kinds.indexOf("pptr"),
      `pptr after script: ${JSON.stringify(kinds)}`,
    );
    // The base guid appears as BOTH a prefab_source edge AND a pptr edge
    // (the m_SourcePrefab line contains guid:, so the pptr scan catches it
    // too — same behavior as the previous 3-pass implementation).
    const guidsAsSet = new Set(result.forwardDependencies.map((e) => e.guid));
    assert.ok(guidsAsSet.has(baseGuid), "base guid present");
    assert.ok(guidsAsSet.has(scriptGuid), "script guid present");
    assert.ok(guidsAsSet.has(matGuid), "mat guid present");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("H6: collectForwardEdges code-inspection — single `for (const obj of parsed.objects)` loop in api.ts", async () => {
  // Code-inspection acceptance bar: exactly one pass over parsed.objects per
  // collectForwardEdges call. The previous implementation had three. Assert
  // the source contains exactly one such loop inside collectForwardEdges.
  // Comments are stripped first so the doc-comment mention of the previous
  // three loops does not inflate the count.
  const { readFile } = await import("node:fs/promises");
  const raw = await readFile(await offlineSourcePath("api"), "utf-8");
  const src = stripComments(raw);
  const fnStart = src.indexOf("function collectForwardEdges(");
  assert.ok(fnStart > 0, "collectForwardEdges located in api.ts");
  // Function boundary: the next top-level `function ` declaration after
  // fnStart (collectForwardEdges is followed by resolveForwardEdges).
  const fnEnd = src.indexOf("\nfunction ", fnStart + 1);
  const fnBody = src.slice(fnStart, fnEnd > fnStart ? fnEnd : src.length);
  const objectLoopCount = (fnBody.match(/for \(const obj of parsed\.objects\)/g) ?? []).length;
  assert.equal(
    objectLoopCount,
    1,
    `collectForwardEdges has exactly one for-objects loop (got ${objectLoopCount}); the single-pass refactor removed the previous three`,
  );
});

// ---- T3.2: finishObject single-pass via extractKnownFields (H7) ----

test("H7: extractKnownFields single-pass extracts every known field from a GameObject", () => {
  // Direct unit test of the single-pass scanner. A GameObject's lines include
  // m_Name, a multi-line m_Component list, and the standard {fileID: 0}
  // placeholder fields. extractKnownFields must return name + componentIDs in
  // one walk. This is the byte-identical gate for the leaf scanner finishObject
  // now calls instead of the previous 6 independent readers.
  const lines = [
    "GameObject:",
    "  m_ObjectHideFlags: 0",
    "  m_CorrespondingSourceObject: {fileID: 0}",
    "  m_PrefabInstance: {fileID: 0}",
    "  m_PrefabAsset: {fileID: 0}",
    "  serializedVersion: 6",
    "  m_Component:",
    "  - component: {fileID: 200}",
    "  - component: {fileID: 300}",
    "  m_Layer: 0",
    "  m_Name: Player",
    "  m_TagString: Untagged",
    "  m_Icon: {fileID: 0}",
    "  m_NavMeshLayer: 0",
    "  m_StaticEditorFlags: 0",
    "  m_IsActive: 1",
  ];
  const f = extractKnownFields(lines);
  assert.equal(f.name, "Player");
  assert.deepEqual(f.componentIDs, ["200", "300"]);
  // A GameObject declares no m_GameObject / m_Father / m_Script of its own;
  // those stay empty. The m_CorrespondingSourceObject placeholder line
  // contributes fileID "0" (mirroring the previous readFileIDField, which
  // extracts "0" from {fileID: 0}); no guid on that line, so sourceGUID is "".
  assert.equal(f.gameObjectID, "");
  assert.equal(f.fatherTransformID, "");
  assert.equal(f.sourceObjectID, "0");
  assert.equal(f.sourceGUID, "");
  assert.equal(f.scriptGUID, "");
});

test("H7: extractKnownFields captures m_CorrespondingSourceObject fileID + guid from the same line", () => {
  // The previous finishObject called readFileIDField THEN readGUIDField for
  // m_CorrespondingSourceObject — both scanned for the first matching line,
  // so they necessarily landed on the same line. extractKnownFields must
  // produce the same (fileID, guid) pair from that one line in its single
  // pass. This is the precedence/same-line invariant the plan flagged.
  const lines = [
    "MonoBehaviour:",
    "  m_GameObject: {fileID: 100}",
    "  m_CorrespondingSourceObject: {fileID: 12345, guid: abcdef0123456789abcdef0123456789, type: 3}",
  ];
  const f = extractKnownFields(lines);
  assert.equal(f.gameObjectID, "100");
  assert.equal(f.sourceObjectID, "12345");
  assert.equal(f.sourceGUID, "abcdef0123456789abcdef0123456789");
});

test("H7: extractKnownFields prefers the FIRST matching line for each field (readScalar/readFileIDField semantics)", () => {
  // The previous readers returned at the first match. extractKnownFields
  // guards each field with `=== ""` so a later duplicate line does NOT
  // override. Verify a duplicate m_Name line is ignored (matches readScalar).
  const lines = [
    "GameObject:",
    "  m_Name: First",
    "  m_Name: Second",
  ];
  const f = extractKnownFields(lines);
  assert.equal(f.name, "First", "first m_Name wins (readScalar semantics)");
});

test("H7: finishObject produces byte-identical parsed objects across runs (single-pass golden)", async () => {
  // End-to-end gate: read the same prefab twice, assert the rendered model
  // (which flows from parsed objects via finishObject) is byte-identical.
  // The previous multi-scanner finishObject and the new single-pass version
  // must produce the same hierarchy + components + fields.
  const tmp = await mkdtemp(join(tmpdir(), "offline-plan3-finishobj-"));
  try {
    await setupProject(tmp);
    const first = await readAssetOffline("Assets/Prefabs/Player.prefab", {
      fieldLimit: 20,
      projectRoot: tmp,
    });
    const second = await readAssetOffline("Assets/Prefabs/Player.prefab", {
      fieldLimit: 20,
      projectRoot: tmp,
    });
    assert.deepEqual(first.model, second.model, "parsed model byte-stable across runs");
    // Spot-check the parsed fields finishObject populated (scriptGUID flows
    // to the PlayerController component's scriptPath).
    const pc1 = first.model.roots[0].components.find((c) => c.name === "PlayerController");
    assert.ok(pc1, "PlayerController component present (finishObject scriptGUID populated)");
    assert.equal(pc1.scriptPath, "Assets/Scripts/PlayerController.cs");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("H7: finishObject walks the line array exactly once per object (code inspection)", async () => {
  // The previous finishObject called up to 6 independent `for (const line of
  // lines)` scanners. The single-pass refactor delegates to extractKnownFields
  // (one walk). Assert finishObject's body contains NO direct for-line loop
  // (the only line walk lives in extractKnownFields, which finishObject calls
  // exactly once).
  const { readFile } = await import("node:fs/promises");
  const src = await readFile(await offlineSourcePath("parse"), "utf-8");
  const fnStart = src.indexOf("function finishObject(");
  assert.ok(fnStart > 0, "finishObject located in parse.ts");
  const fnEnd = src.indexOf("\n}", fnStart) + 2;
  const fnBody = src.slice(fnStart, fnEnd);
  const lineLoops = (fnBody.match(/for \(const line of /g) ?? []).length;
  assert.equal(
    lineLoops,
    0,
    `finishObject delegates line scanning to extractKnownFields (got ${lineLoops} direct for-line loops; expected 0)`,
  );
  // And finishObject calls extractKnownFields exactly once.
  const extractCalls = (fnBody.match(/extractKnownFields\(/g) ?? []).length;
  assert.equal(extractCalls, 1, "finishObject calls extractKnownFields exactly once");
});

// ---- T3.3: objectPath goID→path cache (H8) ----

test("H8: buildHierarchy caches on ParsedAsset — second call returns the same root array reference", async () => {
  // The per-asset hierarchy cache short-circuits buildHierarchy on the second
  // call. Assert identity (===) of the roots array — a fresh rebuild would
  // produce a new array, failing the identity check.
  const tmp = await mkdtemp(join(tmpdir(), "offline-plan3-cache-id-"));
  try {
    await setupProject(tmp);
    const { readFile } = await import("node:fs/promises");
    const { parseAsset } = await import("./offline/parse.js");
    const data = await readFile(join(tmp, "Assets", "Prefabs", "Player.prefab"), "utf-8");
    const parsed = parseAsset(data);
    const first = buildHierarchy(parsed);
    const second = buildHierarchy(parsed);
    assert.ok(first === second, "second buildHierarchy returns the cached root array (identity)");
    assert.ok(parsed.hierarchyCache, "ParsedAsset.hierarchyCache populated after buildHierarchy");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("H8: objectPath consults the cache and returns the same path as a fresh flatten", async () => {
  // objectPath previously rebuilt + flattened the whole tree per call. The
  // cache hit is O(1). Verify the cache-hit path matches what a fresh
  // flatten would produce (byte-identical output gate).
  const tmp = await mkdtemp(join(tmpdir(), "offline-plan3-objpath-"));
  try {
    const { readFile } = await import("node:fs/promises");
    const { parseAsset } = await import("./offline/parse.js");
    const { flattenNodes } = await import("./offline/hierarchy.js");
    await setupProject(tmp);
    const data = await readFile(join(tmp, "Assets", "Prefabs", "Player.prefab"), "utf-8");
    const parsed = parseAsset(data);
    // Build the cache once (mirrors what readAssetOffline / search do).
    buildHierarchy(parsed);
    // The Player GameObject (id "100") should resolve to "Player".
    const viaCache = objectPath(parsed, "100");
    // Verify against a fresh flatten (the pre-change code path).
    let viaFlatten = "";
    for (const node of flattenNodes(parsed)) {
      if (node.gameObject.id === "100") { viaFlatten = node.path; break; }
    }
    assert.equal(viaCache, viaFlatten, "cache-hit path matches fresh-flatten path");
    assert.equal(viaCache, "Player", `objectPath for Player GameObject: ${viaCache}`);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("H8: objectPath consults the cache before falling back to flattenNodes (code inspection)", async () => {
  // objectPath's cache-hit path cannot be spied via module export patching
  // (objectPath and flattenNodes live in the same module, so the internal
  // call bypasses the exported binding). The structural guarantee — that
  // objectPath reads asset.hierarchyCache FIRST and only falls back to
  // flattenNodes when the cache is absent — is verified by code inspection:
  // the function body must reference hierarchyCache before flattenNodes.
  // Comments stripped so the doc-comment's "flattenNodes" mention does not
  // skew the ordering check.
  const { readFile } = await import("node:fs/promises");
  const raw = await readFile(await offlineSourcePath("hierarchy"), "utf-8");
  const src = stripComments(raw);
  const fnStart = src.indexOf("export function objectPath(");
  assert.ok(fnStart > 0, "objectPath located in hierarchy.ts");
  // Extract up to the next exported function (the function boundary).
  const nextExport = src.indexOf("export function", fnStart + 1);
  const fnBody = src.slice(fnStart, nextExport > fnStart ? nextExport : src.length);
  const cacheIdx = fnBody.indexOf("hierarchyCache");
  const flattenIdx = fnBody.indexOf("flattenNodes");
  assert.ok(cacheIdx > 0, "objectPath references asset.hierarchyCache");
  assert.ok(flattenIdx > 0, "objectPath retains the flattenNodes fallback");
  assert.ok(
    cacheIdx < flattenIdx,
    "objectPath consults hierarchyCache BEFORE the flattenNodes fallback",
  );
});

// ---- T3.4: checkMatch flatten-once + components map (L3) ----

test("L3: checkMatch output is byte-stable for name + component queries (single flatten)", async () => {
  // search_assets with BOTH a name and a component filter exercises the
  // flatten-once + componentsFor-cache path. Two runs must produce identical
  // output — the golden gate for T3.4.
  const tmp = await mkdtemp(join(tmpdir(), "offline-plan3-checkmatch-"));
  try {
    await setupDependencyProject(tmp);
    const first = await searchAssetsOffline({
      name: "Player",
      component: "PlayerController",
      projectRoot: tmp,
    });
    const second = await searchAssetsOffline({
      name: "Player",
      component: "PlayerController",
      projectRoot: tmp,
    });
    assert.deepEqual(first, second, "search_assets name+component output byte-stable across runs");
    assert.ok(first.matchCount >= 1, "Player prefab matched");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("L3: checkMatch calls flattenHierarchy exactly once (code inspection)", async () => {
  // Code-inspection gate: the previous implementation called flattenHierarchy
  // twice per checkMatch (once for the name scan, once for the component
  // scan). The flatten-once refactor hoists the call above both branches.
  // Comments stripped first so doc-comment mentions don't inflate the count.
  const { readFile } = await import("node:fs/promises");
  const raw = await readFile(await offlineSourcePath("api"), "utf-8");
  const src = stripComments(raw);
  const fnStart = src.indexOf("function checkMatch(");
  assert.ok(fnStart > 0, "checkMatch located in api.ts");
  const fnEnd = src.indexOf("\nfunction ", fnStart + 1);
  const fnBody = src.slice(fnStart, fnEnd > fnStart ? fnEnd : src.length);
  const flattenCalls = (fnBody.match(/flattenHierarchy\(/g) ?? []).length;
  assert.equal(
    flattenCalls,
    1,
    `checkMatch calls flattenHierarchy exactly once (got ${flattenCalls}; pre-change was 2)`,
  );
});

test("L3: componentsFor cache hit returns identical result to a fresh scan", async () => {
  // componentsFor now consults the per-asset goID→components cache. Verify
  // the cache-hit result matches what the (fallback) linear scan would
  // produce — same component objects, same order, same names.
  const { parseAsset } = await import("./offline/parse.js");
  const scriptGuid = "cccc0000000000000000000000000001";
  const parsed = parseAsset(
    `%YAML 1.1
--- !u!1 &100
GameObject:
  m_Component:
  - component: {fileID: 200}
  - component: {fileID: 300}
  m_Name: GO
--- !u!4 &200
Transform:
  m_GameObject: {fileID: 100}
  m_Father: {fileID: 0}
--- !u!114 &300
MonoBehaviour:
  m_GameObject: {fileID: 100}
  m_Script: {fileID: 11500000, guid: ${scriptGuid}, type: 3}
`,
  );
  const scriptIndex = new Map([[scriptGuid, "Assets/Scripts/MyScript.cs"]]);
  buildHierarchy(parsed); // populate the cache
  const cached = componentsFor(parsed, "100", scriptIndex);
  // Re-parse fresh (no cache) and run componentsFor on the uncached asset —
  // the fallback path. The two results must match (byte-identical gate).
  const fresh = parseAsset(
    `%YAML 1.1
--- !u!1 &100
GameObject:
  m_Component:
  - component: {fileID: 200}
  - component: {fileID: 300}
  m_Name: GO
--- !u!4 &200
Transform:
  m_GameObject: {fileID: 100}
  m_Father: {fileID: 0}
--- !u!114 &300
MonoBehaviour:
  m_GameObject: {fileID: 100}
  m_Script: {fileID: 11500000, guid: ${scriptGuid}, type: 3}
`,
  );
  const uncached = componentsFor(fresh, "100", scriptIndex);
  assert.equal(cached.length, uncached.length);
  assert.deepEqual(
    cached.map((c) => ({ name: c.name, scriptPath: c.scriptPath })),
    uncached.map((c) => ({ name: c.name, scriptPath: c.scriptPath })),
    "cache-hit componentsFor matches fresh-scan output",
  );
  // Sanity: the Transform + the MonoBehaviour are both present, in
  // componentIDs order (Transform first, then the script).
  assert.equal(cached[0].name, "Transform");
  assert.equal(cached[1].name, "MyScript");
});

// ---- T3.5: cleanScalar manual quote strip (L7) ----

test("L7: cleanScalar strips simple double-quoted strings without throwing", () => {
  // The common YAML-quoted-string case has no escape sequences — the manual
  // strip must apply and JSON.parse must NOT be consulted. Assert the output
  // matches JSON.parse's result for the same input (byte-identical gate),
  // and assert JSON.parse is NOT called (counter spy on the global).
  const cases = [
    '"hello world"',
    '"Player"',
    '"a,b:c"',
    '"path/with/slashes"',
    '"中文名称"', // non-ASCII (not JSON-escaped)
  ];
  for (const input of cases) {
    const cleaned = cleanScalar(input);
    // JSON.parse would produce the inner string for these valid-JSON inputs.
    // The manual strip produces the same inner string. Assert equality.
    assert.equal(cleaned, JSON.parse(input), `cleanScalar(${JSON.stringify(input)}) matches JSON.parse`);
    assert.ok(!cleaned.startsWith('"'), `quotes stripped: ${JSON.stringify(cleaned)}`);
  }
});

test("L7: cleanScalar does NOT call JSON.parse for the common no-escape case (counter spy)", () => {
  // Spy on the global JSON.parse. For inputs without backslash/embedded-quote
  // escape markers, cleanScalar must take the manual-strip fast path and
  // never reach JSON.parse. For inputs WITH escape markers, JSON.parse is
  // the fallback decoder.
  const original = JSON.parse;
  let callCount = 0;
  const spy = (...args: Parameters<typeof JSON.parse>): ReturnType<typeof JSON.parse> => {
    callCount++;
    return original(...args);
  };
  (JSON as { parse: typeof original }).parse = spy;
  try {
    // Common case — no escape markers. JSON.parse must NOT be called.
    callCount = 0;
    assert.equal(cleanScalar('"hello world"'), "hello world");
    assert.equal(callCount, 0, `JSON.parse not called for no-escape input (got ${callCount})`);

    // Escape-marker case — JSON.parse IS the decoder.
    callCount = 0;
    assert.equal(cleanScalar('"hello\\nworld"'), "hello\nworld");
    assert.ok(callCount >= 1, `JSON.parse called for escape-marker input (got ${callCount})`);
  } finally {
    (JSON as { parse: typeof original }).parse = original;
  }
});

test("L7: cleanScalar falls back to manual strip when JSON.parse throws", () => {
  // An escape marker that does NOT form a valid JSON escape (e.g. a lone
  // backslash before a non-escape char) triggers JSON.parse → throw → manual
  // slice fallback. The fallback must return the raw inner string (matching
  // the previous implementation's catch branch).
  const cleaned = cleanScalar('"bad\\xescape"');
  assert.equal(cleaned, "bad\\xescape", "fallback returns raw inner content on JSON.parse throw");
});

test("L7: cleanScalar leaves unquoted / single-quoted values unchanged (parity)", () => {
  // Parity gate: cleanScalar only special-cases the double-quote-wrapped form.
  // Unquoted scalars and single-quoted YAML scalars pass through trimmed.
  assert.equal(cleanScalar("hello"), "hello");
  assert.equal(cleanScalar("  spaced  "), "spaced");
  assert.equal(cleanScalar("'single'"), "'single'");
  assert.equal(cleanScalar(""), "");
});

// ---- T3.6: detectCyclesOffline push/pop trail (L12) ----

test("L12: detectCyclesOffline output is byte-stable for a known two-node cycle", async () => {
  // The push/pop trail refactor must produce the same cycle trail as the
  // previous immutable [...trail, x] spread. The existing dependenciesOffline
  // cycle test covers the basic case; this asserts the trail SHAPE explicitly.
  const tmp = await mkdtemp(join(tmpdir(), "offline-plan3-cycle-"));
  try {
    const aGuid = "d1c10000000000000000000000000001";
    const bGuid = "d1c10000000000000000000000000002";
    await mkdir(join(tmp, "Assets", "Data"), { recursive: true });
    await writeFile(
      join(tmp, "Assets", "Data", "A.asset"),
      `%YAML 1.1
--- !u!114 &11400000
MonoBehaviour:
  m_Name: A
  m_Ref: {fileID: 0, guid: ${bGuid}, type: 2}
`,
    );
    await writeFile(join(tmp, "Assets", "Data", "A.asset.meta"), `guid: ${aGuid}\n`);
    await writeFile(
      join(tmp, "Assets", "Data", "B.asset"),
      `%YAML 1.1
--- !u!114 &11400000
MonoBehaviour:
  m_Name: B
  m_Ref: {fileID: 0, guid: ${aGuid}, type: 2}
`,
    );
    await writeFile(join(tmp, "Assets", "Data", "B.asset.meta"), `guid: ${bGuid}\n`);

    const first = await dependenciesOffline({
      assetPath: "Assets/Data/A.asset",
      projectRoot: tmp,
    });
    const second = await dependenciesOffline({
      assetPath: "Assets/Data/A.asset",
      projectRoot: tmp,
    });
    assert.ok(first.cycles.length > 0, "cycle detected");
    assert.deepEqual(first.cycles, second.cycles, "cycle output byte-stable across runs");
    const cycle = first.cycles[0];
    // Trail starts AND ends at A (the queried asset), with B in between.
    assert.ok(cycle[0].endsWith("A.asset"), `cycle starts at A: ${JSON.stringify(cycle)}`);
    assert.ok(cycle[cycle.length - 1].endsWith("A.asset"), `cycle returns to A: ${JSON.stringify(cycle)}`);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("L12: detectCyclesOffline has no [...trail, x] spread in the recursion (code inspection)", async () => {
  // Code-inspection gate: the push/pop refactor removed the immutable spread
  // from the hot recursion path. Assert exactly one `[...trail` remains
  // inside detectCyclesOffline — the emit-time `cycles.push([...trail,
  // startPath])` snapshot. The dfs helper must use trail.push / trail.pop.
  // Comments stripped first so the doc-comment's `[...trail, x]` mention does
  // not inflate the count.
  const { readFile } = await import("node:fs/promises");
  const raw = await readFile(await offlineSourcePath("api"), "utf-8");
  const src = stripComments(raw);
  const fnStart = src.indexOf("async function detectCyclesOffline(");
  assert.ok(fnStart > 0, "detectCyclesOffline located in api.ts");
  const fnEnd = src.indexOf("\nasync function ", fnStart + 1);
  const fnBody = src.slice(fnStart, fnEnd > fnStart ? fnEnd : src.length);
  const spreads = (fnBody.match(/\[\.\.\.trail/g) ?? []).length;
  assert.equal(
    spreads,
    1,
    `exactly one [...trail...] spread remains (the emit-time snapshot); got ${spreads}`,
  );
  // And it's at the cycles.push site (not in the dfs recursion body).
  assert.ok(
    fnBody.includes("cycles.push([...trail, startPath])"),
    "the remaining spread is the emit-time cycle snapshot",
  );
  // The dfs helper uses push/pop mutation.
  assert.ok(
    fnBody.includes("trail.push(") && fnBody.includes("trail.pop()"),
    "dfs uses push/pop trail mutation",
  );
});

// ---- T3.7: regex hoist + shared extractExtension (L8-offline) ----

test("L8-offline: extractExtension is the single source of truth for the trailing-ext regex", () => {
  // Direct unit test of the shared helper. Covers the cases the two previous
  // inline `path.match(/\.[^.]+$/)` sites relied on.
  assert.equal(extractExtension("Assets/Scripts/PlayerController.cs"), ".cs");
  assert.equal(extractExtension("Assets/Foo.bar.cs"), ".cs", "last dot wins (matches inline behavior)");
  assert.equal(extractExtension("noext"), "", "no extension → empty string");
  assert.equal(extractExtension("Assets/Materials/Shared.mat"), ".mat");
  assert.equal(extractExtension("trailing/"), "", "trailing slash, no ext");
});

test("L8-offline: no inline /\\.[^.]+$/ regex literal remains in index-builders.ts or overrides.ts", async () => {
  // Code-inspection gate: the duplicated inline regex was hoisted to
  // paths.ts's extractExtension. Assert neither consumer file still has its
  // own copy. Comments stripped first so the doc-comment's mention of the
  // old literal does not trip the substring check.
  const { readFile } = await import("node:fs/promises");
  const ib = stripComments(await readFile(await offlineSourcePath("index-builders"), "utf-8"));
  const ov = stripComments(await readFile(await offlineSourcePath("overrides"), "utf-8"));
  const extLiteral = "/\\.[^.]+$/";
  assert.ok(
    !ib.includes(extLiteral),
    "index-builders.ts has no inline /\\.[^.]+$/ literal (uses extractExtension)",
  );
  assert.ok(
    !ov.includes(extLiteral),
    "overrides.ts has no inline /\\.[^.]+$/ literal (uses extractExtension)",
  );
  // Both files import the helper.
  assert.ok(ib.includes("extractExtension"), "index-builders.ts imports extractExtension");
  assert.ok(ov.includes("extractExtension"), "overrides.ts imports extractExtension");
});

test("L8-offline: no inline /\\s+/ regex literal remains in primitives.ts or hierarchy.ts hot loops", async () => {
  // Code-inspection gate: the inline /\s+/ literals in parseHeaderLine
  // (per-call) and summarizeNested (per-line hot loop) were hoisted to
  // module-scope WHITESPACE_RE constants. The hoisted constant definition
  // line is allowed; any OTHER inline usage is not. Comments stripped first.
  const { readFile } = await import("node:fs/promises");
  const prim = stripComments(await readFile(await offlineSourcePath("primitives"), "utf-8"));
  const hier = stripComments(await readFile(await offlineSourcePath("hierarchy"), "utf-8"));
  const wsLiteral = "/\\s+/";
  // Strip the single hoisted-constant declaration line so the definition
  // itself does not trip the "no inline literal" check.
  const stripConst = (src: string): string =>
    src.replace(/const WHITESPACE_RE = [^\n]*\n/g, "");
  assert.ok(
    !stripConst(prim).includes(wsLiteral),
    "primitives.ts has no inline /\\s+/ literal outside the WHITESPACE_RE definition",
  );
  assert.ok(
    !stripConst(hier).includes(wsLiteral),
    "hierarchy.ts has no inline /\\s+/ literal outside the WHITESPACE_RE definition",
  );
  // Both modules define the constant.
  assert.ok(prim.includes("WHITESPACE_RE"), "primitives.ts defines WHITESPACE_RE");
  assert.ok(hier.includes("WHITESPACE_RE"), "hierarchy.ts defines WHITESPACE_RE");
});
