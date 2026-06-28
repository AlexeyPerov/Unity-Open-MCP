import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 3 / T20.3.2 — UI (uGUI) domain tool. Built-in UI module (no extra
// UPM); the `ui` group is hidden until manage_tools activates it. Mutating:
// runs the full gate path; paths_hint is the host / new-root scene path.
// Address the host by instance_id > path > name (same model as gameobject_* /
// component_*). When no host is addressed, a new scene root is created
// (new_root_name controls its name; defaults to "Canvas"). Param shape mirrors
// AnkleBreaker's unity_ui_create_canvas (renderMode overlay/camera/world +
// EventSystem).
const targetSchema = {
  instance_id: {
    type: "integer",
    default: 0,
    description: "Host GameObject instance ID. Highest priority resolver. When omitted, a new scene root is created.",
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
    description: "Mutation scope — the host's (or new root's) scene path.",
  },
  gate: {
    enum: ["enforce", "warn", "off"],
    default: "enforce",
  },
};

export const uiCanvasAdd: Tool = {
  name: "unity_open_mcp_ui_canvas_add",
  description:
    "Add a Canvas to a GameObject (or as a new scene root when no host is addressed). " +
    "Ensures the canvas has a CanvasScaler + GraphicRaycaster, and ensures an EventSystem " +
    "exists in the open scene(s). Set render_mode (ScreenSpaceOverlay | ScreenSpaceCamera | " +
    "WorldSpace, default ScreenSpaceOverlay) and sorting_order (default 0). Idempotent — " +
    "re-using an existing Canvas reports added:false (the scaler / raycaster / EventSystem " +
    "are still ensured). Mutating: runs the full gate path; paths_hint is the host / " +
    "new-root scene path. Built-in UI module (no package dependency); the ui group is " +
    "hidden until manage_tools activates it.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      ...targetSchema,
      render_mode: {
        type: "string",
        default: "ScreenSpaceOverlay",
        enum: ["ScreenSpaceOverlay", "ScreenSpaceCamera", "WorldSpace"],
        description: "Canvas render mode.",
      },
      sorting_order: {
        type: "integer",
        default: 0,
        description: "Canvas sorting order (higher = drawn on top).",
      },
      new_root_name: {
        type: "string",
        description: "Name for the new scene root when no host is addressed (defaults to 'Canvas').",
      },
    },
    additionalProperties: false,
  },
};
