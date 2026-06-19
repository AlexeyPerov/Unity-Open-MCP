import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.10 — Animation extension tool. Requires the animation
// extension pack. Mutating: runs the full gate path.
export const animationCreate: Tool = {
  name: "unity_open_mcp_animation_create",
  description:
    "Create empty AnimationClip assets at one or more 'Assets/'-rooted .anim " +
    "paths. Intermediate folders are created. Each path is validated " +
    "independently — bad entries land in `errors`, the rest still create. Pair " +
    "with animation_modify to populate curves and events afterwards. Mutating: " +
    "runs the full gate path; paths_hint is the list of .anim paths being created. " +
    "Requires the animation extension pack installed in the project.",
  inputSchema: {
    type: "object",
    required: ["asset_paths", "paths_hint"],
    properties: {
      asset_paths: {
        type: "array",
        items: { type: "string" },
        description:
          "One or more 'Assets/'-rooted .anim paths to create " +
          "(e.g. ['Assets/Animations/Idle.anim']).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the .anim paths being created.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
