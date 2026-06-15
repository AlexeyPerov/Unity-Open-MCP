import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";
import type { LiveClient } from "./live-client.js";
import type { BatchSpawn } from "./batch-spawn.js";
import { AssetModelCache, isCompressible, routeCompressible } from "./compressible-router.js";
import { listAssetsOffline, findReferencesOffline } from "./offline.js";
import { buildCapabilities } from "./capabilities/build-capabilities.js";
import { RULE_CATALOG, FIX_CATALOG } from "./capabilities/rule-catalog.js";
import { generateSkill } from "./skill/generate-skill.js";
import { ALL_TOOLS } from "./tools/index.js";
import { BATCH_TOOL_NAMES } from "./batch-spawn.js";

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

    // capabilities — local capability-discovery surface (no live/batch hop).
    if (toolName === "unity_open_mcp_capabilities") {
      return this.routeCapabilities(args);
    }

    // generate_skill — local skill generation (no live/batch hop).
    if (toolName === "unity_agent_generate_skill") {
      return this.routeGenerateSkill(args);
    }

    // find_references — offline-first when no bridge is connected; live
    // ReferenceGraph remains available when the bridge is up.
    if (toolName === "unity_open_mcp_find_references") {
      return this.routeFindReferences(args);
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

  private async routeCapabilities(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const kind =
      args.kind === "tools" || args.kind === "rules" || args.kind === "fixes"
        ? args.kind
        : undefined;
    const includePlanned = args.include_planned !== false;

    const result = buildCapabilities(
      {
        tools: ALL_TOOLS,
        batchToolNames: BATCH_TOOL_NAMES,
        rules: RULE_CATALOG,
        fixes: FIX_CATALOG,
      },
      { kind, includePlanned },
    );
    return {
      content: [
        { type: "text", text: JSON.stringify({ ...result, _source: "local" }) },
      ],
      isError: false,
    };
  }

  private async routeGenerateSkill(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const write = args.write === true;
    const rawClients = Array.isArray(args.clients) ? args.clients : [];
    const clients = rawClients.filter(
      (c): c is string =>
        typeof c === "string" && (c === "claude" || c === "cursor" || c === "opencode"),
    );

    const caps = buildCapabilities(
      {
        tools: ALL_TOOLS,
        batchToolNames: BATCH_TOOL_NAMES,
        rules: RULE_CATALOG,
        fixes: FIX_CATALOG,
      },
      { includePlanned: false },
    );

    try {
      const result = await generateSkill(this.projectPath, caps, {
        write,
        clients: clients.length > 0 ? clients : undefined,
      });
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({
              skill: result.skill,
              project: result.project,
              written: result.written,
              _source: "local",
            }),
          },
        ],
        isError: false,
      };
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({
              error: { code: "skill_generation_failed", message },
            }),
          },
        ],
        isError: true,
      };
    }
  }

  private async routeFindReferences(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const liveAvailable = await this.live.isLiveAvailable();
    if (liveAvailable) {
      console.error("[unity-open-mcp] Route: find_references -> live");
      const result = await this.live.route("unity_open_mcp_find_references", args);
      return injectRouteMeta(result, { route: "live" });
    }

    console.error(
      "[unity-open-mcp] Route: find_references -> offline (live bridge unavailable)",
    );

    const assetPath = typeof args.asset_path === "string" ? args.asset_path : undefined;
    const guid = typeof args.guid === "string" ? args.guid : undefined;

    if (!assetPath && !guid) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({
              error: {
                code: "missing_parameter",
                message: "Either 'asset_path' or 'guid' is required.",
              },
            }),
          },
        ],
        isError: true,
      };
    }

    try {
      const result = await findReferencesOffline({
        assetPath,
        guid,
        detail: typeof args.detail === "string" ? args.detail : "normal",
        maxResults: typeof args.max_results === "number" ? args.max_results : 100,
        maxPerFile: typeof args.max_per_file === "number" ? args.max_per_file : 5,
        patternThreshold: typeof args.pattern_threshold === "number" ? args.pattern_threshold : 0,
        projectRoot: this.projectPath,
      });
      return {
        content: [
          { type: "text", text: JSON.stringify({ ...result, _source: "offline" }) },
        ],
        isError: false,
      };
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({
              error: { code: "offline_error", message },
            }),
          },
        ],
        isError: true,
      };
    }
  }
}
