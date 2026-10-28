import { makeTool } from "./schema-fragments.js";

// M20 Plan 1 / T20.1.1 — inline-image capture. Same targets as
// unity_senses_screenshot (scene / game / composed / isolated) but returns the PNG as an
// inline base64 image content block instead of writing a temp file. The bridge
// includes an `inlineImage` field (base64 PNG); the MCP server unwraps it into
// an MCP image content block so an agent that doesn't read the filesystem
// still sees the image.
export const captureInline = makeTool(
  "unity_senses_capture_inline",
  "Capture a screenshot and return it as an inline base64 PNG image " +
    "(no temp file written), so an agent that doesn't read the filesystem can " +
    "still view it. Views: 'scene' (the Scene view), 'game' (Camera.main only), " +
    "'composed' (the player's full frame — all cameras + Screen-Space Overlay UI — at " +
    "the game's own resolution unless width/height are passed; another aspect is padded, " +
    "never stretched, and reported as scaled_from + content_rect; overlay 'safe_area' draws " +
    "the safe area), and 'isolated' (a clean 2x2 composite — Front/Right/Back/Top — of a " +
    "single GameObject with layer culling and background choice). Game/composed responses " +
    "name the device or Game view size they were taken at (`capturedAt`). Returns an MCP " +
    "image content block plus metadata. Requires a live Unity Editor connection.",
  {
    properties: {
          view: {
            type: "string",
            enum: ["scene", "game", "composed", "isolated"],
            default: "scene",
            description:
              "Capture target. 'scene' = Scene view camera, 'game' = Camera.main only " +
              "(Overlay canvases NOT rendered), 'composed' = full player frame (all cameras " +
              "by depth + Screen-Space Overlay UI), 'isolated' = clean 2x2 composite of one GameObject.",
          },
          width: {
            type: "integer",
            minimum: 64,
            maximum: 4096,
            description:
              "Capture width in pixels (with height). Omitted: 1280 for scene/game/isolated; 'composed' " +
              "returns the game's native resolution. For 'isolated', per quadrant (full image is 2x). For " +
              "'composed', a width/height of another aspect than the game is fitted and padded (letterbox), " +
              "never stretched — the response then carries scaled_from [w, h] and content_rect [x, y, w, h] " +
              "(top-left origin).",
          },
          height: {
            type: "integer",
            minimum: 64,
            maximum: 4096,
            description:
              "Capture height in pixels (with width). Omitted: 720 for scene/game/isolated; native for " +
              "'composed'. For 'isolated', per quadrant (full image is 2x).",
          },
          object_path: {
            type: "string",
            description:
              "Required for 'isolated' view. Hierarchy path of the target " +
              "GameObject (e.g. \"Player\" or \"Enemies/Goblin\").",
          },
          size: {
            type: "string",
            enum: ["native"],
            description:
              "'native' = the game's own resolution (the Game view size or the simulated device screen), " +
              "no scaling. Implied for 'composed' when width/height are omitted; also valid for 'game' " +
              "(Camera.main rendered at that size). Do not combine with width/height.",
          },
          overlay: {
            type: "string",
            enum: ["safe_area"],
            description:
              "[composed] 'safe_area' tints the unsafe band (outside Screen.safeArea: notch, home indicator, " +
              "cutouts) and outlines the safe rect on the returned image, for review. The response repeats " +
              "safe_area [x, y, w, h] in screen pixels (bottom-left origin).",
          },
          background: {
            type: "string",
            enum: ["transparent", "solid", "skybox"],
            default: "skybox",
            description: "Background for 'isolated' view. Ignored for scene/game.",
          },
        },
  },
);
