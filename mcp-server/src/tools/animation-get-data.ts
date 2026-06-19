import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.10 — Animation extension tool. Requires the animation
// extension pack. Read-only, gate-free.
export const animationGetData: Tool = {
  name: "unity_open_mcp_animation_get_data",
  description:
    "Inspect an AnimationClip asset (.anim) — name, length, frame rate, wrap " +
    "mode, looping/legacy/humanMotion flags, plus the full set of float curve " +
    "bindings, object-reference curve bindings, and animation events. Read-only, " +
    "gate-free. Use this to discover valid (path, propertyName, type) tuples " +
    "for animation_modify SetCurve / RemoveCurve entries. Requires the animation " +
    "extension pack installed in the project.",
  inputSchema: {
    type: "object",
    required: ["asset_path"],
    properties: {
      asset_path: {
        type: "string",
        description: "'Assets/'-rooted path to the existing '.anim' asset.",
      },
    },
    additionalProperties: false,
  },
};
