import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const delta = makeTool(
  "unity_open_mcp_delta",
  "Compare current project health vs a checkpoint.",
  {
    required: ["checkpoint_id"],
        properties: {
          checkpoint_id: { type: "string" },
          paths: {
            type: "array",
            items: { type: "string" },
            description: "Re-validate scope; defaults to checkpoint paths",
          },
        },
  },
);
