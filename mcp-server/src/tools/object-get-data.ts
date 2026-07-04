import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 6 — typed object data read. Read-only (gate-free). Returns token-
// bounded structured data for any live UnityEngine.Object (scene instance or
// asset). Uses the depth-limited reflective walker from OutputSerializer
// (same engine invoke_method uses for its return value) so the shape is
// consistent across tools. Complements component_get (which is scoped to one
// Component via SerializedObject) — this tool works on ANY Object and uses
// reflective field/property walk instead of SerializedProperty.
export const objectGetData: Tool = {
  name: "unity_open_mcp_object_get_data",
  description:
    "Read token-bounded structured data for any live UnityEngine.Object (scene GameObject, " +
    "Component, ScriptableObject, Material, or any asset). Read-only and gate-free. Uses a " +
    "depth-limited reflective walk over public fields and (optionally) properties — the same " +
    "engine invoke_method uses to serialize return values, so the shape is consistent. " +
    "Address the object by instance_id (live scene/asset instance) or asset_path (asset on " +
    "disk); instance_id wins when both are set. Prefer component_get for one Component's " +
    "serialized fields (it uses SerializedObject and is more accurate for Inspector fields); " +
    "use this tool for ScriptableObjects, Materials, or any Object that is not a Component.",
  inputSchema: {
    type: "object",
    properties: {
      instance_id: {
        type: ["string", "integer"],
        default: 0,
        description:
          "Instance ID of a live UnityEngine.Object (scene GameObject/Component or asset " +
          "instance). Highest priority resolver.",
      },
      asset_path: {
        type: "string",
        description:
          "Asset path (e.g. \"Assets/Data/Config.asset\") to load via AssetDatabase. Used " +
          "when instance_id is not set.",
      },
      include_fields: {
        type: "boolean",
        default: true,
        description: "Walk public fields.",
      },
      include_properties: {
        type: "boolean",
        default: true,
        description:
          "Walk public properties. On by default to match invoke_method's return shape; " +
          "set false to suppress properties (some have side effects on read).",
      },
      max_depth: {
        type: "integer",
        default: 4,
        minimum: 0,
        description: "Max recursion depth when walking nested objects (default 4).",
      },
      max_items: {
        type: "integer",
        default: 100,
        minimum: 0,
        description: "Max items emitted per list/enumerable (default 100). Truncated lists report a `truncated` count.",
      },
    },
    additionalProperties: false,
  },
};
