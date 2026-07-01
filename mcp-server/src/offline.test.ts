// Tests for the M9 Plan 3 offline asset reader. These prove the T1.1
// acceptance criteria: read hierarchy + components + GUID→path for any
// text-serialized asset without a running Editor, works in CI (no Unity
// license), no Library/ dependency.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm, mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

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
import { renderAssetSummary } from "./compression/compact.js";
import type { AssetModel } from "./compression/asset-model.js";

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
