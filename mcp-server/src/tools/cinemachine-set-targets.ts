import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 6 / T20.6.1 — Cinemachine set_targets. Reflection-gated in the
// bridge. Mutating: runs the full gate path; paths_hint is the host scene path.
export const cinemachineSetTargets: Tool = {
  name: "unity_open_mcp_cinemachine_set_targets",
  description:
    "Set the Follow and/or Look At targets on a CinemachineCamera. Each target " +
    "is addressed by instance_id or path; omit a target to leave it unchanged. " +
    "Mutating: runs the full gate path; paths_hint is the host scene path. " +
    "Requires Cinemachine 3.x.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: { type: "integer", description: "CinemachineCamera host GameObject instance id." },
      path: { type: "string", description: "Host hierarchy path (used when instance_id is 0)." },
      name: { type: "string", description: "Host name (last-resort resolver)." },
      follow_instance_id: { type: "integer", description: "Follow target GameObject instance id." },
      follow_path: { type: "string", description: "Follow target hierarchy path." },
      look_at_instance_id: { type: "integer", description: "Look At target GameObject instance id." },
      look_at_path: { type: "string", description: "Look At target hierarchy path." },
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
