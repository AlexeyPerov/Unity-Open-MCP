// M9 Plan 2 + Plan 3 — compressible-tool router.
//
// Intercepts `read_asset` / `search_assets`: produces the structured
// AssetModel / SearchModel (offline parser for text-serialized assets, or live
// bridge for binary formats) and applies the shared compression module
// (compact.ts) to produce the compact drill-down response.
//
// A session-scoped AssetModel cache lets `--component` / `--path` / `--id`
// drill-downs reuse the last-fetched model instead of re-parsing the asset.
// The cache key includes field_limit and depth (the parameters that change what
// the source returns); detail/component/path/id are pure TS-side render options
// and do not invalidate the cache.

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { LiveClient } from "./live-client.js";
import type { AssetModel, SearchModel } from "./compression/asset-model.js";
import {
  renderAssetSummary,
  renderSearchSummary,
  type RenderOptions,
} from "./compression/compact.js";
import { readAssetOffline, isOfflineAsset } from "./offline.js";
import { searchAssetsOffline } from "./offline.js";
import { makeErrorResult } from "./results.js";
import {
  readProfileAndDetail,
  applyPaging,
  attachPagination,
  isOutputProfile,
} from "./output-profile.js";

const COMPRESSIBLE = new Set([
  "unity_open_mcp_read_asset",
  "unity_open_mcp_search_assets",
]);

export function isCompressible(toolName: string): boolean {
  return COMPRESSIBLE.has(toolName);
}

const CACHE_LIMIT = 8;

interface CacheEntry {
  model: AssetModel;
}

/** Session-scoped LRU for the last few AssetModels read by `read_asset`. */
export class AssetModelCache {
  private entries = new Map<string, CacheEntry>();

  get(key: string): AssetModel | null {
    const entry = this.entries.get(key);
    if (!entry) return null;
    // Move-to-end so the LRU eviction order updates.
    this.entries.delete(key);
    this.entries.set(key, entry);
    return entry.model;
  }

  set(key: string, model: AssetModel): void {
    if (this.entries.has(key)) this.entries.delete(key);
    this.entries.set(key, { model });
    while (this.entries.size > CACHE_LIMIT) {
      const oldest = this.entries.keys().next().value;
      if (oldest === undefined) break;
      this.entries.delete(oldest);
    }
  }

  clear(): void {
    this.entries.clear();
  }

  get size(): number {
    return this.entries.size;
  }
}

function makeResult(payload: unknown, cacheHit: boolean, source?: "offline" | "live"): CallToolResult {
  return {
    content: [
      {
        type: "text",
        text: JSON.stringify({
          ...(payload as Record<string, unknown>),
          _cache: cacheHit ? "hit" : "miss",
          ...(source ? { _source: source } : {}),
        }),
      },
    ],
    isError: false,
  };
}

/**
 * Route a compressible tool: try offline parser first for text-serialized
 * assets, fall back to the live bridge for binary formats. Cache the model
 * (read_asset only), apply the compression module, and return.
 */
export async function routeCompressible(
  toolName: string,
  args: Record<string, unknown>,
  live: LiveClient,
  cache: AssetModelCache,
  projectPath: string,
): Promise<CallToolResult> {
  if (toolName === "unity_open_mcp_read_asset") {
    return routeReadAsset(args, live, cache, projectPath);
  }
  return routeSearchAssets(args, live, projectPath);
}

async function routeReadAsset(
  args: Record<string, unknown>,
  live: LiveClient,
  cache: AssetModelCache,
  projectPath: string,
): Promise<CallToolResult> {
  const assetPath = typeof args.asset_path === "string" ? args.asset_path : "";
  if (assetPath === "") {
    return makeErrorResult({ code: "missing_parameter", message: "'asset_path' is required." });
  }

  const fieldLimit = typeof args.field_limit === "number" ? args.field_limit : 0;
  const depth = typeof args.depth === "number" ? args.depth : -1;
  const cacheKey = `${assetPath}|${fieldLimit}|${depth}`;

  let model = cache.get(cacheKey);
  let cacheHit = true;
  let source: "offline" | "live" = "offline";

  if (!model) {
    cacheHit = false;

    if (isOfflineAsset(assetPath)) {
      try {
        const result = await readAssetOffline(assetPath, { fieldLimit, projectRoot: projectPath });
        model = result.model;
        source = "offline";
        cache.set(cacheKey, model);
      } catch {
        // Offline parse failed — fall through to live bridge.
        model = null;
      }
    }

    if (!model) {
      // Fall back to live bridge (for binary formats or parse failures).
      const liveAvailable = await live.isLiveAvailable();
      if (!liveAvailable) {
        return makeErrorResult({
          code: "source_unavailable",
          message: isOfflineAsset(assetPath)
            ? `Offline parse failed and live bridge is unavailable for: ${assetPath}`
            : `Binary or unsupported format requires the live bridge, which is unavailable: ${assetPath}`,
        });
      }
      const raw = await live.route("unity_open_mcp_read_asset", args);
      const parsed = parseContentJson(raw);
      if (parsed === null) {
        return raw.isError
          ? raw
          : makeErrorResult({ code: "bridge_error", message: "Bridge returned an unreadable asset model." });
      }
      if (parsed.error) {
        return { ...raw, isError: true };
      }
      model = parsed as unknown as AssetModel;
      source = "live";
      cache.set(cacheKey, model);
    }
  }

  // M22 — resolve the output profile onto the existing detail axis. `profile`
  // is the documented public param; `detail` is the back-compat alias. Profile
  // wins when both are present.
  const { detail } = readProfileAndDetail(args, "summary");

  const opts: RenderOptions = {
    detail,
    component: typeof args.component === "string" ? args.component : undefined,
    path: typeof args.path === "string" ? args.path : undefined,
    id: typeof args.id === "string" ? args.id : undefined,
    override: typeof args.override === "boolean" ? args.override : undefined,
    depth: typeof args.depth === "number" ? args.depth : undefined,
    limit: typeof args.limit === "number" ? args.limit : undefined,
  };

  const compact = renderAssetSummary(model, opts);

  // M22 — page the TREE rows when page_size is requested. Drill-down responses
  // (component/id/override) and non-hierarchical assets have no TREE to page,
  // so paging only applies when a tree was rendered.
  const result =
    compact.tree && Array.isArray(compact.tree) && typeof args.page_size === "number" && args.page_size > 0
      ? pageTree(compact, args.page_size as number, typeof args.cursor === "string" ? (args.cursor as string) : undefined)
      : compact;

  return makeResult(result, cacheHit, source);
}

