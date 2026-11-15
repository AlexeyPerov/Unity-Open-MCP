// Offline GUID resolution in packages: a component whose script lives in a
// package (Library/PackageCache, a file: reference, an embedded folder, a
// built-in package of the Unity install) is not a missing script, and a field
// reference to a package asset (a URP material's shader, a texture) is not a
// missing reference. Every offline reader agrees on that, a GUID that
// resolves nowhere still is missing, and with a package source absent the
// verdict is "unverified", not missing.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm, mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
  readAssetOffline,
  searchAssetsOffline,
  findReferencesOffline,
  dependenciesOffline,
  scanIntegrityOffline,
} from "../offline.js";
import { discoverPackageSources, findPackageAssets } from "./package-index.js";
import { AssetModelCache } from "../compressible-router.js";

const G_CACHE_CS = "aaaa0000000000000000000000000001"; // Library/PackageCache/com.example.ui@1a2b3c/Runtime/FancyImage.cs
const G_CACHE_DLL = "aaaa0000000000000000000000000002"; // Library/PackageCache/com.example.ui@1a2b3c/Plugins/Native.dll
const G_LOCAL = "aaaa0000000000000000000000000003"; // file:../../LocalPkg → Runtime/Widget.cs
const G_EMBEDDED = "aaaa0000000000000000000000000004"; // Packages/com.example.embedded/Scripts/Thing.cs
const G_BOGUS = "dead0000000000000000000000000bad"; // resolves nowhere (a copy sits in an ignored Samples~ folder)
const G_FONT = "0000000000000000e000000000000000"; // Unity built-in resources
const G_SHADER = "cccc0000000000000000000000000001"; // Library/PackageCache/com.example.ui@1a2b3c/Shaders/Fancy.shader
const G_TEXTURE = "cccc0000000000000000000000000002"; // Library/PackageCache/com.example.ui@1a2b3c/Textures/Noise.png
const G_LOCAL_MAT = "cccc0000000000000000000000000003"; // file:../../LocalPkg → Materials/Base.mat
const G_BOGUS_FIELD = "dead0000000000000000000000000f1d"; // resolves nowhere (a copy sits in an ignored Samples~ folder)
const SKIN = "Assets/UI/Skin.mat";

function meta(guid: string): string {
  return `fileFormatVersion: 2\nguid: ${guid}\nMonoImporter:\n  serializedVersion: 2\n`;
}

function monoBehaviour(id: number, guid: string, extra = ""): string {
  return `--- !u!114 &${id}
MonoBehaviour:
  m_GameObject: {fileID: 100}
  m_Enabled: 1
  m_Script: {fileID: 11500000, guid: ${guid}, type: 3}
${extra}`;
}

const PANEL = `%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &100
GameObject:
  m_Component:
  - component: {fileID: 200}
  - component: {fileID: 301}
  - component: {fileID: 302}
  - component: {fileID: 303}
  - component: {fileID: 304}
  - component: {fileID: 305}
  m_Name: Panel
--- !u!224 &200
RectTransform:
  m_GameObject: {fileID: 100}
  m_Father: {fileID: 0}
${monoBehaviour(301, G_CACHE_CS, `  m_Font: {fileID: 10102, guid: ${G_FONT}, type: 0}\n`)}${monoBehaviour(302, G_CACHE_DLL)}${monoBehaviour(303, G_LOCAL)}${monoBehaviour(304, G_EMBEDDED)}${monoBehaviour(305, G_BOGUS)}`;

// A material in Assets/ whose fields point into packages, plus one bogus GUID.
const SKIN_MAT = `%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  m_Name: Skin
  m_Shader: {fileID: 4800000, guid: ${G_SHADER}, type: 3}
  m_Parent: {fileID: 2100000, guid: ${G_LOCAL_MAT}, type: 2}
  m_SavedProperties:
    m_TexEnvs:
    - _BaseMap:
        m_Texture: {fileID: 2800000, guid: ${G_TEXTURE}, type: 3}
    - _DetailMap:
        m_Texture: {fileID: 2800000, guid: ${G_BOGUS_FIELD}, type: 3}
`;

/**
 * <tmp>/Project — Assets/UI/Panel.prefab, Assets/UI/Skin.mat, Packages/manifest.json + embedded
 * package, Library/PackageCache/com.example.ui@1a2b3c; <tmp>/LocalPkg — a
 * file: package outside the project (like demo's file:../../packages/bridge).
 */
