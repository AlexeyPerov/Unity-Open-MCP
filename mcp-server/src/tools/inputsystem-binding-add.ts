import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.4 — Input System extension tool. Requires the input
// system extension pack. Mutating: runs the full gate path.
export const inputsystemBindingAdd: Tool = {
  name: "unity_open_mcp_inputsystem_binding_add",
  description:
    "Add a simple (non-composite) InputBinding to an Action in a .inputactions " +
    "asset. path is a control path (e.g. '<Keyboard>/space'). groups is optional " +
    "control-scheme group(s), semicolon-separated. interactions / processors are " +
    "optional. For composite bindings (2DVector / 1DAxis) use " +
    "inputsystem_binding_composite_add. Mutating: runs the full gate path; " +
    "paths_hint is the .inputactions asset path. Requires the input system " +
    "extension pack installed in the project.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "map_name", "action_name", "path", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description: "'Assets/'-rooted path to the existing '.inputactions' asset.",
      },
      map_name: { type: "string", description: "ActionMap containing the action." },
      action_name: { type: "string", description: "Action to add the binding to." },
      path: {
        type: "string",
        description: "Control path for the binding (e.g. '<Keyboard>/space').",
      },
      groups: {
        type: "string",
        description: "Optional control-scheme group(s), semicolon-separated.",
      },
      interactions: {
        type: "string",
        description: "Optional interactions (e.g. 'hold', 'press,tap').",
      },
      processors: {
        type: "string",
        description: "Optional processors (e.g. 'invert').",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the .inputactions asset path.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
