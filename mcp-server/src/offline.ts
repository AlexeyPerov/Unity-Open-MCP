// Offline asset reader — parses text-serialized Unity assets (.prefab/.unity/
// .asset) from disk without a running Editor.
//
// Single runtime module (so it loads cleanly under node --experimental-strip-
// types in tests: no cross-file runtime imports). Type-only contract lives in
// compression/asset-model.ts (stripped at test time).
//
// Exports three entry points used by the compressible-tool router:
//  - readAssetOffline  → AssetModel (for read_asset)
//  - searchAssetsOffline → SearchModel (for search_assets)
//  - listAssetsOffline  → listing result (for list_assets)

import { readFile, readdir, stat } from "node:fs/promises";
import { join, basename, extname, relative, sep } from "node:path";
import type {
  AssetModel,
  AssetComponent,
  HierarchyNode,
  FlatObject,
  ComponentField,
  PrefabOverrideEntry,
  AssetIntegrityIssue,
  SearchModel,
  SearchMatch,
  SearchObjectMatch,
} from "./compression/asset-model.js";

// ===========================================================================
// Native Unity class IDs → names.
// ===========================================================================

const nativeClassNames: Record<number, string> = {
  1: "GameObject",
  4: "Transform",
  20: "Camera",
  23: "MeshRenderer",
  33: "MeshFilter",
  64: "MeshCollider",
  65: "BoxCollider",
  81: "AudioListener",
  82: "AudioSource",
  95: "Animator",
  108: "Light",
  114: "MonoBehaviour",
  115: "MonoScript",
  120: "LineRenderer",
  137: "SkinnedMeshRenderer",
  156: "Terrain",
  198: "ParticleSystem",
  212: "SpriteRenderer",
  222: "CanvasRenderer",
  223: "Canvas",
  224: "RectTransform",
  225: "CanvasGroup",
  329: "VideoPlayer",
  73398921: "VFXRenderer",
};

function nativeClassName(classID: number): string {
  return nativeClassNames[classID] ?? "";
}

// ===========================================================================
// Kind / extension utilities.
// ===========================================================================

const extToKind: Record<string, string> = {
  ".anim": "anim",
  ".asmdef": "asmdef",
  ".asset": "asset",
  ".controller": "controller",
  ".cs": "cs",
  ".mat": "mat",
  ".overrideController": "controller",
  ".physicsMaterial2D": "physics2d",
  ".physicMaterial": "physics",
  ".playable": "playable",
  ".prefab": "prefab",
  ".preset": "preset",
  ".shader": "shader",
  ".shadergraph": "shadergraph",
  ".shadersubgraph": "shadergraph",
  ".spriteatlas": "atlas",
  ".terrainlayer": "terrainlayer",
  ".unity": "scene",
  ".uss": "uss",
  ".uxml": "uxml",
  ".vfx": "vfx",
  ".meta": "meta",
};

// ===========================================================================
// M24 — offline-parseable extension sets.
// YAML_PARSEABLE covers text-serialized Unity YAML; JSON_PARSEABLE covers the
// JSON asset kinds unity-scanner does not handle (our differentiator). Both are
// merged into OFFLINE_PARSEABLE for the read_asset / search / find_references
// gates. LISTABLE stays broader (it never parses, only enumerates files).
// ===========================================================================

const YAML_PARSEABLE_EXTENSIONS = new Set([
  ".prefab", ".unity", ".asset", ".mat", ".controller", ".anim", ".playable",
  ".preset", ".spriteatlas", ".terrainlayer", ".vfx",
]);

const JSON_PARSEABLE_EXTENSIONS = new Set([
  ".asmdef", ".shadergraph", ".shadersubgraph",
]);

const OFFLINE_PARSEABLE_EXTENSIONS = new Set<string>([
  ...YAML_PARSEABLE_EXTENSIONS,
  ...JSON_PARSEABLE_EXTENSIONS,
]);

function offlineParseKind(assetPath: string): "yaml" | "json" | null {
  const dot = assetPath.lastIndexOf(".");
  if (dot < 0) return null;
  const ext = assetPath.slice(dot).toLowerCase();
  if (YAML_PARSEABLE_EXTENSIONS.has(ext)) return "yaml";
  if (JSON_PARSEABLE_EXTENSIONS.has(ext)) return "json";
  return null;
}

const kindAliases: Record<string, string> = {
  prefabs: "prefab",
  scenes: "scene",
  unity: "scene",
  scripts: "cs",
  script: "cs",
  material: "mat",
  materials: "mat",
};

function kindForPath(path: string): string {
  const ext = extname(path);
  if (extToKind[ext]) return extToKind[ext];
  if (ext === "") return "none";
  return ext.slice(1).toLowerCase();
}

function normalizeKind(kind: string): string {
  const trimmed = kind.trim().toLowerCase();
  if (kindAliases[trimmed]) return kindAliases[trimmed];
  return trimmed;
}

function parseKindSet(raw: string): Set<string> {
  const out = new Set<string>();
  for (const part of raw.split(",")) {
    const kind = normalizeKind(part);
    if (kind !== "") out.add(kind);
  }
  return out;
}

const skipDirs = new Set([
  ".git", ".vs", "Library", "Logs", "obj", "Obj",
  "Temp", "Build", "Builds", "UserSettings", "node_modules",
]);

function shouldSkipDir(name: string): boolean {
  return skipDirs.has(name);
}

// ===========================================================================
// YAML parser types.
// ===========================================================================

interface ParsedObject {
  id: string;
  classID: number;
  type: string;
  lines: string[];
  order: number;
  name: string;
  componentIDs: string[];
  gameObjectID: string;
  fatherTransformID: string;
  sourceObjectID: string;
  sourceGUID: string;
  scriptGUID: string;
}

interface HierarchyResult {
  gameObject: ParsedObject;
  children: HierarchyResult[];
  path: string;
  depth: number;
}

interface ParsedAsset {
  path: string;
  kind: string;
  guid: string;
  objects: ParsedObject[];
  byID: Map<string, ParsedObject>;
}

type ScriptIndex = Map<string, string>;
type GUIDIndex = Map<string, string>;

// ===========================================================================
// YAML parser — split YAML into documents, extract key fields per object.
// ===========================================================================

function parseAsset(data: string): ParsedAsset {
  const objects: ParsedObject[] = [];
  const byID = new Map<string, ParsedObject>();
  const lines = data.split("\n");

  let current: ParsedObject | null = null;

  for (const line of lines) {
    const header = parseHeaderLine(line);
    if (header) {
      if (current) finishObject(current, objects, byID);
      current = {
        id: header.id,
        classID: header.classID,
        type: "",
        lines: [],
        order: objects.length,
        name: "",
        componentIDs: [],
        gameObjectID: "",
        fatherTransformID: "",
        sourceObjectID: "",
        sourceGUID: "",
        scriptGUID: "",
      };
      continue;
    }
    if (current === null) continue;
    current.lines.push(line);
    scanObjectTypeLine(current, line);
  }
  if (current) finishObject(current, objects, byID);

  if (objects.length === 0) {
    throw new Error("no Unity YAML objects found");
  }

  applyPrefabNameOverrides(objects);
  return { path: "", kind: "", guid: "", objects, byID };
}

function finishObject(
  o: ParsedObject,
  objects: ParsedObject[],
  byID: Map<string, ParsedObject>,
): void {
  if (o.lines.length > 0) {
    o.name = readScalar(o.lines, "m_Name");
    switch (o.type) {
      case "GameObject":
        o.componentIDs = readComponentIDs(o.lines);
        break;
      case "Transform":
      case "RectTransform":
        o.gameObjectID = readFileIDField(o.lines, "m_GameObject");
        o.fatherTransformID = readFileIDField(o.lines, "m_Father");
        break;
      default:
        o.gameObjectID = readFileIDField(o.lines, "m_GameObject");
        break;
    }
    o.sourceObjectID = readFileIDField(o.lines, "m_CorrespondingSourceObject");
    o.sourceGUID = readGUIDField(o.lines, "m_CorrespondingSourceObject");
    if (o.type === "MonoBehaviour") {
      o.scriptGUID = readGUIDField(o.lines, "m_Script");
    }
  }
  objects.push(o);
  byID.set(o.id, o);
}

// ===========================================================================
// Hierarchy — build GameObject tree from Transform parent links.
// ===========================================================================

function buildHierarchy(asset: ParsedAsset): HierarchyResult[] {
  const transformToGO = new Map<string, string>();
  const goToTransform = new Map<string, string>();
  const parentTransform = new Map<string, string>();

  for (const obj of asset.objects) {
    if (obj.type !== "Transform" && obj.type !== "RectTransform") continue;
    transformToGO.set(obj.id, obj.gameObjectID);
    goToTransform.set(obj.gameObjectID, obj.id);
    parentTransform.set(obj.id, obj.fatherTransformID);
  }

  const gameObjects = asset.objects.filter((o) => o.type === "GameObject");
  const nodes = new Map<string, HierarchyResult>();
  for (const go of gameObjects) {
    nodes.set(go.id, { gameObject: go, children: [], path: "", depth: 0 });
  }

  const hasParent = new Set<string>();
  for (const [goID, transformID] of goToTransform) {
    const node = nodes.get(goID);
    if (!node) continue;
    const parentGO = transformToGO.get(parentTransform.get(transformID) ?? "");
    const parent = parentGO ? nodes.get(parentGO) : undefined;
    if (!parent || parent === node) continue;
    parent.children.push(node);
    hasParent.add(goID);
  }

  const roots: HierarchyResult[] = [];
  for (const go of gameObjects) {
    const node = nodes.get(go.id);
    if (!node || hasParent.has(go.id)) continue;
    roots.push(node);
  }

  sortNodes(roots);
  for (const root of roots) assignNodePath(root, "", 0);
  return roots;
}

function sortNodes(nodes: HierarchyResult[]): void {
  nodes.sort((a, b) => a.gameObject.order - b.gameObject.order);
  for (const node of nodes) sortNodes(node.children);
}

function assignNodePath(
  node: HierarchyResult,
  parent: string,
  depth: number,
): void {
  node.depth = depth;
  let name = node.gameObject.name;
  if (name === "") name = `<unnamed:${node.gameObject.id}>`;
  node.path = parent === "" ? name : `${parent}/${name}`;
  for (const child of node.children) {
    assignNodePath(child, node.path, depth + 1);
  }
}

// ===========================================================================
// Components — resolve component objects for a GameObject.
// ===========================================================================

function componentsFor(
  asset: ParsedAsset,
  goID: string,
  scriptIndex: ScriptIndex,
): { object: ParsedObject; name: string; scriptPath: string }[] {
  const goObj = asset.byID.get(goID);
  if (!goObj) return [];

  const out: { object: ParsedObject; name: string; scriptPath: string }[] = [];
  const seen = new Set<string>();

  for (const compID of goObj.componentIDs) {
    const compObj = asset.byID.get(compID);
    if (!compObj) continue;
    seen.add(compObj.id);
    const { name, scriptPath } = componentName(compObj, scriptIndex);
    out.push({ object: compObj, name, scriptPath });
  }

  for (const compObj of asset.objects) {
    if (compObj.gameObjectID !== goID || seen.has(compObj.id) || compObj.type === "GameObject") continue;
    seen.add(compObj.id);
    const { name, scriptPath } = componentName(compObj, scriptIndex);
    out.push({ object: compObj, name, scriptPath });
  }

  return out;
}

