// M9 Plan 2 — Shared compression + "compact before complete" renderer.
//
// Single runtime module (so it loads cleanly under node --experimental-strip-
// types in tests: no cross-file runtime imports). Type-only contract lives in
// asset-model.ts (stripped at test time).
//
// Two responsibilities, both authoritative here:
//  1. unity-scanner-style compression primitives (compressNames, component-set
//     declarations, render-only leaf folding, omission counts).
//  2. the drill-down renderer that turns an AssetModel / SearchModel into the
//     compact wire response (default = MAP; drill-down flags expand detail).
//
// Ported from references/unity-scanner-master/internal/format/compress.go and
// the folding logic in cmd/read.go, adapted to operate on AssetModel. Both the
// live path (bridge -> AssetModel JSON -> MCP server compresses) and the future
// offline path (M9 Plan 3 parser -> AssetModel -> same code) flow through here.

import type {
  AssetModel,
  AssetComponent,
  HierarchyNode,
  PrefabOverrideEntry,
  SearchModel,
  SearchMatch,
  SearchObjectMatch,
} from "./asset-model.js";

// ===========================================================================
// compressNames — fold numbered runs of 3+ (Recipe_001..018).
// ===========================================================================

/**
 * Fold stable numbered sequences such as Recipe_001..018. Runs of fewer than
 * three stay explicit. Names without a trailing number pass through. Ported
 * from unity-scanner CompressNames (compress.go).
 */
export function compressNames(names: string[]): string[] {
  if (names.length === 0) return [];

  const seen = new Set<string>();
  const unique: string[] = [];
  for (const name of names) {
    if (name === "" || seen.has(name)) continue;
    seen.add(name);
    unique.push(name);
  }
  unique.sort();

  const numbered: NumberedName[] = [];
  const plain: string[] = [];
  for (const name of unique) {
    const parsed = splitTrailingNumber(name);
    if (parsed === null) {
      plain.push(name);
    } else {
      numbered.push(parsed);
    }
  }

  numbered.sort((a, b) => {
    if (a.prefix !== b.prefix) return a.prefix < b.prefix ? -1 : 1;
    if (a.width !== b.width) return a.width - b.width;
    return a.num - b.num;
  });

  const out: string[] = [...plain];

  let i = 0;
  while (i < numbered.length) {
    let j = i + 1;
    while (
      j < numbered.length &&
      numbered[j].prefix === numbered[i].prefix &&
      numbered[j].width === numbered[i].width &&
      numbered[j].num === numbered[j - 1].num + 1
    ) {
      j++;
    }

    if (j - i >= 3) {
      const first = numbered[i];
      const last = numbered[j - 1];
      out.push(
        `${first.prefix}${String(first.num).padStart(first.width, "0")}..${String(last.num).padStart(last.width, "0")}`,
      );
    } else {
      for (let k = i; k < j; k++) out.push(numbered[k].raw);
    }
    i = j;
  }

  out.sort();
  return out;
}

interface NumberedName {
  raw: string;
  prefix: string;
  num: number;
  width: number;
}

function splitTrailingNumber(name: string): NumberedName | null {
  let end = name.length;
  let start = end;
  while (start > 0 && name[start - 1] >= "0" && name[start - 1] <= "9") {
    start--;
  }
  if (start === end) return null;

  let num = 0;
  for (let i = start; i < end; i++) {
    const digit = name.charCodeAt(i) - "0".charCodeAt(0);
    if (num > (Number.MAX_SAFE_INTEGER - digit) / 10) return null;
    num = num * 10 + digit;
  }
  return { raw: name, prefix: name.slice(0, start), num, width: end - start };
}

// ===========================================================================
// Component classification (mirrors unity-scanner read.go).
// ===========================================================================

