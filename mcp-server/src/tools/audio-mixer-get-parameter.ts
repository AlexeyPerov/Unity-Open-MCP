import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 3 / T20.3.1 — Audio domain tool. Reads an exposed float on an
// AudioMixer asset. Built-in audio module. Read-only, gate-free.
export const audioMixerGetParameter: Tool = {
  name: "unity_open_mcp_audio_mixer_get_parameter",
  description:
    "Read a float value on an AudioMixer asset's exposed parameter. mixer_path is an " +
    "Assets/-rooted .mix asset path; parameter_name is the exposed parameter name. " +
    "Returns the current value, or a `parameter_not_exposed` error when the name is " +
    "not exposed. Read-only, gate-free.",
  inputSchema: {
    type: "object",
    required: ["mixer_path", "parameter_name"],
    properties: {
      mixer_path: {
        type: "string",
        description: "Assets/-rooted .mix asset path.",
      },
      parameter_name: {
        type: "string",
        description: "Exposed parameter name.",
      },
    },
    additionalProperties: false,
  },
};
