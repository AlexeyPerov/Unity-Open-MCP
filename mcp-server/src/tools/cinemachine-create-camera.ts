import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 6 / T20.6.1 — Cinemachine extension tool. Reflection-gated in the
// bridge (the assembly always compiles; the Cinemachine 3.x presence is
// detected at call time, returning cinemachine_3x_required /
// cinemachine_package_required when unsupported). Mutating: runs the full gate
// path; paths_hint is the active scene path (the new GameObject lives there).
export const cinemachineCreateCamera = makeTool(
  "unity_open_mcp_cinemachine_create_camera",
  "Create a new GameObject carrying a CinemachineCamera (Cinemachine 3.x) in " +
    "the active scene. Optionally set name, position, rotation, parent_path, " +
    "priority, and follow / look_at targets (instance_id or hierarchy path). " +
    "Mutating: runs the full gate path; paths_hint is the active scene path. " +
    "Requires Cinemachine 3.x (`com.unity.cinemachine` >= 3.x).",
  {
    required: ["paths_hint"],
        properties: {
          name: { type: "string", description: "Optional GameObject name." },
          parent_path: {
            type: "string",
            description: "Optional slash-separated hierarchy path to a parent GameObject.",
          },
          position: { type: "string", description: "Optional position as 'x,y,z'." },
          rotation: { type: "string", description: "Optional Euler rotation (degrees) as 'x,y,z'." },
          priority: {
            type: "integer",
            default: 0,
            description: "Camera priority (higher wins the Brain's active slot).",
          },
          follow_instance_id: {
            type: ["string", "integer"],
            description: "Optional Follow target GameObject instance id.",
          },
          follow_path: {
            type: "string",
            description: "Optional Follow target hierarchy path (used when follow_instance_id is 0).",
          },
          look_at_instance_id: {
            type: ["string", "integer"],
            description: "Optional Look At target GameObject instance id.",
          },
          look_at_path: {
            type: "string",
            description: "Optional Look At target hierarchy path.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the active scene path (the new GameObject lives there)." },
          gate: { ...GATE_PROP },
        },
  },
);
