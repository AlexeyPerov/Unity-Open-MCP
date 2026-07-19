import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — typed asset delete. Wraps AssetDatabase.DeleteAsset (single)
// + MoveAssetToTrash (fallback). Mutating: runs the full gate path; the gate
// will surface now-broken references to the deleted assets in its delta.
export const assetsDelete = makeTool(
  "unity_open_mcp_assets_delete",
  "Delete asset(s) from the project. Each path must be Assets/-rooted and exist. " +
    "Per-entry errors are collected, not abortive. Mutating: runs the full gate path " +
    "(checkpoint -> delete -> validate -> delta). `paths_hint` must list each deleted " +
    "asset path so the gate can flag dangling references that pointed at it. Consider " +
    "calling unity_open_mcp_find_references first to see who depends on these assets.",
  {
    required: ["paths", "paths_hint"],
        properties: {
          paths: {
            type: "array",
            items: { type: "string" },
            minItems: 1,
            description: "Asset paths to delete (Assets/-rooted).",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Deleted asset paths (the gate's validation scope). No whole-project fallback." },
          gate: { ...GATE_PROP },
        },
  },
);
