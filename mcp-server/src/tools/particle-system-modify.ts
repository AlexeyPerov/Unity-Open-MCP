import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.9 — Particle System extension tool. Requires the
// particle system extension pack. Mutating: runs the full gate path.
const targetSchema = {
  instance_id: {
    type: ["string", "integer"],
    default: 0,
    description: "Host GameObject instance ID. Highest priority resolver.",
  },
  path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
  name: { type: "string", description: "Host GameObject name (first match). Lowest priority resolver." },
  paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the host scene path." },
  gate: { ...GATE_PROP },
};

export const particleSystemModify = makeTool(
  "unity_open_mcp_particle_system_modify",
  "Apply a per-module field patch to a ParticleSystem component. module is one " +
    "of 'main' / 'emission' / 'shape' / 'color_over_lifetime' / 'size_over_lifetime' / " +
    "'rotation_over_lifetime' / 'noise' / 'collision' / 'trails' / 'renderer'. " +
    "fields_json is a JSON object of {field: value} entries to set on that module " +
    "(only the documented fields per module are accepted; unknown fields are " +
    "skipped and reported). Use particle_system_get first to discover valid " +
    "module + field names. Mutating: runs the full gate path; paths_hint is the " +
    "host scene path. Requires the particle system extension pack installed in " +
    "the project.",
  {
    required: ["module", "fields_json", "paths_hint"],
        properties: {
          ...targetSchema,
          module: {
            type: "string",
            enum: [
              "main",
              "emission",
              "shape",
              "color_over_lifetime",
              "size_over_lifetime",
              "rotation_over_lifetime",
              "noise",
              "collision",
              "trails",
              "renderer",
            ],
            description: "Which particle module to patch.",
          },
          fields_json: {
            type: "string",
            description:
              "JSON object of {field: value} entries to apply to the chosen module. " +
              "Per-module field surface (use particle_system_get to see current values): " +
              "main: duration(read-only) / loop / prewarm / startDelay / startLifetime / " +
              "startSpeed / startSize / startSize3D / startRotation3D / startRotation / " +
              "gravityModifier / playOnAwake / maxParticles / simulationSpace(Local/World/Custom) / " +
              "scalingMode(Hierarchy/Local/Shape). " +
              "emission: enabled / rateOverTime / rateOverDistance. " +
              "shape: enabled / shapeType / radius / radiusThickness / angle / arc / position('x,y,z') / " +
              "rotation('x,y,z') / scale('x,y,z'). " +
              "color_over_lifetime: enabled. " +
              "size_over_lifetime: enabled / separateAxes. " +
              "rotation_over_lifetime: enabled / separateAxes / x / y / z (radians/sec). " +
              "noise: enabled / strength / frequency / scrollSpeed / damping / octaveCount / quality(Low/Medium/High). " +
              "collision: enabled / type(Planes/World) / dampen / bounce / lifetimeLoss. " +
              "trails: enabled / ratio / lifetime. " +
              "renderer: renderMode / alignment / sortMode / maskInteraction.",
          },
        },
  },
);
