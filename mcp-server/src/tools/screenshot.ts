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
    "'composed' without width/height returns the frame at the game's own resolution (the Game view " +
    "size or the Device Simulator screen — set it first with unity_open_mcp_game_view); in play mode it " +
    "is the frame the game rendered, without Editor chrome or the Simulator's device bezel. A " +
    "width/height of another aspect is padded (letterbox), never stretched, and reported as " +
    "scaled_from + content_rect. Game/composed responses name what they were taken at in `capturedAt` " +
    "(mode, device or size entry, orientation, screen). Returns the saved PNG file path. Requires a " +
    "live Unity Editor connection.",
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
              "Screen-Space Overlay UI; in play mode the play-mode view's rendered frame, in edit " +
              "mode enabled cameras composited by depth — Overlay canvases do not render in edit " +
              "mode), 'isolated' = clean 2x2 composite of one GameObject.",
          },
          width: {
            type: "integer",
            minimum: 64,
            maximum: 4096,
            description:
              "Capture width in pixels (with height for 'composed'). Omitted: 1280 for scene/game/isolated; 'composed' " +
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
              "Capture height in pixels (with width for 'composed'). Omitted: 720 for scene/game/isolated; native for " +
              "'composed'. For 'isolated', per quadrant (full image is 2x).",
          },
          object_path: {
            type: "string",
            description:
              "Required for 'isolated' view. Hierarchy path of the target GameObject " +
              "(e.g. \"Player\" or \"Enemies/Goblin\").",
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
            description: "Background for 'isolated' view. Ignored for scene/game/composed.",
          },
        },
  },
);
