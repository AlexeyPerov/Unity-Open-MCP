import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 3 / T20.3.2 — UI (uGUI) domain tool. Typed patch on a uGUI
// component. Built-in UI module. Mutating: runs the full gate path; paths_hint
// is the host scene path. Mirrors component_modify's shape but is scoped to
// uGUI types so the value conversion is purpose-built for the common fields
// (Graphic.color, Graphic.raycastTarget, Selectable.interactable, LayoutElement
// preferred sizes, Text / TMP_Text text).
export const uiElementModify = makeTool(
  "unity_open_mcp_ui_element_modify",
  "Set typed fields on a uGUI component attached to a target GameObject. Select the " +
    "component by 'component_type' (Text | TMP_Text | Image | Button | Slider | Toggle | " +
    "InputField | Canvas | CanvasScaler | GraphicRaycaster | HorizontalLayoutGroup | " +
    "VerticalLayoutGroup | GridLayoutGroup | LayoutElement | Selectable). Each entry is " +
    "{ field, value, type? } where type is 'int' | 'float' | 'bool' | 'string' | 'color' " +
    "| 'vector' (default inferred from the current value). Unknown fields are reported " +
    "as errors and the tool fails atomically (no partial writes) when any field fails " +
    "to convert. Mutating: runs the full gate path; paths_hint is the host scene path. " +
    "Built-in UI module (no package dependency); the ui group is hidden until " +
    "manage_tools activates it.",
  {
    required: ["component_type", "fields_json", "paths_hint"],
        properties: {
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
          component_type: {
            type: "string",
            description:
              "uGUI component type name. One of Text | TMP_Text | Image | Button | Slider | " +
              "Toggle | InputField | Canvas | CanvasScaler | GraphicRaycaster | " +
              "HorizontalLayoutGroup | VerticalLayoutGroup | GridLayoutGroup | LayoutElement | " +
              "Selectable.",
          },
          fields_json: {
            type: "string",
            description:
              "JSON array of { field, value, type? } entries. type is 'int' | 'float' | 'bool' " +
              "| 'string' | 'color' | 'vector' (default inferred). color is 'r,g,b,a' (0-1); " +
              "vector is 'x,y' (Vector2) or 'x,y,z' (Vector3).",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the host scene path. No whole-project fallback." },
          gate: { ...GATE_PROP },
        },
  },
);
