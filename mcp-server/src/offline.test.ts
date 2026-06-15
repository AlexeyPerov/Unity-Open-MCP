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
