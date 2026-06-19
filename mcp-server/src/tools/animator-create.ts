import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.10 — Animation extension tool. Requires the animation
// extension pack. Mutating: runs the full gate path.
export const animatorCreate: Tool = {
  name: "unity_open_mcp_animator_create",
  description:
    "Create empty AnimatorController assets at one or more 'Assets/'-rooted " +
    ".controller paths. Intermediate folders are created. Each path is validated " +
    "independently — bad entries land in `errors`, the rest still create. Pair " +
    "with animator_modify to add layers/states/parameters afterwards. Mutating: " +
    "runs the full gate path; paths_hint is the list of .controller paths being " +
    "created. Requires the animation extension pack installed in the project.",
  inputSchema: {
    type: "object",
    required: ["asset_paths", "paths_hint"],
    properties: {
      asset_paths: {
        type: "array",
        items: { type: "string" },
        description:
          "One or more 'Assets/'-rooted .controller paths to create " +
          "(e.g. ['Assets/Animators/Player.controller']).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the .controller paths being created.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