function componentName(
  obj: ParsedObject,
  scriptIndex: ScriptIndex,
): { name: string; scriptPath: string } {
  if (obj.type === "MonoBehaviour") {
    const path = scriptIndex.get(obj.scriptGUID);
    if (path) {
      const base = basename(path);
      const ext = extname(path);
      const name = ext ? base.slice(0, -ext.length) : base;
      return { name, scriptPath: path };
    }
    if (obj.scriptGUID !== "") {
      return { name: `MonoBehaviour(${shortGUID(obj.scriptGUID)})`, scriptPath: "" };
    }
  }
  const native = nativeClassName(obj.classID);
  if (native) return { name: native, scriptPath: "" };
  if (obj.type !== "") return { name: obj.type, scriptPath: "" };
  return { name: `ClassID:${obj.classID}`, scriptPath: "" };
}

// ===========================================================================
// Fields — extract top-level serialized fields.
// ===========================================================================

const skipFieldNames = new Set([
  "m_Name", "m_ObjectHideFlags", "m_CorrespondingSourceObject",
  "m_PrefabInstance", "m_PrefabAsset", "serializedVersion",
  "m_GameObject", "m_Script", "m_Enabled",
  "m_EditorHideFlags", "m_EditorClassIdentifier", "m_Modification",
]);

function fieldsWithHidden(
  asset: ParsedAsset,
  obj: ParsedObject,
  limit: number,
  guidIndex: GUIDIndex,
): { fields: { name: string; value: string }[]; hidden: number } {
  const fields: { name: string; value: string }[] = [];
  let hidden = 0;

  for (let i = 0; i < obj.lines.length; i++) {
    const line = obj.lines[i];
    if (!isTopLevelFieldLine(line)) continue;
    const trim = line.trim();
    const colonIdx = trim.indexOf(":");
    if (colonIdx < 0) continue;
    const key = trim.slice(0, colonIdx).trim();
    if (skipFieldNames.has(key)) continue;

    if (limit > 0 && fields.length >= limit) {
      hidden++;
      continue;
    }

    let value = trim.slice(colonIdx + 1).trim();
    if (value === "") {
      value = summarizeNested(obj.lines, i + 1);
    }
    value = resolveReferences(value, guidIndex, asset);
    fields.push({ name: displayFieldName(key), value });
  }

  return { fields, hidden };
}

function displayFieldName(name: string): string {
  if (name.startsWith("<") && name.endsWith(">k__BackingField")) {
    return name.slice(1, -">k__BackingField".length);
  }
  return name;
}

function isTopLevelFieldLine(line: string): boolean {
  return (
    line.startsWith("  ") &&
    !line.startsWith("    ") &&
    !line.startsWith("  -")
  );
}

function summarizeNested(lines: string[], start: number): string {
  const maxParts = 4;
  const parts: string[] = [];
  let parent = "";
  let sequenceIndex = 0;

  for (let i = start; i < lines.length; i++) {
    const line = lines[i];
    if (line.startsWith("  ") && !line.startsWith("    ") && !line.startsWith("  -")) break;
    const trim = line.trim();
    if (trim === "") continue;
    if (trim.startsWith("- ")) {
      let v = trim.slice(2);
      if (parent !== "") {
        v = `${parent}[${sequenceIndex}]: ${v}`;
        sequenceIndex++;
      }
      parts.push(v);
    } else if (trim.endsWith(":")) {
      parent = trim.slice(0, -1);
      sequenceIndex = 0;
      continue;
    } else {
      parts.push(trim.split(/\s+/).join(" "));
    }
    if (parts.length >= maxParts) break;
  }

  if (parts.length === 0) return "<object>";
  return parts.join(" | ");
}

// ===========================================================================
// Reference resolution.
// ===========================================================================

function resolveReferences(
  value: string,
  guidIndex: GUIDIndex,
  asset: ParsedAsset,
): string {
  const guids = findGUIDs(value);
  if (guids.length > 0) {
    const refs: string[] = [];
    const seen = new Set<string>();
    for (const guid of guids) {
      const path = guidIndex.get(guid);
      if (!path || seen.has(path)) continue;
      seen.add(path);
      const name = basename(path);
      const ext = extname(name);
      refs.push(ext ? name.slice(0, -ext.length) : name);
    }
    if (refs.length > 0) return `${value} -> ${refs.join(", ")}`;
  }

  if (guids.length === 0) {
    const refs: string[] = [];
    const seen = new Set<string>();
    for (const fileID of findFileIDs(value)) {
      const label = localReferenceLabel(asset, fileID);
      if (label === "" || seen.has(label)) continue;
      seen.add(label);
      refs.push(label);
    }
    if (refs.length > 0) return `${value} -> ${refs.join(", ")}`;
  }

  return value;
}

function localReferenceLabel(
  asset: ParsedAsset,
  fileID: string,
  scriptIndex: ScriptIndex = new Map(),
): string {
  if (fileID === "" || fileID === "0") return "";
  const obj = asset.byID.get(fileID);
  if (!obj) return "";
  if (obj.type === "GameObject") return objectPath(asset, obj.id);
  const { name } = componentName(obj, scriptIndex);
  if (obj.gameObjectID !== "") {
    const path = objectPath(asset, obj.gameObjectID);
    if (path) return `${name} on ${path}`;
  }
  if (obj.name !== "") return `${name} ${obj.name}`;
  return `${name} ${obj.id}`;
}

function objectPath(asset: ParsedAsset, goID: string): string {
  for (const node of flattenNodes(asset)) {
    if (node.gameObject.id === goID) return node.path;
  }
  const obj = asset.byID.get(goID);
  return obj ? obj.name : "";
}

function flattenNodes(asset: ParsedAsset): HierarchyResult[] {
  const out: HierarchyResult[] = [];
  const walk = (nodes: HierarchyResult[]) => {
    for (const node of nodes) {
      out.push(node);
      walk(node.children);
    }
  };
  walk(buildHierarchy(asset));
  return out;
}

// ===========================================================================
// Prefab override name resolution + override parsing.
// ===========================================================================

function applyPrefabNameOverrides(objects: ParsedObject[]): void {
  const names = new Map<string, string>();
  for (const obj of objects) {
    if (obj.type !== "PrefabInstance") continue;
    for (const override of parsePrefabOverrides(obj.lines)) {
      if (override.kind !== "property" || override.propertyPath !== "m_Name" || override.value === "") continue;
      const id = extractFileID(override.target);
      const guid = extractGUID(override.target);
      if (id === "" || guid === "") continue;
      names.set(`${guid.toLowerCase()}\0${id}`, override.value);
    }
  }
  if (names.size === 0) return;

  for (const obj of objects) {
    if (obj.name !== "" || obj.sourceObjectID === "" || obj.sourceGUID === "") continue;
    const name = names.get(`${obj.sourceGUID.toLowerCase()}\0${obj.sourceObjectID}`);
    if (name) obj.name = name;
  }
}

interface PrefabOverride {
  kind: string;
  target: string;
  propertyPath: string;
  value: string;
  addedObject: string;
}

function parsePrefabOverrides(lines: string[]): PrefabOverride[] {
  const out: PrefabOverride[] = [];
  let section = "";

  for (let i = 0; i < lines.length; i++) {
    const trim = lines[i].trim();
    switch (trim) {
      case "m_Modifications:": section = "modifications"; continue;
      case "m_RemovedComponents:": section = "removed-components"; continue;
      case "m_RemovedGameObjects:": section = "removed-gameobjects"; continue;
      case "m_AddedComponents:": section = "added-components"; continue;
      case "m_AddedGameObjects:": section = "added-gameobjects"; continue;
    }
    if (!trim.startsWith("- ")) continue;

    switch (section) {
      case "modifications": {
        const { override, next } = parsePrefabPropertyOverride(lines, i);
        if (override.target !== "") out.push(override);
        i = next;
        break;
      }
      case "removed-components":
      case "removed-gameobjects": {
        const target = trim.slice(2).trim();
        if (target !== "" && target !== "[]") {
          out.push({ kind: section, target, propertyPath: "", value: "", addedObject: "" });
        }
        break;
      }
      case "added-components": {
        const { override, next } = parsePrefabAddedComponentOverride(lines, i, "added-component");
        if (override.target !== "" || override.addedObject !== "") out.push(override);
        i = next;
        break;
      }
      case "added-gameobjects": {
        const { override, next } = parsePrefabAddedComponentOverride(lines, i, "added-gameobjects");
        if (override.target !== "" || override.addedObject !== "") out.push(override);
        i = next;
        break;
      }
    }
  }
  return out;
}

function parsePrefabPropertyOverride(
  lines: string[],
  start: number,
): { override: PrefabOverride; next: number } {
  const override: PrefabOverride = { kind: "property", target: "", propertyPath: "", value: "", addedObject: "" };
  for (let i = start; i < lines.length; i++) {
    const trim = lines[i].trim();
    if (i > start && isPrefabOverrideSection(trim)) return { override, next: i - 1 };
    if (i > start && trim.startsWith("- ")) return { override, next: i - 1 };
    if (trim.startsWith("- target:")) {
      override.target = trim.slice("- target:".length).trim();
    } else if (trim.startsWith("propertyPath:")) {
      override.propertyPath = displayFieldName(cleanScalar(trim.slice("propertyPath:".length).trim()));
    } else if (trim.startsWith("value:")) {
      override.value = cleanScalar(trim.slice("value:".length).trim());
    } else if (trim.startsWith("objectReference:") && override.value === "") {
      override.value = trim.slice("objectReference:".length).trim();
    }
  }
  return { override, next: lines.length - 1 };
}

function parsePrefabAddedComponentOverride(
  lines: string[],
  start: number,
  kind: string,
): { override: PrefabOverride; next: number } {
  const override: PrefabOverride = { kind, target: "", propertyPath: "", value: "", addedObject: "" };
  for (let i = start; i < lines.length; i++) {
    const trim = lines[i].trim();
    if (i > start && isPrefabOverrideSection(trim)) return { override, next: i - 1 };
    if (i > start && trim.startsWith("- ")) return { override, next: i - 1 };
    if (trim.startsWith("- targetCorrespondingSourceObject:")) {
      override.target = trim.slice("- targetCorrespondingSourceObject:".length).trim();
    } else if (trim.startsWith("addedObject:")) {
      override.addedObject = trim.slice("addedObject:".length).trim();
    }
  }
  return { override, next: lines.length - 1 };
}

function isPrefabOverrideSection(trim: string): boolean {
  switch (trim) {
    case "m_Modifications:":
    case "m_RemovedComponents:":
    case "m_RemovedGameObjects:":
    case "m_AddedComponents:":
    case "m_AddedGameObjects:":
      return true;
    default:
      return false;
  }
}

// ===========================================================================
// Prefab override collection — resolve raw overrides to readable labels.
// ===========================================================================

function buildSourceMap(parsed: ParsedAsset): Map<string, ParsedObject> {
  const map = new Map<string, ParsedObject>();
  for (const obj of parsed.objects) {
    if (obj.sourceObjectID === "" || obj.sourceGUID === "") continue;
    map.set(`${obj.sourceGUID.toLowerCase()}\0${obj.sourceObjectID}`, obj);
  }
  return map;
}

