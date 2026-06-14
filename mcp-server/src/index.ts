#!/usr/bin/env node

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  ListToolsRequestSchema,
  CallToolRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { ALL_TOOLS } from "./tools/index.js";
import { LiveClient } from "./live-client.js";
import { BatchSpawn } from "./batch-spawn.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

const DEFAULT_PORT = 19120;

function getEnv(): { projectPath: string; port: number } {
  const projectPath = process.env.UNITY_PROJECT_PATH;
  if (!projectPath) {
    console.error(
      "unity-agent-mcp: UNITY_PROJECT_PATH environment variable is required.",
    );
    process.exit(1);
  }
  const port = process.env.UNITY_AGENT_BRIDGE_PORT
    ? parseInt(process.env.UNITY_AGENT_BRIDGE_PORT, 10)
    : DEFAULT_PORT;
  return { projectPath, port };
}

async function main() {
  const { port } = getEnv();

  const server = new Server(
    { name: "unity-agent", version: "0.1.0" },
    { capabilities: { tools: {} } },
  );

  const liveClient = new LiveClient(port);
  const batchSpawn = new BatchSpawn();

  server.setRequestHandler(ListToolsRequestSchema, async () => ({
    tools: ALL_TOOLS,
  }));

  server.setRequestHandler(
    CallToolRequestSchema,
    async (request): Promise<CallToolResult> => {
      const { name, arguments: args } = request.params;
      const callArgs = (args ?? {}) as Record<string, unknown>;

      if (batchSpawn.isBatchTool(name)) {
        return batchSpawn.route(name, callArgs);
      }

      return liveClient.route(name, callArgs);
    },
  );

  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((err) => {
  console.error("unity-agent-mcp fatal:", err);
  process.exit(1);
});
