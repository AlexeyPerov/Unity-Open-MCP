import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 5 — typed editor selection set. Mutates editor selection state but
// touches no assets — routes as a gate-free direct-response tool. Supports
// resolving targets by instance_id (scene object), path (hierarchy path), or
// asset_path (asset on disk). Accepts one target (single selection) or an
// array (multi-selection). Undo-recorded so a human can Ctrl+Z it.
export const selectionSet = makeTool(
  "unity_open_mcp_selection_set",
  "Set the Unity Editor selection. Mutates editor selection state only " +
    "(writes no assets), so it is gate-free and returns directly without the " +
    "gate envelope. Resolve targets by instance_id (scene GameObject / " +
    "Component), path (hierarchy path), or asset_path (asset on disk, e.g. " +
    "'Assets/Prefabs/Player.prefab'). Pass a single target for " +
    "Selection.activeObject, or an array for multi-selection " +
    "(Selection.objects). Clear the selection by passing an empty target set. " +
    "Undo-recorded so a human can Ctrl+Z it. Prefer this over raw " +
    "execute_csharp Selection.activeObject = ... — schema-validated, supports " +
    "asset + scene targets uniformly, and surfaces a not_found error " +
    "structured instead of silently selecting nothing.",
  {
    properties: {
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description:
              "Scene GameObject / Component instance id to select. Single-target " +
              "shorthand — prefer targets[] when selecting more than one.",
          },
          path: {
            type: "string",
            description:
              "Hierarchy path (e.g. 'Player/Body') of a scene object to select. " +
              "Single-target shorthand — prefer targets[] when selecting more than one.",
          },
          name: {
            type: "string",
            description:
              "Scene object name (first match) to select. Lowest-priority " +
              "resolver — prefer instance_id or path for stability across reloads. " +
              "Single-target shorthand.",
          },
          asset_path: {
            type: "string",
            description:
              "Asset path (e.g. 'Assets/Prefabs/Player.prefab') of an asset on " +
              "disk to select. Single-target shorthand.",
          },
          targets: {
            type: "array",
            items: {
              type: "object",
              properties: {
                instance_id: { type: "integer", default: 0 },
                path: { type: "string" },
                name: { type: "string" },
                asset_path: { type: "string" },
              },
              additionalProperties: false,
            },
            description:
              "Multi-selection list. When provided, overrides the single-target " +
              "shorthand fields. Each entry resolves by instance_id > asset_path > " +
              "path > name (asset_path wins for disk assets; the others resolve " +
              "scene objects). Pass an empty array (or omit every target field) " +
              "to clear the selection.",
          },
          clear: {
            type: "boolean",
            default: false,
            description:
              "Shortcut to clear the selection entirely (Selection.objects = {}). " +
              "Equivalent to passing targets: [].",
          },
        },
  },
);
