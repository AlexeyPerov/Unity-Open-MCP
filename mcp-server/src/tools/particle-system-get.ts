import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.9 — Particle System extension tool. Requires the
// particle system extension pack. Read-only, gate-free.
const targetSchema = {
  instance_id: {
    type: "integer",
    default: 0,
    description: "Host GameObject instance ID. Highest priority resolver.",
  },
  path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
  name: { type: "string", description: "Host GameObject name (first match). Lowest priority resolver." },
};

export const particleSystemGet: Tool = {
  name: "unity_open_mcp_particle_system_get",
  description:
    "Inspect a ParticleSystem component on a scene GameObject — runtime state " +
    "(isPlaying / isPaused / isEmitting / isStopped / particleCount / time) plus " +
    "opt-in data for the well-known modules (main / emission / shape / " +
    "color_over_lifetime / size_over_lifetime / rotation_over_lifetime / noise / " +
    "collision / trails / renderer). include_main defaults to true; everything " +
    "else defaults to false. Set include_all to emit every module. Read-only, " +
    "gate-free. Use this to discover valid module + field names for " +
    "particle_system_modify. Requires the particle system extension pack installed " +
    "in the project.",
  inputSchema: {
    type: "object",
    properties: {
      ...targetSchema,
      include_main: { type: "boolean", default: true, description: "Include the Main module." },
      include_emission: { type: "boolean", default: false, description: "Include the Emission module." },
      include_shape: { type: "boolean", default: false, description: "Include the Shape module." },
      include_color_over_lifetime: { type: "boolean", default: false, description: "Include the Color over Lifetime module." },
      include_size_over_lifetime: { type: "boolean", default: false, description: "Include the Size over Lifetime module." },
      include_rotation_over_lifetime: { type: "boolean", default: false, description: "Include the Rotation over Lifetime module." },
      include_noise: { type: "boolean", default: false, description: "Include the Noise module." },
      include_collision: { type: "boolean", default: false, description: "Include the Collision module." },
      include_trails: { type: "boolean", default: false, description: "Include the Trails module." },
      include_renderer: { type: "boolean", default: false, description: "Include the sibling ParticleSystemRenderer component." },
      include_all: { type: "boolean", default: false, description: "Override every per-module flag and emit all modules." },
    },
    additionalProperties: false,
  },
};
