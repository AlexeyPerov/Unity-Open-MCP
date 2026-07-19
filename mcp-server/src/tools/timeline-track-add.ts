import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 6 / T20.6.2 — Timeline track_add. Compile-gated. Mutating.
export const timelineTrackAdd = makeTool(
  "unity_open_mcp_timeline_track_add",
  "Add a track of the requested type to a TimelineAsset. track_type is one " +
    "of: Animation, Activation, Audio, Signal, Control, Group, Playable. " +
    "Address the timeline by asset_path (preferred) or instance_id. " +
    "parent_track_index optionally nests the new track under an existing Group " +
    "track. Returns the new track's index. Mutating: runs the full gate path; " +
    "paths_hint is the timeline asset path.",
  {
    required: ["track_type", "paths_hint"],
        properties: {
          asset_path: { type: "string", description: "TimelineAsset path ('Assets/.../*.playable')." },
          instance_id: { type: "integer", description: "TimelineAsset instance id (fallback when asset_path omitted)." },
          track_type: {
            type: "string",
            enum: ["Animation", "Activation", "Audio", "Signal", "Control", "Group", "Playable"],
            description: "Track type to add.",
          },
          track_name: { type: "string", description: "Optional display name for the new track." },
          parent_track_index: {
            type: "integer",
            default: -1,
            description: "Optional index of a Group track to nest under (-1 = root).",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the timeline asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
