import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.4 — Input System extension tool. The bridge-side handler
// is embedded in the bridge (compile-gated by UNITY_OPEN_MCP_EXT_INPUTSYSTEM,
// active when com.unity.inputsystem is present). Mutating: runs the full gate
// path; paths_hint is the new .inputactions asset path.
export const inputsystemAssetCreate = makeTool(
  "unity_open_mcp_inputsystem_asset_create",
  "Create a new InputActionAsset at an 'Assets/'-rooted path ending in " +
    "'.inputactions'. Optionally seed an initial ActionMap. Intermediate " +
    "folders must already exist (use assets_create_folder first). Mutating: " +
    "runs the full gate path; paths_hint is the new .inputactions asset path. " +
    "Requires the input system extension pack installed in the project.",
  {
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
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the new .inputactions asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
