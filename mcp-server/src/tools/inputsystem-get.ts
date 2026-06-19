import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.4 — Input System extension tool. Requires the input
// system extension pack. Read-only, gate-free.
export const inputsystemGet: Tool = {
  name: "unity_open_mcp_inputsystem_get",
  description:
    "Read the full structure of a .inputactions InputActionAsset — ActionMaps, " +
    "Actions (type / expectedControlType), Bindings (path / groups / interactions " +
    "/ processors / index / composite flags) and Control Schemes. Read-only, " +
    "gate-free. Use this to discover map / action / binding names to drive the " +
    "other tools. Requires the input system extension pack installed in the project.",
  inputSchema: {
    type: "object",
    required: ["asset_path"],
    properties: {
      asset_path: {
        type: "string",
        description: "'Assets/'-rooted path to the existing '.inputactions' asset.",
      },
    },
    additionalProperties: false,
  },
};
