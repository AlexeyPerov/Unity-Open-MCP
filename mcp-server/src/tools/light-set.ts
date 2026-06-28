import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 2 / T20.2.1 — Lighting domain tool. Built-in lighting module.
// Mutating: runs the full gate path.
export const lightSet: Tool = {
  name: "unity_open_mcp_light_set",
  description:
    "Set typed Light fields: light_type (Spot | Point | Directional | Area | " +
    "Rectangle), color ([r,g,b,(a)] 0-1), intensity (float), range (float), " +
    "spot_angle (float, Spot only), shadows (none | hard | soft), render_mode " +
    "(Auto | Important | NotImportant), culling_mask (int LayerMask value). " +
    "Each field is optional — omit to leave unchanged. Mutating: runs the full " +
    "gate path; paths_hint is the host scene path.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
      path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
      name: { type: "string", description: "Host GameObject name (first match)." },
      light_type: {
        type: "string",
        enum: ["Spot", "Point", "Directional", "Area", "Rectangle"],
        description: "LightType name.",
      },
      color: {
        type: "string",
        description: "Color as 'r,g,b,(a)' 0-1.",
      },
      intensity: { type: "number", description: "Light intensity." },
      range: { type: "number", description: "Light range (Point/Spot)." },
      spot_angle: { type: "number", description: "Spot angle in degrees (Spot only)." },
      shadows: {
        type: "string",
        enum: ["none", "hard", "soft", "None", "Hard", "Soft"],
        description: "LightShadows (none | hard | soft).",
      },
      render_mode: {
        type: "string",
        enum: ["Auto", "Important", "NotImportant"],
        description: "LightRenderMode.",
      },
      culling_mask: {
        type: "integer",
        description: "Culling mask as an int LayerMask value.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the scene path that contains the host.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
