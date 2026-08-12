// Input simulation — 3D / legacy world-space pointer (M3).
//
// Reaches physics-collider gameplay and old-Input-Manager code that
// inputsim_pointer (uGUI ExecuteEvents) cannot. Mechanics: Physics.Raycast from
// the screen → SendMessage("OnMouseDown"/"OnMouseUp"/"OnMouseUpAsButton"/
// "OnMouseDrag"). No package dependency beyond built-in UnityEngine.Physics.
// Gate-free, play-mode-only. Compile-gated on com.unity.ugui (shares the uGUI
// sub-asmdef for target/screen resolution helpers, though it uses neither
// EventSystem nor uGUI types at the dispatch path).
import { makeTool } from "./schema-fragments.js";

export const inputsimPointer3d = makeTool(
  "unity_open_mcp_inputsim_pointer3d",
  "Simulate a world-space / legacy mouse interaction during play mode by " +
    "raycasting from the screen point through Camera.main and SendMessage-ing the " +
    "OnMouseDown / OnMouseUp / OnMouseUpAsButton / OnMouseDrag magic methods on " +
    "the hit collider. Reaches physics gameplay and old-Input-Manager code that " +
    "inputsim_pointer (uGUI ExecuteEvents) cannot — the only way to drive " +
    "world-space gameplay in a project running the legacy Input Manager. " +
    "Resolution: object_id > target > screen_x/screen_y (same as inputsim_pointer). " +
    "Play-mode only — refuses with `play_mode_required` otherwise. Gate-free. " +
    "Requires a tagged MainCamera in the scene.",
  {
    required: ["action"],
    properties: {
      action: {
        type: "string",
        enum: ["click", "down", "up", "drag"],
        description:
          "click = OnMouseDown + OnMouseUpAsButton + OnMouseUp; down = OnMouseDown; " +
          "up = OnMouseUp; drag = OnMouseDown + OnMouseDrag×drag_steps + OnMouseUp " +
          "(requires to_x/to_y).",
      },
      object_id: {
        type: "number",
        description:
          "InstanceId of a GameObject used to refine the screen point (its center " +
          "is raycast). The actual target is whatever the ray hits at that point. " +
          "Wins over target and screen_x/screen_y.",
      },
      target: {
        type: "string",
        description: "GameObject name/path used to refine the screen point. Alternative to object_id/screen.",
      },
      screen_x: { type: "number", description: "Screen X (pixels) to raycast from. Pair with screen_y." },
      screen_y: { type: "number", description: "Screen Y (pixels) to raycast from. Pair with screen_x." },
      to_x: { type: "number", description: "Drag end — screen X (action: drag only). Pair with to_y." },
      to_y: { type: "number", description: "Drag end — screen Y (action: drag only). Pair with to_x." },
      drag_steps: {
        type: "integer",
        default: 8,
        minimum: 1,
        maximum: 100,
        description:
          "Number of OnMouseDrag calls between from and to (action: drag). Capped at " +
          "100 to bound main-thread cost. Default 8.",
      },
    },
  },
);
