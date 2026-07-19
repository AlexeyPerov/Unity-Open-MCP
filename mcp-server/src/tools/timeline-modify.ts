import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 6 / T20.6.2 — Timeline modify. Compile-gated. Reflective field
// patcher for TimelineAsset / TrackAsset / clip PlayableAsset.
export const timelineModify = makeTool(
  "unity_open_mcp_timeline_modify",
  "Set one or more serialized fields on a TimelineAsset, a TrackAsset, or a " +
    "clip's PlayableAsset. Address the timeline by asset_path (preferred) or " +
    "instance_id; pass track_index to target a track, or track_index + " +
    "clip_index to target a clip's asset. Each entry is { field, value, type? } " +
    "where type is 'int' | 'float' | 'bool' | 'string' | 'vector' (default " +
    "inferred). Per-field errors are accumulated, not thrown. Mutating: runs " +
    "the full gate path; paths_hint is the timeline asset path.",
  {
    required: ["fields_json", "paths_hint"],
        properties: {
          asset_path: { type: "string", description: "TimelineAsset path." },
          instance_id: { type: "integer", description: "TimelineAsset instance id." },
          track_index: {
            type: "integer",
            default: -1,
            description: "Track index to target (-1 = timeline root).",
          },
          clip_index: {
            type: "integer",
            default: -1,
            description: "Clip index within the track (-1 = ignore; requires track_index >= 0).",
          },
          fields_json: {
            type: "string",
            description:
              "JSON array of {field, value, type?} entries. type is " +
              "'int'|'float'|'bool'|'string'|'vector' (default inferred from the " +
              "current value).",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the timeline asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
