import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 2 — typed GameObject create. Mutating: runs the full gate path.
// Scene side-effect — scope paths_hint to the destination scene path.
export const gameobjectCreate: Tool = {
  name: "unity_open_mcp_gameobject_create",
  description:
    "Create a new GameObject in the active scene, optionally parented under an existing scene " +
    "GameObject and pre-positioned. Pass `primitive_type` to spawn a Unity primitive (Cube/Sphere/" +
    "Capsule/Cylinder/Plane/Quad) with its default renderer+collider. Mutating: runs the full gate " +
    "path; `paths_hint` should be the destination scene path (the GameObject is a scene side-effect). " +
    "Returns the new instance's instanceId, name, path, transform, and component list so the agent " +
    "can immediately chain component_add / modify / set_parent without an extra lookup.",
  inputSchema: {
    type: "object",
    required: ["name", "paths_hint"],
    properties: {
      name: {
        type: "string",
        description: "Name for the new GameObject.",
      },
      primitive_type: {
        type: "string",
        enum: ["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"],
        description:
          "When set, the GameObject is created via GameObject.CreatePrimitive (adds the matching " +
          "MeshFilter/MeshRenderer/Collider). Omit for an empty GameObject.",
      },
      parent_path: {
        type: "string",
        description:
          "Optional destination scene path \"Root/Parent\" — the parent must already exist. " +
          "Omit to create at scene root.",
      },
      position: {
        type: "string",
        description: "Optional position as \"x,y,z\". Defaults to 0,0,0.",
      },
      rotation: {
        type: "string",
        description: "Optional Euler rotation in degrees as \"x,y,z\". Defaults to 0,0,0.",
      },
      scale: {
        type: "string",
        description: "Optional scale as \"x,y,z\". Defaults to 1,1,1.",
      },
      local_space: {
        type: "boolean",
        default: false,
        description:
          "When true, position/rotation are interpreted in the parent's local space (matches " +
          "the Inspector when the GameObject is parented). Default false = world space.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — include the active scene path (the GameObject is a scene side-effect).",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
