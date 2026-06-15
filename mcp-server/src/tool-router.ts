import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";
import type { LiveClient } from "./live-client.js";
import type { BatchSpawn } from "./batch-spawn.js";
import { AssetModelCache, isCompressible, routeCompressible } from "./compressible-router.js";
import { listAssetsOffline } from "./offline.js";

export interface RouteMeta {
  route: "live" | "batch";
  fallbackReason?: string;
}

function injectRouteMeta(
  result: CallToolResult,
  meta: RouteMeta,
): CallToolResult {
  if (result.content.length === 0) return result;
  const first = result.content[0];
  if (first.type !== "text") return result;
  try {
    const body = JSON.parse(first.text) as Record<string, unknown>;
    body._route = meta;
    return {
      ...result,
      content: [{ type: "text", text: JSON.stringify(body) }],
    };
  } catch {
    return result;
  }
}

export class ToolRouter implements Router {
  private readonly modelCache = new AssetModelCache();
  constructor(
    private live: LiveClient,
    private batch: BatchSpawn,
    private projectPath: string,
  ) {}

  async route(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    // list_assets — always offline (no live equivalent needed).
    if (toolName === "unity_open_mcp_list_assets") {
      return this.routeListAssets(args);
    }

    // Compact drill-down reads: offline-first for text-serialized assets, fall
    // back to live bridge for binary formats. The compressible-router handles
    // the source selection internally.
    if (isCompressible(toolName)) {
      const result = await routeCompressible(
        toolName,
        args,
        this.live,
        this.modelCache,
        this.projectPath,
      );
      return injectRouteMeta(result, { route: "live" });
    }

    const canBatch = this.batch.isBatchTool(toolName);
    const isPing = toolName === "unity_open_mcp_ping";

    if (!canBatch && !isPing) {
      const result = await this.live.route(toolName, args);
      return injectRouteMeta(result, { route: "live" });
    }

    const liveAvailable = await this.live.isLiveAvailable();

    if (liveAvailable) {
      console.error(`[unity-open-mcp] Route: ${toolName} -> live`);
      const result = await this.live.route(toolName, args);
      return injectRouteMeta(result, { route: "live" });
    }

    console.error(
      `[unity-open-mcp] Route: ${toolName} -> batch (live bridge unavailable)`,
    );

    if (isPing) {
      return this.batchPingResult();
    }

    const result = await this.batch.route(toolName, args);
    return injectRouteMeta(result, {
      route: "batch",
      fallbackReason: "live_unavailable",
    });
  }

  private batchPingResult(): CallToolResult {
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            connected: false,
            projectPath: this.projectPath,
            unityVersion: "unknown",
            bridgeVersion: "unknown",
            mode: "batch",
            compiling: false,
            isPlaying: false,
            _route: {
              route: "batch",
              fallbackReason: "live_unavailable",
            },
          }),
        },
      ],
      isError: false,
    };
  }

  private async routeListAssets(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    try {
      const result = await listAssetsOffline({
        folder: typeof args.folder === "string" ? args.folder : "Assets",
        type: typeof args.type === "string" ? args.type : undefined,
        maxPerFolder: typeof args.max_per_folder === "number" ? args.max_per_folder : 30,
        projectRoot: this.projectPath,
      });
      return {
        content: [{ type: "text", text: JSON.stringify({ ...result, _source: "offline" }) }],
        isError: false,
      };
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      return {
        content: [
          { type: "text", text: JSON.stringify({ error: { code: "offline_error", message } }) },
        ],
        isError: true,
      };
    }
  }
}
