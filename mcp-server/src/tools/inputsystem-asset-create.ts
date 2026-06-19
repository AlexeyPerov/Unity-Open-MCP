import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.4 — Input System extension tool. Requires the
// `com.alexeyperov.unity-open-mcp-ext-inputsystem` extension pack installed in
// the target project. Mutating: runs the full gate path; paths_hint is the new
// .inputactions asset path.
export const inputsystemAssetCreate: Tool = {
  name: "unity_open_mcp_inputsystem_asset_create",
  description:
    "Create a new InputActionAsset at an 'Assets/'-rooted path ending in " +
    "'.inputactions'. Optionally seed an initial ActionMap. Intermediate " +
    "folders must already exist (use assets_create_folder first). Mutating: " +
    "runs the full gate path; paths_hint is the new .inputactions asset path. " +
    "Requires the input system extension pack installed in the project.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description:
          "'Assets/'-rooted path ending in '.inputactions' " +
          "(e.g. 'Assets/Input/Player.inputactions').",
      },
      initial_action_map: {
        type: "string",
        description: "Optional name for an initial ActionMap added to the new asset.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the new .inputactions asset path.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
