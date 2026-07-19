import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 2 — typed component destroy. Mutating: runs the full gate path.
// Scene side-effect — scope paths_hint to the scene that contains the host.
export const componentDestroy = makeTool(
  "unity_open_mcp_component_destroy",
  "Remove one or more Components from a GameObject in the active scene by type name. " +
    "Undo-recorded. Mutating: runs the full gate path; `paths_hint` is the scene path that " +
    "contains the host. Each type is resolved by full name or class-name fallback; per-type " +
    "errors (unknown type, not present) are accumulated in the response. Use component_list_all " +
    "to discover attachable types and component_get to inspect a component before removing it.",
  {
    required: ["component_types", "paths_hint"],
        properties: {
          component_types: {
            type: "array",
            items: { type: "string" },
            description:
              "Component type names to remove (full name preferred, class-name fallback). When a type " +
              "allows multiples, only the first match is removed per call.",
          },
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Host GameObject instance ID. Highest priority resolver.",
          },
          path: {
            type: "string",
            description: "Host hierarchy path \"Root/Child\".",
          },
          name: {
            type: "string",
            description: "Host GameObject name (first match). Lowest priority resolver.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the scene path that contains the host." },
          gate: { ...GATE_PROP },
        },
  },
);
