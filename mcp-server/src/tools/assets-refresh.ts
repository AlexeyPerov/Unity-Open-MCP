import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — AssetDatabase.Refresh wrapper. Counts as a light mutation
// (it may trigger an import/compile), so it is marked mutating with gate
// Enforce, but its `paths_hint` may be the whole project when `whole_project`
// is true (Refresh itself is whole-project by nature). Per the plan, this
// is the only tool allowed to bind a whole-project scope because the
// refresh IS the whole-project operation.
export const assetsRefresh = makeTool(
  "unity_open_mcp_assets_refresh",
  "Refresh the AssetDatabase to pick up files added/removed/changed on disk outside " +
    "Unity. Use after direct filesystem edits (e.g. creating folders, dropping textures). " +
    "May trigger asset import and, for .cs files, a recompile — that is reflected in the " +
    "gate delta. Light mutation: runs the gate path. Pass `whole_project: true` (default) " +
    "when you changed files in arbitrary places; pass it `false` with a scoped `paths_hint` " +
    "for a known small change set.",
  {
    properties: {
          whole_project: {
            type: "boolean",
            default: true,
            description:
              "When true (default), runs a full AssetDatabase.Refresh() and paths_hint may be omitted " +
              "(refresh is whole-project by nature). When false, paths_hint is required and bounds the gate scope.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Optional. Required when whole_project is false. Omitted/ignored when whole_project is true." },
          gate: { ...GATE_PROP },
        },
  },
);
