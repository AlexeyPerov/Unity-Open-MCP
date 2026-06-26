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

  const opts: RenderOptions = {
    detail: typeof args.detail === "string" ? (args.detail as RenderOptions["detail"]) : "summary",
    component: typeof args.component === "string" ? args.component : undefined,
    path: typeof args.path === "string" ? args.path : undefined,
    id: typeof args.id === "string" ? args.id : undefined,
    override: typeof args.override === "boolean" ? args.override : undefined,
    depth: typeof args.depth === "number" ? args.depth : undefined,
    limit: typeof args.limit === "number" ? args.limit : undefined,
  };

  const compact = renderAssetSummary(model, opts);
  return makeResult(compact, cacheHit, source);
}

async function routeSearchAssets(
  args: Record<string, unknown>,
  live: LiveClient,
  projectPath: string,
): Promise<CallToolResult> {
  try {
    const model = await searchAssetsOffline({
      name: typeof args.name === "string" ? args.name : undefined,
      component: typeof args.component === "string" ? args.component : undefined,
      guid: typeof args.guid === "string" ? args.guid : undefined,
      type: typeof args.type === "string" ? args.type : undefined,
      folder: typeof args.folder === "string" ? args.folder : "Assets",
      projectRoot: projectPath,
      maxResults: typeof args.max_results === "number" ? args.max_results : 50,
    });

    const objectLimit = typeof args.object_limit === "number" ? args.object_limit : 12;
    const matchLimit = typeof args.max_results === "number" ? args.max_results : 50;
    const compact = renderSearchSummary(model, { objectLimit, matchLimit });
    return makeResult(compact, false, "offline");
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

  const objectLimit = typeof args.object_limit === "number" ? args.object_limit : 12;
  const matchLimit = typeof args.max_results === "number" ? args.max_results : 50;
  const compact = renderSearchSummary(parsed as unknown as SearchModel, { objectLimit, matchLimit });
  return makeResult(compact, false, "live");
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
