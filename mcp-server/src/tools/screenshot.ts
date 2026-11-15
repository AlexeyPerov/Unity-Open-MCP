import { makeTool } from "./schema-fragments.js";
import { IMAGE_REGION_PROP, IMAGE_SCALE_PROP, IMAGE_MAX_EDGE_PROP, IMAGE_SAVE_TO_PROP } from "./image-post-process.js";
import { GAME_VIEW_ORIENTATIONS } from "./game-view.js";

export const screenshot = makeTool(
  "unity_senses_screenshot",
  "Capture a screenshot from the Unity Editor. Supports five views: " +
    "'scene' (the Scene view), 'game' (renders Camera.main only — NOT the full " +
    "Game view; Screen-Space Overlay UI and multi-camera compositions are NOT " +
    "included), 'composed' (the player's full composite frame — every camera by " +
    "depth plus Screen-Space Overlay canvases; use this to verify UI edits), " +
    "'isolated' (a clean 2x2 composite — Front/Right/Back/Top — of a single " +
    "GameObject with layer culling and background choice), and 'prefab' (a UI prefab — " +
    "asset_path — or a canvas / panel of a loaded scene — game_object_path — rendered in edit mode " +
    "in a preview scene through a temporary Screen Space - Camera canvas, at width/height, a Device " +
    "Simulator device or a Game view size label; a panel without a Canvas gets one whose CanvasScaler " +
    "matches the project; no project scene is loaded, dirtied or changed — use it for UI layout checks " +
    "instead of a scratch scene). " +
    "'composed' without width/height returns the frame at the game's own resolution (the Game view " +
    "size or the Device Simulator screen — set it first with unity_open_mcp_game_view); in play mode it " +
    "is the frame the game rendered, without Editor chrome or the Simulator's device bezel; in edit mode " +
    "Overlay canvases do not render (use 'prefab'). A " +
    "width/height of another aspect is padded (letterbox), never stretched, and reported as " +
    "scaled_from + content_rect. Game/composed/prefab responses name what they were taken at in `capturedAt` " +
    "(mode, device or size entry, orientation, screen); prefab responses add `prefab` (canvas own/wrapped, " +
    "scale_factor, the CanvasScaler settings used and where they came from). Post-processing: region crops, " +
    "scale / max_edge shrink, save_to copies the final PNG (e.g. into a task folder) — no shell image tools needed; " +
    "`postProcess` reports source_size, region and size. Returns the saved PNG file path. Requires a " +
    "live Unity Editor connection.",
  {
    properties: {
          view: {
            type: "string",
            enum: ["scene", "game", "composed", "isolated", "prefab"],
            default: "scene",
            description:
              "Capture target. 'scene' = Scene view camera, 'game' = Camera.main only " +
              "(Overlay canvases and other cameras are NOT rendered — prefer 'composed' " +
              "to verify UI), 'composed' = the player's full frame (all cameras by depth + " +
              "Screen-Space Overlay UI; in play mode the play-mode view's rendered frame, in edit " +
              "mode enabled cameras composited by depth — Overlay canvases do not render in edit " +
              "mode), 'isolated' = clean 2x2 composite of one GameObject, 'prefab' = a UI prefab " +
              "(asset_path) or scene canvas (game_object_path) rendered in a preview scene, edit mode only.",
          },
          width: {
            type: "integer",
            minimum: 64,
            maximum: 4096,
            description:
              "Capture width in pixels (with height for 'composed' and 'prefab'). Omitted: 1280 for scene/game/isolated; " +
              "'composed' and 'prefab' use the game's native resolution. For 'isolated', per quadrant (full image is 2x). For " +
              "'composed', a width/height of another aspect than the game is fitted and padded (letterbox), " +
              "never stretched — the response then carries scaled_from [w, h] and content_rect [x, y, w, h] " +
              "(top-left origin). For 'prefab' it is the screen the UI is laid out for (e.g. 1242 x 2688).",
          },
          height: {
            type: "integer",
            minimum: 64,
            maximum: 4096,
            description:
              "Capture height in pixels (with width for 'composed' and 'prefab'). Omitted: 720 for scene/game/isolated; " +
              "native for 'composed' and 'prefab'. For 'isolated', per quadrant (full image is 2x).",
          },
          object_path: {
            type: "string",
            description:
              "Required for 'isolated' view. Hierarchy path of the target GameObject " +
              "(e.g. \"Player\" or \"Enemies/Goblin\").",
          },
          asset_path: {
            type: "string",
            minLength: 1,
            description:
              "[prefab] The UI prefab to render, e.g. \"Assets/UI/Shop.prefab\" (as saved on disk). A root Canvas is " +
              "rendered with its own CanvasScaler, switched to Screen Space - Camera for the render; a panel without " +
              "a Canvas (RectTransform root) is wrapped in a temporary canvas. Missing → asset_not_found; not a " +
              "prefab → not_a_prefab; no Canvas / RectTransform (or World Space canvases only) → not_a_ui_prefab.",
          },
          game_object_path: {
            type: "string",
            minLength: 1,
            description:
              "[prefab] Instead of asset_path: a Canvas or a panel under one in a loaded scene or the open prefab " +
              "stage (the shared GameObject resolver), cloned into the preview scene and rendered as above — a " +
              "panel keeps the scale of the canvas it sits under. The scene is only read.",
          },
          device: {
            type: "string",
            minLength: 1,
            description:
              "[prefab] Render at a Device Simulator device's screen (id from unity_open_mcp_game_view " +
              "list_devices — exact, or a unique substring) instead of width/height; overlay 'safe_area' then " +
              "draws that device's safe area. Unknown → device_not_found; several → ambiguous_device.",
          },
          orientation: {
            type: "string",
            enum: [...GAME_VIEW_ORIENTATIONS],
            description: "[prefab, with device] Device orientation. Default portrait.",
          },
          size_label: {
            type: "string",
            minLength: 1,
            description:
              "[prefab] Render at a fixed-resolution Game view size entry by label (e.g. 'Open MCP 1080x1920') " +
              "instead of width/height. Unknown → size_not_found with the labels.",
          },
          canvas_scaler: {
            type: "object",
            additionalProperties: false,
            description:
              "[prefab] CanvasScaler values for the render, applied over the base (scaler_from, else the rendered " +
              "canvas's own CanvasScaler, else for a panel the canvas above it in its scene or the first screen " +
              "canvas with a CanvasScaler in the loaded scenes — active scene first — else Unity's defaults). The " +
              "response's prefab.scaler says which base was used and lists these overrides.",
            properties: {
              ui_scale_mode: { type: "string", enum: ["constant_pixel_size", "scale_with_screen_size", "constant_physical_size"] },
              scale_factor: { type: "number", exclusiveMinimum: 0 },
              reference_resolution: {
                type: "array",
                minItems: 2,
                maxItems: 2,
                items: { type: "number", exclusiveMinimum: 0 },
                description: "[width, height] in pixels.",
              },
              screen_match_mode: { type: "string", enum: ["match_width_or_height", "expand", "shrink"] },
              match_width_or_height: { type: "number", minimum: 0, maximum: 1 },
              reference_pixels_per_unit: { type: "number", exclusiveMinimum: 0 },
              physical_unit: { type: "string", enum: ["centimeters", "millimeters", "inches", "points", "picas"] },
              fallback_screen_dpi: { type: "number", exclusiveMinimum: 0 },
              default_sprite_dpi: { type: "number", exclusiveMinimum: 0 },
            },
          },
          scaler_from: {
            type: "string",
            minLength: 1,
            description:
              "[prefab] Copy the CanvasScaler from this canvas: a prefab asset path (its root canvas, else its first " +
              "canvas with a CanvasScaler) or a GameObject path in a loaded scene / the open prefab stage (its root " +
              "canvas). Not found or no CanvasScaler → scaler_not_found.",
          },
          size: {
            type: "string",
            enum: ["native"],
            description:
              "'native' = the game's own resolution (the Game view size or the simulated device screen), " +
              "no scaling. Implied for 'composed' and 'prefab' when no size is given; also valid for 'game' " +
              "(Camera.main rendered at that size). Do not combine with width/height.",
          },
          overlay: {
            type: "string",
            enum: ["safe_area"],
            description:
              "[composed, prefab] 'safe_area' tints the unsafe band (outside the safe area: notch, home indicator, " +
              "cutouts) and outlines the safe rect on the returned image, for review. The safe area is Screen.safeArea " +
              "for composed, the named device's for prefab (the Simulator's when no size is given; the whole screen " +
              "otherwise). The response repeats safe_area [x, y, w, h] in screen pixels (bottom-left origin).",
          },
          background: {
            type: "string",
            enum: ["transparent", "solid", "skybox"],
            default: "skybox",
            description:
              "Background for 'isolated' view; for 'prefab', 'transparent' keeps alpha 0 behind the UI and any other " +
              "value is solid grey. Ignored for scene/game/composed.",
          },
          region: { ...IMAGE_REGION_PROP },
          scale: { ...IMAGE_SCALE_PROP },
          max_edge: { ...IMAGE_MAX_EDGE_PROP },
          save_to: { ...IMAGE_SAVE_TO_PROP },
        },
  },
);
