import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 3 / T20.3.1 — Audio domain tool. Sets an exposed float on an
// AudioMixer asset. Built-in audio module. Mutating: runs the full gate path;
// paths_hint is the mixer asset path (asset mutation, not scene).
export const audioMixerSetParameter = makeTool(
  "unity_open_mcp_audio_mixer_set_parameter",
  "Set a float value on an AudioMixer asset's exposed parameter. mixer_path is an " +
    "Assets/-rooted .mix asset path; parameter_name is the exposed parameter name; " +
    "value is the raw float (dB for volume params). normalize (default false) maps a " +
    "0-1 input onto the -80..0 dB range so a friendly volume slider can be passed " +
    "directly. The mixer asset is marked dirty — call assets_refresh / scene_save to " +
    "commit. Mutating: runs the full gate path; paths_hint is the mixer asset path.",
  {
    required: ["mixer_path", "parameter_name", "paths_hint"],
        properties: {
          mixer_path: {
            type: "string",
            description: "Assets/-rooted .mix asset path.",
          },
          parameter_name: {
            type: "string",
            description: "Exposed parameter name (expose it in the Audio Mixer window first).",
          },
          value: {
            type: "number",
            default: 0,
            description: "Raw float value (dB for volume params), or a 0-1 slider when normalize is true.",
          },
          normalize: {
            type: "boolean",
            default: false,
            description: "Map value (0-1) onto the -80..0 dB range.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the mixer asset path. No whole-project fallback." },
          gate: { ...GATE_PROP },
        },
  },
);
