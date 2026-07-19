import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 3 / T20.3.2 — UI (uGUI) domain tool. Adds a layout group to a parent
// RectTransform. Built-in UI module. Mutating: runs the full gate path;
// paths_hint is the parent scene path. Idempotent — re-using an existing group
// of the same type reports added:false.
export const uiLayoutGroupAdd = makeTool(
  "unity_open_mcp_ui_layout_group_add",
  "Add a layout group to a parent RectTransform. layout_type is " +
    "HorizontalLayoutGroup | VerticalLayoutGroup | GridLayoutGroup. Optional: padding " +
    "(left,right,top,bottom — defaults 0), spacing (x,y — defaults 0), child_alignment " +
    "(TextAnchor name, default UpperLeft), child_control_width / child_control_height " +
    "(default true), child_force_expand_width / child_force_expand_height (default true). " +
    "Idempotent — re-using an existing group of the same type reports added:false. " +
    "Mutating: runs the full gate path; paths_hint is the parent scene path. Built-in " +
    "UI module (no package dependency); the ui group is hidden until manage_tools " +
    "activates it.",
  {
    required: ["layout_type", "paths_hint"],
        properties: {
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Parent GameObject instance ID. Highest priority resolver.",
          },
          path: {
            type: "string",
            description: "Parent hierarchy path \"Root/Parent\".",
          },
          name: {
            type: "string",
            description: "Parent GameObject name (first match). Lowest priority resolver.",
          },
          layout_type: {
            type: "string",
            enum: ["HorizontalLayoutGroup", "VerticalLayoutGroup", "GridLayoutGroup"],
            description: "Layout group type to add.",
          },
          padding: {
            type: "string",
            description: "Padding as 'left,right,top,bottom' (int). Defaults to 0 on all sides.",
          },
          spacing: {
            type: "string",
            description: "Spacing as 'x,y' (GridLayoutGroup) or 'x' (HV groups — y ignored). Defaults to 0.",
          },
          child_alignment: {
            type: "string",
            description: "TextAnchor name (e.g. 'UpperLeft', 'MiddleCenter'). Default UpperLeft.",
          },
          child_control_width: {
            type: "boolean",
            default: true,
            description: "HorizontalOrVerticalLayoutGroup: control child widths.",
          },
          child_control_height: {
            type: "boolean",
            default: true,
            description: "HorizontalOrVerticalLayoutGroup: control child heights.",
          },
          child_force_expand_width: {
            type: "boolean",
            default: true,
            description: "HorizontalOrVerticalLayoutGroup: force-expand child widths.",
          },
          child_force_expand_height: {
            type: "boolean",
            default: true,
            description: "HorizontalOrVerticalLayoutGroup: force-expand child heights.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the parent scene path. No whole-project fallback." },
          gate: { ...GATE_PROP },
        },
  },
);
