#!/usr/bin/env node

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  ListToolsRequestSchema,
  CallToolRequestSchema,
  ListResourcesRequestSchema,
  ReadResourceRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { ALL_TOOLS } from "./tools/index.js";
import { ALL_RESOURCES } from "./resources/index.js";
import { LiveClient } from "./live-client.js";
import { BatchSpawn } from "./batch-spawn.js";
import { ToolRouter } from "./tool-router.js";
import { PingCache } from "./ping-cache.js";
import { ResourceRouter } from "./resource-router.js";
import { withSchemaDefaults } from "./schema-defaults.js";
import { resolvePort, resolveAuthToken } from "./instance-discovery.js";
import { BridgeEventStream } from "./event-stream.js";
import type { CallToolResult, Tool } from "@modelcontextprotocol/sdk/types.js";

/** Name → tool lookup, built once for default-injection in the CallTool handler. */
const TOOL_BY_NAME = new Map<string, Tool>(
  ALL_TOOLS.map((t) => [t.name, t]),
);

/**
 * Resolve env for the MCP server. UNITY_PROJECT_PATH is mandatory. The port
 * uses M13 T4.3 instance discovery:
 *   1. UNITY_OPEN_MCP_BRIDGE_PORT env var (override wins; users who pin a
 *      port keep working as before)
 *   2. ~/.unity-agent/instances/<hash>.json lock file (when its pid is alive)
 *   3. deterministic hash of the project path (20000 + sha256 % 10000)
 * The resolved port is logged so users can see which bridge was picked.
 */
function getEnv(): { projectPath: string; port: number; authToken?: string } {
  const projectPath = process.env.UNITY_PROJECT_PATH;
  if (!projectPath) {
    console.error(
      "unity-open-mcp: UNITY_PROJECT_PATH environment variable is required.",
    );
    process.exit(1);
  }
  const rawEnvPort = process.env.UNITY_OPEN_MCP_BRIDGE_PORT;
  const envPort = rawEnvPort ? parseInt(rawEnvPort, 10) : undefined;
  const port = resolvePort(
    projectPath,
    Number.isInteger(envPort) ? envPort : undefined,
  );
  const source =
    rawEnvPort && Number.isInteger(envPort)
      ? "env override"
      : "instance discovery";
  console.error(
    `[unity-open-mcp] Bridge port resolved to ${port} (${source}) for project ${projectPath}`,
  );
  // M14 — auto-discover the bridge's per-session bearer token from the same
  // instance lock we read the port from. Undefined when no live lock exists
  // (older bridge / env port override); in that case the client sends no
  // Authorization header and the bridge must be in authMode "none".
  const authToken = resolveAuthToken(
    projectPath,
    Number.isInteger(envPort) ? envPort : undefined,
  );
  if (authToken) {
    console.error("[unity-open-mcp] Bridge auth token discovered from instance lock.");
  } else {
    console.error(
      "[unity-open-mcp] No bridge auth token discovered (authMode must be \"none\").",
    );
  }
  return { projectPath, port, authToken };
}

export function createServer(
  projectPath: string,
  port: number,
  authToken?: string,
): Server {
  const server = new Server(
    { name: "unity-open-mcp", version: "0.1.0" },
    { capabilities: { tools: {}, resources: {} } },
  );

  const pingCache = new PingCache();
  const liveClient = new LiveClient(port, pingCache, authToken);
  const batchSpawn = new BatchSpawn();
  // M13 T4.4 — one SSE subscription per server process. The MCP server is the
  // only long-lived hop between the bridge and the LLM; a per-process reader
  // amortizes the connection and lets every `unity_agent_pull_events` call
  // share the same buffered queue.
  const eventStream = new BridgeEventStream(
    `http://127.0.0.1:${port}`,
    undefined,
    authToken,
  );
  const router = new ToolRouter(liveClient, batchSpawn, projectPath, eventStream);
  const resourceRouter = new ResourceRouter({
    live: liveClient,
    pingCache,
    projectPath,
    port,
  });

  server.setRequestHandler(ListToolsRequestSchema, async () => ({
    tools: ALL_TOOLS,
  }));

  server.setRequestHandler(
    CallToolRequestSchema,
    async (request): Promise<CallToolResult> => {
      const { name, arguments: args } = request.params;
      const callArgs = (args ?? {}) as Record<string, unknown>;
      // Fill missing top-level scalar defaults (e.g. timeout_ms) from the
      // tool's documented schema. MCP clients may omit these; without this
      // step every downstream layer falls back to its own hardcoded value and
      // silently contradicts the schema (historically 30s vs run_tests' 60s).
      const tool = TOOL_BY_NAME.get(name);
      const routedArgs = tool ? withSchemaDefaults(tool, callArgs) : callArgs;
      return router.route(name, routedArgs);
    },
  );

  server.setRequestHandler(ListResourcesRequestSchema, async () => ({
    resources: ALL_RESOURCES,
  }));

  server.setRequestHandler(ReadResourceRequestSchema, async (request) => {
    const { uri } = request.params;
    return resourceRouter.read(uri);
  });

  return server;
}

async function main() {
  const { port, projectPath, authToken } = getEnv();
  const server = createServer(projectPath, port, authToken);
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((err) => {
  console.error("unity-open-mcp fatal:", err);
  process.exit(1);
});
