import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const findReferences: Tool = {
  name: "unity_agent_find_references",
  description:
    "Reverse dependency lookup for assets. Returns all assets that reference the given asset path or GUID.",
  inputSchema: {
    type: "object",
    properties: {
      asset_path: { type: "string", description: "Asset path (e.g. Assets/Prefabs/Player.prefab)" },
      guid: { type: "string", pattern: "^[0-9a-fA-F]{32}$", description: "Asset GUID (32 hex chars)" },
      detail: { enum: ["summary", "normal", "verbose"], default: "normal" },
      max_results: { type: "integer", default: 100, description: "Maximum number of referencing assets to return" },
    },
    oneOf: [
      { required: ["asset_path"] },
      { required: ["guid"] },
    ],
  },
};