async function fixture(opts: { library?: boolean } = {}): Promise<{ tmp: string; project: string }> {
  const tmp = await mkdtemp(join(tmpdir(), "offline-pkg-"));
  const project = join(tmp, "Project");
  await mkdir(join(project, "Assets", "UI"), { recursive: true });
  await writeFile(join(project, "Assets", "UI", "Panel.prefab"), PANEL);
  await writeFile(join(project, "Assets", "UI", "Panel.prefab.meta"), meta("bbbb0000000000000000000000000001"));
  await writeFile(join(project, SKIN), SKIN_MAT);
  await writeFile(join(project, SKIN + ".meta"), meta("bbbb0000000000000000000000000002"));

  await mkdir(join(project, "Packages", "com.example.embedded", "Scripts"), { recursive: true });
  await writeFile(join(project, "Packages", "manifest.json"), JSON.stringify({
    dependencies: {
      "com.example.ui": "1.2.0",
      "com.example.local": "file:../../LocalPkg",
    },
  }));
  await writeFile(join(project, "Packages", "com.example.embedded", "package.json"), JSON.stringify({ name: "com.example.embedded" }));
  await writeFile(join(project, "Packages", "com.example.embedded", "Scripts", "Thing.cs"), "class Thing {}");
  await writeFile(join(project, "Packages", "com.example.embedded", "Scripts", "Thing.cs.meta"), meta(G_EMBEDDED));

  await mkdir(join(tmp, "LocalPkg", "Runtime"), { recursive: true });
  await writeFile(join(tmp, "LocalPkg", "package.json"), JSON.stringify({ name: "com.example.local" }));
  await writeFile(join(tmp, "LocalPkg", "Runtime", "Widget.cs"), "class Widget {}");
  await writeFile(join(tmp, "LocalPkg", "Runtime", "Widget.cs.meta"), meta(G_LOCAL));
  await mkdir(join(tmp, "LocalPkg", "Materials"), { recursive: true });
  await writeFile(join(tmp, "LocalPkg", "Materials", "Base.mat.meta"), meta(G_LOCAL_MAT));

  if (opts.library !== false) {
    const pkg = join(project, "Library", "PackageCache", "com.example.ui@1a2b3c");
    await mkdir(join(pkg, "Runtime"), { recursive: true });
    await mkdir(join(pkg, "Plugins"), { recursive: true });
    await mkdir(join(pkg, "Samples~", "Demo"), { recursive: true });
    await writeFile(join(pkg, "package.json"), JSON.stringify({ name: "com.example.ui" }));
    await writeFile(join(pkg, "Runtime", "FancyImage.cs"), "class FancyImage {}");
    await writeFile(join(pkg, "Runtime", "FancyImage.cs.meta"), meta(G_CACHE_CS));
    await writeFile(join(pkg, "Plugins", "Native.dll.meta"), meta(G_CACHE_DLL));
    // Unity ignores folders ending in ~: a GUID only there is not a package asset.
    await writeFile(join(pkg, "Samples~", "Demo", "Bogus.cs.meta"), meta(G_BOGUS));
    await writeFile(join(pkg, "Samples~", "Demo", "Bogus.mat.meta"), meta(G_BOGUS_FIELD));
    await mkdir(join(pkg, "Shaders"), { recursive: true });
    await mkdir(join(pkg, "Textures"), { recursive: true });
    await writeFile(join(pkg, "Shaders", "Fancy.shader"), "Shader \"Example/Fancy\" {}");
    await writeFile(join(pkg, "Shaders", "Fancy.shader.meta"), meta(G_SHADER));
    await writeFile(join(pkg, "Textures", "Noise.png.meta"), meta(G_TEXTURE));
  }
  return { tmp, project };
}

const codes = (issues: Array<{ code: string; detail: string }> | undefined, guid: string) =>
  (issues ?? []).filter((i) => i.detail.includes(guid)).map((i) => i.code);