export function isTrivialComponent(name: string): boolean {
  switch (name) {
    case "Transform":
    case "RectTransform":
    case "MeshFilter":
    case "MeshRenderer":
    case "SkinnedMeshRenderer":
    case "SpriteRenderer":
    case "CanvasRenderer":
    case "LineRenderer":
      return true;
    default:
      return false;
  }
}

export function isRenderOnly(componentNames: string[]): boolean {
  if (componentNames.length === 0) return false;
  let hasRenderer = false;
  for (const name of componentNames) {
    if (!isTrivialComponent(name)) return false;
    if (name !== "Transform" && name !== "RectTransform") hasRenderer = true;
  }
  return hasRenderer;
}

export function hasFocusComponent(componentNames: string[]): boolean {
  return componentNames.some((name) => !isTrivialComponent(name));
}

export function componentMatches(component: AssetComponent, query: string): boolean {
  if (query === "") return true;
  const q = query.toLowerCase();
  if (component.name.toLowerCase().includes(q)) return true;
  if (component.scriptPath && component.scriptPath.toLowerCase().includes(q)) return true;
  return false;
}

export function displayObjectName(node: HierarchyNode): string {
  return node.name !== "" ? node.name : `<unnamed:${node.fileID ?? node.path}>`;
}

// ===========================================================================
// Component-set declaration (CMP) + hierarchy flattening.
// ===========================================================================

export interface ComponentSetDeclaration {
  code: string;
  names: string[];
}

export interface AssignedNode {
  node: HierarchyNode;
  index: number;
  componentNames: string[];
  componentSet: string | null;
  focus: boolean;
  renderOnly: boolean;
}

export function flattenAndAssign(
  roots: HierarchyNode[],
): { rows: AssignedNode[]; sets: ComponentSetDeclaration[] } {
  const rows: AssignedNode[] = [];
  const codeByKey = new Map<string, string>();
  const sets: ComponentSetDeclaration[] = [];

  const walk = (nodes: HierarchyNode[]) => {
    for (const node of nodes) {
      const componentNames = node.components.map((c) => c.name);
      const key = componentNames.join("\u0000");
      let code: string | null = null;
      if (key !== "") {
        const existing = codeByKey.get(key);
        if (existing !== undefined) {
          code = existing;
        } else {
          code = `c${sets.length + 1}`;
          codeByKey.set(key, code);
          sets.push({ code, names: componentNames });
        }
      }
      rows.push({
        node,
        index: rows.length,
        componentNames,
        componentSet: code,
        focus: hasFocusComponent(componentNames),
        renderOnly: isRenderOnly(componentNames),
      });
      walk(node.children);
    }
  };
  walk(roots);
  return { rows, sets };
}

// ===========================================================================
// Render-only leaf folding + omission counts.
// ===========================================================================

export interface FoldedRow {
  index: number;
  endIndex: number;
  depth: number;
  name: string;
  path: string;
  componentSet: string | null;
  count: number;
  foldedNames?: string[];
  folded: boolean;
}

export interface FoldResult {
  rows: FoldedRow[];
  hiddenByDepth: number;
  hiddenByLimit: number;
  collapsed: number;
}

/**
 * Walk the assigned rows and produce the folded TREE representation. Render-only
 * leaf runs of 3+ (same depth, same component set, no children, no focus) are
 * collapsed into a single folded row whose names run through compressNames.
 * Depth and limit caps produce omission counts instead of silent truncation.
 *
 * Mirrors unity-scanner printTreeRows / collapsibleRunEnd (read.go). Full-tree
 * mode (no folding) is selected by `fold = false`.
 */