function collectOverrides(
  parsed: ParsedAsset,
  scriptIndex: ScriptIndex,
  guidIndex: GUIDIndex,
): PrefabOverrideEntry[] {
  const sourceMap = buildSourceMap(parsed);
  const entries: PrefabOverrideEntry[] = [];

  for (const obj of parsed.objects) {
    if (obj.type !== "PrefabInstance") continue;
    for (const ov of parsePrefabOverrides(obj.lines)) {
      entries.push(resolveOverrideEntry(ov, parsed, sourceMap, scriptIndex, guidIndex));
    }
  }

  return entries;
}

function resolveOverrideEntry(
  ov: PrefabOverride,
  parsed: ParsedAsset,
  sourceMap: Map<string, ParsedObject>,
  scriptIndex: ScriptIndex,
  guidIndex: GUIDIndex,
): PrefabOverrideEntry {
  const entry: PrefabOverrideEntry = { kind: ov.kind as PrefabOverrideEntry["kind"] };

  switch (ov.kind) {
    case "property":
      entry.propertyPath = ov.propertyPath;
      entry.value = normalizeOverrideValue(ov.value, guidIndex, parsed);
      entry.target = resolveOverrideTarget(ov.target, sourceMap, parsed, scriptIndex);
      break;
    case "added-component":
    case "added-gameobjects":
      entry.target = resolveOverrideTarget(ov.target, sourceMap, parsed, scriptIndex);
      entry.addedObject = resolveAddedObject(ov.addedObject, parsed, scriptIndex);
      break;
    default:
      entry.target = resolveOverrideTarget(ov.target, sourceMap, parsed, scriptIndex);
      break;
  }

  return entry;
}

function normalizeOverrideValue(
  value: string,
  guidIndex: GUIDIndex,
  parsed: ParsedAsset,
): string {
  const trimmed = value.trim();
  if (trimmed === "{fileID: 0}" || trimmed === "{}") return "null";
  return resolveReferences(value, guidIndex, parsed);
}

function resolveOverrideTarget(
  target: string,
  sourceMap: Map<string, ParsedObject>,
  parsed: ParsedAsset,
  scriptIndex: ScriptIndex,
): string | undefined {
  if (target === "") return undefined;
  const fileID = extractFileID(target);
  const guid = extractGUID(target);
  if (fileID === "" && guid === "") return undefined;

  if (guid !== "" && fileID !== "") {
    const localObj = sourceMap.get(`${guid.toLowerCase()}\0${fileID}`);
    if (localObj) {
      return labelForParsedObject(localObj, parsed, scriptIndex);
    }
  }

  if (fileID !== "") {
    const label = localReferenceLabel(parsed, fileID, scriptIndex);
    if (label) return label;
  }

  return undefined;
}

function resolveAddedObject(
  addedObject: string,
  parsed: ParsedAsset,
  scriptIndex: ScriptIndex,
): string | undefined {
  if (addedObject === "") return undefined;
  const fileID = extractFileID(addedObject);
  if (fileID === "") return undefined;
  const label = localReferenceLabel(parsed, fileID, scriptIndex);
  return label || undefined;
}

function labelForParsedObject(
  obj: ParsedObject,
  parsed: ParsedAsset,
  scriptIndex: ScriptIndex,
): string {
  if (obj.type === "GameObject") return objectPath(parsed, obj.id);
  const { name } = componentName(obj, scriptIndex);
  if (obj.gameObjectID !== "") {
    const path = objectPath(parsed, obj.gameObjectID);
    if (path) return `${name} on ${path}`;
  }
  if (obj.name !== "") return `${name} ${obj.name}`;
  return `${name} ${obj.id}`;
}

// ===========================================================================
// Field-level GUID scanning.
// ===========================================================================

function fieldGUIDs(obj: ParsedObject): Set<string> {
  const guids = new Set<string>();
  for (const line of obj.lines) {
    if (!line.includes("guid:") || line.includes("m_Script")) continue;
    for (const guid of findGUIDs(line)) guids.add(guid);
  }
  return guids;
}

function scriptGUIDs(asset: ParsedAsset): Set<string> {
  const guids = new Set<string>();
  for (const obj of asset.objects) {
    if (obj.scriptGUID !== "") guids.add(obj.scriptGUID);
  }
  return guids;
}

// ===========================================================================
// Line-scanning helpers.
// ===========================================================================

function scanObjectTypeLine(obj: ParsedObject, line: string): boolean {
  if (obj.type !== "" || line.length <= 1 || line[0] === " " || !line.endsWith(":")) {
    return false;
  }
  obj.type = line.slice(0, -1).trim();
  return true;
}

// ===========================================================================
// Low-level extraction primitives.
// ===========================================================================

interface HeaderInfo { classID: number; id: string; }

function parseHeaderLine(line: string): HeaderInfo | null {
  const prefix = "--- !u!";
  if (!line.startsWith(prefix)) return null;
  const rest = line.slice(prefix.length);
  const space = rest.indexOf(" ");
  if (space <= 0 || space + 2 > rest.length || rest[space + 1] !== "&") return null;
  const classID = parsePositiveInt(rest.slice(0, space));
  if (classID === null) return null;
  let id = rest.slice(space + 2);
  const fields = id.split(/\s+/);
  if (fields.length > 0) id = fields[0];
  if (id === "") return null;
  return { classID, id };
}

function parsePositiveInt(value: string): number | null {
  if (value.length === 0) return null;
  let out = 0;
  for (const ch of value) {
    if (ch < "0" || ch > "9") return null;
    out = out * 10 + (ch.charCodeAt(0) - "0".charCodeAt(0));
  }
  return out;
}

function readScalar(lines: string[], key: string): string {
  const prefix = `  ${key}:`;
  for (const line of lines) {
    if (line.startsWith(prefix)) return cleanScalar(line.slice(prefix.length).trim());
  }
  return "";
}

function readFileIDField(lines: string[], key: string): string {
  const prefix = `  ${key}:`;
  for (const line of lines) {
    if (line.startsWith(prefix)) return extractFileID(line);
  }
  return "";
}

function readGUIDField(lines: string[], key: string): string {
  const prefix = `  ${key}:`;
  for (const line of lines) {
    if (line.startsWith(prefix)) return extractGUID(line);
  }
  return "";
}

function readComponentIDs(lines: string[]): string[] {
  const ids: string[] = [];
  for (const line of lines) {
    if (line.includes("- component:")) {
      const id = extractFileID(line);
      if (id !== "" && id !== "0") ids.push(id);
    }
  }
  return ids;
}

function extractFileID(line: string): string {
  const start = line.indexOf("fileID:");
  if (start < 0) return "";
  let i = start + "fileID:".length;
  while (i < line.length && line[i] === " ") i++;
  const begin = i;
  if (i < line.length && line[i] === "-") i++;
  while (i < line.length && line[i] >= "0" && line[i] <= "9") i++;
  if (i === begin || (line[begin] === "-" && i === begin + 1)) return "";
  return line.slice(begin, i);
}

function extractGUID(line: string): string {
  const start = line.indexOf("guid:");
  if (start < 0) return "";
  return scanGUID(line.slice(start + "guid:".length));
}

function findGUIDs(value: string): string[] {
  const out: string[] = [];
  let offset = 0;
  while (offset < value.length) {
    const start = value.indexOf("guid:", offset);
    if (start < 0) break;
    offset = start + "guid:".length;
    const guid = scanGUID(value.slice(offset));
    if (guid !== "") {
      out.push(guid);
      offset += guid.length;
    }
  }
  return out;
}

function findFileIDs(value: string): string[] {
  const out: string[] = [];
  let offset = 0;
  while (offset < value.length) {
    const start = value.indexOf("fileID:", offset);
    if (start < 0) break;
    offset = start + "fileID:".length;
    while (offset < value.length && value[offset] === " ") offset++;
    const begin = offset;
    if (offset < value.length && value[offset] === "-") offset++;
    while (offset < value.length && value[offset] >= "0" && value[offset] <= "9") offset++;
    if (offset === begin || (value[begin] === "-" && offset === begin + 1)) continue;
    out.push(value.slice(begin, offset));
  }
  return out;
}

function scanGUID(value: string): string {
  let i = 0;
  while (i < value.length && value[i] === " ") i++;
  const start = i;
  while (i < value.length && isHex(value.charCodeAt(i))) i++;
  if (i === start) return "";
  return value.slice(start, i).toLowerCase();
}

function isHex(code: number): boolean {
  return (
    (code >= 48 && code <= 57) ||
    (code >= 97 && code <= 102) ||
    (code >= 65 && code <= 70)
  );
}

function cleanScalar(value: string): string {
  value = value.trim();
  if (value.length >= 2 && value[0] === '"' && value[value.length - 1] === '"') {
    try {
      return JSON.parse(value);
    } catch {
      return value.slice(1, -1);
    }
  }
  return value;
}

function shortGUID(guid: string): string {
  if (guid.length <= 8) return guid;
  return guid.slice(0, 8);
}

// ===========================================================================
// GUID index builder — reads .meta files.
// ===========================================================================

function readMetaGUID(data: string): string {
  let start: number;
  if (data.startsWith("guid:")) {
    start = "guid:".length;
  } else {
    const idx = data.indexOf("\nguid:");
    if (idx < 0) return "";
    start = idx + 1 + "guid:".length;
  }
  const end = data.indexOf("\n", start);
  const slice = end < 0 ? data.slice(start) : data.slice(start, end);
  return slice.trim();
}

function toAssetPath(projectRoot: string, absPath: string): string {
  let rel = relative(projectRoot, absPath);
  if (sep === "\\") rel = rel.replace(/\\/g, "/");
  return rel;
}

async function buildGUIDIndex(
  projectRoot: string,
  wanted?: Set<string>,
): Promise<GUIDIndex> {
  const index: GUIDIndex = new Map();
  if (wanted && wanted.size === 0) return index;
  const assetsDir = join(projectRoot, "Assets");
  await walkMeta(assetsDir, async (metaPath) => {
    try {
      const data = await readFile(metaPath, "utf-8");
      let guid = readMetaGUID(data);
      if (guid === "") return;
      guid = guid.toLowerCase();
      if (wanted && !wanted.has(guid)) return;
      index.set(guid, toAssetPath(projectRoot, metaPath.slice(0, -5)));
    } catch { /* skip */ }
  });
  return index;
}

async function buildScriptIndex(
  projectRoot: string,
  wanted?: Set<string>,
): Promise<ScriptIndex> {
  const index: ScriptIndex = new Map();
  if (wanted && wanted.size === 0) return index;
  const guidIndex = await buildGUIDIndex(projectRoot, wanted);
  for (const [guid, path] of guidIndex) {
    if (path.endsWith(".cs")) index.set(guid, path);
  }
  return index;
}

async function buildScriptIndexForQuery(
  projectRoot: string,
  query: string,
): Promise<ScriptIndex> {
  const index: ScriptIndex = new Map();
  const q = query.trim().toLowerCase();
  if (q === "") return index;
  const assetsDir = join(projectRoot, "Assets");
  await walkMeta(assetsDir, async (metaPath) => {
    if (!metaPath.endsWith(".cs.meta")) return;
    const scriptPath = metaPath.slice(0, -5);
    const base = basename(scriptPath);
    const ext = extname(base);
    const name = ext ? base.slice(0, -ext.length) : base;
    if (!name.toLowerCase().includes(q)) return;
    try {
      const data = await readFile(metaPath, "utf-8");
      const guid = readMetaGUID(data);
      if (guid === "") return;
      index.set(guid.toLowerCase(), toAssetPath(projectRoot, scriptPath));
    } catch { /* skip */ }
  });
  return index;
}

