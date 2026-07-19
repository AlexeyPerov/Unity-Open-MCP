import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const checkpointCreate = makeTool(
  "unity_open_mcp_checkpoint_create",
  "Create a manual checkpoint for later delta comparison.",
  {
    properties: {
          paths: {
            type: "array",
            items: { type: "string" },
            description: "Scope; empty = whole project summary (expensive)",
          },
          label: { type: "string" },
        },
  },
);
