import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 3 / T20.3.1 — Audio domain tool. Built-in audio module (no extra
// UPM); the `audio` group is hidden until manage_tools activates it. Mutating:
// runs the full gate path; paths_hint is the scene path containing the host.
// Address the host by instance_id > path > name (same model as gameobject_* /
// component_*). Param shape mirrors AnkleBreaker's unity_audio_create_source
// (clipPath / volume / pitch / loop / playOnAwake / spatialBlend) + adds
// spatialize and 3D min/max distance.
const targetSchema = {
  instance_id: {
    type: "integer",
    default: 0,
    description: "Host GameObject instance ID. Highest priority resolver.",
  },
  path: {
    type: "string",
    description: "Host hierarchy path \"Root/Child\".",
  },
  name: {
    type: "string",
    description: "Host GameObject name (first match). Lowest priority resolver.",
  },
  paths_hint: {
    type: "array",
    items: { type: "string" },
    description: "Mutation scope — the scene path that contains the host.",
  },
  gate: {
    enum: ["enforce", "warn", "off"],
    default: "enforce",
  },
};

export const audioSourceAdd: Tool = {
  name: "unity_open_mcp_audio_source_add",
  description:
    "Add an AudioSource component to a GameObject. Optionally assign an AudioClip " +
    "(clip_path, Assets/-rooted), and set volume (0-1, default 1), pitch (default 1), " +
    "loop (default true), play_on_awake (default true), spatial_blend (0=2D, 1=3D, " +
    "default 0), spatialize (default false), and 3D min/max distance. Idempotent — " +
    "re-using an existing AudioSource reports added:false. Mutating: runs the full " +
    "gate path; paths_hint is the scene path that contains the host. Built-in audio " +
    "module (no package dependency); the audio group is hidden until manage_tools " +
    "activates it.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      ...targetSchema,
      clip_path: {
        type: "string",
        description: "Assets/-rooted AudioClip path (e.g. 'Assets/Audio/music.wav').",
      },
      volume: {
        type: "number",
        default: 1,
        description: "Volume 0-1.",
      },
      pitch: {
        type: "number",
        default: 1,
        description: "Pitch multiplier.",
      },
      loop: {
        type: "boolean",
        default: true,
        description: "Loop playback.",
      },
      play_on_awake: {
        type: "boolean",
        default: true,
        description: "Play when the scene starts / component awakes.",
      },
      spatial_blend: {
        type: "number",
        default: 0,
        description: "0=2D, 1=3D.",
      },
      spatialize: {
        type: "boolean",
        default: false,
        description: "Enable spatialization (requires a spatializer plugin for effect).",
      },
      min_distance: {
        type: "number",
        default: 1,
        description: "Min distance for 3D attenuation.",
      },
      max_distance: {
        type: "number",
        default: 500,
        description: "Max distance for 3D attenuation.",
      },
    },
    additionalProperties: false,
  },
};