async function walkMeta(
  dir: string,
  fn: (metaPath: string) => Promise<void>,
): Promise<void> {
  let entries: string[];
  try { entries = await readdir(dir); } catch { return; }
  await Promise.all(entries.map(async (name) => {
    if (shouldSkipDir(name)) return;
    const fullPath = join(dir, name);
    try {
      const s = await stat(fullPath);
      if (s.isDirectory()) await walkMeta(fullPath, fn);
      else if (name.endsWith(".meta")) await fn(fullPath);
    } catch { /* skip */ }
  }));
}

// ===========================================================================
// readAssetOffline — parse a text-serialized asset → AssetModel.
// ===========================================================================

export function isOfflineAsset(assetPath: string): boolean {
  return offlineParseKind(assetPath) !== null;
}

export interface OfflineReadResult {
  model: AssetModel;
  source: "offline";
}

/**
 * M24 — full-hierarchy parity helper. The offline `objectCount` counts every
 * YAML object in the file; the live `read_asset` bridge counts only GameObjects
 * + the components attached to them (it walks the loaded Transform tree, not
 * the raw YAML). This helper returns the live-comparable counts so an
 * acceptance test can verify the offline-reconstructed tree matches a live read
 * node-for-node and component-for-component, without depending on the bridge.
 *
 * Returns `{ nodes, components }` where `nodes` is the GameObject count and
 * `components` is the sum of components across the tree — the two numbers the
 * live `ReadAssetTool` emits as `objectCount` (nodes+components) and
 * `componentCount`.
 */
export function countHierarchy(model: AssetModel): { nodes: number; components: number } {
  let nodes = 0;
  let components = 0;
  const walk = (list: HierarchyNode[]): void => {
    for (const node of list) {
      nodes++;
      components += node.components.length;
      walk(node.children);
    }
  };
  walk(model.roots);
  return { nodes, components };
}

export async function readAssetOffline(
  assetPath: string,
  opts: { fieldLimit: number; projectRoot: string },
): Promise<OfflineReadResult> {
  const absPath = join(opts.projectRoot, assetPath);
  const data = await readFile(absPath, "utf-8");
  const kind = kindForPath(assetPath);
  const guid = await safeReadMetaGUID(absPath + ".meta");
  const parseKind = offlineParseKind(assetPath);

  // M24 — JSON asset kinds (.asmdef/.shadergraph/.shadersubgraph/.vfx) parse
  // through a separate path; unity-scanner handles YAML only, so this is the
  // coverage differentiator. vfx is YAML+binary, but its object headers + GUID
  // refs still parse via the YAML path (binary blobs are skipped).
  if (parseKind === "json") {
    const guidIndex = await buildGUIDIndex(opts.projectRoot, new Set(findGUIDs(data)));
    const model = buildJsonAssetModel(data, assetPath, kind, guid, guidIndex, opts.fieldLimit);
    return { model, source: "offline" };
  }

  const parsed = parseAsset(data);
  parsed.path = assetPath;
  parsed.kind = kind;
  parsed.guid = guid;

  const wantedScripts = scriptGUIDs(parsed);
  const scriptIndex = await buildScriptIndex(opts.projectRoot, wantedScripts);
  const wantedGUIDs = collectFieldGUIDs(parsed);
  const guidIndex = await buildGUIDIndex(opts.projectRoot, wantedGUIDs);
  for (const [g, p] of scriptIndex) guidIndex.set(g, p);

  const integrity = runYamlIntegrityChecks(parsed, guidIndex);
  return {
    model: { ...buildAssetModel(parsed, scriptIndex, guidIndex, opts.fieldLimit), ...(integrity ? { integrity } : {}) },
    source: "offline",
  };
}

async function safeReadMetaGUID(metaPath: string): Promise<string> {
  try {
    return readMetaGUID(await readFile(metaPath, "utf-8"));
  } catch { return ""; }
}

function collectFieldGUIDs(asset: ParsedAsset): Set<string> {
  const guids = new Set<string>();
  for (const obj of asset.objects) {
    for (const line of obj.lines) {
      if (!line.includes("guid:") || line.includes("m_Script")) continue;
      for (const g of findGUIDs(line)) guids.add(g);
    }
  }
  return guids;
}

function buildAssetModel(
  parsed: ParsedAsset,
  scriptIndex: ScriptIndex,
  guidIndex: GUIDIndex,
  fieldLimit: number,
): AssetModel {
  const hierarchy = buildHierarchy(parsed);
  const overrides = collectOverrides(parsed, scriptIndex, guidIndex);

  if (hierarchy.length > 0) {
    const roots: HierarchyNode[] = hierarchy.map((node) =>
      convertNode(node, parsed, scriptIndex, guidIndex, fieldLimit),
    );
    let componentCount = 0;
    const countComps = (n: HierarchyNode) => {
      componentCount += n.components.length;
      n.children.forEach(countComps);
    };
    roots.forEach(countComps);
    return {
      kind: parsed.kind,
      path: parsed.path,
      guid: parsed.guid || undefined,
      objectCount: parsed.objects.length,
      componentCount,
      roots,
      ...(overrides.length > 0 ? { overrides } : {}),
    };
  }

  const flatObjects: FlatObject[] = [];
  for (const obj of parsed.objects) {
    if (obj.type === "PrefabInstance") continue;
    const { name } = componentName(obj, scriptIndex);
    const { fields } = fieldsWithHidden(parsed, obj, fieldLimit, guidIndex);
    flatObjects.push({
      name: obj.name || name,
      type: name,
      fields: fields.length > 0 ? fields.map(toComponentField) : undefined,
    });
  }
  return {
    kind: parsed.kind,
    path: parsed.path,
    guid: parsed.guid || undefined,
    objectCount: parsed.objects.length,
    componentCount: 0,
    roots: [],
    flatObjects,
    ...(overrides.length > 0 ? { overrides } : {}),
  };
}

// ===========================================================================
// M24 — JSON asset parser (.asmdef / .shadergraph / .shadersubgraph).
//
// unity-scanner handles YAML only; this path is the coverage differentiator.
// Each kind has a distinct on-disk shape:
//  - .asmdef  → a single JSON object (assembly-definition manifest).
//  - .shadergraph / .shadersubgraph → a JSON-document stream: multiple pretty-
//    printed JSON objects concatenated on blank-line boundaries, each carrying
//    an "m_Type" discriminator. The first is always the graph root.
// GUID references inside the JSON are resolved against the meta index the same
// way YAML field refs are.
// ===========================================================================

function buildJsonAssetModel(
  data: string,
  assetPath: string,
  kind: string,
  guid: string,
  guidIndex: GUIDIndex,
  fieldLimit: number,
): AssetModel {
  const issues: AssetIntegrityIssue[] = [];
  const objects = parseJsonAsset(data, issues);

  const flatObjects: FlatObject[] = [];
  for (const obj of objects) {
    const fields = jsonFields(obj.value, fieldLimit, guidIndex);
    flatObjects.push({
      name: obj.name,
      type: obj.type,
      fields: fields.length > 0 ? fields : undefined,
    });
  }

  const integrity = runJsonIntegrityChecks(kind, objects, issues, assetPath);
  return {
    kind,
    path: assetPath,
    guid: guid || undefined,
    objectCount: objects.length,
    componentCount: 0,
    roots: [],
    flatObjects,
    ...(integrity.length > 0 ? { integrity } : {}),
  };
}

interface JsonParsedObject {
  /** Display label (m_Name / m_ObjectId / name key, or a generated label). */
  name: string;
  /** Type label (top-level kind, or the m_Type discriminator for shadergraph). */
  type: string;
  /** The parsed JSON value. */
  value: unknown;
}

/**
 * Parse a JSON asset into one or more JsonParsedObjects. `.asmdef` is a single
 * object; `.shadergraph` is a stream of objects. Malformed JSON records an
 * integrity issue instead of throwing — a partial read is more useful to an
 * agent than a hard failure when the bridge is down.
 */
function parseJsonAsset(data: string, issues: AssetIntegrityIssue[]): JsonParsedObject[] {
  const ext = "json";
  const chunks = splitJsonStream(data);
  if (chunks.length === 0) {
    issues.push({ code: "malformed_json", detail: "no JSON objects found", severity: "error" });
    return [];
  }

  const out: JsonParsedObject[] = [];
  for (let i = 0; i < chunks.length; i++) {
    let value: unknown;
    try {
      value = JSON.parse(chunks[i]);
    } catch (e) {
      issues.push({
        code: "malformed_json",
        detail: `object ${i + 1}: ${(e as Error).message}`,
        severity: "error",
      });
      continue;
    }
    out.push(jsonParsedObject(value, i));
  }
  return out;

  function jsonParsedObject(value: unknown, index: number): JsonParsedObject {
    if (value !== null && typeof value === "object" && !Array.isArray(value)) {
      const obj = value as Record<string, unknown>;
      const type =
        typeof obj.m_Type === "string" && obj.m_Type !== ""
          ? jsonShortType(obj.m_Type)
          : "json";
      const name =
        pickString(obj.m_Name) ??
        pickString(obj.m_ObjectId) ??
        pickString(obj.name) ??
        (type !== "json" ? `${type}#${index}` : `object#${index}`);
      return { name, type, value };
    }
    return { name: `value#${index}`, type: ext, value };
  }
}

function pickString(v: unknown): string | undefined {
  return typeof v === "string" && v !== "" ? v : undefined;
}

/** Shorten a ShaderGraph m_Type like "UnityEditor.ShaderGraph.MultiplyNode" → "MultiplyNode". */
function jsonShortType(full: string): string {
  const dot = full.lastIndexOf(".");
  return dot >= 0 ? full.slice(dot + 1) : full;
}

/**
 * A `.shadergraph` is a stream of pretty-printed JSON objects. They are
 * separated by blank lines, but a single object's pretty-printed body also
 * contains newlines — so the split must track brace depth, not line gaps.
 * `.asmdef` is a single object; this still returns a one-element array.
 */
function splitJsonStream(data: string): string[] {
  const trimmed = data.trim();
  if (trimmed === "") return [];
  const chunks: string[] = [];
  let depth = 0;
  let inString = false;
  let escape = false;
  let start = -1;

  for (let i = 0; i < trimmed.length; i++) {
    const ch = trimmed[i];
    if (inString) {
      if (escape) escape = false;
      else if (ch === "\\") escape = true;
      else if (ch === '"') inString = false;
      continue;
    }
    if (ch === '"') { inString = true; continue; }
    if (ch === "{") {
      if (depth === 0) start = i;
      depth++;
    } else if (ch === "}") {
      depth--;
      if (depth === 0 && start >= 0) {
        chunks.push(trimmed.slice(start, i + 1));
        start = -1;
      }
    }
  }
  // Fallback: if brace tracking found nothing (e.g. a top-level array or a
  // malformed file), try a single JSON.parse on the whole content so the
  // malformed_json issue path can report it.
  if (chunks.length === 0) chunks.push(trimmed);
  return chunks;
}

/**
 * Flatten a parsed JSON value into display fields. Scalars, arrays, and nested
 * objects are summarized into short `name: value` rows, mirroring the YAML
 * field renderer. GUID refs are resolved against the meta index.
 */
function jsonFields(
  value: unknown,
  limit: number,
  guidIndex: GUIDIndex,
): { name: string; value: string }[] {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return [];
  const obj = value as Record<string, unknown>;
  const out: { name: string; value: string }[] = [];
  for (const [key, raw] of Object.entries(obj)) {
    if (out.length >= limit && limit > 0) break;
    out.push({ name: key, value: jsonScalar(raw, guidIndex) });
  }
  return out;
}

