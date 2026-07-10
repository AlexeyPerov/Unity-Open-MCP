// AssetModel builders + integrity checks for the offline reader.
//
// Extracted from the former monolithic offline.ts (M28-refactoring Plan 3,
// T3.1). Converts a parsed YAML/JSON asset into the AssetModel the compression
// layer renders, and runs the per-asset integrity checks (orphaned prefab
// instance, missing script/asset refs, malformed JSON, asmdef/shadergraph
// shape). Depends on types + parse + hierarchy + overrides + references
// (injected) + primitives.

import type {
  AssetComponent,
  AssetIntegrityIssue,
  AssetModel,
  ComponentField,
  FlatObject,
  HierarchyNode,
} from "../compression/asset-model.js";
import type {
  GUIDIndex,
  HierarchyResult,
  JsonParsedObject,
  ParsedAsset,
  ParsedObject,
  ScriptIndex,
} from "./types.js";
import { EMPTY_PARSED } from "./types.js";
import { findGUIDs } from "./primitives.js";
import { readGUIDField } from "./primitives.js";
import { parseJsonAsset, jsonFields, pickString } from "./parse.js";
import {
  buildHierarchy,
  componentsFor,
  componentName,
  fieldsWithHidden,
} from "./hierarchy.js";
import { collectOverrides } from "./overrides.js";
import { resolveReferences } from "./references.js";

// ===========================================================================
// YAML AssetModel builder.
// ===========================================================================

export function buildAssetModel(
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
    const { fields } = fieldsWithHidden(parsed, obj, fieldLimit, guidIndex, resolveReferences);
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
      const { fields } = fieldsWithHidden(parsed, c.object, fieldLimit, guidIndex, resolveReferences);
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
// JSON AssetModel builder.
// ===========================================================================

export function buildJsonAssetModel(
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
    const fields = jsonFields(obj.value, fieldLimit, guidIndex, resolveReferences);
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

// ===========================================================================
// M24 — offline integrity checks.
// ===========================================================================

export function runYamlIntegrityChecks(parsed: ParsedAsset, guidIndex: GUIDIndex): AssetIntegrityIssue[] {
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

export function runJsonIntegrityChecks(
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

// Re-export EMPTY_PARSED consumers (jsonScalar used it; kept for parity).
export { EMPTY_PARSED };
