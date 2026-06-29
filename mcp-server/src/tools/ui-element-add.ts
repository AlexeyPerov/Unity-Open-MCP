import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 3 / T20.3.2 — UI (uGUI) domain tool. Adds a uGUI element as a child
// of a parent RectTransform. Built-in UI module. Mutating: runs the full gate
// path; paths_hint is the parent scene path. The parent MUST exist — every
// uGUI element lives under a Canvas in the hierarchy. TMP_Text requires the
// TextMesh Pro package; when absent, returns `tmp_package_required` (no silent
// legacy-Text fallback). Param shape: element types + parent + common fields.
export const uiElementAdd: Tool = {
  name: "unity_open_mcp_ui_element_add",
  description:
    "Add a uGUI element as a child of a parent RectTransform. element_type is one of " +
    "Text | TMP_Text | Image | Button | Slider | Toggle | InputField. Common fields: " +
    "text (Text / TMP_Text / InputField), color (Graphic.color, r,g,b,a 0-1), " +
    "sprite_path (Image / Button / Toggle sprite, Assets/-rooted). TMP_Text requires the " +
    "TextMesh Pro package — when absent, returns `tmp_package_required` (no silent " +
    "legacy-Text fallback). Mutating: runs the full gate path; paths_hint is the parent " +
    "scene path. Built-in UI module (no package dependency); the ui group is hidden " +
    "until manage_tools activates it.",
  inputSchema: {
    type: "object",
    required: ["element_type", "paths_hint"],
    properties: {
      parent_instance_id: {
        type: "integer",
        default: 0,
        description: "Parent GameObject instance ID. Highest priority resolver.",
      },
      parent_path: {
        type: "string",
        description: "Parent hierarchy path \"Root/Parent\".",
      },
      parent_name: {
        type: "string",
        description: "Parent GameObject name (first match). Lowest priority resolver.",
      },
      element_type: {
        type: "string",
        enum: ["Text", "TMP_Text", "Image", "Button", "Slider", "Toggle", "InputField"],
        description: "uGUI element type to add.",
      },
      element_name: {
        type: "string",
        description: "Name for the new element GameObject (defaults to element_type).",
      },
      text: {
        type: "string",
        description: "Initial text (Text / TMP_Text / InputField).",
      },
      color: {
        type: "string",
        description: "Graphic.color as 'r,g,b,a' (0-1).",
      },
      sprite_path: {
        type: "string",
        description: "Assets/-rooted Sprite path (Image / Button / Toggle sprite).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the parent scene path. No whole-project fallback.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