/** Page the rendered TREE rows and attach a pagination block. */
function pageTree(
  compact: ReturnType<typeof renderAssetSummary>,
  pageSize: number,
  cursor: string | undefined,
): ReturnType<typeof renderAssetSummary> {
  const tree = compact.tree ?? [];
  const { page, block } = applyPaging(tree, "read_asset", { page_size: pageSize, cursor });
  const withPage: ReturnType<typeof renderAssetSummary> = { ...compact, tree: page };
  // When paging, the per-page omission story is the pagination block; clear the
  // legacy whole-tree hint so it does not mislead.
  if (withPage.hint && withPage.moreHidden === undefined) {
    delete withPage.hint;
  }
  return attachPagination(withPage, block);
}

async function routeSearchAssets(
  args: Record<string, unknown>,
  live: LiveClient,
  projectPath: string,
): Promise<CallToolResult> {
  // M22 — profile drives the object_limit default (compact = tight per-file
  // cap; balanced/full raise it). An explicit object_limit always wins.
  const profile = isOutputProfile(args.profile) ? args.profile : undefined;
  const objectLimitDefault = profile === "compact" || profile === undefined
    ? 12
    : 50;
  const objectLimit = typeof args.object_limit === "number" ? args.object_limit : objectLimitDefault;
  const wantPaging = typeof args.page_size === "number" && args.page_size > 0;

  try {
    // When paging, ask the source for the full match set (no matchLimit cap)
    // so the cursor can walk it; otherwise honor the legacy max_results cap.
    const sourceMax = wantPaging ? 0 : (typeof args.max_results === "number" ? args.max_results : 50);
    const model = await searchAssetsOffline({
      name: typeof args.name === "string" ? args.name : undefined,
      component: typeof args.component === "string" ? args.component : undefined,
      guid: typeof args.guid === "string" ? args.guid : undefined,
      type: typeof args.type === "string" ? args.type : undefined,
      folder: typeof args.folder === "string" ? args.folder : "Assets",
      projectRoot: projectPath,
      maxResults: sourceMax,
    });

    const compact = renderSearchSummary(model, { objectLimit, matchLimit: sourceMax });
    const result = wantPaging
      ? pageSearch(compact, args.page_size as number, typeof args.cursor === "string" ? (args.cursor as string) : undefined)
      : compact;
    return makeResult(result, false, "offline");
  } catch {
    // Fall back to live bridge.
  }

  const raw = await live.route("unity_open_mcp_search_assets", args);
  const parsed = parseContentJson(raw);
  if (parsed === null) {
    return raw.isError
      ? raw
      : makeErrorResult({ code: "bridge_error", message: "Bridge returned an unreadable search result." });
  }
  if (parsed.error) {
    return { ...raw, isError: true };
  }

  const liveMax = wantPaging ? 0 : (typeof args.max_results === "number" ? args.max_results : 50);
  const compact = renderSearchSummary(parsed as unknown as SearchModel, { objectLimit, matchLimit: liveMax });
  const result = wantPaging
    ? pageSearch(compact, args.page_size as number, typeof args.cursor === "string" ? (args.cursor as string) : undefined)
    : compact;
  return makeResult(result, false, "live");
}

/**
 * Page the rendered `matches` array and attach a pagination block. The legacy
 * `truncated` count (matches hidden by the source cap) is preserved; the
 * pagination block's `truncated` is the resumable tail within the current page
 * window.
 */
function pageSearch(
  compact: ReturnType<typeof renderSearchSummary>,
  pageSize: number,
  cursor: string | undefined,
): ReturnType<typeof renderSearchSummary> {
  const matches = compact.matches ?? [];
  const { page, block } = applyPaging(matches, "search_assets", { page_size: pageSize, cursor });
  const withPage: ReturnType<typeof renderSearchSummary> = {
    ...compact,
    matches: page,
    shown: page.length,
  };
  return attachPagination(withPage, block);
}

function parseContentJson(result: CallToolResult): Record<string, unknown> | null {
  if (result.content.length === 0) return null;
  const first = result.content[0];
  if (first.type !== "text") return null;
  try {
    return JSON.parse(first.text) as Record<string, unknown>;
  } catch {
    return null;
  }
}
