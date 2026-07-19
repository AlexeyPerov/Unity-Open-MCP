import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — typed prefab instance unpack. Mutating: runs the full gate path.
export const prefabUnpack = makeTool(
  "unity_open_mcp_prefab_unpack",
  "Unpack a prefab instance into a plain GameObject (severing the prefab link). Mutating: runs " +
    "the full gate path; `paths_hint` should be the scene path holding the instance. Resolve the " +
    "instance by `instance_id` (canonical) or `path` / `name` (fallback).",
  {
    required: ["paths_hint"],
        properties: {
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Instance ID of the prefab instance to unpack (canonical address).",
          },
          path: {
            type: "string",
            description: "Hierarchy path of the prefab instance (fallback address).",
          },
          name: {
            type: "string",
            description: "Prefab instance GameObject name (lowest priority address).",
          },
          completely: {
            type: "boolean",
            default: false,
            description:
              "When true, recursively unpack nested prefab instances. When false (default), only the " +
              "outermost root is unpacked.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Scene path holding the instance (the gate's validation scope)." },
          gate: { ...GATE_PROP },
        },
  },
);
