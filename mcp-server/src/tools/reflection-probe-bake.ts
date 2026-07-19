import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 2 / T20.2.2 — Lighting domain tool. Reflection probe bake is the
// long mutation with a settle window — routed through the gate
// (EditorSettle lifecycle, Gate = Enforce) so agents wait for the bake to
// complete before the next mutation. Built-in lighting module.
export const reflectionProbeBake = makeTool(
  "unity_open_mcp_reflection_probe_bake",
  "Bake a ReflectionProbe. bake_mode: 'realtime' (bake into the probe's " +
    "runtime texture via ReflectionProbe.Bake), 'baked' (queue a full lightmap " +
    "bake incl. probes via Lightmapping.BakeAsync), or 'custom' (write a baked " +
    "snapshot into a named cubemap asset path via " +
    "Lightmapping.BakeReflectionProbeSnapshot — the asset is created if absent). " +
    "For 'custom', pass target_path (an Assets/-rooted .cubemap path). The bake " +
    "can take seconds; EditorSettle waits for completion + asset refresh before " +
    "returning. Mutating: runs the full gate path; paths_hint includes the probe " +
    "scene path and (for custom mode) the output cubemap asset path.",
  {
    required: ["paths_hint"],
        properties: {
          instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
          path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
          name: { type: "string", description: "Host GameObject name (first match)." },
          bake_mode: {
            type: "string",
            enum: ["realtime", "baked", "custom"],
            default: "realtime",
            description:
              "'realtime' — ReflectionProbe.Bake. 'baked' — Lightmapping.BakeAsync " +
              "(full lightmap bake incl. probes). 'custom' — " +
              "Lightmapping.BakeReflectionProbeSnapshot into target_path.",
          },
          target_path: {
            type: "string",
            description:
              "Assets/-rooted .cubemap path (required for bake_mode='custom'). The " +
              "cubemap asset is created if absent.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the probe's scene path and (for custom mode) the " + "output cubemap asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
