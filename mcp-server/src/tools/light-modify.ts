import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 2 / T20.2.1 — Lighting domain tool. Built-in lighting module.
// Mutating: runs the full gate path.
export const lightModify = makeTool(
  "unity_open_mcp_light_modify",
  "Set one or more serialized fields on a Light component attached to a " +
    "target GameObject. Use this when light_set does not cover a niche field; " +
    "otherwise prefer the typed tool. Each entry is { field, value, type? } " +
    "where type is 'int' | 'float' | 'bool' | 'string' | 'vector' | 'color' " +
    "(default inferred from the field's current type). Enum fields (LightType " +
    "/ LightShadows / RenderMode) accept a name or an int index. Per-field " +
    "errors are accumulated — a single bad entry does not abort the batch. " +
    "Mutating: runs the full gate path; paths_hint is the host scene path.",
  {
    required: ["fields_json", "paths_hint"],
        properties: {
          instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
          path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
          name: { type: "string", description: "Host GameObject name (first match)." },
          fields_json: {
            type: "string",
            description:
              "JSON array of { field, value, type? } patches. Example: " +
              "[{\"field\":\"intensity\",\"value\":5.5,\"type\":\"float\"}," +
              "{\"field\":\"type\",\"value\":\"Point\",\"type\":\"string\"}].",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the scene path that contains the host." },
          gate: { ...GATE_PROP },
        },
  },
);
