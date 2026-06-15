import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const findReferences: Tool = {
  name: "unity_open_mcp_find_references",
  description:
    "Reverse dependency lookup for assets. Returns all assets that reference the given asset path or GUID. " +
    "Works offline (scanning YAML on disk) when no Editor is running, or via the live bridge when connected.",
  inputSchema: {
    type: "object",
    properties: {
      asset_path: { type: "string", description: "Asset path (e.g. Assets/Prefabs/Player.prefab)" },
      guid: { type: "string", pattern: "^[0-9a-fA-F]{32}$", description: "Asset GUID (32 hex chars)" },
      detail: {
        enum: ["summary", "normal", "verbose"],
        default: "normal",
        description: "summary: counts only. normal: referencing asset paths grouped by kind/folder. verbose: also includes which fields reference the target.",
      },
      max_results: { type: "integer", default: 100, description: "Maximum number of referencing assets to return" },
      max_per_file: { type: "integer", default: 5, description: "Verbose mode: max field locations per file" },
      pattern_threshold: {
        type: "integer",
        default: 0,
        description: "Collapse folders with >= this many referencing files into a single summary entry (0 = disabled)",
      },
    },
    oneOf: [
      { required: ["asset_path"] },
      { required: ["guid"] },
    ],
  },
};
