// Low-level extraction primitives for the offline YAML/JSON parser.
//
// Extracted from the former monolithic offline.ts (M28-refactoring Plan 3,
// T3.1). These are the leaf scanning functions — GUID/fileID extraction,
// scalar cleaning, header parsing, line-shape classification. They depend only
// on the shared types module; no cross-submodule runtime imports.

import type { HeaderInfo, ParsedObject } from "./types.js";

// M31-optimizations Plan 3 / L8-offline — module-scope regex. Previously an
// inline `id.split(/\s+/)` literal inside parseHeaderLine (a per-call function
// invoked once per YAML object header). Hoisting makes the single-compile
// guarantee explicit across engines.
const WHITESPACE_RE = /\s+/;

// ===========================================================================
// Line-scanning helpers.
// ===========================================================================

export function scanObjectTypeLine(obj: ParsedObject, line: string): boolean {
  if (obj.type !== "" || line.length <= 1 || line[0] === " " || !line.endsWith(":")) {
    return false;
  }
  obj.type = line.slice(0, -1).trim();
  return true;
}

// ===========================================================================
// Low-level extraction primitives.
// ===========================================================================

export function parseHeaderLine(line: string): HeaderInfo | null {
  const prefix = "--- !u!";
  if (!line.startsWith(prefix)) return null;
  const rest = line.slice(prefix.length);
  const space = rest.indexOf(" ");
  if (space <= 0 || space + 2 > rest.length || rest[space + 1] !== "&") return null;
  const classID = parsePositiveInt(rest.slice(0, space));
  if (classID === null) return null;
  let id = rest.slice(space + 2);
  const fields = id.split(WHITESPACE_RE);
  if (fields.length > 0) id = fields[0];
  if (id === "") return null;
  return { classID, id };
}

export function parsePositiveInt(value: string): number | null {
  if (value.length === 0) return null;
  let out = 0;
  for (const ch of value) {
    if (ch < "0" || ch > "9") return null;
    out = out * 10 + (ch.charCodeAt(0) - "0".charCodeAt(0));
  }
  return out;
}

export function readScalar(lines: string[], key: string): string {
  const prefix = `  ${key}:`;
  for (const line of lines) {
    if (line.startsWith(prefix)) return cleanScalar(line.slice(prefix.length).trim());
  }
  return "";
}

export function readFileIDField(lines: string[], key: string): string {
  const prefix = `  ${key}:`;
  for (const line of lines) {
    if (line.startsWith(prefix)) return extractFileID(line);
  }
  return "";
}

export function readGUIDField(lines: string[], key: string): string {
  const prefix = `  ${key}:`;
  for (const line of lines) {
    if (line.startsWith(prefix)) return extractGUID(line);
  }
  return "";
}

export function readComponentIDs(lines: string[]): string[] {
  const ids: string[] = [];
  for (const line of lines) {
    if (line.includes("- component:")) {
      const id = extractFileID(line);
      if (id !== "" && id !== "0") ids.push(id);
    }
  }
  return ids;
}

// ===========================================================================
// M31-optimizations Plan 3 / H7 — single-pass known-field extraction.
//
// finishObject previously called up to 6 independent scanners (readScalar,
// readFileIDField ×2, readFileIDField + readGUIDField, readGUIDField,
// readComponentIDs) on the same lines array — each restarting from line 0
// (O(6 × lines) per object). extractKnownFields walks the line array exactly
// once and returns every field finishObject needs, preserving the exact
// semantics of the previous scanners:
//   - Each field takes the FIRST matching line (line.startsWith(`  ${key}:`)).
//   - m_CorrespondingSourceObject yields BOTH a fileID (sourceObjectID) and a
//     guid (sourceGUID) from the same line — the previous code read each
//     independently, but both extract from the first matching line, and a
//     single line's fileID + guid are what those scanners would have returned.
//   - componentIDs collects every `- component:` line (multi-valued).
// The result is keyed by field name; the per-object switch in finishObject
// picks the subset relevant to the object's type (mirroring the previous
// behavior where e.g. only GameObject runs readComponentIDs).
// ===========================================================================

/** Prefix strings the single-pass scanner matches. Kept as `const` so the
 * startsWith checks inline cleanly. */
const NAME_PREFIX = "  m_Name:";
const GAMEOBJECT_PREFIX = "  m_GameObject:";
const FATHER_PREFIX = "  m_Father:";
const SOURCE_OBJ_PREFIX = "  m_CorrespondingSourceObject:";
const SCRIPT_PREFIX = "  m_Script:";
const COMPONENT_MARKER = "- component:";

export interface KnownFields {
  name: string;
  gameObjectID: string;
  fatherTransformID: string;
  sourceObjectID: string;
  sourceGUID: string;
  scriptGUID: string;
  componentIDs: string[];
}

/**
 * Walk `lines` once and return every known field finishObject / collectForward-
 * Edges need. The returned shape mirrors what the previous independent
 * scanners produced, so downstream parsed-object population is byte-identical.
 */
