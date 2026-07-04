import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const spatialQuery: Tool = {
  name: "unity_senses_spatial_query",
  description:
    "Physics-based spatial queries against the current scene: raycast, " +
    "overlap, bounds, ground_check, nearest. Requires a loaded scene (live " +
    "Editor only). raycast/overlap/ground_check hit Colliders only (objects " +
    "without a Collider are invisible); bounds and nearest also see render-only " +
    "objects. Address targets by instance_id (canonical live handle, changes on " +
    "domain reload), path (\"Root/Child\"), or name. Vectors are \"x,y,z\" " +
    "strings. Responses include the hit object's instanceId, name, and path so " +
    "the agent can re-acquire it.",
  inputSchema: {
    type: "object",
    properties: {
      action: {
        type: "string",
        enum: ["raycast", "overlap", "bounds", "ground_check", "nearest"],
        description:
          "Query type. raycast: line cast returning hit point/normal/object. " +
          "overlap: volume query (sphere/box/capsule) returning objects inside. " +
          "bounds: combined world AABB of an object. ground_check: cast downward " +
          "(or along direction) from an object or point to find the surface " +
          "below. nearest: closest objects to an object or point.",
      },
      origin: {
        type: "string",
        description:
          "[raycast] Ray origin as \"x,y,z\".",
      },
      direction: {
        type: "string",
        description:
          "[raycast] Ray direction as \"x,y,z\". Also used by ground_check to " +
          "override the downward default.",
      },
      max_distance: {
        type: "number",
        default: 0,
        minimum: 0,
        description:
          "[raycast / ground_check] Maximum cast distance. 0 (default) = no limit.",
      },
      shape: {
        type: "string",
        enum: ["sphere", "box", "capsule"],
        default: "sphere",
        description: "[overlap] Volume shape.",
      },
      center: {
        type: "string",
        description: "[overlap] Volume center as \"x,y,z\".",
      },
      radius: {
        type: "number",
        default: 1,
        description: "[overlap] Sphere/capsule radius (defaults to 1 if <= 0).",
      },
      half_extents: {
        type: "string",
        description:
          "[overlap box] Half extents as \"x,y,z\". Defaults to 0.5,0.5,0.5.",
      },
      end: {
        type: "string",
        description:
          "[overlap capsule] Capsule end point as \"x,y,z\". Defaults to center.",
      },
      layer: {
        type: "string",
        description:
          "[raycast / overlap / ground_check] Restrict physics to a single " +
          "layer by name. Omit for all layers.",
      },
      query_triggers: {
        type: "boolean",
        default: false,
        description:
          "[raycast / overlap] Include trigger colliders. Default ignores them.",
      },
      instance_id: {
        type: ["string", "integer"],
        default: 0,
        description:
          "[bounds / ground_check / nearest] Target GameObject instance ID " +
          "(0 = not set). Highest priority target address.",
      },
      path: {
        type: "string",
        description:
          "[bounds / ground_check / nearest] Target hierarchy path " +
          "\"Root/Child/Leaf\".",
      },
      name: {
        type: "string",
        description:
          "[bounds / ground_check / nearest] Target GameObject name " +
          "(first match). Lowest priority target address.",
      },
      include_children: {
        type: "boolean",
        default: true,
        description:
          "[bounds] Encapsulate child renderers/colliders into the AABB.",
      },
      point: {
        type: "string",
        description:
          "[ground_check / nearest] Probe point as \"x,y,z\" when no target " +
          "object is given.",
      },
      max: {
        type: "integer",
        default: 5,
        minimum: 1,
        maximum: 200,
        description: "[nearest] Maximum number of objects returned (token cap).",
      },
      component: {
        type: "string",
        description:
          "[nearest] Only include objects that have this component type name.",
      },
      tag: {
        type: "string",
        description: "[nearest] Only include objects with this tag.",
      },
    },
    required: ["action"],
    additionalProperties: false,
  },
};
