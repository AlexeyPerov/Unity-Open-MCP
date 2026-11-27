import test from "node:test";
import assert from "node:assert/strict";

import { confirmCreatedAfterReload, createdAssetPaths, existingPaths } from "./created-asset-check.js";

const SCENE = "Assets/Fixtures/Playtest/ExploreFixture.unity";
const reloadCut = {
  error: { code: "editor_reloading", message: "'unity_open_mcp_scene_create' ran, but a domain reload started before its response was written." },
  dispatched: true,
  transition: "reloading",
  compile: { generationAtStart: 7 },
};

test("createdAssetPaths reads the target argument of create-type tools only", () => {
  assert.deepEqual(createdAssetPaths("unity_open_mcp_scene_create", { path: SCENE }), [SCENE]);
  assert.deepEqual(createdAssetPaths("unity_open_mcp_prefab_create", { prefab_asset_path: "Assets\\P.prefab" }), ["Assets/P.prefab"]);
  assert.deepEqual(createdAssetPaths("unity_open_mcp_animation_create", { asset_paths: ["Assets/A.anim", "", 3] }), ["Assets/A.anim"]);
  assert.deepEqual(createdAssetPaths("unity_open_mcp_gameobject_create", { name: "X" }), []);
});

test("existingPaths resolves project-relative paths under the project", () => {
  const seen: string[] = [];
  const found = existingPaths("/proj", ["Assets/A.unity", "/abs/B.unity"], p => { seen.push(p); return p === "/proj/Assets/A.unity"; });
  assert.deepEqual([...found], ["Assets/A.unity"]);
  assert.deepEqual(seen, ["/proj/Assets/A.unity", "/abs/B.unity"]);
  // A bare path some tools resolve under Assets/ is found there too.
  assert.deepEqual([...existingPaths("/proj", ["Scenes/S.unity"], p => p === "/proj/Assets/Scenes/S.unity")], ["Scenes/S.unity"]);
});

test("a create cut off by a reload whose asset is now on disk answers ok, not editor_reloading", () => {
  const out = confirmCreatedAfterReload("unity_open_mcp_scene_create", reloadCut, [SCENE], new Set(), new Set([SCENE]));
  assert.ok(out);
  assert.equal(out.isError, false);
  assert.equal(out.body.status, "ok");
  assert.equal(out.body.committed, true);
  assert.deepEqual(out.body.created, [SCENE]);
  const steps = out.body.agentNextSteps as string[];
  assert.match(steps[0], /Do NOT repeat scene_create/);
  assert.match(steps[1], /"after_generation":7/);
});

test("a create cut off by a reload whose asset is missing stays an error that says so", () => {
  const out = confirmCreatedAfterReload("unity_open_mcp_scene_create", reloadCut, [SCENE], new Set(), new Set());
  assert.ok(out);
  assert.equal(out.isError, true);
  assert.deepEqual(out.body.notCreated, [SCENE]);
  assert.match((out.body.agentNextSteps as string[])[0], /did not create/);
  assert.equal((out.body.error as { code: string }).code, "editor_reloading");
});

test("the check does not apply to other answers or to a target that existed before", () => {
  const before = new Set([SCENE]);
  assert.equal(confirmCreatedAfterReload("unity_open_mcp_scene_create", reloadCut, [SCENE], before, before), null);
  assert.equal(confirmCreatedAfterReload("unity_open_mcp_scene_create", { ...reloadCut, dispatched: false }, [SCENE], new Set(), new Set([SCENE])), null);
  assert.equal(confirmCreatedAfterReload("unity_open_mcp_scene_create", { error: { code: "already_exists" } }, [SCENE], new Set(), new Set([SCENE])), null);
});
