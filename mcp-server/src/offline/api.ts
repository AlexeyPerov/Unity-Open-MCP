// Public entry points for the offline asset reader.
//
// Extracted from the former monolithic offline.ts (M28-refactoring Plan 3,
// T3.1). These are the functions the compressible-tool router and tool-router
// call: readAssetOffline, searchAssetsOffline, listAssetsOffline,
// findReferencesOffline, dependenciesOffline, scanIntegrityOffline, plus the
// isOfflineAsset / countHierarchy helpers and the result interfaces.
//
// Composes all the offline submodules. Wires the overrides label-registry at
// import time so the overrides resolver can call back into the hierarchy/
// reference layers without an import cycle.

import { readFile } from "node:fs/promises";
import { basename, extname, join } from "node:path";
import type {
  AssetModel,
  HierarchyNode,
  SearchMatch,
  SearchModel,
  SearchObjectMatch,
} from "../compression/asset-model.js";
import type {
  GUIDIndex,
  ParsedAsset,
  ParsedObject,
  ScriptIndex,
} from "./types.js";
// HierarchyResult is re-exported indirectly via the hierarchy functions; the
// api module does not name the type directly.
import {
  OFFLINE_PARSEABLE_EXTENSIONS,
  kindForPath,
  normalizeKind,
  offlineParseKind,
  parseKindSet,
} from "./types.js";
import { cleanScalar, findGUIDs, readGUIDField } from "./primitives.js";
import { parseAsset } from "./parse.js";
import { displayFieldName } from "./names.js";
import {
  buildAssetModel,
  buildJsonAssetModel,
  runYamlIntegrityChecks,
} from "./model.js";
import {
  buildGUIDIndex,
  buildScriptIndex,
  buildScriptIndexForQuery,
  buildGuidAndScriptIndex,
  buildGuidScriptAndNameIndex,
  collectFiles,
  collectMetaTriples,
  parallelMap,
  resolveGUIDPaths,
  safeReadMetaGUID,
  scriptGUIDs,
  walkFiles,
  walkMeta,
  type MetaTriple,
} from "./index-builders.js";
import { toAssetPath, relativeDir } from "./paths.js";
import { resolveReferences, OVERRIDE_LABEL_HELPERS } from "./references.js";
import { setLabelHelpers } from "./overrides.js";
import {
  buildHierarchy,
  componentsFor,
  flattenHierarchy,
} from "./hierarchy.js";

// Wire the overrides label-registry once at module load. This breaks the
// overrides→hierarchy/references cycle by late-binding the real helpers.
setLabelHelpers(OVERRIDE_LABEL_HELPERS);

// ===========================================================================
// isOfflineAsset / countHierarchy helpers.
// ===========================================================================

