import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.4 — Input System extension tool. Requires the input
// system extension pack. Mutating: runs the full gate path.
export const inputsystemControlschemeAdd = makeTool(
  "unity_open_mcp_inputsystem_controlscheme_add",
  "Add an InputControlScheme to a .inputactions asset. required_devices / " +
    "optional_devices are arrays of device control paths (e.g. '<Gamepad>', " +
    "'<Keyboard>', '<Mouse>'). Mutating: runs the full gate path; paths_hint " +
    "is the .inputactions asset path. Requires the input system extension " +
    "pack installed in the project.",
  {
    required: ["asset_path", "scheme_name", "paths_hint"],
        properties: {
          asset_path: {
            type: "string",
            description: "'Assets/'-rooted path to the existing '.inputactions' asset.",
          },
          scheme_name: {
            type: "string",
            description: "Unique name for the new control scheme (e.g. 'KeyboardMouse', 'Gamepad').",
          },
          required_devices: {
            type: "array",
            items: { type: "string" },
            description:
              "Device control paths that MUST be present (e.g. ['<Keyboard>', '<Mouse>']). " +
              "All must be present for the scheme to be usable.",
          },
          optional_devices: {
            type: "array",
            items: { type: "string" },
            description: "Device control paths that MAY be present (e.g. ['<Pen>']).",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the .inputactions asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