test("read_asset offline resolves scripts in PackageCache, file: and embedded packages; a bogus GUID stays missing", async () => {
  const { tmp, project } = await fixture();
  try {
    const { model, dependencies } = await readAssetOffline("Assets/UI/Panel.prefab", { fieldLimit: 0, projectRoot: project });
    for (const g of [G_CACHE_CS, G_CACHE_DLL, G_LOCAL, G_EMBEDDED, G_FONT])
      assert.deepEqual(codes(model.integrity, g), [], `${g} resolves: ${JSON.stringify(model.integrity)}`);
    assert.deepEqual(codes(model.integrity, G_BOGUS), ["missing_script_reference"]);
    assert.equal(model.integrity!.find((i) => i.detail.includes(G_BOGUS))!.severity, "error");

    const deps = new Map(dependencies);
    assert.equal(deps.get(G_CACHE_CS), "Packages/com.example.ui/Runtime/FancyImage.cs");
    assert.equal(deps.get(G_CACHE_DLL), "Packages/com.example.ui/Plugins/Native.dll");
    assert.equal(deps.get(G_LOCAL), "Packages/com.example.local/Runtime/Widget.cs");
    assert.equal(deps.get(G_EMBEDDED), "Packages/com.example.embedded/Scripts/Thing.cs");
    assert.equal(deps.get(G_FONT), "Library/unity default resources");
    assert.equal(deps.get(G_BOGUS), null);

    const names = (model.roots[0]?.components ?? []).map((c) => c.name);
    for (const name of ["FancyImage", "Widget", "Thing"]) assert.ok(names.includes(name), `component named ${name}: ${names}`);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("without Library/ a script GUID that resolves nowhere is unverified, not a missing script", async () => {
  const { tmp, project } = await fixture({ library: false });
  try {
    const { model } = await readAssetOffline("Assets/UI/Panel.prefab", { fieldLimit: 0, projectRoot: project });
    // file: and embedded packages are still on disk.
    assert.deepEqual(codes(model.integrity, G_LOCAL), []);
    assert.deepEqual(codes(model.integrity, G_EMBEDDED), []);
    for (const g of [G_CACHE_CS, G_CACHE_DLL, G_BOGUS]) {
      const issue = model.integrity!.find((i) => i.detail.includes(g));
      assert.equal(issue?.code, "unverified_script_reference", JSON.stringify(model.integrity));
      assert.equal(issue?.severity, "warning");
      assert.match(issue!.detail, /com\.example\.ui: Library\/PackageCache does not exist/);
    }
    assert.ok(!model.integrity!.some((i) => i.code === "missing_script_reference"));

    const deps = await dependenciesOffline({ assetPath: "Assets/UI/Panel.prefab", projectRoot: project });
    assert.deepEqual([...(deps.unverifiedForwardGuids ?? [])].sort(), [G_BOGUS, G_CACHE_CS, G_CACHE_DLL].sort());
    assert.ok(!deps.brokenForwardGuids.includes(G_BOGUS));
    assert.match(deps.unverifiedReason ?? "", /Library\/PackageCache/);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("a missing file: package is named in the unverified reason", async () => {
  const { tmp, project } = await fixture();
  try {
    await rm(join(tmp, "LocalPkg"), { recursive: true, force: true });
    const { model } = await readAssetOffline("Assets/UI/Panel.prefab", { fieldLimit: 0, projectRoot: project });
    const issue = model.integrity!.find((i) => i.detail.includes(G_LOCAL));
    assert.equal(issue?.code, "unverified_script_reference");
    assert.match(issue!.detail, /com\.example\.local: file:\.\.\/\.\.\/LocalPkg not found/);
    assert.deepEqual(codes(model.integrity, G_CACHE_CS), []);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("dependencies, scan, find_references and search_assets agree with read_asset offline", async () => {
  const { tmp, project } = await fixture();
  try {
    const deps = await dependenciesOffline({ assetPath: "Assets/UI/Panel.prefab", projectRoot: project });
    assert.deepEqual(deps.brokenForwardGuids, [G_BOGUS]);
    assert.equal(deps.unverifiedForwardGuids, undefined);
    const script = deps.forwardDependencies.find((e) => e.guid === G_CACHE_CS);
    assert.equal(script?.assetPath, "Packages/com.example.ui/Runtime/FancyImage.cs");
    assert.equal(script?.resolved, true);

    const scan = await scanIntegrityOffline({ projectRoot: project });
    const panel = scan.issues.filter((i) => i.path === "Assets/UI/Panel.prefab");
    assert.deepEqual(panel.map((i) => i.code), ["missing_script_reference"], JSON.stringify(panel));
    assert.ok(panel[0].detail.includes(G_BOGUS));

    const refs = await findReferencesOffline({ guid: G_LOCAL, projectRoot: project });
    assert.equal(refs.queriedAssetPath, "Packages/com.example.local/Runtime/Widget.cs");
    assert.deepEqual(refs.referencedBy.map((r) => r.assetPath), ["Assets/UI/Panel.prefab"]);

    const search = await searchAssetsOffline({ component: "fancyimage", projectRoot: project });
    assert.deepEqual(search.matches.map((m) => m.path), ["Assets/UI/Panel.prefab"]);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("the read_asset cache stays valid for package scripts and notices a package change", async () => {
  const { tmp, project } = await fixture();
  try {
    const path = "Assets/UI/Panel.prefab";
    const result = await readAssetOffline(path, { fieldLimit: 0, projectRoot: project });
    const cache = new AssetModelCache();
    cache.set("k", result.model, 1, result.dependencies, result.scriptGuids);
    assert.ok(await cache.getFresh("k", 1, project, path), "unchanged project: still a hit");

    // The package's script now carries another GUID (a package update).
    await writeFile(
      join(project, "Library", "PackageCache", "com.example.ui@1a2b3c", "Runtime", "FancyImage.cs.meta"),
      meta("cccc0000000000000000000000000009"),
    );
    assert.equal(await cache.getFresh("k", 1, project, path), null, "a moved package script invalidates the model");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("built-in packages resolve from the Unity install's BuiltInPackages folder", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "offline-pkg-builtin-"));
  try {
    const project = join(tmp, "Project");
    await mkdir(join(project, "Packages"), { recursive: true });
    await mkdir(join(project, "Library", "PackageCache"), { recursive: true });
    await writeFile(join(project, "Packages", "manifest.json"), JSON.stringify({ dependencies: { "com.unity.ugui": "1.0.0" } }));
    await writeFile(join(project, "Packages", "packages-lock.json"), JSON.stringify({
      dependencies: { "com.unity.ugui": { version: "1.0.0", depth: 0, source: "builtin", dependencies: {} } },
    }));
    const builtIn = join(tmp, "BuiltInPackages");
    await mkdir(join(builtIn, "com.unity.ugui", "Runtime", "UI", "Core"), { recursive: true });
    await writeFile(join(builtIn, "com.unity.ugui", "Runtime", "UI", "Core", "Image.cs.meta"), meta(G_CACHE_CS));

    const found = await discoverPackageSources(project, { builtInPackagesDir: builtIn });
    assert.deepEqual(found.missing, []);
    const hits = await findPackageAssets(found.sources, { wanted: new Set([G_CACHE_CS]) });
    assert.equal(hits.get(G_CACHE_CS)?.path, "Packages/com.unity.ugui/Runtime/UI/Core/Image.cs");

    const none = await discoverPackageSources(project, { builtInPackagesDir: null });
    assert.equal(none.missing.length, 1);
    assert.match(none.missing[0].why, /built-in package/);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("read_asset offline resolves field GUIDs that point into packages; a bogus one stays missing", async () => {
  const { tmp, project } = await fixture();
  try {
    const { model, dependencies } = await readAssetOffline(SKIN, { fieldLimit: 0, projectRoot: project });
    for (const g of [G_SHADER, G_TEXTURE, G_LOCAL_MAT])
      assert.deepEqual(codes(model.integrity, g), [], `${g} resolves: ${JSON.stringify(model.integrity)}`);
    assert.deepEqual(codes(model.integrity, G_BOGUS_FIELD), ["missing_reference"]);

    const deps = new Map(dependencies);
    assert.equal(deps.get(G_SHADER), "Packages/com.example.ui/Shaders/Fancy.shader");
    assert.equal(deps.get(G_TEXTURE), "Packages/com.example.ui/Textures/Noise.png");
    assert.equal(deps.get(G_LOCAL_MAT), "Packages/com.example.local/Materials/Base.mat");
    assert.equal(deps.get(G_BOGUS_FIELD), null);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("dependencies, scan and find_references resolve package field GUIDs as read_asset does", async () => {
  const { tmp, project } = await fixture();
  try {
    const deps = await dependenciesOffline({ assetPath: SKIN, projectRoot: project });
    assert.deepEqual(deps.brokenForwardGuids, [G_BOGUS_FIELD]);
    assert.equal(deps.unverifiedForwardGuids, undefined);
    const shader = deps.forwardDependencies.find((e) => e.guid === G_SHADER);
    assert.equal(shader?.assetPath, "Packages/com.example.ui/Shaders/Fancy.shader");
    assert.equal(shader?.resolved, true);

    const scan = await scanIntegrityOffline({ projectRoot: project });
    const skin = scan.issues.filter((i) => i.path === SKIN);
    assert.deepEqual(skin.map((i) => i.code), ["missing_reference"], JSON.stringify(skin));
    assert.ok(skin[0].detail.includes(G_BOGUS_FIELD));

    // By GUID: the target path is the package asset.
    const byGuid = await findReferencesOffline({ guid: G_SHADER, projectRoot: project });
    assert.equal(byGuid.queriedAssetPath, "Packages/com.example.ui/Shaders/Fancy.shader");
    assert.deepEqual(byGuid.referencedBy.map((r) => r.assetPath), [SKIN]);
    // By the package asset path (the folder is Library/PackageCache/<name>@<hash>).
    const byPath = await findReferencesOffline({ assetPath: "Packages/com.example.ui/Shaders/Fancy.shader", projectRoot: project });
    assert.equal(byPath.queriedAssetGuid, G_SHADER);
    assert.deepEqual(byPath.referencedBy.map((r) => r.assetPath), [SKIN]);
    const reverse = await dependenciesOffline({ assetPath: "Packages/com.example.local/Materials/Base.mat", projectRoot: project });
    assert.equal(reverse.queriedAssetGuid, G_LOCAL_MAT);
    assert.deepEqual(reverse.reverseDependencies.map((r) => r.assetPath), [SKIN]);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("without Library/ a field GUID that resolves nowhere is unverified, not missing", async () => {
  const { tmp, project } = await fixture({ library: false });
  try {
    const { model } = await readAssetOffline(SKIN, { fieldLimit: 0, projectRoot: project });
    // The file: package is still on disk.
    assert.deepEqual(codes(model.integrity, G_LOCAL_MAT), []);
    for (const g of [G_SHADER, G_TEXTURE, G_BOGUS_FIELD]) {
      const issue = model.integrity!.find((i) => i.detail.includes(g));
      assert.equal(issue?.code, "unverified_reference", JSON.stringify(model.integrity));
      assert.equal(issue?.severity, "warning");
      assert.match(issue!.detail, /com\.example\.ui: Library\/PackageCache does not exist/);
    }

    const deps = await dependenciesOffline({ assetPath: SKIN, projectRoot: project });
    assert.deepEqual([...(deps.unverifiedForwardGuids ?? [])].sort(), [G_BOGUS_FIELD, G_SHADER, G_TEXTURE].sort());
    assert.deepEqual(deps.brokenForwardGuids, []);

    const scan = await scanIntegrityOffline({ projectRoot: project });
    assert.deepEqual(
      scan.issues.filter((i) => i.path === SKIN).map((i) => i.code),
      ["unverified_reference", "unverified_reference", "unverified_reference"],
    );
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("the package walk reads only script metas for scripts and every meta for field GUIDs", async () => {
  const { tmp, project } = await fixture();
  try {
    const { sources } = await discoverPackageSources(project);
    const scriptsOnly = await findPackageAssets(sources, { wanted: new Set([G_SHADER, G_CACHE_CS]) });
    assert.deepEqual([...scriptsOnly.keys()], [G_CACHE_CS]);
    const all = await findPackageAssets(sources, { wanted: new Set([G_SHADER, G_CACHE_CS, G_BOGUS_FIELD]), allMetas: true });
    assert.deepEqual([...all.keys()].sort(), [G_CACHE_CS, G_SHADER].sort());
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("the read_asset cache stays valid for package field references and notices a package change", async () => {
  const { tmp, project } = await fixture();
  try {
    const result = await readAssetOffline(SKIN, { fieldLimit: 0, projectRoot: project });
    const cache = new AssetModelCache();
    cache.set("k", result.model, 1, result.dependencies, result.scriptGuids);
    assert.ok(await cache.getFresh("k", 1, project, SKIN), "unchanged project: still a hit");

    await writeFile(
      join(project, "Library", "PackageCache", "com.example.ui@1a2b3c", "Shaders", "Fancy.shader.meta"),
      meta("cccc0000000000000000000000000009"),
    );
    assert.equal(await cache.getFresh("k", 1, project, SKIN), null, "a moved package shader invalidates the model");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});
