import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.10 — Animation extension tool. Requires the animation
// extension pack. Read-only, gate-free.
export const animatorGetData = makeTool(
  "unity_open_mcp_animator_get_data",
  "Inspect an AnimatorController asset (.controller) — name, parameters " +
    "(name / type / defaults), layers, and per-layer state machines (states, " +
    "default state, state-to-state transitions, any-state transitions, " +
    "sub-state machines). Read-only, gate-free. Use this to discover valid " +
    "layer / state / parameter names for animator_modify. Requires the " +
    "animation extension pack installed in the project.",
  {
    required: ["asset_path"],
        properties: {
          asset_path: {
            type: "string",
            description: "'Assets/'-rooted path to the existing '.controller' asset.",
          },
        },
  },
);
