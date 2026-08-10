import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const screenshot = makeTool(
  "unity_senses_screenshot",
  "Capture a screenshot from the Unity Editor. Supports four views: " +
    "'scene' (the Scene view), 'game' (renders Camera.main only — NOT the full " +
    "Game view; Screen-Space Overlay UI and multi-camera compositions are NOT " +
    "included), 'composed' (the player's full composite frame — every camera by " +
    "depth plus Screen-Space Overlay canvases; use this to verify UI edits), and " +
    "'isolated' (a clean 2x2 composite — Front/Right/Back/Top — of a single " +
    "GameObject with layer culling and background choice). " +
    "Returns the saved PNG file path. Requires a live Unity Editor connection.",
  {
    properties: {
          view: {
            type: "string",
            enum: ["scene", "game", "composed", "isolated"],
            default: "scene",
            description:
              "Capture target. 'scene' = Scene view camera, 'game' = Camera.main only " +
              "(Overlay canvases and other cameras are NOT rendered — prefer 'composed' " +
              "to verify UI), 'composed' = the player's full frame (all cameras by depth + " +
              "Screen-Space Overlay UI; in play mode uses ScreenCapture, in edit mode " +
              "composites enabled cameras by depth), 'isolated' = clean 2x2 composite of " +
              "one GameObject.",
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
            description: "Background for 'isolated' view. Ignored for scene/game/composed.",
          },
        },
  },
);
