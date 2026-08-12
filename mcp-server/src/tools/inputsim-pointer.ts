// Input simulation — uGUI pointer dispatch (EventSystem + ExecuteEvents).
//
// Compile-gated on com.unity.ugui in the bridge (UNITY_OPEN_MCP_EXT_INPUTSIM_UGUI).
// The workhorse for "click/swipe/drag a named object" during play mode.
//
// Resolution precedence: object_id > target > screen_x/screen_y.
//   - object_id: a handle from scene_get_data / inputsim_probe, resolved via
//     InstanceId (long, EntityId-safe on Unity 6000.5+). The most reliable way to
//     address a specific object when names are ambiguous.
//   - target: GameObject name or slash-path. Duplicate detection returns
//     `ambiguous_target` with candidate paths; trailing-segment match lets a path
//     inside an instantiated prefab resolve without the full scene-root path.
//   - screen_x/screen_y: raycasts through EventSystem.current.RaycastAll.
//
// The response carries honesty fields — `interactable`, `blockedBy`, `dropTarget`
// — so a click on a disabled button or behind a modal is never reported as an
// unqualified ok. Dispatch still happens either way (occlusion-skipping is a
// feature for reaching things a raycast would miss); the fields let the agent
// judge whether the interaction was actually possible.
//
// Play-mode only (play_mode_required otherwise). Gate-free (writes no assets).
// Pair with inputsim_step to advance frames between the interaction and a
// screenshot so tweens/animations settle before you look.
import { makeTool } from "./schema-fragments.js";

export const inputsimPointer = makeTool(
  "unity_open_mcp_inputsim_pointer",
  "Simulate a uGUI pointer interaction on a named GameObject or screen point " +
    "during play mode, via the EventSystem + ExecuteEvents. Resolution precedence: " +
    "object_id (a handle from scene_get_data / inputsim_probe) > target (name or " +
    "slash-path, with duplicate detection + partial-path match) > screen_x/screen_y " +
    "(raycasts through EventSystem.current). The response reports `interactable`, " +
    "`blockedBy`, and (for drag) `dropTarget`/`dropLanded` so a click on a disabled " +
    "button or behind a modal is never a silent ok. Play-mode only — refuses with " +
    "`play_mode_required` otherwise; pair with editor_set_state to enter play mode " +
    "and inputsim_step to advance frames before a screenshot. Gate-free. Requires " +
    "com.unity.ugui.",
  {
    required: ["action"],
    properties: {
      action: {
        type: "string",
        enum: ["click", "double_click", "press", "release", "hover", "hover_exit", "submit", "drag"],
        description:
          "Pointer action. click = down+up+click (IPointerClickHandler); " +
          "double_click = two triples with clickCount incrementing (so handlers " +
          "reading eventData.clickCount==2 fire); press = pointer down only; " +
          "release = pointer up only; hover = pointer Enter ONLY (so tooltip/" +
          "highlight state can be screenshotted); hover_exit = pointer Exit (the " +
          "pair); submit = ISubmitHandler (keyboard-style activate); drag = " +
          "pointerDown → beginDrag → drag×N → pointerUp → drop (on the to-point " +
          "target) → endDrag, matching uGUI's release order, with IDropHandler " +
          "fired on the drop target.",
      },
      object_id: {
        type: "number",
        description:
          "InstanceId of the GameObject to dispatch on (from scene_get_data or " +
          "inputsim_probe). The most reliable addressing when names are ambiguous. " +
          "Wins over target and screen_x/screen_y. On Unity 6000.5+ this is an " +
          "EntityId; pass it as a JSON number (safe up to 2^53).",
      },
      target: {
        type: "string",
        description:
          "GameObject name ('StartButton') or slash-path ('Canvas/Panel/StartButton'). " +
          "Path matching is trailing-segment aware, so 'Panel/Button' matches a " +
          "nested prefab without knowing the scene root. When a name matches more " +
          "than one active object the tool returns `ambiguous_target` with candidate " +
          "paths — pass the full path or an object_id. Dispatched directly via " +
          "ExecuteEvents.ExecuteHierarchy, so it fires even if the target is " +
          "occluded (the response `blockedBy` field reports that case).",
      },
      screen_x: {
        type: "number",
        description:
          "Screen X (pixels) for the screen-point fallback path. Raycasts through " +
          "EventSystem.current.RaycastAll and dispatches to the top hit. Pair with " +
          "screen_y; omitted when object_id/target is given.",
      },
      screen_y: {
        type: "number",
        description: "Screen Y (pixels) for the screen-point fallback path. Pair with screen_x.",
      },
      view: {
        type: "string",
        enum: ["game", "scene"],
        default: "game",
        description:
          "Reserved for a future scene-view path; currently only the game view " +
          "(Camera.main / canvas camera) is used to resolve screen points. Default 'game'.",
      },
      button: {
        type: "string",
        enum: ["left", "right", "middle"],
        default: "left",
        description: "Pointer button reported in PointerEventData. Default 'left'.",
      },
      drag_steps: {
        type: "integer",
        default: 8,
        minimum: 1,
        maximum: 100,
        description:
          "Number of interpolated move events between drag endpoints. Each step " +
          "runs a full EventSystem.RaycastAll, so this is capped at 100 to bound " +
          "main-thread cost. The full begin/drag×N/pointerUp/drop/endDrag sequence " +
          "dispatches in one frame (use inputsim_step afterward to let drag-driven " +
          "tweens settle). Default 8.",
      },
      from_target: {
        type: "string",
        description:
          "Drag start — target name/path. When supplied, takes precedence over " +
          "from_x/from_y; passing BOTH forms returns `both_endpoint_forms` (the " +
          "explicit, non-silent precedence rule).",
      },
      from_x: { type: "number", description: "Drag start — screen X (alternative to from_target)." },
      from_y: { type: "number", description: "Drag start — screen Y (alternative to from_target)." },
      to_target: {
        type: "string",
        description:
          "Drag end / drop site — target name/path. When supplied, takes precedence " +
          "over to_x/to_y; passing BOTH returns `both_endpoint_forms`. The drop " +
          "target receives IDropHandler.OnDrop.",
      },
      to_x: { type: "number", description: "Drag end — screen X (alternative to to_target)." },
      to_y: { type: "number", description: "Drag end — screen Y (alternative to to_target)." },
    },
  },
);
