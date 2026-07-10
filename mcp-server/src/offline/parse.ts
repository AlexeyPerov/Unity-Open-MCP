// YAML + JSON text parsing for the offline asset reader.
//
// Extracted from the former monolithic offline.ts (M28-refactoring Plan 3,
// T3.1). Two parsing paths:
//   - parseAsset — splits a Unity YAML document stream into ParsedObjects,
//     extracting the per-object key fields (m_Name, component IDs, transform
//     parent links, source-prefab refs, m_Script GUIDs). Runs the prefab
//     override name-resolution pass at the end of parsing.
//   - The JSON asset path (.asmdef / .shadergraph / .shadersubgraph): a
//     brace-depth stream splitter + per-object discriminator. unity-scanner
//     handles YAML only, so this is the coverage differentiator.
//
// Depends on types + primitives + overrides (for applyPrefabNameOverrides at
// parse time).

import type {
  AssetIntegrityIssue,
} from "../compression/asset-model.js";
import type {
  GUIDIndex,
  JsonParsedObject,
  ParsedAsset,
  ParsedObject,
} from "./types.js";
import { EMPTY_PARSED } from "./types.js";
import {
  parseHeaderLine,
  readComponentIDs,
  readFileIDField,
  readGUIDField,
  readScalar,
  scanObjectTypeLine,
} from "./primitives.js";
import { applyPrefabNameOverrides } from "./overrides.js";

// ===========================================================================
// YAML parser — split YAML into documents, extract key fields per object.
// ===========================================================================

export function parseAsset(data: string): ParsedAsset {
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

export function parseJsonAsset(data: string, issues: AssetIntegrityIssue[]): JsonParsedObject[] {
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

export function pickString(v: unknown): string | undefined {
  return typeof v === "string" && v !== "" ? v : undefined;
}

/** Shorten a ShaderGraph m_Type like "UnityEditor.ShaderGraph.MultiplyNode" → "MultiplyNode". */
export function jsonShortType(full: string): string {
  const dot = full.lastIndexOf(".");
  return dot >= 0 ? full.slice(dot + 1) : full;
}

/**
 * A `.shadergraph` is a stream of pretty-printed JSON objects. They are
 * separated by blank lines, but a single object's pretty-printed body also
 * contains newlines — so the split must track brace depth, not line gaps.
 * `.asmdef` is a single object; this still returns a one-element array.
 */
export function splitJsonStream(data: string): string[] {
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
 * field renderer. GUID refs are resolved against the meta index via the
 * injected resolver (avoids a direct reference-resolution import here).
 */
export function jsonFields(
  value: unknown,
  limit: number,
  guidIndex: GUIDIndex,
  resolveRef: (value: string, guidIndex: GUIDIndex, asset: ParsedAsset) => string,
): { name: string; value: string }[] {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return [];
  const obj = value as Record<string, unknown>;
  const out: { name: string; value: string }[] = [];
  for (const [key, raw] of Object.entries(obj)) {
    if (out.length >= limit && limit > 0) break;
    out.push({ name: key, value: jsonScalar(raw, guidIndex, resolveRef) });
  }
  return out;
}

export function jsonScalar(
  value: unknown,
  guidIndex: GUIDIndex,
  resolveRef: (value: string, guidIndex: GUIDIndex, asset: ParsedAsset) => string,
): string {
  if (value === null) return "null";
  switch (typeof value) {
    case "string":
      return resolveRef(value, guidIndex, EMPTY_PARSED);
    case "number":
    case "boolean":
      return String(value);
    default: {
      if (Array.isArray(value)) return `[${value.length}]`;
      const obj = value as Record<string, unknown>;
      // Inline {fileID, guid, type} PPtr → resolve to asset name.
      if (typeof obj.guid === "string" && obj.guid.length === 32) {
        return resolveRef(`{fileID: ${obj.fileID ?? 0}, guid: ${obj.guid}, type: ${obj.type ?? 0}}`, guidIndex, EMPTY_PARSED);
      }
      const keys = Object.keys(obj);
      if (keys.length === 0) return "{}";
      return `{${keys.slice(0, 4).join(", ")}${keys.length > 4 ? ", …" : ""}}`;
    }
  }
}
