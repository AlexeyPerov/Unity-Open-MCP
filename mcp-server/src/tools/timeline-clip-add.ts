import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 6 / T20.6.2 — Timeline clip_add. Compile-gated. Mutating.
export const timelineClipAdd = makeTool(
  "unity_open_mcp_timeline_clip_add",
  "Add a clip to a Timeline track. Address the timeline by asset_path " +
    "(preferred) or instance_id, the track by track_index or track_name (name " +
    "first match). start_time and duration are optional (seconds). On a typed " +
    "track (Animation / Activation / Audio) the clip kind follows the track; " +
    "on a Playable track, set clip_type to 'animation' / 'audio' / " +
    "'activation' / 'default'. Returns the new clip's index + start. Mutating: " +
    "runs the full gate path; paths_hint is the timeline asset path.",
  {
    required: ["paths_hint"],
        properties: {
          asset_path: { type: "string", description: "TimelineAsset path." },
          instance_id: { type: "integer", description: "TimelineAsset instance id." },
          track_index: { type: "integer", default: -1, description: "Track index (-1 to use track_name)." },
          track_name: { type: "string", description: "Track display name (first match wins)." },
          clip_type: {
            type: "string",
            enum: ["animation", "audio", "activation", "default"],
            description: "Clip kind on a generic Playable track. Ignored on typed tracks.",
          },
          clip_name: { type: "string", description: "Optional display name for the new clip." },
          start_time: {
            type: "number",
            description: "Optional clip start in seconds (< 0 to leave default).",
          },
          duration: {
            type: "number",
            description: "Optional clip duration in seconds (<= 0 to leave default).",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the timeline asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
