// Hierarchy building + component/field resolution for the offline reader.
//
// Extracted from the former monolithic offline.ts (M28-refactoring Plan 3,
// T3.1). Builds the GameObject tree from Transform parent links, resolves the
// component set for each GameObject (including MonoBehaviour script-class
// names), and extracts the top-level serialized fields per component.
//
// Depends on types + primitives + overrides (scriptBaseName) + names
// (displayFieldName). The reference resolver is injected via a registry so
// this module does not import references.ts (which would be a cycle).

import { extname } from "node:path";
import type {
  GUIDIndex,
  HierarchyResult,
  ParsedAsset,
  ParsedObject,
  ScriptIndex,
} from "./types.js";
import { nativeClassName } from "./types.js";
import { skipFieldNames } from "./types.js";
import { shortGUID } from "./primitives.js";
import { displayFieldName } from "./names.js";

// M31-optimizations Plan 3 / L8-offline — module-scope regex. Previously an
// inline `trim.split(/\s+/).join(" ")` literal inside summarizeNested's per-
// line loop (a hot path during read_asset field rendering). Hoisting makes
// the single-compile guarantee explicit.
const WHITESPACE_RE = /\s+/;

// ===========================================================================
// Hierarchy — build GameObject tree from Transform parent links.
// ===========================================================================

