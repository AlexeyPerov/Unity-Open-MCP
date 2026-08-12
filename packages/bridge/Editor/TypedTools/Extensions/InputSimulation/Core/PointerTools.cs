// Input simulation embedded domain — uGUI pointer dispatch tool.
//
// The workhorse for "click/swipe/drag a named object" during play mode. See
// InputSimulationJson.cs for the domain gating rationale and PointerTargets.cs
// for the resolution + interactability model.
//
// feedback-input.md fixes folded in:
//   P1 — drag now fires IDropHandler on the drop target, in uGUI's release order
//        (pointerUp → drop → endDrag), and reports dropTarget / dropLanded.
//   P2 — response carries interactable + blockedBy, so a disabled/occluded click
//        is never an unqualified ok.
//   P3 — PointerEventData fully populated (pointerPressRaycast, pressEventCamera,
//        pointerPress, pointerDrag, dragging, clickCount) so Slider/ScrollRect/
//        InputField drag math works on screen-camera / world canvases.
//   P4 — screen point is the rect center, not the pivot (in PointerTargets).
//   P5 — object_id (long) > target > screen; target resolves with ambiguity
//        detection and partial-path matching.
//   P6 — hover is enter-only; hover_exit is the pair.
//   P7 — target-wins precedence on both drag endpoints; both_endpoint_forms
//        error when a caller supplies both forms for one endpoint.
//
// Parameter names are snake_case to match the JSON-schema keys (the registry
// lowercases each C# parameter name's first char to derive its JSON key).
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    [BridgeToolType]
    public static class PointerTools
    {
        [BridgeTool("unity_open_mcp_inputsim_pointer",
            Title = "Input Simulation: uGUI Pointer",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = false,
            IdempotentHint = false,
            DestructiveHint = false,
            Lifecycle = LifecyclePolicy.None,
            Group = "input-simulation")]
        [System.ComponentModel.Description(
            "Simulate a uGUI pointer interaction on a named GameObject or screen " +
            "point during play mode via EventSystem + ExecuteEvents. Play-mode only; " +
            "gate-free. Requires com.unity.ugui.")]
        public static string Pointer(
            string action,
            long? object_id = null,
            string target = null,
            float? screen_x = null,
            float? screen_y = null,
            string view = "game",
            string button = "left",
            int drag_steps = 8,
            string from_target = null,
            float? from_x = null,
            float? from_y = null,
            string to_target = null,
            float? to_x = null,
            float? to_y = null)
        {
            _ = view; // reserved for a future scene-view path; game view only today.

            if (!EditorApplication.isPlaying)
                return InputSimulationJson.Error("play_mode_required",
                    "Input simulation requires play mode. Call " +
                    "unity_open_mcp_editor_set_state(state=\"play\") first.");

            if (EventSystem.current == null)
                return InputSimulationJson.Error("no_event_system",
                    "No active EventSystem in the scene. uGUI pointer dispatch " +
                    "requires an EventSystem component (create one via ui_canvas_add " +
                    "or add an EventSystem GameObject).");

            var inputButton = ParseButton(button);

            if (action == "drag")
                return DoDrag(inputButton, drag_steps, object_id,
                    from_target, from_x, from_y, to_target, to_x, to_y);

            return DoSingle(action, inputButton, object_id, target, screen_x, screen_y);
        }

        // ===================================================================
        // Single-point actions: click / double_click / press / release /
        // hover / hover_exit / submit
        // ===================================================================

        private static string DoSingle(
            string action,
            PointerEventData.InputButton inputButton,
            long? objectId, string target, float? sx, float? sy)
        {
            // Resolve target + screen point + raycast result (P5: object_id > target > screen).
            var resolved = ResolveTarget(objectId, target, sx, sy, out var point,
                out var raycast, out var error);
            if (resolved == null) return error;

            var outcome = NewOutcome(resolved, point);

            switch (action)
            {
                case "click":
                    DispatchClick(resolved, point, inputButton, raycast, outcome, clickCount: 1);
                    break;
                case "double_click":
                    // Two triples with clickCount incrementing (P3) — handlers that
                    // detect a double click via eventData.clickCount == 2 now see it.
                    DispatchClick(resolved, point, inputButton, raycast, outcome, clickCount: 1);
                    DispatchClick(resolved, point, inputButton, raycast, outcome, clickCount: 2);
                    break;
                case "press":
                    {
                        var ped = NewEventData(point, inputButton, raycast, resolved);
                        Dispatch(resolved, ped, ExecuteEvents.pointerDownHandler, "pointerDown", outcome);
                        break;
                    }
                case "release":
                    {
                        var ped = NewEventData(point, inputButton, raycast, resolved);
                        Dispatch(resolved, ped, ExecuteEvents.pointerUpHandler, "pointerUp", outcome);
                        break;
                    }
                case "hover": // P6: enter-only (was enter+exit — impossible to screenshot the hover state)
                    {
                        var ped = NewEventData(point, inputButton, raycast, resolved);
                        Dispatch(resolved, ped, ExecuteEvents.pointerEnterHandler, "pointerEnter", outcome);
                        break;
                    }
                case "hover_exit":
                    {
                        var ped = NewEventData(point, inputButton, raycast, resolved);
                        Dispatch(resolved, ped, ExecuteEvents.pointerExitHandler, "pointerExit", outcome);
                        break;
                    }
                case "submit":
                    {
                        var ped = NewEventData(point, inputButton, raycast, resolved);
                        Dispatch(resolved, ped, ExecuteEvents.submitHandler, "submit", outcome);
                        break;
                    }
                default:
                    return InputSimulationJson.Error("invalid_action",
                        $"Unknown pointer action '{action}'. Valid: click, double_click, " +
                        "press, release, hover, hover_exit, submit, drag.");
            }

            ComputeHonestyFields(outcome, resolved, point);
            return InputSimulationJson.BuildPointerOk(outcome);
        }

        // click = pointerDown → pointerUp → pointerClick. clickCount is set on the
        // event data so OnPointerClick handlers that branch on clickCount (Button
        // doesn't, but double-click detectors do) read the intended count.
        private static void DispatchClick(
            GameObject resolved, Vector2 point, PointerEventData.InputButton inputButton,
            RaycastResult raycast, PointerOutcome outcome, int clickCount)
        {
            var ped = NewEventData(point, inputButton, raycast, resolved);
            ped.clickCount = clickCount;
            ped.clickTime = Time.realtimeSinceStartup;
            Dispatch(resolved, ped, ExecuteEvents.pointerDownHandler, "pointerDown", outcome);
            Dispatch(resolved, ped, ExecuteEvents.pointerUpHandler, "pointerUp", outcome);
            Dispatch(resolved, ped, ExecuteEvents.pointerClickHandler, "pointerClick", outcome);
        }

        // ===================================================================
        // Drag: uGUI release order with IDropHandler on the drop target (P1)
        // ===================================================================

        private static string DoDrag(
            PointerEventData.InputButton inputButton, int dragSteps, long? objectId,
            string fromTarget, float? fromX, float? fromY,
            string toTarget, float? toX, float? toY)
        {
            // P7: target-wins on BOTH endpoints; error when both forms supplied.
            Vector2 fromPoint, toPoint;
            GameObject pressTarget;

            // The `from` end MUST resolve to an object (it becomes the pressTarget),
            // so a bare screen point raycasts and errors no_hit on empty space.
            var fromResolved = ResolveEndpoint(objectId, fromTarget, fromX, fromY,
                "from_target/from_x", requireObject: true, out fromPoint, out var fromErr);
            if (fromResolved == null) return fromErr;
            pressTarget = fromResolved;

            // The `to` end may land in empty space (valid — no drop target). Only
            // hard resolution failures (ambiguous / both-forms) surface as errors.
            var toResolved = ResolveEndpoint(null, toTarget, toX, toY,
                "to_target/to_x", requireObject: false, out toPoint, out var toErr);
            if (toErr != null) return toErr;

            // feedback S6 — bound drag steps (each runs a full RaycastAll).
            if (dragSteps < 1) dragSteps = 1;
            if (dragSteps > 100) dragSteps = 100;

            var outcome = NewOutcome(pressTarget, fromPoint);
            outcome.IsDrag = true;
            var pressRaycast = RaycastAt(fromPoint);

            var ped = NewEventData(fromPoint, inputButton, pressRaycast, pressTarget);
            ped.pressPosition = fromPoint;
            ped.pointerPress = pressTarget;
            ped.pointerDrag = pressTarget;

            // press + beginDrag
            Dispatch(pressTarget, ped, ExecuteEvents.pointerDownHandler, "pointerDown", outcome);
            ped.dragging = true;
            Dispatch(pressTarget, ped, ExecuteEvents.beginDragHandler, "beginDrag", outcome);

            // move across interpolated points
            var prev = fromPoint;
            for (int i = 1; i <= dragSteps; i++)
            {
                float t = (float)i / dragSteps;
                ped.position = Vector2.Lerp(fromPoint, toPoint, t);
                ped.delta = ped.position - prev;
                prev = ped.position;
                ped.pointerCurrentRaycast = RaycastAt(ped.position);
                Dispatch(pressTarget, ped, ExecuteEvents.dragHandler, "drag", outcome);
            }

            ped.position = toPoint;
            ped.delta = toPoint - prev;

            // P1: uGUI release order — pointerUp → drop (on drop target) → endDrag.
            ped.dragging = false;
            Dispatch(pressTarget, ped, ExecuteEvents.pointerUpHandler, "pointerUp", outcome);

            // Resolve the drop target at toPoint and dispatch IDropHandler (P1).
            GameObject dropTarget = null;
            if (toResolved != null)
            {
                dropTarget = toResolved;
            }
            else
            {
                var dropHit = PointerTargets.RaycastTop(toPoint, out _);
                if (dropHit != null) dropTarget = dropHit;
            }
            if (dropTarget != null)
            {
                Dispatch(dropTarget, ped, ExecuteEvents.dropHandler, "drop", outcome);
                outcome.DropTarget = PointerTargets.BuildPath(dropTarget);
                outcome.DropLanded = true;
            }
            else
            {
                // No drop target — `dropLanded: false` already carries that signal.
                // Do NOT add "drop" to dispatched; that array lists events actually fired.
                outcome.DropTarget = null;
                outcome.DropLanded = false;
            }

            Dispatch(pressTarget, ped, ExecuteEvents.endDragHandler, "endDrag", outcome);

            ComputeHonestyFields(outcome, pressTarget, fromPoint);
            outcome.ScreenPoint = toPoint;
            return InputSimulationJson.BuildPointerOk(outcome);
        }

        // ===================================================================
        // Resolution helpers
        // ===================================================================

        // P5 precedence: object_id > target > screen_x/screen_y. Sets `point`,
        // `raycast` (non-null only when a real raycast produced the hit), and
        // `error` (non-null only on failure). Returns null + error on failure.
        private static GameObject ResolveTarget(
            long? objectId, string target, float? sx, float? sy,
            out Vector2 point, out RaycastResult raycast, out string error)
        {
            point = default;
            raycast = default;
            error = null;

            if (objectId.HasValue)
            {
                var go = PointerTargets.FindByInstanceId(objectId.Value);
                if (go == null)
                {
                    error = InputSimulationJson.Error("object_not_found",
                        $"No live GameObject for object_id {objectId.Value}.");
                    return null;
                }
                point = PointerTargets.ScreenPointOf(go);
                return go;
            }

            if (!string.IsNullOrEmpty(target))
            {
                var candidates = new List<string>();
                var go = PointerTargets.FindByPath(target, candidates);
                if (go == null && candidates.Count > 0)
                {
                    error = InputSimulationJson.ErrorWithCandidates("ambiguous_target",
                        $"Target '{target}' matches {candidates.Count} active GameObjects. " +
                        "Pass the full path, an object_id from scene_get_data, or a longer " +
                        "trailing segment.", candidates);
                    return null;
                }
                if (go == null)
                {
                    error = InputSimulationJson.Error("target_not_found",
                        $"No active GameObject found for target '{target}'.");
                    return null;
                }
                point = PointerTargets.ScreenPointOf(go);
                return go;
            }

            if (sx.HasValue && sy.HasValue)
            {
                point = new Vector2(sx.Value, sy.Value);
                var hit = PointerTargets.RaycastTop(point, out var results);
                if (hit == null)
                {
                    error = InputSimulationJson.Error("no_hit",
                        $"No uGUI element under screen point ({sx.Value}, {sy.Value}). " +
                        "The EventSystem raycast returned no results.");
                    return null;
                }
                if (results.Count > 0) raycast = results[0];
                return hit;
            }

            error = InputSimulationJson.Error("no_target_or_screen_point",
                "Provide object_id, target (name/path), or both screen_x and screen_y.");
            return null;
        }

        // Resolve a drag endpoint (from_/to_). P7: target-wins; both_endpoint_forms
        // when both target and screen are supplied. `requireObject` distinguishes the
        // two ends: the `from` end MUST resolve to a GameObject (it becomes the
        // pressTarget), so a bare screen point raycasts and errors `no_hit` on empty
        // space; the `to` end may land in empty space (returns null with NO error —
        // valid for a drag with no drop target). (feedback B2)
        private static GameObject ResolveEndpoint(
            long? objectId, string target, float? sx, float? sy, string label,
            bool requireObject,
            out Vector2 point, out string error)
        {
            point = default;
            error = null;

            if (objectId.HasValue)
            {
                var go = PointerTargets.FindByInstanceId(objectId.Value);
                if (go == null)
                {
                    error = InputSimulationJson.Error("object_not_found",
                        $"No live GameObject for object_id {objectId.Value}.");
                    return null;
                }
                point = PointerTargets.ScreenPointOf(go);
                return go;
            }

            bool hasTarget = !string.IsNullOrEmpty(target);
            bool hasScreen = sx.HasValue && sy.HasValue;

            if (hasTarget && hasScreen) // P7
            {
                error = InputSimulationJson.Error("both_endpoint_forms",
                    $"Both target and screen coordinates supplied for {label}. " +
                    "Pass one form per endpoint (target wins when both are given; " +
                    "this error makes that explicit instead of silent).");
                return null;
            }

            if (hasTarget)
            {
                var candidates = new List<string>();
                var go = PointerTargets.FindByPath(target, candidates);
                if (go == null && candidates.Count > 0)
                {
                    error = InputSimulationJson.ErrorWithCandidates("ambiguous_target",
                        $"Endpoint target '{target}' matches {candidates.Count} active GameObjects.",
                        candidates);
                    return null;
                }
                if (go == null)
                {
                    error = InputSimulationJson.Error("target_not_found",
                        $"No active GameObject found for target '{target}'.");
                    return null;
                }
                point = PointerTargets.ScreenPointOf(go);
                return go;
            }

            if (hasScreen)
            {
                point = new Vector2(sx.Value, sy.Value);
                if (requireObject)
                {
                    // The `from` end must land on an object (it becomes the
                    // pressTarget); raycast at the point and error no_hit when
                    // nothing is under it. The `to` end falls through to the
                    // empty-space-is-valid return below.
                    var hit = PointerTargets.RaycastTop(point, out _);
                    if (hit == null)
                    {
                        error = InputSimulationJson.Error("no_hit",
                            $"No uGUI element under the {label} screen point " +
                            $"({sx.Value}, {sy.Value}). The EventSystem raycast " +
                            "returned no results.");
                        return null;
                    }
                    return hit;
                }
                // No error — screen point always "resolves"; whether something is
                // under it is checked separately at the drop site.
                return null;
            }

            error = InputSimulationJson.Error("no_endpoint",
                $"No target or screen point supplied for {label}.");
            return null;
        }

        // ===================================================================
        // Event-data + outcome helpers (P3)
        // ===================================================================

        private static PointerEventData NewEventData(
            Vector2 point, PointerEventData.InputButton btn,
            RaycastResult raycast, GameObject resolved)
        {
            var ped = new PointerEventData(EventSystem.current)
            {
                position = point,
                pressPosition = point,
                button = btn,
            };
            // P3: populate the raycast-derived fields. pressEventCamera is the
            // load-bearing one — Slider/ScrollRect/InputField OnDrag use it to
            // convert screen → local rect; null yields a wrong local point under
            // screen-camera / world canvases.
            ApplyRaycast(ped, raycast, resolved);
            return ped;
        }

        private static void ApplyRaycast(PointerEventData ped, RaycastResult raycast, GameObject resolved)
        {
            // pressEventCamera / enterEventCamera are READ-ONLY derived properties:
            // they return pointerPressRaycast.module.eventCamera. They have no
            // setter, so they MUST NOT be assigned directly (CS0200). Instead the
            // camera is routed through a real BaseRaycaster on the RaycastResult's
            // `module` field — it then resolves on its own. (feedback B1)
            if (IsValidRaycast(ref raycast))
            {
                // Valid raycast already carries its originating module, so
                // pressEventCamera resolves correctly once the result is set.
                ped.pointerCurrentRaycast = raycast;
                ped.pointerPressRaycast = raycast;
            }
            else if (resolved != null)
            {
                // Target-path synthesis: pressEventCamera is derived from
                // pointerPressRaycast.module, so the synthesized RaycastResult
                // must carry the resolved raycaster for it to resolve non-null.
                // Slider/ScrollRect/InputField OnDrag use pressEventCamera to
                // convert screen → local rect; a null camera yields a wrong local
                // point under screen-camera / world canvases.
                var raycaster = resolved.GetComponentInParent<BaseRaycaster>();
                var synth = new RaycastResult
                {
                    gameObject = resolved,
                    module = raycaster,
                    screenPosition = ped.position,
                };
                ped.pointerCurrentRaycast = synth;
                ped.pointerPressRaycast = synth;
            }
        }

        private static bool IsValidRaycast(ref RaycastResult r)
            => r.gameObject != null;

        private static RaycastResult RaycastAt(Vector2 point)
        {
            PointerTargets.RaycastTop(point, out var results);
            return results != null && results.Count > 0 ? results[0] : default;
        }

        private static PointerOutcome NewOutcome(GameObject resolved, Vector2 point)
        {
            return new PointerOutcome
            {
                TargetPath = PointerTargets.BuildPath(resolved),
                ScreenPoint = point,
                HasHandler = PointerTargets.HasAnyPointerHandler(resolved),
            };
        }

        // P2: compute interactable + blockedBy after dispatch. blockedBy is the top
        // raycast hit at the press point when it is neither the target nor a
        // descendant — i.e. something is in front of the target.
        private static void ComputeHonestyFields(PointerOutcome outcome, GameObject resolved, Vector2 point)
        {
            outcome.Interactable = PointerTargets.ComputeInteractable(resolved);

            var topHit = PointerTargets.RaycastTop(point, out _);
            if (topHit != null && topHit != resolved
                && !PointerTargets.IsSameOrDescendant(resolved, topHit))
            {
                outcome.BlockedBy = PointerTargets.BuildPath(topHit);
            }
            else
            {
                outcome.BlockedBy = null;
            }
        }

        private static void Dispatch<T>(
            GameObject target, PointerEventData ped,
            ExecuteEvents.EventFunction<T> functor, string name, PointerOutcome outcome)
            where T : IEventSystemHandler
        {
            ExecuteEvents.ExecuteHierarchy(target, ped, functor);
            outcome.Dispatched.Add(name);
        }

        private static PointerEventData.InputButton ParseButton(string button)
        {
            if (string.IsNullOrEmpty(button)) return PointerEventData.InputButton.Left;
            switch (button.ToLowerInvariant())
            {
                case "right": return PointerEventData.InputButton.Right;
                case "middle": return PointerEventData.InputButton.Middle;
                default: return PointerEventData.InputButton.Left;
            }
        }
    }
}
