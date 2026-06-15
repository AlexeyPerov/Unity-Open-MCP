// M9 Plan 2 — compressible-tool router.
//
// Intercepts `read_asset` / `search_assets`: fetches the structured
// AssetModel / SearchModel JSON from the live bridge (direct-response path) and
// applies the shared compression module (compact.ts) to produce the compact
// drill-down response. This is the one place where the live path meets the
// compression module — the same module will serve the offline parser in M9
// Plan 3, so the algorithm lives in exactly one place.
//
// A session-scoped AssetModel cache lets `--component` / `--path` / `--id`
// drill-downs reuse the last-fetched model instead of re-parsing the asset.
// The cache key includes field_limit and depth (the parameters that change what
// the bridge returns); detail/component/path/id are pure TS-side render options
// and do not invalidate the cache.

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { LiveClient } from "./live-client.js";
import type { AssetModel, SearchModel } from "./compression/asset-model.js";
import {
  renderAssetSummary,
  renderSearchSummary,
  type RenderOptions,
} from "./compression/compact.js";

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

function makeErrorResult(message: string, code: string): CallToolResult {
  return {
    content: [{ type: "text", text: JSON.stringify({ error: { code, message } }) }],
    isError: true,
  };
}

function makeResult(payload: unknown, cacheHit: boolean): CallToolResult {
  return {
    content: [
      {
        type: "text",
        text: JSON.stringify({
          ...(payload as Record<string, unknown>),
          _cache: cacheHit ? "hit" : "miss",
        }),
      },
    ],
    isError: false,
  };
}

/**
 * Route a compressible tool: fetch the structured model from the live bridge,
 * cache it (read_asset only), apply the compression module, and return.
 */
export async function routeCompressible(
  toolName: string,
  args: Record<string, unknown>,
  live: LiveClient,
  cache: AssetModelCache,
): Promise<CallToolResult> {
  if (toolName === "unity_open_mcp_read_asset") {
    return routeReadAsset(args, live, cache);
  }
  return routeSearchAssets(args, live);
}

async function routeReadAsset(
  args: Record<string, unknown>,
  live: LiveClient,
  cache: AssetModelCache,
): Promise<CallToolResult> {
  const assetPath = typeof args.asset_path === "string" ? args.asset_path : "";
  if (assetPath === "") {
    return makeErrorResult("'asset_path' is required.", "missing_parameter");
  }

  const fieldLimit = typeof args.field_limit === "number" ? args.field_limit : 0;
  const depth = typeof args.depth === "number" ? args.depth : -1;
  const cacheKey = `${assetPath}|${fieldLimit}|${depth}`;

  let model = cache.get(cacheKey);
  let cacheHit = true;
  if (!model) {
    cacheHit = false;
    // Fetch the structured model from the bridge (direct-response path).
    const raw = await live.route("unity_open_mcp_read_asset", args);
    const parsed = parseContentJson(raw);
    if (parsed === null) {
      return raw.isError
        ? raw
        : makeErrorResult("Bridge returned an unreadable asset model.", "bridge_error");
    }
    if (parsed.error) {
      // Forward bridge-side errors (asset_not_found, etc.) unchanged.
      return { ...raw, isError: true };
    }
    model = parsed as unknown as AssetModel;
    cache.set(cacheKey, model);
  }

  const opts: RenderOptions = {
    detail: typeof args.detail === "string" ? (args.detail as RenderOptions["detail"]) : "summary",
    component: typeof args.component === "string" ? args.component : undefined,
    path: typeof args.path === "string" ? args.path : undefined,
    id: typeof args.id === "string" ? args.id : undefined,
    depth: typeof args.depth === "number" ? args.depth : undefined,
    limit: typeof args.limit === "number" ? args.limit : undefined,
  };

  const compact = renderAssetSummary(model, opts);
  return makeResult(compact, cacheHit);
}

async function routeSearchAssets(
  args: Record<string, unknown>,
  live: LiveClient,
): Promise<CallToolResult> {
  const raw = await live.route("unity_open_mcp_search_assets", args);
  const parsed = parseContentJson(raw);
  if (parsed === null) {
    return raw.isError
      ? raw
      : makeErrorResult("Bridge returned an unreadable search result.", "bridge_error");
  }
  if (parsed.error) {
    return { ...raw, isError: true };
  }

  const objectLimit = typeof args.object_limit === "number" ? args.object_limit : 12;
  const matchLimit = typeof args.max_results === "number" ? args.max_results : 50;
  const compact = renderSearchSummary(parsed as unknown as SearchModel, { objectLimit, matchLimit });
  return makeResult(compact, false);
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