export function foldHierarchy(
  rows: AssignedNode[],
  opts: { depth?: number; limit?: number; fold?: boolean } = {},
): FoldResult {
  const depth = opts.depth ?? -1;
  const limit = opts.limit ?? 0;
  const fold = opts.fold ?? true;

  const visible: AssignedNode[] = [];
  let hiddenByDepth = 0;
  // rows is DFS-ordered: a node's entire subtree occupies a consecutive run.
  // When depth-capping, skip the whole run so descendants are not re-counted.
  let i = 0;
  while (i < rows.length) {
    const row = rows[i];
    if (depth >= 0 && row.node.depth > depth) {
      const subtreeSize = 1 + countDescendants(row.node);
      hiddenByDepth += subtreeSize;
      i += subtreeSize;
      continue;
    }
    visible.push(row);
    i++;
  }

  const out: FoldedRow[] = [];
  let collapsed = 0;

  let f = 0;
  while (f < visible.length) {
    const first = visible[f];
    if (fold) {
      let end = f + 1;
      while (
        end < visible.length &&
        canCollapse(first) &&
        canCollapse(visible[end]) &&
        visible[end].node.depth === first.node.depth &&
        visible[end].componentSet === first.componentSet
      ) {
        end++;
      }
      if (end - f >= 3) {
        const group = visible.slice(f, end);
        const names = group.map((r) => displayObjectName(r.node));
        out.push({
          index: first.index,
          endIndex: group[group.length - 1].index,
          depth: first.node.depth,
          name: names[0],
          path: first.node.path,
          componentSet: first.componentSet,
          count: group.length,
          foldedNames: compressNames(names),
          folded: true,
        });
        collapsed += group.length;
        f = end;
        continue;
      }
    }
    out.push({
      index: first.index,
      endIndex: first.index,
      depth: first.node.depth,
      name: displayObjectName(first.node),
      path: first.node.path,
      componentSet: first.componentSet,
      count: 1,
      folded: false,
    });
    f++;
  }

  let hiddenByLimit = 0;
  if (limit > 0 && out.length > limit) {
    let droppedNodes = 0;
    for (const row of out.slice(limit)) droppedNodes += row.count;
    hiddenByLimit = droppedNodes;
    out.length = limit;
  }

  return { rows: out, hiddenByDepth, hiddenByLimit, collapsed };
}

function canCollapse(row: AssignedNode): boolean {
  return row.renderOnly && !row.focus && row.node.children.length === 0;
}

function countDescendants(node: HierarchyNode): number {
  return node.children.reduce((sum, child) => sum + 1 + countDescendants(child), 0);
}

// ===========================================================================
// Drill-down renderer.
// ===========================================================================

export type DetailLevel = "summary" | "normal" | "verbose";

export interface RenderOptions {
  detail?: DetailLevel;
  component?: string;
  path?: string;
  id?: string;
  override?: boolean;
  depth?: number;
  limit?: number;
}

export interface TreeNodeOut {
  idx: number;
  name: string;
  depth: number;
  cmp?: string;
  toIdx?: number;
  names?: string[];
  count?: number;
  components?: string[];
}

export interface ComponentMatchOut {
  object: string;
  component: string;
  scriptPath?: string;
  fields?: { name: string; value: string }[];
  moreFieldsHidden?: number;
}

export interface IdResult {
  id: string;
  matched: boolean;
  object?: {
    name?: string;
    path?: string;
    components?: string[];
    fields?: { name: string; value: string }[];
  };
  note?: string;
}

export interface CompactAssetResult {
  asset: string;
  path: string;
  guid?: string;
  objects: number;
  components: number;
  depth?: number;
  cmp?: Record<string, string[]>;
  tree?: TreeNodeOut[];
  flatObjects?: AssetModel["flatObjects"];
  note?: string;
  moreHidden?: number;
  collapsed?: number;
  hint?: string;
  componentMatches?: ComponentMatchOut[];
  componentQuery?: string;
  pathScope?: string;
  idResult?: IdResult;
  overrides?: PrefabOverrideEntry[];
}