function jsonScalar(value: unknown, guidIndex: GUIDIndex): string {
  if (value === null) return "null";
  switch (typeof value) {
    case "string":
      return resolveReferences(value, guidIndex, EMPTY_PARSED);
    case "number":
    case "boolean":
      return String(value);
    default: {
      if (Array.isArray(value)) return `[${value.length}]`;
      const obj = value as Record<string, unknown>;
      // Inline {fileID, guid, type} PPtr → resolve to asset name.
      if (typeof obj.guid === "string" && obj.guid.length === 32) {
        return resolveReferences(`{fileID: ${obj.fileID ?? 0}, guid: ${obj.guid}, type: ${obj.type ?? 0}}`, guidIndex, EMPTY_PARSED);
      }
      const keys = Object.keys(obj);
      if (keys.length === 0) return "{}";
      return `{${keys.slice(0, 4).join(", ")}${keys.length > 4 ? ", …" : ""}}`;
    }
  }
}

// A ParsedAsset stand-in used only by resolveReferences' local-reference path.
// JSON assets have no in-file fileIDs, so local ref resolution is a no-op.
const EMPTY_PARSED: ParsedAsset = { path: "", kind: "", guid: "", objects: [], byID: new Map() };

// ===========================================================================
// M24 — offline integrity checks. The verify rule suite (M25) consumes these
// signals; here we only emit asset-local findings derivable from raw bytes.
// ===========================================================================

function runYamlIntegrityChecks(parsed: ParsedAsset, guidIndex: GUIDIndex): AssetIntegrityIssue[] {
  const issues: AssetIntegrityIssue[] = [];

  // Orphaned PrefabInstance: a PrefabInstance whose m_SourcePrefab GUID is
  // missing from the project (the base prefab was deleted or never imported).
  for (const obj of parsed.objects) {
    if (obj.type !== "PrefabInstance") continue;
    const sourceGuid = readGUIDField(obj.lines, "m_SourcePrefab");
    if (sourceGuid !== "" && !guidIndex.has(sourceGuid)) {
      issues.push({
        code: "orphaned_prefab_instance",
        detail: `m_SourcePrefab guid ${sourceGuid} not found in project`,
        severity: "warning",
      });
    }
  }

  // Unresolved field GUIDs (missing references) — every guid: in a field that
  // the meta index cannot resolve. Component m_Script guids get their own code
  // so a missing-script is distinguishable from a missing asset ref.
  const missingScripts = new Set<string>();
  const missingRefs = new Set<string>();
  for (const obj of parsed.objects) {
    for (const line of obj.lines) {
      if (!line.includes("guid:")) continue;
      const isScript = line.includes("m_Script");
      for (const g of findGUIDs(line)) {
        if (guidIndex.has(g)) continue;
        if (isScript) missingScripts.add(g);
        else missingRefs.add(g);
      }
    }
  }
  for (const g of missingScripts) {
    issues.push({ code: "missing_script_reference", detail: `script guid ${g} unresolved`, severity: "error" });
  }
  for (const g of missingRefs) {
    issues.push({ code: "missing_reference", detail: `asset guid ${g} unresolved`, severity: "warning" });
  }

  return issues;
}

function runJsonIntegrityChecks(
  kind: string,
  objects: JsonParsedObject[],
  parseIssues: AssetIntegrityIssue[],
  _assetPath: string,
): AssetIntegrityIssue[] {
  const issues = [...parseIssues];

  if (kind === "asmdef") {
    const first = objects[0]?.value;
    if (first && typeof first === "object" && !Array.isArray(first)) {
      const obj = first as Record<string, unknown>;
      if (pickString(obj.name) === undefined) {
        issues.push({ code: "asmdef_missing_name", detail: "asmdef has no 'name' field", severity: "error" });
      }
    }
  }

  if (kind === "shadergraph") {
    // A valid graph stream must start with the GraphData root.
    const rootType = objects[0]?.type;
    if (rootType !== undefined && !rootType.includes("GraphData")) {
      issues.push({
        code: "shadergraph_root_missing",
        detail: `first object is ${rootType}, expected a GraphData root`,
        severity: "warning",
      });
    }
  }

  return issues;
}

function convertNode(
  node: HierarchyResult,
  parsed: ParsedAsset,
  scriptIndex: ScriptIndex,
  guidIndex: GUIDIndex,
  fieldLimit: number,
): HierarchyNode {
  const comps = componentsFor(parsed, node.gameObject.id, scriptIndex);
  const components: AssetComponent[] = comps.map((c) => {
    const comp: AssetComponent = { name: c.name };
    if (c.scriptPath) comp.scriptPath = c.scriptPath;
    if (fieldLimit > 0) {
      const { fields } = fieldsWithHidden(parsed, c.object, fieldLimit, guidIndex);
      if (fields.length > 0) comp.fields = fields.map(toComponentField);
    }
    return comp;
  });
  return {
    name: node.gameObject.name,
    path: node.path,
    depth: node.depth,
    fileID: node.gameObject.id,
    components,
    children: node.children.map((child) =>
      convertNode(child, parsed, scriptIndex, guidIndex, fieldLimit),
    ),
  };
}

function toComponentField(f: { name: string; value: string }): ComponentField {
  return { name: f.name, value: f.value };
}

// ===========================================================================
// searchAssetsOffline — scan Assets/ → SearchModel.
// ===========================================================================

const SEARCHABLE_EXTENSIONS = OFFLINE_PARSEABLE_EXTENSIONS;

export async function searchAssetsOffline(
  opts: {
    name?: string; component?: string; guid?: string; type?: string;
    folder?: string; projectRoot: string; maxResults?: number;
  },
): Promise<SearchModel> {
  const nameQuery = (opts.name ?? "").trim().toLowerCase();
  const componentQuery = (opts.component ?? "").trim().toLowerCase();
  const guidQuery = (opts.guid ?? "").trim().toLowerCase();
  const typeFilter = opts.type ? parseKindSet(opts.type) : null;
  const folder = opts.folder ?? "Assets";
  const maxResults = opts.maxResults ?? 50;
  const searchDir = join(opts.projectRoot, folder);

  const scriptIndex = componentQuery
    ? await buildScriptIndexForQuery(opts.projectRoot, componentQuery)
    : new Map<string, string>();

  const guidIndex = guidQuery
    ? await resolveGUIDPaths(opts.projectRoot, guidQuery)
    : new Map<string, string>();

  const matches: SearchMatch[] = [];
  const files = await collectFiles(searchDir);

  for (const filePath of files) {
    if (matches.length >= maxResults) break;
    const assetPath = toAssetPath(opts.projectRoot, filePath);
    const ext = extname(filePath).toLowerCase();
    if (!SEARCHABLE_EXTENSIONS.has(ext)) continue;
    const kind = kindForPath(filePath);
    if (typeFilter && typeFilter.size > 0 && !typeFilter.has(kind)) continue;

    const guid = await safeReadMetaGUID(filePath + ".meta");

    // M24 — JSON asset kinds route through a content-based match path (they
    // have no GameObject/component tree for the YAML matcher to walk).
    if (offlineParseKind(filePath) === "json") {
      let content: string;
      try { content = await readFile(filePath, "utf-8"); } catch { continue; }
      const match = checkJsonMatch(content, guidIndex, assetPath, kind, guid, {
        nameQuery, componentQuery, guidQuery,
      });
      if (match) matches.push(match);
      continue;
    }

    let parsed: ParsedAsset;
    try { parsed = parseAsset(await readFile(filePath, "utf-8")); } catch { continue; }
    parsed.path = assetPath;
    parsed.kind = kind;

    const match = checkMatch(parsed, scriptIndex, guidIndex, assetPath, kind, guid, { nameQuery, componentQuery, guidQuery });
    if (match) matches.push(match);
  }

  return {
    query: { name: opts.name, component: opts.component, guid: opts.guid, type: opts.type },
    matchCount: matches.length,
    matches,
    truncated: 0,
  };
}

function checkMatch(
  parsed: ParsedAsset,
  scriptIndex: ScriptIndex,
  guidIndex: Map<string, string>,
  assetPath: string,
  kind: string,
  guid: string,
  criteria: { nameQuery: string; componentQuery: string; guidQuery: string },
): SearchMatch | null {
  const reasons: string[] = [];
  const objects: SearchObjectMatch[] = [];
  const { nameQuery, componentQuery, guidQuery } = criteria;
  const fileName = basename(assetPath);

  if (nameQuery && fileName.toLowerCase().includes(nameQuery)) reasons.push("file-name");

  if (guidQuery) {
    if (guid.toLowerCase() === guidQuery) reasons.push("guid");
    let hasFieldGUID = false;
    for (const obj of parsed.objects) {
      if (fieldGUIDs(obj).has(guidQuery)) { hasFieldGUID = true; break; }
    }
    if (hasFieldGUID) reasons.push("guid");
  }

  const hierarchy = buildHierarchy(parsed);

  if (nameQuery) {
    for (const node of flattenHierarchy(hierarchy)) {
      if (node.gameObject.name.toLowerCase().includes(nameQuery)) {
        const comps = componentsFor(parsed, node.gameObject.id, scriptIndex);
        objects.push({ path: node.path, components: comps.map((c) => c.name) });
      }
    }
    if (objects.length > 0 && !reasons.includes("file-name")) reasons.push("gameobject");
  }

  if (componentQuery) {
    let componentMatch = false;
    for (const node of flattenHierarchy(hierarchy)) {
      const comps = componentsFor(parsed, node.gameObject.id, scriptIndex);
      const matching = comps.filter(
        (c) => c.name.toLowerCase().includes(componentQuery) ||
          (c.scriptPath && c.scriptPath.toLowerCase().includes(componentQuery)),
      );
      if (matching.length > 0) {
        componentMatch = true;
        if (!objects.some((o) => o.path === node.path)) {
          objects.push({ path: node.path, components: comps.map((c) => c.name) });
        }
      }
    }
    if (componentMatch && !reasons.includes("component")) reasons.push("component");
  }

  if (!componentQuery && !nameQuery && !guidQuery) reasons.push("file-name");
  if (reasons.length === 0) return null;

  return { path: assetPath, guid: guid || undefined, kind, reasons, objects: objects.length > 0 ? objects : undefined };
}

/**
 * M24 — content-based matcher for JSON asset kinds (.asmdef/.shadergraph).
 * They have no GameObject tree, so matching is by file name, raw-text GUID
 * presence, and content substring (name/component queries search the raw text
 * since JSON is human-readable). This keeps `search_assets` offline-capable for
 * the expanded type set.
 */
function checkJsonMatch(
  content: string,
  guidIndex: Map<string, string>,
  assetPath: string,
  kind: string,
  guid: string,
  criteria: { nameQuery: string; componentQuery: string; guidQuery: string },
): SearchMatch | null {
  const reasons: string[] = [];
  const { nameQuery, componentQuery, guidQuery } = criteria;
  const fileName = basename(assetPath);
  const lower = content.toLowerCase();

  if (nameQuery && fileName.toLowerCase().includes(nameQuery)) reasons.push("file-name");

  if (guidQuery) {
    if (guid.toLowerCase() === guidQuery) reasons.push("guid");
    else if (lower.includes(guidQuery)) reasons.push("guid");
  }

  if (nameQuery && !reasons.includes("file-name") && lower.includes(nameQuery)) {
    reasons.push("gameobject");
  }
  if (componentQuery && lower.includes(componentQuery)) {
    reasons.push("component");
  }

  if (!componentQuery && !nameQuery && !guidQuery) reasons.push("file-name");
  if (reasons.length === 0) return null;

  void guidIndex;
  return { path: assetPath, guid: guid || undefined, kind, reasons };
}

