// Input simulation — interactable discovery (M2).
//
// Turns the surface self-sufficient: the testing loop becomes
//   probe → click by path → step → screenshot
// instead of screenshot → guess a name → target_not_found → guess again.
// Lists active uGUI interactables (IPointer*Handler / IDragHandler / IDropHandler /
// ISubmitHandler / Selectable) with hierarchy path, screen rect, interactable
// flag, instanceId, and whether anything occludes their center.
//
// Read-only. Works in edit mode AND play mode (use it to build a click plan
// before entering play mode). Compile-gated on com.unity.ugui.
import { makeTool } from "./schema-fragments.js";

export const inputsimProbe = makeTool(
  "unity_open_mcp_inputsim_probe",
  "List active uGUI interactables (objects with IPointer*Handler / IDragHandler / " +
    "IDropHandler / ISubmitHandler / Selectable) so an agent can build a click plan " +
    "without guessing names. Each entry carries: hierarchy path, name, instanceId " +
    "(pass it to inputsim_pointer.object_id for unambiguous targeting), screen " +
    "rect, interactable (false when the Selectable or an ancestor CanvasGroup " +
    "disables it), and occluded (true when something else raycast-hits in front of " +
    "the element's center — play mode only). Read-only; works in edit mode and play " +
    "mode. The intended loop: probe → inputsim_pointer (object_id) → inputsim_step " +
    "→ screenshot. Requires com.unity.ugui.",
  {
    required: [],
    properties: {
      page_size: {
        type: "integer",
        default: 50,
        minimum: 1,
        maximum: 200,
        description: "Max entries per page. Default 50, hard cap 200.",
      },
      cursor: {
        type: "string",
        description:
          "Opaque pagination cursor from a previous response's pagination.next_cursor. " +
          "Omit on the first call.",
      },
      scene: {
        type: "string",
        description:
          "Optional loaded-scene name filter — only enumerate interactables in this " +
          "scene. Omit to enumerate across all loaded scenes.",
      },
    },
  },
);
