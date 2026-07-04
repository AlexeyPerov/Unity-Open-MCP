import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 3 / T20.3.3 — Constraints & LOD domain tool. Built-in engine
// modules (UnityEngine.AnimationModule for constraints, UnityEngine.CoreModule
// for LODGroup) — no extra UPM; the `constraints` group is hidden until
// manage_tools activates it. Mutating: runs the full gate path; paths_hint is
// the host scene path. Address the host by instance_id > path > name (same
// model as gameobject_* / component_*). Param shape: type + source + activate.
const targetSchema = {
  instance_id: {
    type: ["string", "integer"],
    default: 0,
    description: "Host GameObject instance ID. Highest priority resolver.",
  },
  path: {
    type: "string",
    description: "Host hierarchy path \"Root/Child\".",
  },
  name: {
    type: "string",
    description: "Host GameObject name (first match). Lowest priority resolver.",
  },
  paths_hint: {
    type: "array",
    items: { type: "string" },
    description: "Mutation scope — the host scene path. No whole-project fallback.",
  },
  gate: {
    enum: ["enforce", "warn", "off"],
    default: "enforce",
  },
};

export const constraintAdd: Tool = {
  name: "unity_open_mcp_constraint_add",
  description:
    "Add an animation constraint component to a GameObject. constraint_type is one of " +
    "PositionConstraint | RotationConstraint | AimConstraint | ParentConstraint | " +
    "ScaleConstraint. Optional: source_path (the constrained-to Transform, resolved " +
    "to a GameObject and its Transform taken), weight (0-1, default 1), " +
    "constraint_active (default true). Idempotent — re-using an existing constraint of " +
    "the same type reports added:false (source / weight / activation still applied). " +
    "Mutating: runs the full gate path; paths_hint is the host scene path. Built-in " +
    "engine modules (no package dependency); the constraints group is hidden until " +
    "manage_tools activates it.",
  inputSchema: {
    type: "object",
    required: ["constraint_type", "paths_hint"],
    properties: {
      ...targetSchema,
      constraint_type: {
        type: "string",
        enum: [
          "PositionConstraint",
          "RotationConstraint",
          "AimConstraint",
          "ParentConstraint",
          "ScaleConstraint",
        ],
        description: "Animation constraint type to add.",
      },
      source_path: {
        type: "string",
        description: "Source GameObject hierarchy path (its Transform is taken). Optional.",
      },
      source_instance_id: {
        type: ["string", "integer"],
        default: 0,
        description: "Source GameObject instance ID. Highest priority source resolver.",
      },
      source_name: {
        type: "string",
        description: "Source GameObject name (first match). Lowest priority source resolver.",
      },
      weight: {
        type: "number",
        default: 1,
        description: "Source weight (0-1). Clamped. Ignored when no source is provided.",
      },
      constraint_active: {
        type: "boolean",
        default: true,
        description: "Whether the constraint is active after add.",
      },
    },
    additionalProperties: false,
  },
};
