import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, IGNORE_SCENE_DIRTY_BASE, CONFIRM_BYPASS_BASE, makeTool } from "./schema-fragments.js";

export const executeMenu = makeTool(
  "unity_open_mcp_execute_menu",
  "Execute a Unity Editor menu item.",
  {
    required: ["menu_path"],
        properties: {
          menu_path: {
            type: "string",
            description: "e.g. Assets/Refresh, File/Save Project",
          },
          paths_hint: { ...PATHS_HINT_TYPE },
          ignore_scene_dirty: { ...IGNORE_SCENE_DIRTY_BASE, description: "Bypass the active-scene dirty guard. By default a disruptive op " + "(recompile / scene switch / menu that can disrupt the editor) is " + "refused with scene_dirty when any loaded scene has unsaved changes, " + "so Unity's native save modal never interrupts the flow. Set true to " + "proceed and accept the risk of a native save prompt." },
          confirm_bypass: { ...CONFIRM_BYPASS_BASE, description: "Bypass the deny heuristic for destructive menu paths " + "(File/Quit, File/Exit, Assets/Reimport All). Requires gate: \"off\" " + "as well — both flags must be set. The bypass is audited." },
          gate: { ...GATE_PROP },
        },
  },
);
