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
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

const DEFAULT_PORT = 19120;

function getEnv(): { projectPath: string; port: number } {
  const projectPath = process.env.UNITY_PROJECT_PATH;
  if (!projectPath) {
    console.error(
      "unity-open-mcp: UNITY_PROJECT_PATH environment variable is required.",
    );
    process.exit(1);
  }
  const port = process.env.UNITY_OPEN_MCP_BRIDGE_PORT
    ? parseInt(process.env.UNITY_OPEN_MCP_BRIDGE_PORT, 10)
    : DEFAULT_PORT;
  return { projectPath, port };
}

export function createServer(projectPath: string, port: number): Server {
  const server = new Server(
    { name: "unity-open-mcp", version: "0.1.0" },
    { capabilities: { tools: {}, resources: {} } },
  );

  const pingCache = new PingCache();
  const liveClient = new LiveClient(port, pingCache);
  const batchSpawn = new BatchSpawn();
  const router = new ToolRouter(liveClient, batchSpawn, projectPath);
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
      return router.route(name, callArgs);
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
  const { port, projectPath } = getEnv();
  const server = createServer(projectPath, port);
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((err) => {
  console.error("unity-open-mcp fatal:", err);
  process.exit(1);
});
