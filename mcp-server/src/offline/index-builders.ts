// GUID / script index builders + directory walkers for the offline reader.
//
// Extracted from the former monolithic offline.ts (M28-refactoring Plan 3,
// T3.1). Builds the GUID→path and script-GUID→path indexes by walking .meta
// files under Assets/, plus the recursive file/directory walkers the public
// entry points use. Depends on types + primitives only.

import { readFile, readdir, stat } from "node:fs/promises";
import { join } from "node:path";
import type { GUIDIndex, ParsedAsset, ScriptIndex } from "./types.js";
import { shouldSkipDir, toAssetPath, extractExtension } from "./paths.js";
import { readMetaGUID } from "./primitives.js";

// ===========================================================================
// Bounded-parallel map helper (M31-optimizations Plan 2 / M4).
//
// Walkers fan out `Promise.all(entries.map(...))` to overlap stat/readdir
// syscalls across sibling entries. Unbounded fan-out on a project with
// thousands of sibling files can exhaust the process fd table before the
// first responses settle. `parallelMap` applies `fn` to each entry inside
// fixed-size chunks, awaiting each chunk before starting the next. The chunk
// order matches input order so callers that push results in iteration order
// keep deterministic output.
//
// 64 is the same heuristic the existing walkMeta path effectively relies on
// (its Promise.all burst typically stays well under the per-process soft fd
// limit on macOS/Linux, ~256). Tuned to leave headroom for the rest of the
// process while still overlapping the bulk of the stat latency.
// ===========================================================================

const PARALLEL_CHUNK_SIZE = 64;

export async function parallelMap<T, R>(
  items: readonly T[],
  fn: (item: T, index: number) => Promise<R>,
  chunkSize: number = PARALLEL_CHUNK_SIZE,
): Promise<R[]> {
  const out: R[] = [];
  for (let i = 0; i < items.length; i += chunkSize) {
    const slice = items.slice(i, i + chunkSize);
    const mapped = await Promise.all(
      slice.map((item, j) => fn(item, i + j)),
    );
    for (const m of mapped) out.push(m);
  }
  return out;
}

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
  // M31-optimizations Plan 2 / L2 — delegate to buildGuidScriptAndNameIndex
  // so the script-name → guid → path mapping is produced by the same
  // single-walk primitive the read_asset path uses. The function's public
  // contract (scriptIndex filtered by name substring) is preserved; the
  // scriptNameIndex + guidIndex byproducts are dropped here because the
  // call site only needs the script subset.
  const q = query.trim().toLowerCase();
  if (q === "") return new Map<string, string>();
  const { scriptNameIndex } = await buildGuidScriptAndNameIndex(projectRoot, {
    componentQuery: q,
  });
  const index: ScriptIndex = new Map();
  for (const [guid, entry] of scriptNameIndex) index.set(guid, entry.path);
  return index;
}

// ===========================================================================
// M31-optimizations Plan 2 / H5 + L2 — combined single-walk index builder.
//
// readAssetOffline previously called buildScriptIndex then buildGUIDIndex
// back-to-back. buildScriptIndex internally calls buildGUIDIndex, so the meta
// tree was walked twice in a row for every cold read_asset (a cache miss).
// searchAssetsOffline with a `component` query called buildScriptIndexForQuery
// (a third walk with the same shape).
//
// buildGuidAndScriptIndex walks the meta tree ONCE, populating both the
// guidIndex and the scriptIndex (the .cs subset) from the same pass. Callers
// union their wanted-GUID sets first so the single walk serves both needs.
// buildGuidScriptAndNameIndex extends the same walk to also collect the
// script-name → guid → path mapping that the component-query path needs,
// collapsing L2's separate buildScriptIndexForQuery walk into the same pass.
// ===========================================================================

export interface ScriptNameEntry {
  /** Lowercased script name (filename without extension) — the substring the
   *  component query matches against. */
  name: string;
  /** Project-relative asset path of the .cs file. */
  path: string;
}

export interface CombinedGuidScriptIndex {
  guidIndex: GUIDIndex;
  scriptIndex: ScriptIndex;
}

