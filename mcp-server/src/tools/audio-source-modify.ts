import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 3 / T20.3.1 — Audio domain tool. Built-in audio module. Mutating:
// runs the full gate path; paths_hint covers the host scene path (and the
// mixer asset path when mixer_group_path is set).
export const audioSourceModify: Tool = {
  name: "unity_open_mcp_audio_source_modify",
  description:
    "Set typed AudioSource fields: clip_path (Assets/-rooted AudioClip), volume (0-1), " +
    "pitch, loop, play_on_awake, spatial_blend (0=2D, 1=3D), spatialize, min_distance, " +
    "max_distance, doppler_level, spread, and mixer_group_path (Assets/-rooted .mix — " +
    "assigns the first AudioMixerGroup exposed by the mixer; pass clear_mixer_group: " +
    "true to clear). Each field is optional — omit to leave unchanged. Mutating: runs " +
    "the full gate path; paths_hint is the host scene path (and the mixer asset path " +
    "when mixer_group_path is set).",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
      path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
      name: { type: "string", description: "Host GameObject name (first match)." },
      clip_path: { type: "string", description: "Assets/-rooted AudioClip path." },
      volume: { type: "number", description: "Volume 0-1." },
      pitch: { type: "number", description: "Pitch multiplier." },
      loop: { type: "boolean", description: "Loop playback." },
      play_on_awake: { type: "boolean", description: "Play on awake." },
      spatial_blend: { type: "number", description: "0=2D, 1=3D." },
      spatialize: { type: "boolean", description: "Enable spatialization." },
      min_distance: { type: "number", description: "3D min distance." },
      max_distance: { type: "number", description: "3D max distance." },
      doppler_level: { type: "number", description: "Doppler factor (0-5)." },
      spread: { type: "number", description: "Spread angle 0-360." },
      mixer_group_path: {
        type: "string",
        description:
          "Assets/-rooted .mix — assigns the first AudioMixerGroup exposed by the " +
          "mixer to AudioSource.outputAudioMixerGroup.",
      },
      clear_mixer_group: {
        type: "boolean",
        default: false,
        description: "Clear outputAudioMixerGroup (wins over mixer_group_path when both set).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the host scene path (and the mixer asset path when " +
          "mixer_group_path is set). No whole-project fallback.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