/** Render the compact asset summary / drill-down. */
export function renderAssetSummary(
  model: AssetModel,
  opts: RenderOptions = {},
): CompactAssetResult {
  const detail = opts.detail ?? "summary";
  const result: CompactAssetResult = {
    asset: model.kind,
    path: model.path,
    guid: model.guid,
    objects: model.objectCount,
    components: model.componentCount,
  };

  // Non-hierarchical assets (ScriptableObject / Material / .asset): no TREE.
  if (model.roots.length === 0) {
    result.flatObjects = model.flatObjects;
    if (model.note) result.note = model.note;
    return result;
  }

  // --id drill-down: offline-only (live bridge omits fileIDs).
  if (opts.id !== undefined && opts.id !== "") {
    result.idResult = resolveById(model, opts.id);
    return result;
  }

  // --component drill-down: list matching nodes + their fields.
  if (opts.component !== undefined && opts.component !== "") {
    result.componentQuery = opts.component;
    result.componentMatches = resolveComponentDrilldown(model, opts.component);
    return result;
  }

  // --override drill-down: prefab variant override list.
  if (opts.override) {
    if (model.overrides && model.overrides.length > 0) {
      result.overrides = model.overrides;
    } else {
      result.note = "no prefab overrides (asset has no PrefabInstance or no modifications)";
    }
    return result;
  }

  // Default / --path / detail: folded TREE.
  const { rows, sets } = flattenAndAssign(model.roots);

  if (sets.length > 0) {
    const cmp: Record<string, string[]> = {};
    for (const set of sets) cmp[set.code] = set.names;
    result.cmp = cmp;
  }

  const scopedRows = opts.path
    ? rows.filter((r) => containsFold(r.node.path, opts.path!))
    : rows;

  const folded = foldHierarchy(scopedRows, {
    depth: opts.depth ?? -1,
    limit: opts.limit ?? defaultLimit(detail),
    fold: detail !== "verbose",
  });

  result.tree = folded.rows.map((row) => toTreeNode(row, detail, sets));
  if (opts.depth !== undefined && opts.depth >= 0) result.depth = opts.depth;
  if (opts.path) result.pathScope = opts.path;

  const hidden = folded.hiddenByDepth + folded.hiddenByLimit;
  if (hidden > 0 || folded.collapsed > 0) {
    result.moreHidden = hidden;
    result.collapsed = folded.collapsed;
    if (!opts.path) {
      result.hint =
        "use detail=verbose, component=<name>, path=<subtree>, or id=<fileID> to drill down";
    }
  }

  return result;
}

function toTreeNode(
  row: FoldedRow,
  detail: DetailLevel,
  sets: ComponentSetDeclaration[],
): TreeNodeOut {
  const node: TreeNodeOut = {
    idx: row.index,
    name: row.folded && row.foldedNames ? row.foldedNames.join(", ") : row.name,
    depth: row.depth,
  };
  if (row.componentSet) node.cmp = row.componentSet;
  if (row.folded) {
    node.toIdx = row.endIndex;
    node.count = row.count;
  }
  const hasCode = row.componentSet !== null;
  const showInline = detail === "verbose" || (detail === "normal" && !hasCode);
  if (showInline && !row.folded && row.componentSet !== null) {
    const decl = sets.find((s) => s.code === row.componentSet);
    if (decl && decl.names.length > 0) node.components = decl.names;
  }
  return node;
}

function resolveComponentDrilldown(model: AssetModel, query: string): ComponentMatchOut[] {
  const out: ComponentMatchOut[] = [];
  const walk = (node: HierarchyNode) => {
    for (const component of node.components) {
      if (!componentMatches(component, query)) continue;
      const match: ComponentMatchOut = {
        object: node.path,
        component: component.name,
      };
      if (component.scriptPath) match.scriptPath = component.scriptPath;
      if (component.fields && component.fields.length > 0) {
        match.fields = component.fields.map((f) => ({ name: f.name, value: f.value }));
      }
      out.push(match);
    }
    for (const child of node.children) walk(child);
  };
  for (const root of model.roots) walk(root);
  return out;
}

