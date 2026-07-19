import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 6 / T20.6.1 — Cinemachine brain_ensure. Reflection-gated. Mutating
// (adds a component) but idempotent (already-present Brain is a no-op).
export const cinemachineBrainEnsure = makeTool(
  "unity_open_mcp_cinemachine_brain_ensure",
  "Ensure a CinemachineBrain component exists on a Camera GameObject. If " +
    "instance_id / path / name is supplied, ensure on that Camera; otherwise " +
    "locate the main Camera. Adds the Brain when absent (idempotent when " +
    "present). Mutating: runs the full gate path; paths_hint is the host scene " +
    "path. Requires Cinemachine 3.x.",
  {
    required: ["paths_hint"],
        properties: {
          instance_id: { type: "integer", description: "Camera GameObject instance id." },
          path: { type: "string", description: "Camera hierarchy path." },
          name: { type: "string", description: "Camera name (last-resort resolver)." },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the host scene path." },
          gate: { ...GATE_PROP },
        },
  },
);
