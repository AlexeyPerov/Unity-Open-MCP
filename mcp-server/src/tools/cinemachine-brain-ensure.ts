import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 6 / T20.6.1 — Cinemachine brain_ensure. Reflection-gated. Mutating
// (adds a component) but idempotent (already-present Brain is a no-op).
export const cinemachineBrainEnsure: Tool = {
  name: "unity_open_mcp_cinemachine_brain_ensure",
  description:
    "Ensure a CinemachineBrain component exists on a Camera GameObject. If " +
    "instance_id / path / name is supplied, ensure on that Camera; otherwise " +
    "locate the main Camera. Adds the Brain when absent (idempotent when " +
    "present). Mutating: runs the full gate path; paths_hint is the host scene " +
    "path. Requires Cinemachine 3.x.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: { type: "integer", description: "Camera GameObject instance id." },
      path: { type: "string", description: "Camera hierarchy path." },
      name: { type: "string", description: "Camera name (last-resort resolver)." },
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
