import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const ping = makeTool(
  "unity_open_mcp_ping",
  "Bridge health check.",
  {
    properties: {},
  },
);
