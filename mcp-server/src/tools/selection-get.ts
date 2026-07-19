import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 5 — typed editor selection get. Read-only: reports the current
// editor selection (active object + full selection array). Gate-free.
// Complements the read-only addressing model used across the typed surface
// (instance_id > path > name).
export const selectionGet = makeTool(
  "unity_open_mcp_selection_get",
  "Read the current Unity Editor selection. Returns the active object " +
    "(instanceId / name / type / assetPath when asset-backed) plus the full " +
    "selection array when more than one object is selected. Read-only and " +
    "gate-free. Use this to find what a human has clicked in the Hierarchy / " +
    "Project window before mutating it, or to confirm selection_set took " +
    "effect. Prefer this over raw execute_csharp Selection.activeObject — " +
    "schema-validated and surfaces asset vs scene-backed objects uniformly.",
  {
    properties: {
          max_results: {
            type: "integer",
            default: 50,
            minimum: 1,
            description:
              "Cap on the number of selected objects serialized. The active object " +
              "is always included regardless. Truncated count is reported.",
          },
        },
  },
);
