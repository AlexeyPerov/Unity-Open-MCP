import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 1 — typed material keyword set. Mutating: runs the full gate path.
export const materialSetKeyword: Tool = {
  name: "unity_open_mcp_material_set_keyword",
  description:
    "Enable or disable a shader keyword on a Material. Records an Undo before mutating. " +
    "Mutating: runs the full gate path; `paths_hint` is the .mat asset path or scene path. " +
    "Resolve the material by `asset_path` (.mat) or `instance_id` (scene Renderer's sharedMaterial " +
    "or the Material instance directly).",
  inputSchema: {
    type: "object",
    required: ["keyword", "enabled", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description: "Material asset path (.mat). Highest priority resolver.",
      },
      instance_id: {
        type: ["string", "integer"],
        default: 0,
        description: "Instance ID of a scene GameObject whose Renderer.sharedMaterial is mutated, OR the Material instance directly.",
      },
      keyword: {
        type: "string",
        description: "Shader keyword name (e.g. '_EMISSION').",
      },
      enabled: {
        type: "boolean",
        description: "true to enable, false to disable the keyword.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Affected .mat asset path or scene path (the gate's validation scope).",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
