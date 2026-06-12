import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const delta: Tool = {
  name: "unity_agent_delta",
  description: "Compare current project health vs a checkpoint.",
  inputSchema: {
    type: "object",
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
};
