// Playtest — UI screen snapshot.
//
// A read-only, gate-free listing of the uGUI on screen shaped like an
// accessibility tree: role, text (UnityEngine.UI.Text, plus TextMeshPro when
// it is installed), screen rect, visibility (on_screen | clipped | offscreen |
// transparent | zero_size), interactable, occluded_by, sprite, depth — and a
// diagnostics block (text overflow and zero-size controls always; a
// placeholder pattern and a minimum touch size only when passed). The bridge
// shares the node model with `target_text` label targeting on inputsim_pointer
// / inputsim_touch / inputsim_gesture, so the label the snapshot shows is the
// label a tap resolves. Bridge-side paging (page_size + cursor), like
// inputsim_probe. Compiles with com.unity.ugui (the uGUI half of input
// simulation); group `playtest`.
import { makeTool } from "./schema-fragments.js";

export const UI_SNAPSHOT_ROLES = ["button", "toggle", "slider", "input", "scroll", "text", "image", "container"] as const;

export const uiSnapshot = makeTool(
  "unity_open_mcp_ui_snapshot",
  "Read what the uGUI screen shows, in edit or play mode: a flat accessibility tree of nodes " +
    "{ id, path, role (button | toggle | slider | input | scroll | text | image | container), text, " +
    "rect [x, y, w, h] in Screen pixels (bottom-left origin, the space of inputsim screen_x/screen_y), " +
    "visible, visibility (on_screen | clipped | offscreen | transparent | zero_size; reason: no_camera for " +
    "a World Space canvas nothing can project), interactable, " +
    "occluded_by, sprite, depth } plus `screen` { w, h, safe_area, dpi } and `textSources` " +
    "(ugui, plus tmp when TextMeshPro is installed). A control's text is its label (descendant texts); " +
    "visibility follows the CanvasGroup alpha chain, RectMask2D / Mask clipping and screen bounds; " +
    "occluded_by is the front-most raycast hit covering a raycast-target node (play mode only). " +
    "PROFILES: compact (default) = visible nodes that have text or are controls, labels folded into " +
    "their control, null keys omitted; balanced = every visible node (images, containers) + sprite/depth; " +
    "full = every active node in any visibility state with every key. Bound it with page_size + cursor " +
    "(pagination.next_cursor). DIAGNOSTICS (first page): overflow (text larger than its rect) and " +
    "zero_size_interactables always; placeholder (texts matching placeholder_pattern) and " +
    "small_touch_targets (controls under min_touch_size px, with Screen.dpi) only when passed. " +
    "Then tap by label: inputsim_pointer / inputsim_touch target_text, or inputsim_gesture keyframe " +
    "target_text. uGUI only (no UI Toolkit). Read-only, gate-free. Requires com.unity.ugui.",
  {
    properties: {
      profile: {
        type: "string",
        enum: ["compact", "balanced", "full"],
        default: "compact",
        description:
          "compact (default) = visible text/control nodes, labels folded into their control; " +
          "balanced = every visible node + sprite/depth; full = every active node in any visibility " +
          "state (hidden / clipped / offscreen / zero-size included) with every key.",
      },
      page_size: {
        type: "integer",
        default: 50,
        minimum: 1,
        maximum: 200,
        description: "Max nodes per page. Default 50, cap 200. Follow pagination.next_cursor for the rest.",
      },
      cursor: {
        type: "string",
        description: "Opaque cursor from a previous response's pagination.next_cursor. Omit on the first call.",
      },
      text_contains: {
        type: "string",
        description:
          "Keep nodes whose text contains this (case-insensitive, after trimming, whitespace collapse " +
          "and rich-text tag removal).",
      },
      role: {
        type: "string",
        description: `Keep only these roles (comma-separated): ${UI_SNAPSHOT_ROLES.join(", ")}.`,
      },
      under: {
        type: "string",
        description:
          "Scope to the subtree of a hierarchy path ('Canvas/Shop') or a trailing path suffix ('Shop'). " +
          "Diagnostics use the same scope. Unknown → under_not_found.",
      },
      visible_only: {
        type: "boolean",
        description:
          "Drop nodes that are not on screen. Defaults to true for compact/balanced and false for full; " +
          "set false on compact to see hidden text and controls too.",
      },
      placeholder_pattern: {
        type: "string",
        description:
          "Regex (.NET, case-sensitive) flagging texts that look like unresolved localisation keys, e.g. " +
          "'^[*#{].*[*#}]$'. Only checked when passed; there is no built-in pattern. Over 100 ms on a " +
          "text or 2 s for the snapshot → regex_timeout.",
      },
      min_touch_size: {
        type: "number",
        exclusiveMinimum: 0,
        description:
          "Flag visible controls whose rect is narrower or shorter than this many screen pixels " +
          "(diagnostics.small_touch_targets, which also reports Screen.dpi so you can convert from dp: " +
          "px = dp × dpi / 160). Only checked when passed.",
      },
    },
  },
);
