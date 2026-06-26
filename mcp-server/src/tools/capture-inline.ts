import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 1 / T20.1.1 — inline-image capture. Same targets as
// unity_senses_screenshot (scene / game / isolated) but returns the PNG as an
// inline base64 image content block instead of writing a temp file. The bridge
// includes an `inlineImage` field (base64 PNG); the MCP server unwraps it into
// an MCP image content block so an agent that doesn't read the filesystem
// still sees the image.
export const captureInline: Tool = {
  name: "unity_senses_capture_inline",
  description:
    "Capture a screenshot and return it as an inline base64 PNG image " +
    "(no temp file written), so an agent that doesn't read the filesystem can " +
    "still view it. Supports three views: 'scene' (the Scene view), 'game' " +
    "(the main camera / Game view), and 'isolated' (a clean 2x2 composite — " +
    "Front/Right/Back/Top — of a single GameObject with layer culling and " +
    "background choice). Returns an MCP image content block plus metadata. " +
    "Requires a live Unity Editor connection.",
  inputSchema: {
    type: "object",
    properties: {
      view: {
        type: "string",
        enum: ["scene", "game", "isolated"],
        default: "scene",
        description:
          "Capture target. 'scene' = Scene view camera, 'game' = main game " +
          "camera, 'isolated' = clean 2x2 composite of one GameObject.",
      },
      width: {
        type: "integer",
        default: 1280,
        minimum: 64,
        maximum: 4096,
        description:
          "Capture width in pixels. For 'isolated', this is per-quadrant " +
          "(full image is 2x).",
      },
      height: {
        type: "integer",
        default: 720,
        minimum: 64,
        maximum: 4096,
        description:
          "Capture height in pixels. For 'isolated', this is per-quadrant " +
          "(full image is 2x).",
      },
      object_path: {
        type: "string",
        description:
          "Required for 'isolated' view. Hierarchy path of the target " +
          "GameObject (e.g. \"Player\" or \"Enemies/Goblin\").",
      },
      background: {
        type: "string",
        enum: ["transparent", "solid", "skybox"],
        default: "skybox",
        description: "Background for 'isolated' view. Ignored for scene/game.",
      },
    },
    additionalProperties: false,
  },
};
