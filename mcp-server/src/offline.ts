// Offline asset reader — parses text-serialized Unity assets (.prefab/.unity/
// .asset) from disk without a running Editor. Ported from unity-scanner
// internal/unityasset (yaml.go, scan.go, kind.go, project.go).
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
  SearchModel,
  SearchMatch,
  SearchObjectMatch,
} from "./compression/asset-model.js";

// ===========================================================================
// Native Unity class IDs → names (from unity-scanner yaml.go).
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
// Kind / extension utilities (from unity-scanner kind.go).
// ===========================================================================

const extToKind: Record<string, string> = {
  ".anim": "anim",
  ".asset": "asset",
  ".controller": "controller",
  ".cs": "cs",
  ".mat": "mat",
  ".overrideController": "controller",
  ".physicsMaterial2D": "physics2d",
  ".physicMaterial": "physics",
  ".playable": "playable",
  ".prefab": "prefab",
  ".shader": "shader",
  ".spriteatlas": "atlas",
  ".unity": "scene",
  ".uss": "uss",
  ".uxml": "uxml",
  ".meta": "meta",
};

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
// (Ported from unity-scanner yaml.go ParseAssetWithOptions.)
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

function localReferenceLabel(asset: ParsedAsset, fileID: string): string {
  if (fileID === "" || fileID === "0") return "";
  const obj = asset.byID.get(fileID);
  if (!obj) return "";
  if (obj.type === "GameObject") return objectPath(asset, obj.id);
  const { name } = componentName(obj, new Map());
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
        const { override, next } = parsePrefabAddedComponentOverride(lines, i);
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
): { override: PrefabOverride; next: number } {
  const override: PrefabOverride = { kind: "added-component", target: "", propertyPath: "", value: "", addedObject: "" };
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
// GUID index builder — reads .meta files (from unity-scanner yaml.go).
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

const OFFLINE_EXTENSIONS = new Set([
  ".prefab", ".unity", ".asset", ".mat", ".controller", ".anim", ".playable",
]);

export function isOfflineAsset(assetPath: string): boolean {
  const dot = assetPath.lastIndexOf(".");
  if (dot < 0) return false;
  return OFFLINE_EXTENSIONS.has(assetPath.slice(dot).toLowerCase());
}

export interface OfflineReadResult {
  model: AssetModel;
  source: "offline";
}

export async function readAssetOffline(
  assetPath: string,
  opts: { fieldLimit: number; projectRoot: string },
): Promise<OfflineReadResult> {
  const absPath = join(opts.projectRoot, assetPath);
  const data = await readFile(absPath, "utf-8");
  const parsed = parseAsset(data);
  parsed.path = assetPath;
  parsed.kind = kindForPath(assetPath);
  parsed.guid = await safeReadMetaGUID(absPath + ".meta");

  const wantedScripts = scriptGUIDs(parsed);
  const scriptIndex = await buildScriptIndex(opts.projectRoot, wantedScripts);
  const wantedGUIDs = collectFieldGUIDs(parsed);
  const guidIndex = await buildGUIDIndex(opts.projectRoot, wantedGUIDs);
  for (const [g, p] of scriptIndex) guidIndex.set(g, p);

  return { model: buildAssetModel(parsed, scriptIndex, guidIndex, opts.fieldLimit), source: "offline" };
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
  };
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

const SEARCHABLE_EXTENSIONS = new Set([
  ".prefab", ".unity", ".asset", ".mat", ".controller", ".anim", ".playable",
]);

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

    let parsed: ParsedAsset;
    try { parsed = parseAsset(await readFile(filePath, "utf-8")); } catch { continue; }
    parsed.path = assetPath;
    parsed.kind = kind;
    const guid = await safeReadMetaGUID(filePath + ".meta");

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

const REFERENCEABLE_EXTENSIONS = new Set([
  ".prefab", ".unity", ".asset", ".mat", ".controller", ".anim", ".playable",
]);

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
