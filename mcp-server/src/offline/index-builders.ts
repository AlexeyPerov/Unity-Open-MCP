// GUID / script index builders + directory walkers for the offline reader.
//
// Extracted from the former monolithic offline.ts (M28-refactoring Plan 3,
// T3.1). Builds the GUID→path and script-GUID→path indexes by walking .meta
// files under Assets/, plus the recursive file/directory walkers the public
// entry points use. Depends on types + primitives only.

import { readFile, readdir, stat } from "node:fs/promises";
import { join } from "node:path";
import type { GUIDIndex, ParsedAsset, ScriptIndex } from "./types.js";
import { shouldSkipDir, toAssetPath } from "./paths.js";
import { readMetaGUID } from "./primitives.js";

// ===========================================================================
// Parsed-asset GUID extraction — scopes the index builders to only the GUIDs
// a given asset references.
// ===========================================================================

/** Collect every MonoBehaviour m_Script GUID declared by a parsed asset. */
export function scriptGUIDs(asset: ParsedAsset): Set<string> {
  const guids = new Set<string>();
  for (const obj of asset.objects) {
    if (obj.scriptGUID !== "") guids.add(obj.scriptGUID);
  }
  return guids;
}

// ===========================================================================
// .meta GUID reading + path normalization.
// ===========================================================================

export async function safeReadMetaGUID(metaPath: string): Promise<string> {
  try {
    return readMetaGUID(await readFile(metaPath, "utf-8"));
  } catch { return ""; }
}

// ===========================================================================
// GUID index builder — reads .meta files.
// ===========================================================================

export async function buildGUIDIndex(
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

export async function buildScriptIndex(
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

export async function buildScriptIndexForQuery(
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
    const base = scriptPath.split("/").pop() ?? scriptPath;
    const ext = scriptPath.match(/\.[^.]+$/)?.[0] ?? "";
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

export async function walkMeta(
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
// Recursive file walkers (non-.meta) shared by search / find-refs / integrity.
// ===========================================================================

export async function collectFiles(dir: string): Promise<string[]> {
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

export async function resolveGUIDPaths(
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

export async function walkFiles(
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
