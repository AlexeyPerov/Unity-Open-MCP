import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 6 / T20.6.2 — Timeline director_bind. Compile-gated. Mutates a
// scene PlayableDirector (component add + asset assignment).
export const timelineDirectorBind: Tool = {
  name: "unity_open_mcp_timeline_director_bind",
  description:
    "Bind a TimelineAsset to a scene PlayableDirector. Adds the PlayableDirector " +
    "component when missing, then assigns the asset. Address the host GameObject " +
    "by instance_id > path > name and the TimelineAsset by asset_path " +
    "(preferred) or instance_id. Mutating: runs the full gate path; paths_hint " +
    "is the host scene path + the asset path. Requires com.unity.timeline.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: { type: "integer", description: "PlayableDirector host GameObject instance id." },
      path: { type: "string", description: "Host hierarchy path." },
      name: { type: "string", description: "Host name (last-resort resolver)." },
      asset_path: { type: "string", description: "TimelineAsset path ('Assets/.../*.playable')." },
      asset_instance_id: { type: "integer", description: "TimelineAsset instance id (fallback when asset_path omitted)." },
      autoplay: {
        type: "boolean",
        default: false,
        description: "When true, set playOnAwake = true on the director.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — host scene path + the asset path.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
