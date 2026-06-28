import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 6 / T20.6.1 — Cinemachine set_body. Reflection-gated. Mutating.
export const cinemachineSetBody: Tool = {
  name: "unity_open_mcp_cinemachine_set_body",
  description:
    "Add or replace the Body component on a CinemachineCamera (the 3.x " +
    "position-control pipeline). body_name selects the component type " +
    "(e.g. CinemachineFollow, CinemachineFramingTransposer, " +
    "CinemachineHardLockToTarget). The current Body component (if any) is " +
    "removed when its type differs. Mutating: runs the full gate path; " +
    "paths_hint is the host scene path. Requires Cinemachine 3.x.",
  inputSchema: {
    type: "object",
    required: ["paths_hint", "body_name"],
    properties: {
      instance_id: { type: "integer", description: "CinemachineCamera host GameObject instance id." },
      path: { type: "string", description: "Host hierarchy path." },
      name: { type: "string", description: "Host name (last-resort resolver)." },
      body_name: {
        type: "string",
        description:
          "Unqualified Cinemachine Body component type name, e.g. " +
          "'CinemachineFollow', 'CinemachineFramingTransposer', " +
          "'CinemachineHardLockToTarget'.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the host scene path.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
