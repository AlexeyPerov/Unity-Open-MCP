import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 1 — read-only material properties. Token-bounded by `max_results`.
// Read-only: gate-free. Resolves the material by `asset_path` (.mat) or
// `instance_id` (scene renderer's sharedMaterial).
export const materialGetProperties = makeTool(
  "unity_open_mcp_material_get_properties",
  "List all shader properties of a Material with their current values. Read-only (gate-free). " +
    "Resolve the material by `asset_path` (.mat) or by `instance_id` of a scene GameObject whose " +
    "Renderer.sharedMaterial is read. Each property carries name, type (Color/Vector/Float/Range/Int/" +
    "Texture), description, and value. Token-bounded by `max_results`; remaining count is reported.",
  {
    properties: {
          asset_path: {
            type: "string",
            description: "Material asset path (.mat). Highest priority resolver.",
          },
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description:
              "Instance ID of a scene GameObject whose Renderer.sharedMaterial is read, OR the Material instance directly. 0 = not set.",
          },
          max_results: {
            type: "integer",
            default: 100,
            minimum: 1,
            description: "Max properties returned; remaining count is reported in 'truncated'.",
          },
        },
  },
);