/**
 * M24 — full-hierarchy parity helper. The offline `objectCount` counts every
 * YAML object in the file; the live `read_asset` bridge counts only GameObjects
 * + the components attached to them (it walks the loaded Transform tree, not
 * the raw YAML). This helper returns the live-comparable counts so an
 * acceptance test can verify the offline-reconstructed tree matches a live read
 * node-for-node and component-for-component, without depending on the bridge.
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

export function isOfflineAsset(assetPath: string): boolean {
  return offlineParseKind(assetPath) !== null;
}

export interface OfflineReadResult {
  model: AssetModel;
  source: "offline";
}

// ===========================================================================
// readAssetOffline — parse a text-serialized asset → AssetModel.
// ===========================================================================

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

  // M31-optimizations Plan 2 / H5 — build both the script (.cs subset) and
  // field-GUID indices in a SINGLE meta walk by unioning the wanted sets.
  // Previously this was buildScriptIndex (which itself walks the meta tree
  // via buildGUIDIndex) followed by another buildGUIDIndex walk — every cold
  // read_asset paid two back-to-back walks of the same tree.
  const wantedScripts = scriptGUIDs(parsed);
  const wantedGUIDs = collectFieldGUIDs(parsed);
  const { guidIndex, scriptIndex } = await buildGuidAndScriptIndex(
    opts.projectRoot,
    wantedGUIDs,
    wantedScripts,
  );

  const integrity = runYamlIntegrityChecks(parsed, guidIndex);
  return {
    model: { ...buildAssetModel(parsed, scriptIndex, guidIndex, opts.fieldLimit), ...(integrity ? { integrity } : {}) },
    source: "offline",
  };
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

  // M31-optimizations Plan 2 / L2 — when a component query is present, build
  // the script-name → guid → path index from the same single walk that would
  // already populate the guid + script indices. Previously this was a
  // separate buildScriptIndexForQuery walk. The guid-query path still uses
  // resolveGUIDPaths (its early-exit-on-first-match optimization is worth
  // more than collapsing its walk into this one).
  const scriptIndex: ScriptIndex = componentQuery
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

function fieldGUIDs(obj: ParsedObject): Set<string> {
  const guids = new Set<string>();
  for (const line of obj.lines) {
    if (!line.includes("guid:") || line.includes("m_Script")) continue;
    for (const guid of findGUIDs(line)) guids.add(guid);
  }
  return guids;
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

// ===========================================================================
// findReferencesOffline — offline reverse reference lookup (T1.4).
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

    // Fast filter: skip files that don't mention the GUID at all. This stays
    // on the joined content (the cheap fast-reject before any per-line work).
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
      // M31-optimizations Plan 2 / L4 — split content into lines ONCE and
      // pass the line array downstream. Previously extractReferenceLocations
      // re-split the same content it was given; now the single split is
      // visible at the call site and the per-line scan reuses it. The
      // substring pre-filter above already ran on the joined content (the
      // fast-reject), so this is the ONLY split for the verbose path.
      const lines = content.split("\n");
      entry.locations = extractReferenceLocationsFromLines(lines, targetGuid, maxPerFile);
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

/**
 * M31-optimizations Plan 2 / L4 — extract field-location labels for a target
 * GUID from a pre-split line array. The caller ({@link findReferencesOffline})
 * splits the content once and passes the lines here so the verbose path does
 * not re-split the same content downstream. The per-line scan iterates the
 * array directly.
 */
function extractReferenceLocationsFromLines(
  lines: string[],
  targetGuid: string,
  maxPerFile: number,
): string[] {
  const locations: string[] = [];

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
  // NOTE: the inline `_source: "offline"` (here, in emptyDependencies, and in
  // IntegrityScanResult) is intentional and load-bearing. These offline routes
  // are returned by tool-router.routeDependencies / routeScanPaths via a DIRECT
  // JSON.stringify (not wrapped in sourceResult/withSource), so the tag must be
  // baked into the payload + declared on the result type. The tool-router
  // helpers (withSource/sourceResult) are not used here because this layer
  // builds the offline result shape; the router re-stamps _source idempotently
  // only on the live drill-down paths. Migrating to the helper would require
  // routing these offline results through sourceResult too (see review T7.3).
  _source: "offline";
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
  let forwardEdges: ForwardEdge[] = [];
  let brokenGuids: string[] = [];
  let cycles: string[][] = [];
  let forwardSkipped: string | undefined;

  const absPath = join(opts.projectRoot, targetPath);
  let data: string | undefined;
  try {
    data = await readFile(absPath, "utf-8");
  } catch {
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
      forwardSkipped = err instanceof Error ? err.message : String(err);
    }
  }

  // ---- Reverse edges ----
  // M31-optimizations Plan 2 / H4 + M5 — build the project-wide reverse-edge
  // graph ONCE per dependenciesOffline call when impact is requested.
  // Previously the single reverse-edge lookup called findReferencesOffline
  // (one full project walk), and computeTransitiveImpact called
  // findReferencesOffline per BFS frontier node — re-walking the whole
  // project K times for a closure of size K (O(closure × project size)). The
  // graph is a Map<guid, ReverseEdge[]> built in a single pass over
  // parseable assets; both the direct reverse-edge lookup and the impact BFS
  // consult it via pure Map.get, so no per-node project walk remains. The
  // graph is a local variable (per-call only; root AGENTS.md forbids disk
  // caches).
  //
  // When impact is NOT requested, skip the graph build — the direct reverse-
  // edge lookup via findReferencesOffline is cheaper than building the whole
  // project graph (it early-exits the candidate scan as soon as it has read
  // each file once, and produces the verbose-fold fields dependenciesOffline
  // does not need but finds cheaper to inherit than to re-derive).
  const reverseEdgeGraph = includeImpact
    ? await buildReverseEdgeGraph(opts.projectRoot)
    : null;

  const reverseEdges: ReverseEdge[] = reverseEdgeGraph
    ? findReferencesFromGraph(reverseEdgeGraph.graph, targetGuid, targetPath)
    : (await findReferencesOffline({
        assetPath: targetPath || undefined,
        guid: targetGuid,
        detail: "normal",
        projectRoot: opts.projectRoot,
      })).referencedBy.map((e) => ({
        assetPath: e.assetPath,
        guid: e.guid ?? "",
        kind: e.kind,
      }));

  // ---- Transitive impact (optional) ----
  let impact: DependenciesOfflineResult["impact"];
  if (includeImpact && reverseEdgeGraph) {
    impact = await computeTransitiveImpactFromGraph(
      targetGuid,
      targetPath,
      reverseEdgeGraph.graph,
      reverseEdgeGraph.pathToGuid,
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
    // Inline tag is load-bearing — see the note on DependenciesOfflineResult._source.
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
    // Inline tag is load-bearing — see the note on DependenciesOfflineResult._source.
    _source: "offline",
  };
}