export interface CombinedGuidScriptNameIndex extends CombinedGuidScriptIndex {
  /** guid → { name, path } for every .cs file seen during the walk. Used by
   *  the search_assets `component` query to resolve a script-name substring
   *  to its guid(s) without a second walk. */
  scriptNameIndex: Map<string, ScriptNameEntry>;
}

/**
 * Build both the GUID→path index and the script (.cs) subset in a SINGLE meta
 * walk, filtered to the union of `wantedGuids` and `wantedScripts`. Mirrors
 * the previous two-call sequence (buildScriptIndex + buildGUIDIndex) but
 * collapses the redundant second walk. `wantedScripts` is a separate argument
 * (rather than folded into `wantedGuids`) so the script subset can be
 * filtered against it even when the caller wants all field GUIDs.
 *
 * When both wanted sets are empty, returns empty indices without touching the
 * filesystem (matches the previous early-out in buildGUIDIndex).
 */
export async function buildGuidAndScriptIndex(
  projectRoot: string,
  wantedGuids?: Set<string>,
  wantedScripts?: Set<string>,
): Promise<CombinedGuidScriptIndex> {
  const guidIndex: GUIDIndex = new Map();
  const scriptIndex: ScriptIndex = new Map();
  if (wantedGuids && wantedGuids.size === 0 && (!wantedScripts || wantedScripts.size === 0)) {
    return { guidIndex, scriptIndex };
  }
  // Union the wanted sets: one entry is read at most once during the walk.
  // A meta file's GUID is interesting if it appears in either set; the script
  // subset is the .cs entries within that intersection.
  const wanted = new Set<string>();
  if (wantedGuids) for (const g of wantedGuids) wanted.add(g);
  if (wantedScripts) for (const g of wantedScripts) wanted.add(g);
  const assetsDir = join(projectRoot, "Assets");
  await walkMeta(assetsDir, async (metaPath) => {
    try {
      const data = await readFile(metaPath, "utf-8");
      let guid = readMetaGUID(data);
      if (guid === "") return;
      guid = guid.toLowerCase();
      if (wanted.size > 0 && !wanted.has(guid)) return;
      const assetPath = toAssetPath(projectRoot, metaPath.slice(0, -5));
      guidIndex.set(guid, assetPath);
      if (assetPath.endsWith(".cs")) scriptIndex.set(guid, assetPath);
    } catch { /* skip */ }
  });
  return { guidIndex, scriptIndex };
}

/**
 * Extend {@link buildGuidAndScriptIndex} with a script-name → guid → path
 * mapping for every `.cs` file seen during the walk. Used by search_assets's
 * `component` query (L2): instead of a separate `buildScriptIndexForQuery`
 * walk, the component path derives its script-name-substring index from the
 * same single walk that builds the guid + script indices.
 *
 * When a `componentQuery` is supplied, only the .cs files whose lowercased
 * filename contains the query are added to `scriptNameIndex` — matching the
 * previous buildScriptIndexForQuery filter exactly. Pass undefined to collect
 * every script's name mapping (the search_assets guid-query path uses the
 * guidIndex only, but the walk is shared so the name index is a free
 * byproduct).
 */
