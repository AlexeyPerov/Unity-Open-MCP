// Prefab override name resolution + override parsing.
//
// Extracted from the former monolithic offline.ts (M28-refactoring Plan 3,
// T3.1). Two responsibilities:
//   1. applyPrefabNameOverrides — runs at parse time to resolve GameObject
//      names inherited from prefab variants (called by parse.ts).
//   2. The raw-override parser + the readable-label resolver used by the
//      hierarchy builder to produce the PrefabOverrideEntry[] model.
//
// Depends on types + primitives + hierarchy (for objectPath / componentName /
// localReferenceLabel used by the label resolver). The hierarchy import is
// one-directional — hierarchy.ts does not import overrides.ts.

import { basename } from "node:path";
import type { PrefabOverrideEntry } from "../compression/asset-model.js";
import type {
  GUIDIndex,
  ParsedAsset,
  ParsedObject,
  PrefabOverride,
  ScriptIndex,
} from "./types.js";
import { displayFieldName } from "./names.js";
import { cleanScalar, extractFileID, extractGUID } from "./primitives.js";

// ===========================================================================
// Prefab override name resolution — runs at parse time.
// ===========================================================================

export function applyPrefabNameOverrides(objects: ParsedObject[]): void {
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

// ===========================================================================
// Raw prefab-override parser — extracts the m_Modifications / m_Removed* /
// m_Added* sections from a PrefabInstance object's lines.
// ===========================================================================

export function parsePrefabOverrides(lines: string[]): PrefabOverride[] {
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
//
// The label resolver (resolveOverrideTarget / resolveAddedObject) depends on
// hierarchy helpers (componentName / localReferenceLabel / objectPath). Those
// are imported lazily via a function injection point below to avoid a cycle —
// hierarchy.ts does not import this module, but this module needs hierarchy's
// label helpers. See the LabelHelpers interface and the late-bound registry.
// ===========================================================================

export interface LabelHelpers {
  objectPath(asset: ParsedAsset, goID: string): string;
  localReferenceLabel(asset: ParsedAsset, fileID: string, scriptIndex: ScriptIndex): string;
  componentName(obj: ParsedObject, scriptIndex: ScriptIndex): { name: string; scriptPath: string };
  resolveReferences(value: string, guidIndex: GUIDIndex, asset: ParsedAsset): string;
}

// Late-bound registry so overrides.ts can call hierarchy.ts helpers without an
// import cycle. The references module (which depends on both) wires the real
// implementations at load time.
let labelHelpers: LabelHelpers | null = null;

export function setLabelHelpers(helpers: LabelHelpers): void {
  labelHelpers = helpers;
}

export function buildSourceMap(parsed: ParsedAsset): Map<string, ParsedObject> {
  const map = new Map<string, ParsedObject>();
  for (const obj of parsed.objects) {
    if (obj.sourceObjectID === "" || obj.sourceGUID === "") continue;
    map.set(`${obj.sourceGUID.toLowerCase()}\0${obj.sourceObjectID}`, obj);
  }
  return map;
}

export function collectOverrides(
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
  if (labelHelpers) return labelHelpers.resolveReferences(value, guidIndex, parsed);
  return value;
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
    if (localObj && labelHelpers) {
      return labelForParsedObject(localObj, parsed, scriptIndex);
    }
  }

  if (fileID !== "" && labelHelpers) {
    const label = labelHelpers.localReferenceLabel(parsed, fileID, scriptIndex);
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
  if (!labelHelpers) return undefined;
  const label = labelHelpers.localReferenceLabel(parsed, fileID, scriptIndex);
  return label || undefined;
}

function labelForParsedObject(
  obj: ParsedObject,
  parsed: ParsedAsset,
  scriptIndex: ScriptIndex,
): string {
  if (!labelHelpers) return obj.name;
  if (obj.type === "GameObject") return labelHelpers.objectPath(parsed, obj.id);
  const { name } = labelHelpers.componentName(obj, scriptIndex);
  if (obj.gameObjectID !== "") {
    const path = labelHelpers.objectPath(parsed, obj.gameObjectID);
    if (path) return `${name} on ${path}`;
  }
  if (obj.name !== "") return `${name} ${obj.name}`;
  return `${name} ${obj.id}`;
}

// Re-export basename-derived helper for the script-path name extraction so the
// hierarchy module's componentName can share it without re-importing node:path.
export function scriptBaseName(path: string): string {
  const base = basename(path);
  const ext = path.match(/\.[^.]+$/)?.[0] ?? "";
  return ext ? base.slice(0, -ext.length) : base;
}
