import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 6 / T20.6.1 — Cinemachine set_lens. Reflection-gated. Mutating.
export const cinemachineSetLens: Tool = {
  name: "unity_open_mcp_cinemachine_set_lens",
  description:
    "Set lens settings on a CinemachineCamera. Fields: field_of_view (degrees), " +
    "near_clip, far_clip, dutch (degrees, lens roll). Omitted fields keep the " +
    "current value. Mutating: runs the full gate path; paths_hint is the host " +
    "scene path. Requires Cinemachine 3.x.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: { type: "integer", description: "CinemachineCamera host GameObject instance id." },
      path: { type: "string", description: "Host hierarchy path." },
      name: { type: "string", description: "Host name (last-resort resolver)." },
      field_of_view: {
        type: "number",
        description: "Field of view in degrees. Pass a value <= 0 to leave unchanged.",
      },
      near_clip: {
        type: "number",
        description: "Near clip plane. Pass <= 0 to leave unchanged.",
      },
      far_clip: {
        type: "number",
        description: "Far clip plane. Pass <= 0 to leave unchanged.",
      },
      dutch: {
        type: "number",
        description: "Lens roll (degrees). Pass a value <= -1000 to leave unchanged.",
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
