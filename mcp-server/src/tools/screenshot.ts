import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const screenshot: Tool = {
  name: "unity_agent_screenshot",
  description:
    "Capture a screenshot from the Unity Editor. Supports three views: " +
    "'scene' (the Scene view), 'game' (the main camera / Game view), and " +
    "'isolated' (a clean 2x2 composite — Front/Right/Back/Top — of a single " +
    "GameObject with layer culling and background choice). " +
    "Returns the saved PNG file path. Requires a live Unity Editor connection.",
  inputSchema: {
    type: "object",
    properties: {
      view: {
        type: "string",
        enum: ["scene", "game", "isolated"],
        default: "scene",
        description:
          "Capture target. 'scene' = Scene view camera, 'game' = main game camera, " +
          "'isolated' = clean 2x2 composite of one GameObject.",
      },
      width: {
        type: "integer",
        default: 1280,
        minimum: 64,
        maximum: 4096,
        description:
          "Capture width in pixels. For 'isolated', this is per-quadrant (full image is 2x).",
      },
      height: {
        type: "integer",
        default: 720,
        minimum: 64,
        maximum: 4096,
        description:
          "Capture height in pixels. For 'isolated', this is per-quadrant (full image is 2x).",
      },
      object_path: {
        type: "string",
        description:
          "Required for 'isolated' view. Hierarchy path of the target GameObject " +
          "(e.g. \"Player\" or \"Enemies/Goblin\").",
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