export function buildHierarchy(asset: ParsedAsset): HierarchyResult[] {
  // M31-optimizations Plan 3 / H8 + L3 — return the cached root list when the
  // per-asset hierarchy cache exists. The cache is built on the first call
  // (see the bottom of this function) and the asset is immutable after parse,
  // so subsequent calls — including those from objectPath/componentsFor
  // re-entering buildHierarchy defensively — reuse the same node objects.
  // Previously every objectPath call rebuilt + flattened the whole tree.
  if (asset.hierarchyCache) return asset.hierarchyCache.roots;

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

  // M31-optimizations Plan 3 / H8 + L3 — build the per-asset lookup indices
  // alongside the tree, in the same pass that walks it. nodesByGoID is the
  // flattened goID→node map (assignNodePath already set .path on each node);
  // componentsByGoID is the goID→components map (one walk over asset.objects
  // collecting components by their gameObjectID backref, plus the GameObject's
  // own componentIDs list). Both are consulted by objectPath / componentsFor
  // for O(1) lookups; before this, objectPath re-flattened the tree per call
  // and componentsFor did a linear scan over all objects per node.
  const nodesByGoID = new Map<string, HierarchyResult>();
  for (const node of nodes.values()) nodesByGoID.set(node.gameObject.id, node);

  const componentsByGoID = new Map<string, ParsedObject[]>();
  // Seed from each GameObject's declared componentIDs (the authoritative
  // component list for types that declare m_Component).
  for (const go of gameObjects) {
    if (go.componentIDs.length === 0) continue;
    const comps: ParsedObject[] = [];
    for (const compID of go.componentIDs) {
      const compObj = asset.byID.get(compID);
      if (compObj) comps.push(compObj);
    }
    if (comps.length > 0) componentsByGoID.set(go.id, comps);
  }
  // Fold in components that declare a gameObjectID backref but are NOT in the
  // GameObject's componentIDs list (the secondary scan the previous
  // componentsFor ran per call). Append-only: do not duplicate componentIDs
  // entries (matched via the seen set).
  for (const compObj of asset.objects) {
    if (compObj.type === "GameObject") continue;
    if (compObj.gameObjectID === "") continue;
    let comps = componentsByGoID.get(compObj.gameObjectID);
    if (!comps) {
      comps = [];
      componentsByGoID.set(compObj.gameObjectID, comps);
    }
    if (!comps.includes(compObj)) comps.push(compObj);
  }

  asset.hierarchyCache = { roots, nodesByGoID, componentsByGoID };
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

export function flattenNodes(asset: ParsedAsset): HierarchyResult[] {
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

export function flattenHierarchy(nodes: HierarchyResult[]): HierarchyResult[] {
  const out: HierarchyResult[] = [];
  const walk = (ns: HierarchyResult[]) => {
    for (const n of ns) { out.push(n); walk(n.children); }
  };
  walk(nodes);
  return out;
}

export function objectPath(asset: ParsedAsset, goID: string): string {
  // M31-optimizations Plan 3 / H8 — consult the per-asset goID→node cache
  // built alongside the hierarchy. Previously this called flattenNodes(asset)
  // (which rebuilt + flattened the whole tree via buildHierarchy) on every
  // invocation, and objectPath is called once per FileID in every field value
  // during resolveReferences — O(fields × nodes). The cache hit is O(1).
  // Defensive fallback: if the cache is absent (e.g. asset was constructed
  // without going through buildHierarchy, or the EMPTY_PARSED stand-in),
  // fall back to the flatten+scan so the function stays correct everywhere.
  const cache = asset.hierarchyCache;
  if (cache) {
    const node = cache.nodesByGoID.get(goID);
    if (node) return node.path;
  } else {
    for (const node of flattenNodes(asset)) {
      if (node.gameObject.id === goID) return node.path;
    }
  }
  const obj = asset.byID.get(goID);
  return obj ? obj.name : "";
}

// ===========================================================================
// Components — resolve component objects for a GameObject.
// ===========================================================================

export function componentsFor(
  asset: ParsedAsset,
  goID: string,
  scriptIndex: ScriptIndex,
): { object: ParsedObject; name: string; scriptPath: string }[] {
  const goObj = asset.byID.get(goID);
  if (!goObj) return [];

  // M31-optimizations Plan 3 / L3 — consult the per-asset goID→components
  // cache built alongside the hierarchy. The cache stores the resolved
  // ParsedObject list (componentIDs-first, then backref-only — the exact
  // order the previous linear scan produced). On a cache hit this is O(out)
  // — no linear scan over all objects. The cache is built once per asset
  // during buildHierarchy; the fallback below preserves the previous
  // per-call scan for assets without a cache (defensive — should not happen
  // in normal flow since buildHierarchy is always called first).
  const cache = asset.hierarchyCache;
  if (cache) {
    const cached = cache.componentsByGoID.get(goID);
    if (cached) {
      const out: { object: ParsedObject; name: string; scriptPath: string }[] = [];
      for (const compObj of cached) {
        const { name, scriptPath } = componentName(compObj, scriptIndex);
        out.push({ object: compObj, name, scriptPath });
      }
      return out;
    }
    // No components recorded for this goID (e.g. a GameObject whose
    // componentIDs list is empty and nothing backrefs it). The previous
    // scan would have returned [] here too.
    return [];
  }

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

export function componentName(
  obj: ParsedObject,
  scriptIndex: ScriptIndex,
): { name: string; scriptPath: string } {
  if (obj.type === "MonoBehaviour") {
    const path = scriptIndex.get(obj.scriptGUID);
    if (path) {
      const base = path.split("/").pop() ?? path;
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

export function localReferenceLabel(
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

// ===========================================================================
// Fields — extract top-level serialized fields.
//
// The reference resolver is injected (resolveRefs) so this module does not
// import references.ts. references.ts depends on hierarchy for objectPath /
// componentName, so the dependency must stay one-directional.
// ===========================================================================

export function fieldsWithHidden(
  asset: ParsedAsset,
  obj: ParsedObject,
  limit: number,
  guidIndex: GUIDIndex,
  resolveRefs: (value: string, guidIndex: GUIDIndex, asset: ParsedAsset) => string,
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
    value = resolveRefs(value, guidIndex, asset);
    fields.push({ name: displayFieldName(key), value });
  }

  return { fields, hidden };
}

export function isTopLevelFieldLine(line: string): boolean {
  return (
    line.startsWith("  ") &&
    !line.startsWith("    ") &&
    !line.startsWith("  -")
  );
}

export function summarizeNested(lines: string[], start: number): string {
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
      parts.push(trim.split(WHITESPACE_RE).join(" "));
    }
    if (parts.length >= maxParts) break;
  }

  if (parts.length === 0) return "<object>";
  return parts.join(" | ");
}