function flattenHierarchy(nodes: HierarchyResult[]): HierarchyResult[] {
  const out: HierarchyResult[] = [];
  const walk = (ns: HierarchyResult[]) => {
    for (const n of ns) { out.push(n); walk(n.children); }
  };
  walk(nodes);
  return out;
}

async function collectFiles(dir: string): Promise<string[]> {
  const results: string[] = [];
  let entries: string[];
  try { entries = await readdir(dir); } catch { return results; }
  for (const name of entries) {
    if (shouldSkipDir(name)) continue;
    const fullPath = join(dir, name);
    try {
      const s = await stat(fullPath);
      if (s.isDirectory()) results.push(...await collectFiles(fullPath));
      else if (!name.endsWith(".meta")) results.push(fullPath);
    } catch { /* skip */ }
  }
  return results;
}

async function resolveGUIDPaths(
  projectRoot: string,
  guidQuery: string,
): Promise<Map<string, string>> {
  const result = new Map<string, string>();
  const assetsDir = join(projectRoot, "Assets");
  await walkMetaForGUIDs(assetsDir, projectRoot, guidQuery, result);
  return result;
}

async function walkMetaForGUIDs(
  dir: string, projectRoot: string, guidQuery: string,
  result: Map<string, string>,
): Promise<void> {
  let entries: string[];
  try { entries = await readdir(dir); } catch { return; }
  for (const name of entries) {
    if (shouldSkipDir(name)) continue;
    const fullPath = join(dir, name);
    try {
      const s = await stat(fullPath);
      if (s.isDirectory()) await walkMetaForGUIDs(fullPath, projectRoot, guidQuery, result);
      else if (name.endsWith(".meta")) {
        const guid = readMetaGUID(await readFile(fullPath, "utf-8")).toLowerCase();
        if (guid === guidQuery) {
          result.set(guid, toAssetPath(projectRoot, fullPath.slice(0, -5)));
        }
      }
    } catch { /* skip */ }
    if (result.size > 0) return;
  }
}

// ===========================================================================
// listAssetsOffline — compressed directory listing.
// ===========================================================================

export interface FolderListing {
  folder: string;
  kinds: Record<string, { count: number; sample: string[] }>;
  fileCount: number;
}

export interface ListAssetsResult {
  root: string;
  typeFilter?: string;
  folders: FolderListing[];
  totalFiles: number;
  totalFolders: number;
  kindSummary: Record<string, number>;
  truncated: number;
}

const LISTABLE_EXTENSIONS = new Set([
  ".prefab", ".unity", ".asset", ".mat", ".controller", ".anim", ".playable",
  ".cs", ".shader", ".spriteatlas", ".physicMaterial", ".physicsMaterial2D",
  ".overrideController", ".uxml", ".uss",
  // M24 — expanded offline-parseable kinds (also listable so they show up in
  // directory listings; listing never parses, so they cost nothing to include).
  ".asmdef", ".shadergraph", ".shadersubgraph", ".vfx", ".preset", ".terrainlayer",
]);

export async function listAssetsOffline(
  opts: {
    folder?: string; type?: string; maxPerFolder?: number;
    projectRoot: string;
  },
): Promise<ListAssetsResult> {
  const folder = opts.folder ?? "Assets";
  const typeFilter = opts.type
    ? new Set(opts.type.split(",").map((t) => normalizeKind(t)).filter((t) => t !== ""))
    : null;
  const searchDir = join(opts.projectRoot, folder);

  const folders: FolderListing[] = [];
  const kindSummary: Record<string, number> = {};
  let totalFiles = 0;
  let truncated = 0;
  const folderMap = new Map<string, FolderListing>();

  await walkFiles(searchDir, opts.projectRoot, async (absPath) => {
    const ext = extname(absPath).toLowerCase();
    if (!LISTABLE_EXTENSIONS.has(ext)) return;
    const kind = kindForPath(absPath);
    if (typeFilter && typeFilter.size > 0 && !typeFilter.has(kind)) return;

    const relDir = relativeDir(opts.projectRoot, absPath);
    const fileName = basename(absPath);
    let listing = folderMap.get(relDir);
    if (!listing) {
      listing = { folder: relDir, kinds: {}, fileCount: 0 };
      folderMap.set(relDir, listing);
      folders.push(listing);
    }
    listing.fileCount++;
    totalFiles++;
    if (!listing.kinds[kind]) listing.kinds[kind] = { count: 0, sample: [] };
    listing.kinds[kind].count++;
    kindSummary[kind] = (kindSummary[kind] ?? 0) + 1;
    const samples = listing.kinds[kind].sample;
    if (samples.length < 5) {
      const nameWithoutExt = ext ? fileName.slice(0, -(ext.length)) : fileName;
      if (!samples.includes(nameWithoutExt)) samples.push(nameWithoutExt);
    } else {
      truncated++;
    }
  });

  folders.sort((a, b) => a.folder.localeCompare(b.folder));
  return {
    root: folder, typeFilter: opts.type, folders,
    totalFiles, totalFolders: folders.length, kindSummary, truncated,
  };
}

function relativeDir(projectRoot: string, absPath: string): string {
  let rel = absPath.slice(projectRoot.length).replace(/\\/g, "/");
  if (rel.startsWith("/")) rel = rel.slice(1);
  const lastSlash = rel.lastIndexOf("/");
  return lastSlash >= 0 ? rel.slice(0, lastSlash) : rel;
}

async function walkFiles(
  dir: string, projectRoot: string,
  fn: (absPath: string) => Promise<void>,
): Promise<void> {
  let entries: string[];
  try { entries = await readdir(dir); } catch { return; }
  for (const name of entries) {
    if (shouldSkipDir(name)) continue;
    if (name.endsWith(".meta")) continue;
    const fullPath = join(dir, name);
    try {
      const s = await stat(fullPath);
      if (s.isDirectory()) await walkFiles(fullPath, projectRoot, fn);
      else await fn(fullPath);
    } catch { /* skip */ }
  }
}

// ===========================================================================
// findReferencesOffline — offline reverse reference lookup (T1.4).
//
// Scans .meta + YAML on disk to find all assets that reference a given target
// (by GUID or asset path). No running Editor required. Output is grouped by
// asset kind and folder, with detail levels and caps to protect token budget.
//
// Algorithm: resolve target GUID → scan all text-serialized files → raw-text
// fast-filter (content.includes(guid)) → optional YAML parse for verbose
// locations → group + cap + collapse.
// ===========================================================================

const REFERENCEABLE_EXTENSIONS = OFFLINE_PARSEABLE_EXTENSIONS;

export interface ReferencedByEntry {
  assetPath: string;
  guid?: string;
  kind: string;
  folder: string;
  /** Verbose-only: field locations where the GUID appears (capped). */
  locations?: string[];
}

export interface CollapsedGroup {
  folder: string;
  count: number;
  kinds: Record<string, number>;
}

export interface FindReferencesOfflineResult {
  queriedAssetPath: string;
  queriedAssetGuid: string;
  referencedBy: ReferencedByEntry[];
  totalCount: number;
  byKind: Record<string, number>;
  byFolder: Record<string, number>;
  collapsedGroups?: CollapsedGroup[];
  detail: string;
  truncated: number;
}

export async function findReferencesOffline(
  opts: {
    assetPath?: string;
    guid?: string;
    detail?: string;
    maxResults?: number;
    maxPerFile?: number;
    patternThreshold?: number;
    projectRoot: string;
  },
): Promise<FindReferencesOfflineResult> {
  const detail = (opts.detail ?? "normal") as "summary" | "normal" | "verbose";
  const maxResults = opts.maxResults ?? 100;
  const maxPerFile = opts.maxPerFile ?? 5;
  const patternThreshold = opts.patternThreshold ?? 0;

  // Resolve target GUID + path.
  let targetGuid = "";
  let targetPath = "";
  if (opts.guid) {
    targetGuid = opts.guid.toLowerCase();
  } else if (opts.assetPath) {
    targetPath = opts.assetPath;
    targetGuid = (await safeReadMetaGUID(
      join(opts.projectRoot, opts.assetPath + ".meta"),
    )).toLowerCase();
  }

  if (targetGuid === "") {
    return {
      queriedAssetPath: targetPath,
      queriedAssetGuid: "",
      referencedBy: [],
      totalCount: 0,
      byKind: {},
      byFolder: {},
      detail,
      truncated: 0,
    };
  }

  // If only GUID was given, resolve target path from .meta index.
  if (targetPath === "") {
    const guidIndex = await buildGUIDIndex(
      opts.projectRoot, new Set([targetGuid]),
    );
    targetPath = guidIndex.get(targetGuid) ?? "";
  }

  // Scan all candidate files in Assets/.
  const candidates = await collectFiles(join(opts.projectRoot, "Assets"));
  const hits: ReferencedByEntry[] = [];

  for (const absPath of candidates) {
    const ext = extname(absPath).toLowerCase();
    if (!REFERENCEABLE_EXTENSIONS.has(ext)) continue;

    let content: string;
    try { content = await readFile(absPath, "utf-8"); } catch { continue; }

    // Fast filter: skip files that don't mention the GUID at all.
    if (!content.includes(targetGuid)) continue;

    const assetPath = toAssetPath(opts.projectRoot, absPath);
    // Skip self-reference.
    if (assetPath === targetPath) continue;

    const kind = kindForPath(absPath);
    const folder = relativeDir(opts.projectRoot, absPath);
    const guid = await safeReadMetaGUID(absPath + ".meta");

    const entry: ReferencedByEntry = {
      assetPath,
      guid: guid || undefined,
      kind,
      folder,
    };

    if (detail === "verbose") {
      entry.locations = extractReferenceLocations(content, targetGuid, maxPerFile);
    }

    hits.push(entry);
  }

  // Group by kind and folder.
  const byKind: Record<string, number> = {};
  const byFolder: Record<string, number> = {};
  for (const hit of hits) {
    byKind[hit.kind] = (byKind[hit.kind] ?? 0) + 1;
    byFolder[hit.folder] = (byFolder[hit.folder] ?? 0) + 1;
  }

  // Pattern collapsing: folders with >= threshold files get collapsed.
  let collapsedGroups: CollapsedGroup[] | undefined;
  let displayHits = hits;

  if (patternThreshold > 0) {
    collapsedGroups = [];
    const collapsedFolders = new Set<string>();
    for (const [folder, count] of Object.entries(byFolder)) {
      if (count < patternThreshold) continue;
      const kinds: Record<string, number> = {};
      for (const hit of hits) {
        if (hit.folder !== folder) continue;
        kinds[hit.kind] = (kinds[hit.kind] ?? 0) + 1;
      }
      collapsedGroups.push({ folder, count, kinds });
      collapsedFolders.add(folder);
    }
    if (collapsedFolders.size > 0) {
      displayHits = hits.filter((h) => !collapsedFolders.has(h.folder));
    }
  }

  // Apply max_results cap and detail filtering.
  const totalCount = hits.length;
  let referencedBy: ReferencedByEntry[];
  let truncated = 0;

  if (detail === "summary") {
    referencedBy = [];
  } else {
    referencedBy = displayHits.slice(0, maxResults);
    truncated = Math.max(0, displayHits.length - maxResults);
  }

  return {
    queriedAssetPath: targetPath,
    queriedAssetGuid: targetGuid,
    referencedBy,
    totalCount,
    byKind,
    byFolder,
    collapsedGroups: collapsedGroups && collapsedGroups.length > 0
      ? collapsedGroups
      : undefined,
    detail,
    truncated,
  };
}

