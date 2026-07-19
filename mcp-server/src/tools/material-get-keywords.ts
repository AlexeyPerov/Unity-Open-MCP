import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 1 — read-only material shader-keyword listing. Gate-free.
export const materialGetKeywords = makeTool(
  "unity_open_mcp_material_get_keywords",
  "List the shader keywords currently enabled on a Material. Read-only (gate-free). " +
    "Resolve the material by `asset_path` (.mat) or `instance_id` of a scene GameObject " +
    "(its Renderer.sharedMaterial is read) or the Material instance directly.",
  {
    properties: {
          asset_path: {
            type: "string",
            description: "Material asset path (.mat). Highest priority resolver.",
          },
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Instance ID of a scene GameObject whose Renderer.sharedMaterial is read, OR the Material instance directly.",
          },
        },
  },
);