/** Collect the forward edges declared by a single asset. */
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
      if (line.includes("m_Script:")) continue;
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

/** Forward-cycle detection via DFS over the transitive forward closure. */
async function detectCyclesOffline(
  startPath: string,
  startEdges: ForwardEdge[],
  projectRoot: string,
  maxDepth: number,
): Promise<string[][]> {
  const cycles: string[][] = [];

  const edgeCache = new Map<string, ForwardEdge[]>();
  edgeCache.set(startPath, startEdges);

  async function edgesOf(path: string): Promise<ForwardEdge[]> {
    const cached = edgeCache.get(path);
    if (cached) return cached;
    try {
      const data = await readFile(join(projectRoot, path), "utf-8");
      const parsed = parseAsset(data);
      const edges = collectForwardEdges(parsed);
      const idx = await buildGUIDIndex(projectRoot, new Set(edges.map((e) => e.guid)));
      resolveForwardEdges(edges, idx);
      edgeCache.set(path, edges);
      return edges;
    } catch {
      edgeCache.set(path, []);
      return [];
    }
  }

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

/**
 * M31-optimizations Plan 2 / H4 + M5 — build the project-wide reverse-edge
 * graph in a SINGLE pass over parseable assets. Returns:
 *   - graph: Map<referenced-guid, ReverseEdge[]> — every asset that references
 *     each guid. Each edge carries the referencing asset's path + kind + its
 *     OWN guid (so the BFS can resolve a frontier node's guid without an
 *     extra .meta read).
 *   - pathToGuid: Map<asset-path, own-guid> — the inverse lookup for the BFS.
 *
 * Used by {@link dependenciesOffline} to serve both the direct reverse-edge
 * lookup AND the transitive impact BFS via pure Map.get, eliminating the
 * O(closure × project size) re-walk the previous per-node
 * findReferencesOffline calls incurred.
 *
 * In-memory, per-call only (a local variable in dependenciesOffline). Root
 * AGENTS.md forbids disk caches; this is the same category as
 * detectCyclesOffline's edgeCache.
 *
 * Edge semantics mirror findReferencesOffline: a file references a guid when
 * its content contains the guid substring. Self-references are filtered at
 * lookup time (findReferencesFromGraph), not here, so the graph captures
 * every edge regardless of the eventual target.
 */
async function buildReverseEdgeGraph(
  projectRoot: string,
): Promise<{
  graph: Map<string, ReverseEdge[]>;
  pathToGuid: Map<string, string>;
}> {
  const graph = new Map<string, ReverseEdge[]>();
  const pathToGuid = new Map<string, string>();
  const candidates = await collectFiles(join(projectRoot, "Assets"));
  // Read each parseable asset once. Scan it for every guid: reference and
  // append the asset's edge to each referenced guid's edge list. Also record
  // the asset's own guid (from its .meta) so the impact BFS can resolve a
  // frontier node's guid without re-reading .meta per hop.
  await parallelMap(candidates, async (absPath) => {
    const ext = extname(absPath).toLowerCase();
    if (!OFFLINE_PARSEABLE_EXTENSIONS.has(ext)) return;
    let content: string;
    try { content = await readFile(absPath, "utf-8"); } catch { return; }
    if (!content.includes("guid:")) return;
    const assetPath = toAssetPath(projectRoot, absPath);
    const kind = kindForPath(absPath);
    // Resolve the asset's own guid once (cheap .meta read, OS-cached).
    const ownGuid = (await safeReadMetaGUID(absPath + ".meta")).toLowerCase();
    const edge: ReverseEdge = { assetPath, guid: ownGuid, kind };
    if (ownGuid !== "") pathToGuid.set(assetPath, ownGuid);
    // findGUIDs returns every guid: substring in the content (lowercased).
    // Dedup within this file via a Set so a guid referenced N times in one
    // asset still produces one edge entry (matches findReferencesOffline,
    // which pushes at most one entry per referencing file).
    const referenced = new Set(findGUIDs(content));
    for (const guid of referenced) {
      let list = graph.get(guid);
      if (!list) { list = []; graph.set(guid, list); }
      list.push(edge);
    }
  });
  return { graph, pathToGuid };
}

/**
 * M31-optimizations Plan 2 / H4 — consult the precomputed reverse-edge graph
 * for a single target guid, mirroring findReferencesOffline's reverse-edge
 * semantics (skip self-references; one entry per referencing asset). Pure
 * Map.get — no I/O. The graph is keyed by lowercased guid; the target guid
 * is already lowercased by dependenciesOffline's resolution path.
 */
function findReferencesFromGraph(
  graph: Map<string, ReverseEdge[]>,
  targetGuid: string,
  targetPath: string,
): ReverseEdge[] {
  const list = graph.get(targetGuid);
  if (!list) return [];
  const out: ReverseEdge[] = [];
  for (const edge of list) {
    if (edge.assetPath === targetPath) continue; // self-reference
    out.push(edge);
  }
  return out;
}

/**
 * M31-optimizations Plan 2 / H4 — graph-driven transitive impact BFS.
 * Replaces the previous computeTransitiveImpact, which called
 * findReferencesOffline per frontier node (re-walking the project K times
 * for a closure of size K — O(closure × project size)). The BFS is now pure
 * Map.get on the precomputed reverse-edge graph; the only remaining per-node
 * work is the pathToGuid lookup (a Map.get, not a .meta read — the asset's
 * own guid was captured once during buildReverseEdgeGraph).
 */
async function computeTransitiveImpactFromGraph(
  startGuid: string,
  startPath: string,
  graph: Map<string, ReverseEdge[]>,
  pathToGuid: Map<string, string>,
  maxDepth: number,
): Promise<NonNullable<DependenciesOfflineResult["impact"]>> {
  const affected: ImpactEntry[] = [];
  const seen = new Set<string>([startPath]);
  let frontier: string[] = [];
  let truncated = false;

  const seed = findReferencesFromGraph(graph, startGuid, startPath);
  for (const e of seed) {
    if (seen.has(e.assetPath)) continue;
    seen.add(e.assetPath);
    frontier.push(e.assetPath);
    affected.push({ assetPath: e.assetPath, depth: 1 });
  }

  for (let depth = 2; depth <= maxDepth; depth++) {
    if (frontier.length === 0) break;
    const next: string[] = [];
    for (const nodePath of frontier) {
      // Resolve the frontier node's guid via the precomputed pathToGuid
      // index — a Map.get, no .meta read. A node whose .meta was missing/
      // blank during the graph build contributes no outgoing edges here,
      // matching the previous safeReadMetaGUID-based path's `if (nodeGuid
      // === "") continue;` guard.
      const nodeGuid = pathToGuid.get(nodePath);
      if (nodeGuid === undefined) continue;
      const referencedByNode = findReferencesFromGraph(graph, nodeGuid, nodePath);
      for (const e of referencedByNode) {
        if (seen.has(e.assetPath)) continue;
        seen.add(e.assetPath);
        next.push(e.assetPath);
        affected.push({ assetPath: e.assetPath, depth });
      }
    }
    frontier = next;
    if (depth === maxDepth && frontier.length > 0) {
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
  // Inline tag is load-bearing — same reason as DependenciesOfflineResult._source
  // (this route is stringified directly by the router, not wrapped in sourceResult).
  _source: "offline";
}

export async function scanIntegrityOffline(
  opts: { projectRoot: string },
): Promise<IntegrityScanResult> {
  const issues: IntegrityScanEntry[] = [];

  // M31-optimizations Plan 2 / H3 — single meta walk for the whole scan.
  // Previously this walked the meta tree 4× (collectFiles, per-file
  // safeReadMetaGUID, walkMeta for orphans, buildGUIDIndex for integrity).
  // The triples carry everything the scan needs: guidByPath, allAssetPaths,
  // orphan detection, and the full GUID index (no second/third walk).
  const triples = await collectMetaTriples(opts.projectRoot);

  // Derive guidByPath and the full GUID index from the single meta-walk pass.
  // `t.assetPath` is absolute (collectMetaTriples strips the .meta suffix
  // from the absolute metaPath); convert to project-relative once.
  const guidByPath = new Map<string, string>();
  const fullGuidIndex: GUIDIndex = new Map();
  for (const t of triples) {
    const assetRel = toAssetPath(opts.projectRoot, t.assetPath);
    if (t.guid !== "") {
      guidByPath.set(assetRel, t.guid);
      fullGuidIndex.set(t.guid, assetRel);
    }
  }
  // allAssetPaths: every real asset on disk (not just those with .meta), used
  // for orphan detection (a .meta whose companion asset is missing) and the
  // assetsScanned count. The previous implementation derived this from a
  // collectFiles walk; that walk is NOT a meta-walk, so keeping it does not
  // regress H3 (which only consolidates the meta walks). The collectFiles
  // walk is bounded-parallel (Plan 2 / M4) and shares the same readdir-order
  // traversal as collectMetaTriples, so iteration order stays byte-identical.
  const assetsDir = join(opts.projectRoot, "Assets");
  const assetFiles = await collectFiles(assetsDir);
  const allAssetPaths = new Set<string>();
  for (const absPath of assetFiles) {
    allAssetPaths.add(toAssetPath(opts.projectRoot, absPath));
  }

  // ---- orphan_meta: .meta whose companion asset is gone ----
  // An orphan is a triple whose companion asset path is NOT in the on-disk
  // asset file set. (Previously detected via a second walkMeta pass diffing
  // against allAssetPaths; now derived from the single triples pass + the
  // one collectFiles walk we already needed for the integrity loop.)
  for (const t of triples) {
    const assetRel = toAssetPath(opts.projectRoot, t.assetPath);
    if (!allAssetPaths.has(assetRel)) {
      issues.push({
        path: assetRel + ".meta",
        code: "orphan_meta",
        severity: "warning",
        detail: `meta file has no companion asset at ${assetRel}`,
      });
    }
  }

  // ---- duplicate_guid: GUID shared by 2+ assets ----
  const pathsByGuid = new Map<string, string[]>();
  for (const [assetPath, guid] of guidByPath) {
    const list = pathsByGuid.get(guid);
    if (list) list.push(assetPath);
    else pathsByGuid.set(guid, [assetPath]);
  }
  // M31-optimizations Plan 2 / H3 — preserve the previous iteration order so
  // duplicate_guid issue ordering stays byte-identical. The previous impl
  // iterated guidByPath (Map insertion order = assetFiles walk order); the
  // triples-derived guidByPath preserves that same order because triples
  // are pushed in walkMeta's readdir-order walk (matching collectFiles).
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
  // fullGuidIndex was populated from the single triples pass above — no
  // separate buildGUIDIndex walk. The asset-body reads below are inherent to
  // scanning (each YAML asset must be parsed to surface its missing refs);
  // this plan only collapsed the meta walks, not the body reads.
  for (const absPath of assetFiles) {
    const ext = extname(absPath).toLowerCase();
    if (!OFFLINE_PARSEABLE_EXTENSIONS.has(ext)) continue;
    const assetPath = toAssetPath(opts.projectRoot, absPath);
    let data: string;
    try { data = await readFile(absPath, "utf-8"); } catch { continue; }
    if (!data.includes("guid:")) continue;

    try {
      const parsed = parseAsset(data);
      parsed.path = assetPath;
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
      // Not parseable as YAML — skip. JSON integrity is per-read only.
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
    // Inline tag is load-bearing — see the note on IntegrityScanResult._source.
    _source: "offline",
  };
}
