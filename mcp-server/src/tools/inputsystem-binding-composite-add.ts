import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.4 — Input System extension tool. Requires the input
// system extension pack. Mutating: runs the full gate path.
export const inputsystemBindingCompositeAdd = makeTool(
  "unity_open_mcp_inputsystem_binding_composite_add",
  "Add a composite InputBinding (e.g. '2DVector' WASD, '1DAxis') to an Action. " +
    "parts_json is a JSON array of { name, path, groups? } entries — e.g. " +
    "[{\"name\":\"up\",\"path\":\"<Keyboard>/w\"},{\"name\":\"down\",\"path\":\"<Keyboard>/s\"}]. " +
    "composite is the composite type (default '2DVector'); also '1DAxis', 'Axis', " +
    "'Dpad'. interactions / processors apply to the composite root. Mutating: " +
    "runs the full gate path; paths_hint is the .inputactions asset path. " +
    "Requires the input system extension pack installed in the project.",
  {
    required: ["asset_path", "map_name", "action_name", "parts_json", "paths_hint"],
        properties: {
          asset_path: {
            type: "string",
            description: "'Assets/'-rooted path to the existing '.inputactions' asset.",
          },
          map_name: { type: "string", description: "ActionMap containing the action." },
          action_name: { type: "string", description: "Action that receives the composite." },
          parts_json: {
            type: "string",
            description:
              "JSON array of { name, path, groups? } composite parts. Example: " +
              "[{\"name\":\"up\",\"path\":\"<Keyboard>/w\"},{\"name\":\"down\",\"path\":\"<Keyboard>/s\"}]. " +
              "2DVector parts: up/down/left/right. 1DAxis parts: negative/positive.",
          },
          composite: {
            type: "string",
            default: "2DVector",
            description: "Composite type (2DVector / 1DAxis / Axis / Dpad).",
          },
          interactions: {
            type: "string",
            description: "Optional interactions applied to the composite root.",
          },
          processors: {
            type: "string",
            description: "Optional processors applied to the composite root.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the .inputactions asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
