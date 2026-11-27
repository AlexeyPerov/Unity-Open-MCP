import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 2 / T20.2.2 — Lighting domain tool. Reflection probe bake is the
// long mutation with a settle window — routed through the gate
// (EditorSettle lifecycle, Gate = Enforce) so agents wait for the bake to
// complete before the next mutation. Built-in lighting module.
export const reflectionProbeBake = makeTool(
  "unity_open_mcp_reflection_probe_bake",
  "Bake a ReflectionProbe. bake_mode: 'realtime' (a quick bake of this probe " +
    "alone, no GI, written next to the scene like the probe Inspector's Bake " +
    "button; in an unsaved scene, ReflectionProbe.RenderProbe into the probe's " +
    "runtime texture), 'baked' (queue a full lightmap bake incl. probes via " +
    "Lightmapping.BakeAsync), or 'custom' (Lightmapping.BakeReflectionProbe " +
    "writes the cubemap to target_path, an Assets/-rooted .exr (HDR) or .png (LDR) " +
    "file imported as a Cubemap; created or overwritten). The bake " +
    "can take seconds; EditorSettle waits for completion + asset refresh before " +
    "returning. Mutating: runs the full gate path; paths_hint includes the probe " +
    "scene path and (for custom mode) the output cubemap asset path.",
  {
    required: ["paths_hint"],
        properties: {
          instance_id: { type: ["string", "integer"], default: 0, description: "Host GameObject instance ID." },
          path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
          name: { type: "string", description: "Host GameObject name (first match)." },
          bake_mode: {
            type: "string",
            enum: ["realtime", "baked", "custom"],
            default: "realtime",
            description:
              "'realtime' — quick bake of this probe alone (RenderProbe in an unsaved " +
              "scene). 'baked' — Lightmapping.BakeAsync (full lightmap bake incl. " +
              "probes). 'custom' — Lightmapping.BakeReflectionProbe into target_path.",
          },
          target_path: {
            type: "string",
            description:
              "Assets/-rooted .exr (HDR) or .png (LDR) path (required for " +
              "bake_mode='custom'); the baked cubemap is written there and imported " +
              "as a Cubemap.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the probe's scene path and (for custom mode) the " + "output cubemap asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