function extractReferenceLocations(
  content: string,
  targetGuid: string,
  maxPerFile: number,
): string[] {
  const locations: string[] = [];
  const lines = content.split("\n");

  for (let i = 0; i < lines.length; i++) {
    if (!lines[i].includes(targetGuid)) continue;
    if (locations.length >= maxPerFile) break;

    const trim = lines[i].trim();
    const cleaned = trim.startsWith("- ") ? trim.slice(2) : trim;
    const colonIdx = cleaned.indexOf(":");
    const field = colonIdx > 0 ? cleaned.slice(0, colonIdx).trim() : cleaned;

    if (field === "target") {
      let label = "prefab modification";
      for (let j = i + 1; j < Math.min(i + 5, lines.length); j++) {
        const pp = lines[j].trim();
        if (pp.startsWith("propertyPath:")) {
          label = `prefab → ${displayFieldName(
            cleanScalar(pp.slice("propertyPath:".length).trim()),
          )}`;
          break;
        }
        if (pp.startsWith("- target:") || pp.startsWith("- ") || pp === "") continue;
        break;
      }
      locations.push(label);
    } else {
      locations.push(displayFieldName(field));
    }
  }

  return locations;
}

// ===========================================================================
// dependenciesOffline — forward + reverse edges + impact analysis (T24.2).
//
// The offline counterpart of the live DependenciesTool. Returns BOTH edge
// directions in one call (forward = what this asset depends on; reverse = what
// references this asset), plus the broken-forward-GUID set, dependency cycles
// through the asset, and an optional transitive impact set ("what breaks if I
// delete/move this?"). All offline — no running Editor required.
//
// Algorithm:
//   - Forward edges: parse the queried asset's YAML, collect every external
//     GUID it references (field refs + m_Script + PrefabInstance
//     m_SourcePrefab), resolve each against the GUID→path index. GUIDs that
//     fail to resolve are the brokenForwardGuids set.
//   - Reverse edges: reuse the findReferencesOffline scan (raw-text GUID
//     filter across Assets/).
//   - Cycles: DFS over the forward edges of the queried asset's transitive
//     closure, scoped by maxCycleDepth to bound the walk.
//   - Transitive impact: BFS over the reverse edges of the queried asset
//     (multi-hop "who depends on what depends on me"), scoped by
//     maxImpactDepth to bound the walk.
// ===========================================================================

/** Edge-kind discriminator — surfaces HOW a forward edge was declared so an
 * agent can reason about a missing ref (a broken m_Script is a different fix
 * than a broken prefab base). Mirrors the C# Dependencies.Scanner edge kinds. */
export type ForwardEdgeKind = "pptr" | "script" | "prefab_source";

export interface ForwardEdge {
  guid: string;
  assetPath: string; // "" when the GUID does not resolve (broken edge)
  kind: ForwardEdgeKind;
  resolved: boolean;
}

export interface ReverseEdge {
  assetPath: string;
  guid: string;
  kind: string;
}

export interface ImpactEntry {
  assetPath: string;
  /** Hop distance from the queried asset (1 = direct reverse edge). */
  depth: number;
}

export interface DependenciesOfflineResult {
  queriedAssetPath: string;
  queriedAssetGuid: string;
  forwardDependencies: ForwardEdge[];
  forwardCount: number;
  /** Distinct unresolved forward-edge target GUIDs (the broken_dependency set). */
  brokenForwardGuids: string[];
  /** Dependency cycles passing through the queried asset (each a path list). */
  cycles: string[][];
  reverseDependencies: ReverseEdge[];
  reverseCount: number;
  /** Transitive reverse closure ("what breaks if I delete/move this?"). Empty
   * unless opts.includeImpact is set — the walk is the expensive part. */
  impact?: {
    affected: ImpactEntry[];
    affectedCount: number;
    maxDepth: number;
    /** True when the impact walk hit the depth bound before exhausting the
     * graph — the affected set is a prefix, not the full closure. */
    truncated: boolean;
  };
  detail: string;
  /** Reverse-edge roster truncation count (max_results cap). */
  truncated: number;
  /** Non-fatal: forward-edge extraction failed to parse the queried asset as
   * YAML (e.g. a JSON-only kind or a binary asset read offline). The forward
   * arrays are empty and this names the reason; reverse edges are unaffected. */
  forwardSkipped?: string;
  _source: "offline";
}

/** Collect the forward edges declared by a single asset: every external GUID
 *  reference (field PPtrs + m_Script + PrefabInstance m_SourcePrefab). This is
 *  the offline forward-edge primitive — unity-scanner has no full forward-edge
 *  builder, so this is authored net-new on the existing GUID-extraction
 *  primitives (findGUIDs / readGUIDField / parseAsset). */
function collectForwardEdges(parsed: ParsedAsset): ForwardEdge[] {
  const edges: ForwardEdge[] = [];
  const seen = new Set<string>();

  // 1. PrefabInstance.m_SourcePrefab — the base-prefab edge of a variant.
  for (const obj of parsed.objects) {
    if (obj.type !== "PrefabInstance") continue;
    const sourceGuid = readGUIDField(obj.lines, "m_SourcePrefab");
    if (sourceGuid !== "" && seen.add(`prefab:${sourceGuid}`)) {
      edges.push({ guid: sourceGuid, assetPath: "", kind: "prefab_source", resolved: false });
    }
  }

  // 2. m_Script GUIDs on MonoBehaviours — the script-class edge.
  for (const obj of parsed.objects) {
    if (obj.type !== "MonoBehaviour") continue;
    if (obj.scriptGUID !== "" && seen.add(`script:${obj.scriptGUID}`)) {
      edges.push({ guid: obj.scriptGUID, assetPath: "", kind: "script", resolved: false });
    }
  }

  // 3. Every other guid: field — material refs, asset refs, animation clips,
  //    etc. m_Script is handled above; m_SourcePrefab target: GUIDs inside
  //    PrefabInstance.m_Modifications are included here so variant overrides
  //    count as forward edges too.
  for (const obj of parsed.objects) {
    for (const line of obj.lines) {
      if (!line.includes("guid:")) continue;
      if (line.includes("m_Script:")) continue; // handled as a script edge above
      for (const g of findGUIDs(line)) {
        if (!seen.add(`pptr:${g}`)) continue;
        edges.push({ guid: g, assetPath: "", kind: "pptr", resolved: false });
      }
    }
  }

  return edges;
}

/** Resolve forward edges against the GUID→path index. Mutates each edge in
 *  place: sets assetPath + resolved. Returns the broken (unresolved) GUIDs. */
function resolveForwardEdges(
  edges: ForwardEdge[],
  guidIndex: GUIDIndex,
): string[] {
  const broken: string[] = [];
  const brokenSeen = new Set<string>();
  for (const edge of edges) {
    const path = guidIndex.get(edge.guid);
    if (path !== undefined) {
      edge.assetPath = path;
      edge.resolved = true;
    } else if (brokenSeen.add(edge.guid)) {
      broken.push(edge.guid);
    }
  }
  return broken;
}

export async function dependenciesOffline(
  opts: {
    assetPath?: string;
    guid?: string;
    detail?: string;
    maxResults?: number;
    /** Include the transitive impact closure (multi-hop reverse). Default
     * false — the BFS is the expensive part of this call. */
    includeImpact?: boolean;
    /** Max hop depth for the impact BFS (default 5). Bounds the walk on large
     * graphs. */
    maxImpactDepth?: number;
    /** Max depth for the forward-cycle DFS (default 8). */
    maxCycleDepth?: number;
    projectRoot: string;
  },
): Promise<DependenciesOfflineResult> {
  const detail = (opts.detail ?? "normal") as "summary" | "normal";
  const maxResults = opts.maxResults ?? 100;
  const includeImpact = opts.includeImpact ?? false;
  const maxImpactDepth = opts.maxImpactDepth ?? 5;
  const maxCycleDepth = opts.maxCycleDepth ?? 8;

  // Resolve target GUID + path (mirrors findReferencesOffline's resolution).
  let targetGuid = "";
  let targetPath = "";
  if (opts.guid) {
    targetGuid = opts.guid.toLowerCase();
  } else if (opts.assetPath) {
    targetPath = opts.assetPath;
    targetGuid = (await safeReadMetaGUID(
      join(opts.projectRoot, opts.assetPath + ".meta"),
    )).toLowerCase();
  }

  if (targetGuid === "") {
    return emptyDependencies(targetPath, "", detail);
  }
  if (targetPath === "") {
    const idx = await buildGUIDIndex(opts.projectRoot, new Set([targetGuid]));
    targetPath = idx.get(targetGuid) ?? "";
  }

  // ---- Forward edges ----
  // Parse the queried asset + collect its declared edges, then resolve each
  // against a GUID→path index scoped to exactly the GUIDs this asset names.
  let forwardEdges: ForwardEdge[] = [];
  let brokenGuids: string[] = [];
  let cycles: string[][] = [];
  let forwardSkipped: string | undefined;

  const absPath = join(opts.projectRoot, targetPath);
  let data: string | undefined;
  try {
    data = await readFile(absPath, "utf-8");
  } catch {
    // The queried asset isn't on disk (GUID resolve but path is stale). No
    // forward edges can be extracted; reverse edges still computed below.
    forwardSkipped = "queried asset not readable on disk";
  }

  if (data !== undefined) {
    try {
      const parsed = parseAsset(data);
      parsed.path = targetPath;
      forwardEdges = collectForwardEdges(parsed);
      const wanted = new Set(forwardEdges.map((e) => e.guid));
      const guidIndex = await buildGUIDIndex(opts.projectRoot, wanted);
      brokenGuids = resolveForwardEdges(forwardEdges, guidIndex);
      if (forwardEdges.length > 0) {
        cycles = await detectCyclesOffline(
          targetPath,
          forwardEdges,
          opts.projectRoot,
          maxCycleDepth,
        );
      }
    } catch (err) {
      // Not parseable as Unity YAML (JSON-only kind, binary, malformed). The
      // forward arrays stay empty; reverse edges are unaffected. This is the
      // honest signal — the offline forward parser covers text-serialized YAML
      // only, matching the documented offline coverage.
      forwardSkipped = err instanceof Error ? err.message : String(err);
    }
  }

  // ---- Reverse edges ---- (reuse the findReferencesOffline machinery).
  const refResult = await findReferencesOffline({
    assetPath: targetPath || undefined,
    guid: targetGuid,
    detail: "normal",
    projectRoot: opts.projectRoot,
  });

  const reverseEdges: ReverseEdge[] = refResult.referencedBy.map((e) => ({
    assetPath: e.assetPath,
    guid: e.guid ?? "",
    kind: e.kind,
  }));

  // ---- Transitive impact (optional) ----
  let impact: DependenciesOfflineResult["impact"];
  if (includeImpact) {
    impact = await computeTransitiveImpact(
      targetGuid,
      targetPath,
      opts.projectRoot,
      maxImpactDepth,
    );
  }

  // ---- Apply detail + caps ----
  const displayForward = detail === "summary" ? [] : forwardEdges;
  const totalCount = reverseEdges.length;
  let displayReverse: ReverseEdge[];
  let truncated = 0;
  if (detail === "summary") {
    displayReverse = [];
  } else if (maxResults > 0 && totalCount > maxResults) {
    displayReverse = reverseEdges.slice(0, maxResults);
    truncated = totalCount - maxResults;
  } else {
    displayReverse = reverseEdges;
  }

  return {
    queriedAssetPath: targetPath,
    queriedAssetGuid: targetGuid,
    forwardDependencies: displayForward,
    forwardCount: forwardEdges.length,
    brokenForwardGuids: brokenGuids,
    cycles,
    reverseDependencies: displayReverse,
    reverseCount: totalCount,
    impact,
    detail,
    truncated,
    ...(forwardSkipped ? { forwardSkipped } : {}),
    _source: "offline",
  };
}

