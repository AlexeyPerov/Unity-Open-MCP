import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 9 / T20.9.4 — SceneView pose mutation. Distinct from scene_focus:
// this sets pose-level camera state directly (position/rotation/projection),
// while scene_focus frames a target object.
export const sceneviewSetCamera = makeTool(
  "unity_open_mcp_sceneview_set_camera",
  "Set the SceneView camera pose (position required; optional rotation / " +
    "orthographic / size). Mutating editor UI state (window/camera movement): " +
    "runs the gate path and requires `paths_hint` scoped to the active scene " +
    "path. Returns `windowMoved: true` and the resulting camera pose.",
  {
    required: ["position", "paths_hint"],
        properties: {
          position: {
            type: "object",
            required: ["x", "y", "z"],
            properties: {
              x: { type: "number" },
              y: { type: "number" },
              z: { type: "number" },
            },
            additionalProperties: false,
            description: "Desired SceneView camera position (world space).",
          },
          rotation: {
            type: "object",
            required: ["x", "y", "z"],
            properties: {
              x: { type: "number" },
              y: { type: "number" },
              z: { type: "number" },
            },
            additionalProperties: false,
            description: "Optional SceneView camera Euler rotation in degrees.",
          },
          orthographic: {
            type: "boolean",
            description: "Optional projection mode switch for the SceneView camera.",
          },
          size: {
            type: "number",
            minimum: 0,
            description:
              "Optional SceneView size (zoom distance factor). Omit to keep current value.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — active scene path (records editor-camera move in gate history)." },
          gate: { ...GATE_PROP },
        },
  },
);
