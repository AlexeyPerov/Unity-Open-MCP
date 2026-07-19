import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 3 — typed scene focus. Mutating: moves the SceneView camera onto
// a GameObject. Runs the full gate path.
export const sceneFocus = makeTool(
  "unity_open_mcp_scene_focus",
  "Frame a GameObject in the SceneView camera and optionally set a view axis. Mutating: runs " +
    "the full gate path; `paths_hint` should be the scene path containing the target. Computes " +
    "the target's combined renderer+collider bounds and calls SceneView.LookAtDirect, so the " +
    "agent can point the editor at a specific object before screenshot or before a human takes " +
    "over. Returns the resulting SceneView pivot / camera position / rotation / size and the " +
    "resolved axis. Resolve the target by `instance_id` (canonical) or `path` / `name`, matching " +
    "spatial_query and gameobject_find. Prefer this over raw execute_csharp SceneView.lastActiveSceneView.",
  {
    required: ["paths_hint"],
        properties: {
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Instance ID of the target GameObject (canonical address).",
          },
          path: {
            type: "string",
            description: "Hierarchy path of the target GameObject (fallback address).",
          },
          name: {
            type: "string",
            description: "Target GameObject name (lowest priority address).",
          },
          axis: {
            type: "string",
            enum: ["top", "bottom", "front", "back", "left", "right"],
            description:
              "Optional view axis to orient the SceneView camera. Omit to keep the current rotation. " +
              "top = look straight down (–Y), bottom = +Y, front = look along –Z, back = +Z, " +
              "left = look along +X, right = –X.",
          },
          size: {
            type: "number",
            minimum: 0,
            description:
              "Optional SceneView size (half-extent of the framing box). Omit to derive from the " +
              "target bounds.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the scene path containing the target." },
          gate: { ...GATE_PROP },
        },
  },
);