function emptyDependencies(
  path: string,
  guid: string,
  detail: string,
): DependenciesOfflineResult {
  return {
    queriedAssetPath: path,
    queriedAssetGuid: guid,
    forwardDependencies: [],
    forwardCount: 0,
    brokenForwardGuids: [],
    cycles: [],
    reverseDependencies: [],
    reverseCount: 0,
    detail,
    truncated: 0,
    _source: "offline",
  };
}

/** Forward-cycle detection via DFS over the transitive forward closure of the
 *  queried asset. Bounded by maxDepth to stay cheap on large graphs; a cycle
 *  is reported when the DFS revisits the queried asset's path. This mirrors
 *  the C# Dependencies.Scanner.DetectCycles shape (each cycle is a path list).
 *
 *  unity-scanner has no cycle detector — this is authored net-new. */
async function detectCyclesOffline(
  startPath: string,
  startEdges: ForwardEdge[],
  projectRoot: string,
  maxDepth: number,
): Promise<string[][]> {
  const cycles: string[][] = [];

  // Build a small forward-edge cache so repeated visits of the same asset
  // parse once. Keyed by asset path.
  const edgeCache = new Map<string, ForwardEdge[]>();
  edgeCache.set(startPath, startEdges);

  async function edgesOf(path: string): Promise<ForwardEdge[]> {
    const cached = edgeCache.get(path);
    if (cached) return cached;
    try {
      const data = await readFile(join(projectRoot, path), "utf-8");
      const parsed = parseAsset(data);
      const edges = collectForwardEdges(parsed);
      // Best-effort resolve so the DFS walks resolved paths only.
      const idx = await buildGUIDIndex(projectRoot, new Set(edges.map((e) => e.guid)));
      resolveForwardEdges(edges, idx);
      edgeCache.set(path, edges);
      return edges;
    } catch {
      edgeCache.set(path, []);
      return [];
    }
  }

  // DFS from each resolved forward neighbor of the start asset, looking for a
  // path back to startPath. visited guards against re-descending the same node
  // within one root-neighbor walk (the cycle itself is the back-edge to start).
  const visiting = new Set<string>();
  async function dfs(current: string, trail: string[]): Promise<void> {
    if (trail.length > maxDepth) return;
    const edges = await edgesOf(current);
    for (const edge of edges) {
      if (!edge.resolved || edge.assetPath === "") continue;
      if (edge.assetPath === startPath) {
        cycles.push([...trail, startPath]);
        continue;
      }
      if (visiting.has(edge.assetPath)) continue;
      visiting.add(edge.assetPath);
      await dfs(edge.assetPath, [...trail, edge.assetPath]);
      visiting.delete(edge.assetPath);
    }
  }

  for (const edge of startEdges) {
    if (!edge.resolved || edge.assetPath === "" || edge.assetPath === startPath) continue;
    visiting.clear();
    visiting.add(edge.assetPath);
    await dfs(edge.assetPath, [startPath, edge.assetPath]);
  }
  return cycles;
}

/** Transitive impact closure via BFS over reverse edges. Starting from the
 *  queried asset's direct referencers, walk one hop out at a time, accumulating
 *  every asset that (transitively) depends on the queried asset. Bounded by
 *  maxDepth. This is the "what breaks if I delete/move this?" answer — neither
 *  the offline nor the live path did multi-hop reverse before T24.2. */
async function computeTransitiveImpact(
  startGuid: string,
  startPath: string,
  projectRoot: string,
  maxDepth: number,
): Promise<NonNullable<DependenciesOfflineResult["impact"]>> {
  const affected: ImpactEntry[] = [];
  const seen = new Set<string>([startPath]);
  let frontier: string[] = [];
  let truncated = false;

  // Seed: direct reverse edges of the queried asset.
  const seed = await findReferencesOffline({
    assetPath: startPath || undefined,
    guid: startGuid,
    detail: "normal",
    projectRoot,
  });
  for (const e of seed.referencedBy) {
    if (seen.has(e.assetPath)) continue;
    seen.add(e.assetPath);
    frontier.push(e.assetPath);
    affected.push({ assetPath: e.assetPath, depth: 1 });
  }

  // BFS: for each level, resolve the reverse edges of every frontier asset.
  for (let depth = 2; depth <= maxDepth; depth++) {
    if (frontier.length === 0) break;
    const next: string[] = [];
    for (const nodePath of frontier) {
      const nodeGuid = await safeReadMetaGUID(join(projectRoot, nodePath + ".meta"));
      if (nodeGuid === "") continue;
      const res = await findReferencesOffline({
        guid: nodeGuid,
        detail: "normal",
        projectRoot,
      });
      for (const e of res.referencedBy) {
        if (seen.has(e.assetPath)) continue;
        seen.add(e.assetPath);
        next.push(e.assetPath);
        affected.push({ assetPath: e.assetPath, depth });
      }
    }
    frontier = next;
    if (depth === maxDepth && frontier.length > 0) {
      // The next level exists but we stop here — the closure is a prefix.
      truncated = true;
    }
  }

  return {
    affected,
    affectedCount: affected.length,
    maxDepth,
    truncated,
  };
}

// ===========================================================================
// scanIntegrityOffline — project-wide offline integrity scan (T24.2 item 3).
//
// The per-read integrity checks (runYamlIntegrityChecks / runJsonIntegrityChecks)
// surface on every read_asset call but are scoped to ONE asset. The verify
// engine (M25) needs PROJECT-WIDE signals that don't fit the per-read model:
//   - orphan_meta: a .meta file whose asset was deleted (no companion file).
//   - duplicate_guid: a GUID shared by two+ .meta files.
//   - (aggregated) missing refs: every unresolved field GUID across Assets/.
//
// These fill the planned remove_orphan_meta / fix_duplicate_guid fix
// placeholders in the capability catalog — the first rules to emit those codes.
// Runs entirely offline; no Editor, no AssetDatabase.
// ===========================================================================

export interface IntegrityScanEntry {
  /** The asset/.meta path the issue is reported on. */
  path: string;
  code: string;
  severity: "error" | "warning" | "info";
  detail: string;
  /** For duplicate_guid: the other paths sharing this GUID. */
  relatedPaths?: string[];
}

export interface IntegrityScanResult {
  issues: IntegrityScanEntry[];
  byCode: Record<string, number>;
  totalIssues: number;
  assetsScanned: number;
  _source: "offline";
}

/** Scan the whole Assets/ tree for project-wide integrity issues that the
 *  per-read checks cannot surface (orphaned .meta, duplicate GUIDs) plus the
 *  aggregated missing-reference set. Offline counterpart of a full verify
 *  scan_paths run for these three rule families. */
export async function scanIntegrityOffline(
  opts: { projectRoot: string },
): Promise<IntegrityScanResult> {
  const issues: IntegrityScanEntry[] = [];
  const assetsDir = join(opts.projectRoot, "Assets");

  // ---- Walk every .meta + every asset file in one pass ----
  // We collect: guidByPath (path -> guid), pathByGuid (guid -> paths[]), and
  // the set of asset files (companions for the orphan-meta check). Skipping a
  // dir uses the same skipDirs policy as every other offline walk.
  const guidByPath = new Map<string, string>();
  const allAssetPaths = new Set<string>();

  const assetFiles = await collectFiles(assetsDir);
  for (const absPath of assetFiles) {
    const assetPath = toAssetPath(opts.projectRoot, absPath);
    allAssetPaths.add(assetPath);
    const metaGuid = await safeReadMetaGUID(absPath + ".meta");
    if (metaGuid !== "") {
      guidByPath.set(assetPath, metaGuid);
    }
  }

  // Also index .meta-only paths (files whose companion was deleted show up
  // here but not in allAssetPaths — those are the orphans). walkMeta gives us
  // every .meta under Assets/.
  const metaOnlyPaths = new Set<string>();
  await walkMeta(assetsDir, async (metaPath) => {
    const assetPath = toAssetPath(opts.projectRoot, metaPath.slice(0, -5));
    if (!allAssetPaths.has(assetPath)) {
      metaOnlyPaths.add(assetPath);
    }
  });

  // ---- orphan_meta: .meta whose companion asset is gone ----
  for (const path of metaOnlyPaths) {
    issues.push({
      path: path + ".meta",
      code: "orphan_meta",
      severity: "warning",
      detail: `meta file has no companion asset at ${path}`,
    });
  }

  // ---- duplicate_guid: GUID shared by 2+ assets ----
  const pathsByGuid = new Map<string, string[]>();
  for (const [assetPath, guid] of guidByPath) {
    const list = pathsByGuid.get(guid);
    if (list) list.push(assetPath);
    else pathsByGuid.set(guid, [assetPath]);
  }
  for (const [guid, paths] of pathsByGuid) {
    if (paths.length < 2) continue;
    for (const p of paths) {
      issues.push({
        path: p,
        code: "duplicate_guid",
        severity: "error",
        detail: `guid ${guid} shared by ${paths.length} assets`,
        relatedPaths: paths.filter((other) => other !== p),
      });
    }
  }

  // ---- aggregated missing references (the per-read check, project-wide) ----
  // Re-uses the full GUID index so unresolved field GUIDs are detected once
  // per asset across the whole tree. This is the offline seed for the
  // missing_references verify rule (M25 consumes these primitives).
  const fullGuidIndex = await buildGUIDIndex(opts.projectRoot);
  for (const absPath of assetFiles) {
    const ext = extname(absPath).toLowerCase();
    if (!OFFLINE_PARSEABLE_EXTENSIONS.has(ext)) continue;
    const assetPath = toAssetPath(opts.projectRoot, absPath);
    let data: string;
    try { data = await readFile(absPath, "utf-8"); } catch { continue; }
    if (!data.includes("guid:")) continue; // fast filter: no refs at all

    try {
      const parsed = parseAsset(data);
      parsed.path = assetPath;
      // Run the per-asset YAML integrity check against the FULL index (not a
      // scoped one) so a ref resolves even if its target lives far away.
      const perAsset = runYamlIntegrityChecks(parsed, fullGuidIndex);
      for (const issue of perAsset) {
        issues.push({
          path: assetPath,
          code: issue.code,
          severity: issue.severity,
          detail: issue.detail,
        });
      }
    } catch {
      // Not parseable as YAML — skip. JSON integrity is per-read only; a
      // project-wide JSON scan is out of scope for the verify-engine seed.
    }
  }

  // ---- group by code ----
  const byCode: Record<string, number> = {};
  for (const issue of issues) {
    byCode[issue.code] = (byCode[issue.code] ?? 0) + 1;
  }

  return {
    issues,
    byCode,
    totalIssues: issues.length,
    assetsScanned: allAssetPaths.size,
    _source: "offline",
  };
}
