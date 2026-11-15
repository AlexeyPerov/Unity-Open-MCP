import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 2 — typed GameObject create. Mutating: runs the full gate path.
// Scene side-effect — scope paths_hint to the destination scene path.
export const gameobjectCreate = makeTool(
  "unity_open_mcp_gameobject_create",
  "Create a new GameObject, optionally parented under an existing GameObject and pre-positioned. Pass " +
    "`primitive_type` to spawn a Unity primitive (Cube/Sphere/Capsule/Cylinder/Plane/Quad) with its default " +
    "renderer+collider. Without parent_path the new root goes to the container in_prefab / scene_path / " +
    "scene_name names (under the prefab root for a prefab); else, while a prefab stage is open, under the " +
    "prefab root (unless paths_hint names a loaded scene and not the prefab); else the active scene. Mutating: " +
    "runs the full gate path; `paths_hint` is the destination scene path (or the prefab asset path). Returns " +
    "the new instance's instanceId, name, path, " +
    "container, transform, and component list so the agent can immediately chain component_add / modify / " +
    "set_parent without an extra lookup.",
  {
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
              "Optional parent hierarchy path \"Root/Parent\" (in a prefab — in_prefab or the open stage — from " +
              "the prefab root) — the parent must already exist. Omit to create a root GameObject.",
          },
          scene_path: {
            type: "string",
            description:
              "Optional container: a loaded scene's asset path (e.g. 'Assets/Scenes/Bootstrap.unity'; load it " +
              "with unity_open_mcp_scene_open Additive first) or the open prefab stage's prefab asset path. The " +
              "new root GameObject goes there, and parent_path is looked up only there.",
          },
          scene_name: {
            type: "string",
            description:
              "Optional target scene name for the new root GameObject (alternative to scene_path). " +
              "The scene must already be loaded. Omit to create in the active scene.",
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
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — include the active scene path (the GameObject is a scene side-effect)." },
          gate: { ...GATE_PROP },
        },
  },
);