function resolveById(model: AssetModel, id: string): IdResult {
  const walk = (node: HierarchyNode): IdResult | null => {
    if (node.fileID === id) {
      return {
        id,
        matched: true,
        object: {
          name: node.name,
          path: node.path,
          components: node.components.map((c) => c.name),
        },
      };
    }
    for (const child of node.children) {
      const found = walk(child);
      if (found) return found;
    }
    return null;
  };
  for (const root of model.roots) {
    const found = walk(root);
    if (found) return found;
  }
  return {
    id,
    matched: false,
    note: model.roots.some((r) => r.fileID === undefined)
      ? "live bridge does not expose fileIDs; re-read via the offline parser (later milestone) or use component/path drill-down"
      : "no object with that fileID",
  };
}

function defaultLimit(detail: DetailLevel): number {
  switch (detail) {
    case "summary":
      return 60;
    case "normal":
      return 120;
    case "verbose":
      return 0;
  }
}

function containsFold(haystack: string, needle: string): boolean {
  return haystack.toLowerCase().includes(needle.toLowerCase());
}

// ===========================================================================
// Search rendering.
// ===========================================================================

export interface CompactSearchResult {
  query: SearchModel["query"];
  matchCount: number;
  shown: number;
  matches: SearchMatchOut[];
  truncated: number;
  ext?: Record<string, string>;
}

export interface SearchMatchOut {
  path: string;
  kind: string;
  guid?: string;
  reasons: string[];
  objects?: SearchObjectMatch[];
  moreObjectsHidden?: number;
}

/**
 * Render search matches. Reason tags are preserved per file; object listings are
 * capped per file with a moreObjectsHidden count. Paths are compacted (Assets/
 * prefix dropped) and an EXT table declared once when multiple kinds appear.
 */
export function renderSearchSummary(
  model: SearchModel,
  opts: { objectLimit?: number; matchLimit?: number } = {},
): CompactSearchResult {
  const objectLimit = opts.objectLimit ?? 12;
  const matchLimit = opts.matchLimit ?? 0;

  const all = model.matches;
  const shown = matchLimit > 0 ? Math.min(all.length, matchLimit) : all.length;

  const kinds = new Set<string>();
  for (const match of all) kinds.add(match.kind);

  const result: CompactSearchResult = {
    query: model.query,
    matchCount: model.matchCount,
    shown,
    matches: all.slice(0, shown).map((match) => toSearchMatchOut(match, objectLimit)),
    truncated: model.truncated,
  };

  if (kinds.size > 0) {
    result.ext = {};
    for (const kind of kinds) result.ext[kind] = extForKind(kind);
  }

  if (shown < all.length) {
    result.truncated = result.truncated + (all.length - shown);
  }

  return result;
}

function toSearchMatchOut(match: SearchMatch, objectLimit: number): SearchMatchOut {
  const out: SearchMatchOut = {
    path: compactPath(match.path),
    kind: match.kind,
    reasons: match.reasons,
  };
  if (match.guid) out.guid = match.guid;
  if (match.objects && match.objects.length > 0) {
    out.objects = match.objects.slice(0, objectLimit);
    if (match.objects.length > objectLimit) {
      out.moreObjectsHidden = match.objects.length - objectLimit;
    }
  }
  return out;
}

/** Drop the Assets/ prefix (declared once via EXT) and normalize slashes. */
export function compactPath(path: string): string {
  let p = path.replace(/\\/g, "/").replace(/^\.\//, "");
  if (p.toLowerCase().startsWith("assets/")) p = p.slice("assets/".length);
  return p;
}

export function extForKind(kind: string): string {
  switch (kind) {
    case "prefab":
      return ".prefab";
    case "scene":
    case "unity":
      return ".unity";
    case "asset":
      return ".asset";
    case "material":
    case "mat":
      return ".mat";
    case "animation":
    case "anim":
      return ".anim";
    case "controller":
      return ".controller";
    default:
      return kind;
  }
}