export async function buildGuidScriptAndNameIndex(
  projectRoot: string,
  opts: {
    wantedGuids?: Set<string>;
    wantedScripts?: Set<string>;
    /** Lowercased substring the script name must contain to land in
     *  scriptNameIndex. Undefined collects every .cs name. */
    componentQuery?: string;
  } = {},
): Promise<CombinedGuidScriptNameIndex> {
  const guidIndex: GUIDIndex = new Map();
  const scriptIndex: ScriptIndex = new Map();
  const scriptNameIndex = new Map<string, ScriptNameEntry>();
  const wanted = new Set<string>();
  if (opts.wantedGuids) for (const g of opts.wantedGuids) wanted.add(g);
  if (opts.wantedScripts) for (const g of opts.wantedScripts) wanted.add(g);
  if (wanted.size === 0 && opts.componentQuery === undefined) {
    return { guidIndex, scriptIndex, scriptNameIndex };
  }
  const q = opts.componentQuery?.trim().toLowerCase();
  const assetsDir = join(projectRoot, "Assets");
  await walkMeta(assetsDir, async (metaPath) => {
    if (!metaPath.endsWith(".cs.meta")) {
      // Non-script meta: only relevant for the wanted-guid set.
      if (wanted.size === 0) return;
      try {
        const data = await readFile(metaPath, "utf-8");
        let guid = readMetaGUID(data);
        if (guid === "") return;
        guid = guid.toLowerCase();
        if (!wanted.has(guid)) return;
        guidIndex.set(guid, toAssetPath(projectRoot, metaPath.slice(0, -5)));
      } catch { /* skip */ }
      return;
    }
    // .cs.meta — derive the script name from the path (no file body read
    // needed; the name is the filename without extension). Mirror
    // buildScriptIndexForQuery's name-extraction exactly so the substring
    // filter matches byte-for-byte. M31-optimizations Plan 3 / L8-offline —
    // extension extraction delegates to the shared extractExtension helper
    // (was an inline `scriptPath.match(/\.[^.]+$/)` literal on the hot
    // meta-walk path; hoisted to paths.ts so this and overrides.ts share one
    // compile).
    const scriptPath = metaPath.slice(0, -5);
    const base = scriptPath.split("/").pop() ?? scriptPath;
    const ext = extractExtension(scriptPath);
    const name = (ext ? base.slice(0, -ext.length) : base).toLowerCase();
    const assetPath = toAssetPath(projectRoot, scriptPath);
    let guid = "";
    try {
      guid = readMetaGUID(await readFile(metaPath, "utf-8"));
    } catch { /* skip */ }
    if (guid === "") return;
    guid = guid.toLowerCase();
    // Only populate guidIndex/scriptIndex when a wanted set is in force —
    // callers that pass only a componentQuery (e.g. buildScriptIndexForQuery)
    // never consult those maps, so skipping their population saves the per-cs
    // Map.set work in that path.
    if (wanted.size > 0 && wanted.has(guid)) {
      guidIndex.set(guid, assetPath);
      scriptIndex.set(guid, assetPath);
    }
    // scriptNameIndex is populated for matching names regardless of the
    // wanted set — the component-query path resolves a script-name substring
    // to its guid(s) unconditionally (mirrors buildScriptIndexForQuery,
    // which never consulted a wanted set).
    if (q === undefined || name.includes(q)) {
      scriptNameIndex.set(guid, { name, path: assetPath });
    }
  });
  return { guidIndex, scriptIndex, scriptNameIndex };
}

// ===========================================================================
// M31-optimizations Plan 2 — test-only walk counters.
//
// The single-walk acceptance criteria (H3 / H5) are easiest to verify with a
// side-channel counter of how many times the meta-tree walker is entered per
// operation. The counters are module-level, reset via resetWalkCounters, and
// incremented at the top of walkMeta / collectFiles. They have zero overhead
// in production (one integer increment per walk entry) and no effect on
// behavior. Read via walkMetaCount / collectFilesCount.
// ===========================================================================

let walkMetaCount = 0;
let collectFilesCount = 0;

/** Test seam: reset the walk counters to zero. */
export function resetWalkCounters(): void {
  walkMetaCount = 0;
  collectFilesCount = 0;
}

/** Test seam: number of times `walkMeta` has been entered since the last
 *  reset. Each call counts the top-level entry plus one recursive entry per
 *  subdirectory — so for a tree with D directories, a single walk reports
 *  walkMetaCount === D. */
export function getWalkMetaCount(): number {
  return walkMetaCount;
}

/** Test seam: number of times `collectFiles` has been entered since the last
 *  reset. Same counting shape as walkMetaCount. */
export function getCollectFilesCount(): number {
  return collectFilesCount;
}

export async function walkMeta(
  dir: string,
  fn: (metaPath: string) => Promise<void>,
): Promise<void> {
  walkMetaCount++;
  let entries: string[];
  try { entries = await readdir(dir); } catch { return; }
  // M31-optimizations Plan 2 / M4 — bounded-parallel fan-out (was an unbounded
  // Promise.all burst; the parallelMap helper caps concurrency at
  // PARALLEL_CHUNK_SIZE so a project with thousands of sibling files does not
  // exhaust the process fd table). Iteration order is preserved.
  await parallelMap(entries, async (name) => {
    if (shouldSkipDir(name)) return;
    const fullPath = join(dir, name);
    try {
      const s = await stat(fullPath);
      if (s.isDirectory()) await walkMeta(fullPath, fn);
      else if (name.endsWith(".meta")) await fn(fullPath);
    } catch { /* skip */ }
  });
}

