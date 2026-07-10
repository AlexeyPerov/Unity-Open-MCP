// Low-level extraction primitives for the offline YAML/JSON parser.
//
// Extracted from the former monolithic offline.ts (M28-refactoring Plan 3,
// T3.1). These are the leaf scanning functions — GUID/fileID extraction,
// scalar cleaning, header parsing, line-shape classification. They depend only
// on the shared types module; no cross-submodule runtime imports.

import type { HeaderInfo, ParsedObject } from "./types.js";

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
  const fields = id.split(/\s+/);
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
    try {
      return JSON.parse(value);
    } catch {
      return value.slice(1, -1);
    }
  }
  return value;
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
