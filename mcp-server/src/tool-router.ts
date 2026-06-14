import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";
import type { LiveClient } from "./live-client.js";
import type { BatchSpawn } from "./batch-spawn.js";

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
  constructor(
    private live: LiveClient,
    private batch: BatchSpawn,
    private projectPath: string,
  ) {}

  async route(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const canBatch = this.batch.isBatchTool(toolName);
    const isPing = toolName === "unity_agent_ping";

    if (!canBatch && !isPing) {
      const result = await this.live.route(toolName, args);
      return injectRouteMeta(result, { route: "live" });
    }

    const liveAvailable = await this.live.isLiveAvailable();

    if (liveAvailable) {
      console.error(`[unity-agent-mcp] Route: ${toolName} -> live`);
      const result = await this.live.route(toolName, args);
      return injectRouteMeta(result, { route: "live" });
    }

    console.error(
      `[unity-agent-mcp] Route: ${toolName} -> batch (live bridge unavailable)`,
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
}