// ===========================================================================
// M31-optimizations Plan 2 / H3 — single meta-walk primitive returning
// { metaPath, assetPath, guid } triples for the whole Assets/ tree.
//
// scanIntegrityOffline previously walked the meta tree 4× per scan (collect-
// Files, per-file safeReadMetaGUID, walkMeta for orphans, buildGUIDIndex for
// the integrity check). collectMetaTriples walks once, returning everything
// the scan needs: every .meta path, its companion asset path (the path
// without the .meta suffix — which may or may not exist on disk, so callers
// detect orphans by checking the assetPaths set), and the lowercased GUID
// (empty string when the .meta file's guid field is missing/blank).
// ===========================================================================

export interface MetaTriple {
  /** Absolute path to the .meta file. */
  metaPath: string;
  /** Companion asset path (metaPath without the .meta suffix). May not exist
   *  on disk — an orphan .meta. Absolute, matching metaPath's form. */
  assetPath: string;
  /** Lowercased GUID from the .meta file, or "" when missing/blank. */
  guid: string;
}

/**
 * Walk the `Assets/` tree once and return one {@link MetaTriple} per `.meta`
 * file. Each triple carries the .meta path, its companion asset path (which
 * may not exist — an orphan), and the lowercased GUID (or "" when missing).
 * Callers derive guidByPath / allAssetPaths / fullGuidIndex / orphan detection
 * from this single pass instead of re-walking the tree for each derivation.
 */
export async function collectMetaTriples(projectRoot: string): Promise<MetaTriple[]> {
  const triples: MetaTriple[] = [];
  const assetsDir = join(projectRoot, "Assets");
  await walkMeta(assetsDir, async (metaPath) => {
    let guid = "";
    try {
      guid = readMetaGUID(await readFile(metaPath, "utf-8")).toLowerCase();
    } catch { /* leave guid = "" */ }
    triples.push({ metaPath, assetPath: metaPath.slice(0, -5), guid });
  });
  return triples;
}

// ===========================================================================
// Recursive file walkers (non-.meta) shared by search / find-refs / integrity.
// ===========================================================================

/**
 * Recursively collect non-`.meta` file paths under `dir`.
 *
 * M31-optimizations Plan 2 / M4 + L5 — fans out via `parallelMap` (bounded
 * `Promise.all` chunks) instead of sequentially awaiting each sibling's
 * stat/readdir, and uses an order-stable merge (each entry's results land in
 * a fixed input-order slot) instead of the previous `results.push(...await
 * collectFiles(fullPath))` spread (which was O(depth × N) element copies).
 *
 * Output ordering matches the previous implementation: entries appear in
 * `readdir` order, with each directory's descendants inlined where the
 * directory sat in its parent's listing. parallelMap preserves input order
 * across chunks, and the slot-based merge preserves it within a chunk — so a
 * subdir's descendants always land in `results` at the position the subdir
 * occupied, regardless of which sibling's stat settled first.
 */
export async function collectFiles(dir: string): Promise<string[]> {
  collectFilesCount++;
  let entries: string[];
  try { entries = await readdir(dir); } catch { return []; }
  // Each entry contributes a list of paths (a file → [fullPath]; a subdir →
  // its own collectFiles result; an error/skip → []). parallelMap returns
  // those lists in input order; flattening once at the end keeps the result
  // deterministic without an accumulator race between concurrent subdirs.
  const perEntry = await parallelMap(entries, async (name) => {
    if (shouldSkipDir(name)) return [];
    const fullPath = join(dir, name);
    try {
      const s = await stat(fullPath);
      if (s.isDirectory()) return collectFiles(fullPath);
      if (!name.endsWith(".meta")) return [fullPath];
    } catch { /* skip */ }
    return [];
  });
  const results: string[] = [];
  for (const list of perEntry) for (const p of list) results.push(p);
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
