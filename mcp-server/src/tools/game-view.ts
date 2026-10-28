// Playtest — Game view resolution and Device Simulator.
//
// get / set_resolution / set_device / list_devices over the Editor's play-mode
// view. set_resolution selects a fixed `w×h` Game view size (creating or
// reusing the custom entry `Open MCP <w>x<h>`, never deleting it) or an
// existing entry by label; set_device is the only place that opens the Device
// Simulator. In play mode the bridge starts a short settle watch on the shared
// play-mode scheduler (the game's Screen size inside the player loop + the
// view's render target) and this server polls it, so a set_* call returns once
// the game reports the new size or answers `not_settled`. Gate-free Editor UI
// change; group `playtest`.
import { makeTool } from "./schema-fragments.js";

export const GAME_VIEW_ACTIONS = ["get", "set_resolution", "set_device", "list_devices"] as const;
export const GAME_VIEW_ORIENTATIONS = ["portrait", "portrait_upside_down", "landscape_left", "landscape_right"] as const;
export const GAME_VIEW_DEFAULT_SETTLE_MS = 5_000;
export const GAME_VIEW_MAX_SETTLE_MS = 20_000;

export const gameView = makeTool(
  "unity_open_mcp_game_view",
  "Read or set what the game renders at: the Game view resolution or a Device Simulator device. " +
    "ACTIONS: get = mode (game | simulator | none), the selected size entry or device, window size in " +
    "logical points and pixels (HiDPI), scale, focused, visible (false = the view is behind another " +
    "tab, which does not render; in play mode a warning says render_texture and screen are then " +
    "stale), render_texture, and `screen` = what the game " +
    "sees (width, height, aspect, safe_area [x, y, w, h] bottom-left origin, insets {top, bottom, left, " +
    "right} — the notch is insets.top in portrait — cutouts, dpi, orientation); set_resolution = fixed " +
    "width × height in pixels via a custom Game view size entry named 'Open MCP <w>x<h>' (created once, " +
    "reused by name, never deleted — the only persistent effect, stored in the Editor's Game view size " +
    "list), or an existing entry by size_label (e.g. 'Free Aspect' to restore what get reported); " +
    "switches the Simulator back to the Game view when needed; set_device = the ONLY action that opens " +
    "the Device Simulator: swaps the Game view tab for the Simulator (or opens one), selects `device` " +
    "(an id from list_devices) and rotates it to `orientation` (default portrait), then reports the " +
    "resulting safe area; orientation_applied is false when the project's orientation settings keep the " +
    "game in another orientation; both set actions bring the Game view / Simulator tab to the front " +
    "inside Unity (a hidden tab does not render and the game falls back to 640x480; the Unity application " +
    "itself is not raised); list_devices = the Simulator's device catalog (id, screen, dpi, " +
    "portrait safe area, insets, notch, orientations), paged. SETTLE: in play mode set_* returns once " +
    "Screen.width/height inside the game and the view's render target report the new size for two " +
    "frames, else error not_settled (paused Editor, hidden Game view tab) with what was observed. " +
    "Nothing is restored implicitly: call get first and set back explicitly. Then capture with " +
    "unity_senses_screenshot / capture_inline view 'composed' (native size by default; overlay " +
    "'safe_area' draws the safe area). Editor UI only, gate-free. Unity 6; other Editors answer " +
    "unsupported_editor_version.",
  {
    required: ["action"],
    properties: {
      action: {
        type: "string",
        enum: [...GAME_VIEW_ACTIONS],
        description:
          "get: read the current view, size/device and screen. set_resolution: width + height (or " +
          "size_label). set_device: device (+ orientation). list_devices: the Simulator catalog (query, " +
          "page_size, cursor).",
      },
      width: {
        type: "integer",
        minimum: 16,
        maximum: 8192,
        description: "[set_resolution] Game render width in pixels (with height).",
      },
      height: {
        type: "integer",
        minimum: 16,
        maximum: 8192,
        description: "[set_resolution] Game render height in pixels (with width).",
      },
      size_label: {
        type: "string",
        minLength: 1,
        description:
          "[set_resolution] Select an existing Game view size entry by its label instead of width/height " +
          "(e.g. 'Free Aspect', '16:9 Aspect', or get's size.label to restore). Unknown → size_not_found " +
          "with the available labels.",
      },
      device: {
        type: "string",
        minLength: 1,
        description:
          "[set_device] Device id from list_devices (exact, case-insensitive), or a substring that matches " +
          "exactly one device. Unknown → device_not_found; several → ambiguous_device with candidates.",
      },
      orientation: {
        type: "string",
        enum: [...GAME_VIEW_ORIENTATIONS],
        description: "[set_device] Device orientation (the Simulator's rotate buttons). Default portrait.",
      },
      query: {
        type: "string",
        description: "[list_devices] Keep devices whose id contains this (case-insensitive).",
      },
      page_size: {
        type: "integer",
        default: 25,
        minimum: 1,
        maximum: 100,
        description: "[list_devices] Devices per page. Default 25, cap 100. Follow pagination.next_cursor.",
      },
      cursor: {
        type: "string",
        description: "[list_devices] Opaque cursor from a previous response's pagination.next_cursor.",
      },
      settle_timeout_ms: {
        type: "integer",
        minimum: 100,
        maximum: GAME_VIEW_MAX_SETTLE_MS,
        description:
          `[set_resolution / set_device, play mode] How long to wait for the game to report the new size. Default ${GAME_VIEW_DEFAULT_SETTLE_MS}.`,
      },
    },
  },
);
