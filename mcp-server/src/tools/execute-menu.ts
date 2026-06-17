import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const executeMenu: Tool = {
  name: "unity_open_mcp_execute_menu",
  description: "Execute a Unity Editor menu item.",
  inputSchema: {
    type: "object",
    required: ["menu_path"],
    properties: {
      menu_path: {
        type: "string",
        description: "e.g. Assets/Refresh, File/Save Project",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
      },
      ignore_scene_dirty: {
        type: "boolean",
        default: false,
        description:
          "Bypass the active-scene dirty guard. By default a disruptive op " +
          "(recompile / scene switch / menu that can disrupt the editor) is " +
          "refused with scene_dirty when any loaded scene has unsaved changes, " +
          "so Unity's native save modal never interrupts the flow. Set true to " +
          "proceed and accept the risk of a native save prompt.",
      },
      confirm_bypass: {
        type: "boolean",
        default: false,
        description:
          "Bypass the deny heuristic for destructive menu paths " +
          "(File/Quit, File/Exit, Assets/Reimport All). Requires gate: \"off\" " +
          "as well — both flags must be set. The bypass is audited.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
