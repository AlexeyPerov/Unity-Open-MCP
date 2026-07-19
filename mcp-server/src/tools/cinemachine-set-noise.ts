import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 6 / T20.6.1 — Cinemachine set_noise. Reflection-gated. Mutating.
export const cinemachineSetNoise = makeTool(
  "unity_open_mcp_cinemachine_set_noise",
  "Add or replace the Noise component on a CinemachineCamera. noise_name " +
    "selects the component type (e.g. CinemachineBasicMultiChannelPerlin, the " +
    "standard shake component). The current Noise component (if any) is " +
    "removed when its type differs. Mutating: runs the full gate path; " +
    "paths_hint is the host scene path. Requires Cinemachine 3.x.",
  {
    required: ["paths_hint", "noise_name"],
        properties: {
          instance_id: { type: "integer", description: "CinemachineCamera host GameObject instance id." },
          path: { type: "string", description: "Host hierarchy path." },
          name: { type: "string", description: "Host name (last-resort resolver)." },
          noise_name: {
            type: "string",
            description:
              "Unqualified Cinemachine Noise component type name, e.g. " +
              "'CinemachineBasicMultiChannelPerlin'.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the host scene path." },
          gate: { ...GATE_PROP },
        },
  },
);
