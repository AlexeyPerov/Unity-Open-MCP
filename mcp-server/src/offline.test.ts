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
} from "./offline.ts";
import { renderAssetSummary } from "./compression/compact.ts";

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
