import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.4 — Input System extension tool. Requires the input
// system extension pack. Mutating: runs the full gate path.
export const inputsystemActionAdd: Tool = {
  name: "unity_open_mcp_inputsystem_action_add",
  description:
    "Add an InputAction to an ActionMap in an existing .inputactions asset. " +
    "action_type is 'Button' (default), 'Value', or 'PassThrough'. " +
    "expected_control_type is optional (e.g. 'Button', 'Vector2', 'Axis'). " +
    "binding is an optional initial binding control path (e.g. '<Keyboard>/space'). " +
    "groups / interactions / processors apply to the initial binding. Mutating: " +
    "runs the full gate path; paths_hint is the .inputactions asset path. " +
    "Requires the input system extension pack installed in the project.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "map_name", "action_name", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description: "'Assets/'-rooted path to the existing '.inputactions' asset.",
      },
      map_name: { type: "string", description: "ActionMap that will own the new action." },
      action_name: {
        type: "string",
        description: "Unique name for the new action within the map.",
      },
      action_type: {
        type: "string",
        enum: ["Button", "Value", "PassThrough"],
        default: "Button",
        description: "Input behavior type.",
      },
      expected_control_type: {
        type: "string",
        description: "Control layout the action expects (e.g. 'Button', 'Vector2', 'Axis').",
      },
      binding: {
        type: "string",
        description: "Optional initial binding control path (e.g. '<Gamepad>/buttonSouth').",
      },
      groups: {
        type: "string",
        description: "Optional control-scheme group(s) for the initial binding (semicolon-separated).",
      },
      interactions: {
        type: "string",
        description: "Optional interactions for the initial binding (e.g. 'hold', 'press,tap').",
      },
      processors: {
        type: "string",
        description: "Optional processors for the initial binding (e.g. 'invert').",
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
