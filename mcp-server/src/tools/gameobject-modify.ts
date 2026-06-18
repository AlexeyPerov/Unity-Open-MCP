import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 2 — typed GameObject modify. Mutating: runs the full gate path.
// Covers name / tag / layer / active AND transform (pos/rot/scale).
export const gameobjectModify: Tool = {
  name: "unity_open_mcp_gameobject_modify",
  description:
    "Modify one or more GameObject fields in a single call: name, tag, layer, active, and " +
    "transform (position, rotation, scale). Only provided fields are touched; omitted fields are " +
    "preserved. Undo-recorded. Mutating: runs the full gate path; `paths_hint` is the scene path " +
    "that contains the target. Use local_space=true to interpret transform values in the parent's " +
    "local space (matches Inspector). Address the target by instance_id > path > name.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: {
        type: "integer",
        default: 0,
        description: "Target GameObject instance ID. Highest priority resolver.",
      },
      path: {
        type: "string",
        description: "Target hierarchy path \"Root/Child\".",
      },
      name_target: {
        type: "string",
        description: "Target GameObject name (first match). Lowest priority resolver.",
      },
      name: {
        type: "string",
        description: "New name. Omit to leave unchanged.",
      },
      tag: {
        type: "string",
        description:
          "New tag. Must be a defined tag (use editor_get_tags from Plan 5 to enumerate). Omit to leave unchanged.",
      },
      layer: {
        type: "integer",
        minimum: 0,
        maximum: 31,
        description: "New layer index (0-31). Omit to leave unchanged.",
      },
      active: {
        type: "boolean",
        description: "Toggle active state. Omit to leave unchanged.",
      },
      position: {
        type: "string",
        description: "New position as \"x,y,z\". Omit to leave unchanged.",
      },
      rotation: {
        type: "string",
        description: "New Euler rotation in degrees as \"x,y,z\". Omit to leave unchanged.",
      },
      scale: {
        type: "string",
        description: "New scale as \"x,y,z\". Omit to leave unchanged.",
      },
      local_space: {
        type: "boolean",
        default: false,
        description:
          "When true, position/rotation are local-space (parent-relative). Default false = world " +
          "space. Unset fields inherit the same space as the previously-set value so they round-trip cleanly.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the scene path that contains the target.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
