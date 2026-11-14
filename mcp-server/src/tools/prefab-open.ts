import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — typed prefab stage open. Mutating: runs the full gate path
// (the stage lifecycle touches editor state). Scope paths_hint to the prefab
// asset path.
export const prefabOpen = makeTool(
  "unity_open_mcp_prefab_open",
  "Open the prefab edit stage for a prefab asset path. Typed GameObject tools then reach the stage: " +
    "address objects by path from the prefab root (\"Root/Child\"; a UI prefab's \"Canvas (Environment)/\" " +
    "wrapper is accepted too) or by instance id. Returns rootPath + rootInstanceId, the container, and autoSave " +
    "(when on, Unity writes stage edits to the asset on a later Editor update; otherwise they stay in the stage " +
    "until prefab_save / prefab_close). Modifications inside the stage propagate to all instances. Pair with " +
    "unity_open_mcp_prefab_close to exit the stage when done. Mutating: runs the full gate path; `paths_hint` " +
    "is the prefab asset path.",
  {
    required: ["prefab_asset_path", "paths_hint"],
        properties: {
          prefab_asset_path: {
            type: "string",
            description: "Prefab asset path to open in the edit stage.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Prefab asset path (the gate's validation scope)." },
          gate: { ...GATE_PROP },
        },
  },
);