export function extractKnownFields(lines: string[]): KnownFields {
  const fields: KnownFields = {
    name: "",
    gameObjectID: "",
    fatherTransformID: "",
    sourceObjectID: "",
    sourceGUID: "",
    scriptGUID: "",
    componentIDs: [],
  };

  for (const line of lines) {
    // m_Name — first match wins (readScalar semantics). Subsequent m_Name
    // lines are ignored.
    if (fields.name === "" && line.startsWith(NAME_PREFIX)) {
      fields.name = cleanScalar(line.slice(NAME_PREFIX.length).trim());
      // readScalar returns "" for an empty value; cleanScalar("") === "" so
      // an `m_Name:` line with no value keeps name === "" and we still treat
      // it as "matched" (mirrors readScalar returning "" from the first
      // match — a later m_Name line would NOT override because the original
      // scanner also returned at the first match).
      continue;
    }
    // m_GameObject — first match wins (readFileIDField semantics).
    if (fields.gameObjectID === "" && line.startsWith(GAMEOBJECT_PREFIX)) {
      fields.gameObjectID = extractFileID(line);
      continue;
    }
    // m_Father — first match wins (readFileIDField semantics).
    if (fields.fatherTransformID === "" && line.startsWith(FATHER_PREFIX)) {
      fields.fatherTransformID = extractFileID(line);
      continue;
    }
    // m_CorrespondingSourceObject — the SAME first matching line yields both
    // the fileID (sourceObjectID) and the guid (sourceGUID). The previous
    // code called readFileIDField then readGUIDField; both scan for the
    // first `  m_CorrespondingSourceObject:` line, so they necessarily land
    // on the same line. Capturing both here in one test mirrors that.
    if (fields.sourceObjectID === "" && line.startsWith(SOURCE_OBJ_PREFIX)) {
      fields.sourceObjectID = extractFileID(line);
      fields.sourceGUID = extractGUID(line);
      continue;
    }
    // m_Script — first match wins (readGUIDField semantics).
    if (fields.scriptGUID === "" && line.startsWith(SCRIPT_PREFIX)) {
      fields.scriptGUID = extractGUID(line);
      continue;
    }
    // - component: {fileID: …} — multi-valued; collect every match
    // (readComponentIDs semantics: skip empty / "0" ids).
    if (line.includes(COMPONENT_MARKER)) {
      const id = extractFileID(line);
      if (id !== "" && id !== "0") fields.componentIDs.push(id);
    }
  }

  return fields;
}

export function extractFileID(line: string): string {
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

export function extractGUID(line: string): string {
  const start = line.indexOf("guid:");
  if (start < 0) return "";
  return scanGUID(line.slice(start + "guid:".length));
}

export function findGUIDs(value: string): string[] {
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

export function findFileIDs(value: string): string[] {
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

export function scanGUID(value: string): string {
  let i = 0;
  while (i < value.length && value[i] === " ") i++;
  const start = i;
  while (i < value.length && isHex(value.charCodeAt(i))) i++;
  if (i === start) return "";
  return value.slice(start, i).toLowerCase();
}

export function isHex(code: number): boolean {
  return (
    (code >= 48 && code <= 57) ||
    (code >= 97 && code <= 102) ||
    (code >= 65 && code <= 70)
  );
}

export function cleanScalar(value: string): string {
  value = value.trim();
  if (value.length >= 2 && value[0] === '"' && value[value.length - 1] === '"') {
    // M31-optimizations Plan 3 / L7 — strip quotes manually before reaching
    // for JSON.parse. The previous implementation routed every double-quoted
    // YAML scalar through JSON.parse, which throws on most non-JSON quotes
    // (backslashes, embedded quotes, etc.) and relies on the catch as the
    // common path. Exception-throwing is expensive. The manual strip below
    // covers the common case (no escape sequences) with zero throws; the
    // JSON.parse fallback is reserved for strings that look like they contain
    // escape sequences (where naive slicing would lose information).
    const inner = value.slice(1, -1);
    if (needsJsonUnescape(inner)) {
      try {
        return JSON.parse(value);
      } catch {
        return inner;
      }
    }
    return inner;
  }
  return value;
}

/**
 * M31-optimizations Plan 3 / L7 — true when the quoted scalar's inner content
 * contains JSON escape sequences (a backslash, or an embedded quote). Naive
 * slicing would drop the backslash's escaping role, so JSON.parse is the
 * correct decoder for these. Otherwise the inner content is already the
 * literal value and the manual strip in {@link cleanScalar} is byte-identical
 * to what JSON.parse would produce (for the no-escape case JSON.parse(value)
 * === value.slice(1, -1)).
 */
function needsJsonUnescape(inner: string): boolean {
  for (let i = 0; i < inner.length; i++) {
    const ch = inner.charCodeAt(i);
    // backslash (0x5C) or double-quote (0x22) — JSON escape markers.
    if (ch === 0x5C || ch === 0x22) return true;
  }
  return false;
}

export function shortGUID(guid: string): string {
  if (guid.length <= 8) return guid;
  return guid.slice(0, 8);
}

// ===========================================================================
// .meta GUID extraction — reads the guid: line from a .meta file's contents.
// ===========================================================================

export function readMetaGUID(data: string): string {
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
