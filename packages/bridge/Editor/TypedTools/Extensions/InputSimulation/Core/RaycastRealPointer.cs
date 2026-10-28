// inputsim_pointer routing: "raycast_real".
//
// Instead of ExecuteEvents on a resolved object, inject a real touch at the
// target's screen point through the project's input backend (legacy touches
// and/or Input System Touchscreen — InputRouting), stepping frames so the
// scene's own EventSystem input module raycasts and dispatches it. Whatever is
// on top at that point receives the press — a cover graphic absorbs a tap meant
// for the button underneath, exactly as a finger would. The response reports
// the top hit before injection, whether it is the requested target, the active
// input module, and the routed backend.
//
// Touch emulates the left button; right/middle are refused. Hover has no touch
// equivalent and is refused (use routing: dispatch).
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    internal static class RaycastRealPointer
    {
        internal static string Run(
            string action, PointerEventData.InputButton button, int dragSteps,
            long? objectId, string target, float? sx, float? sy,
            string fromTarget, float? fromX, float? fromY,
            string toTarget, float? toX, float? toY,
            string backend)
            => Run(action, button, dragSteps, objectId, target, sx, sy, fromTarget, fromX, fromY,
                toTarget, toX, toY, backend, null, default, null);

        // `target_text` path: the press end was already resolved by label.
        internal static string RunResolved(
            string action, PointerEventData.InputButton button, int dragSteps,
            GameObject resolved, Vector2 point, string toTarget, float? toX, float? toY,
            string backend, string textTarget)
            => Run(action, button, dragSteps, null, null, null, null, null, null, null,
                toTarget, toX, toY, backend, resolved, point, textTarget);

        private static string Run(
            string action, PointerEventData.InputButton button, int dragSteps,
            long? objectId, string target, float? sx, float? sy,
            string fromTarget, float? fromX, float? fromY,
            string toTarget, float? toX, float? toY,
            string backend, GameObject preResolved, Vector2 prePoint, string textTarget)
        {
            if (action != "click" && action != "double_click" && action != "press" && action != "release" && action != "drag")
                return InputSimulationJson.Error("invalid_routing_action",
                    $"routing: raycast_real supports click, double_click, press, release and drag; '{action}' has no " +
                    "touch equivalent. Use routing: dispatch.");
            if (button != PointerEventData.InputButton.Left)
                return InputSimulationJson.Error("invalid_button",
                    "routing: raycast_real injects a touch, which emulates the left button only.");

            var busy = PlayModeScheduler.RunningWithResource(TouchTools.InputResource);
            if (busy != null)
                return InputSimulationJson.Error("playmode_operation_busy",
                    "Play-mode operation " + busy + " (inputsim_gesture) is driving input. Wait for it or cancel it first.");

            var route = InputRouting.Decide(InputEnvironment.Detect(), backend, new[] { 0 });
            if (!route.Ok) return route.ErrorJson();

            GameObject resolved;
            Vector2 point, endPoint;
            if (preResolved != null)
            {
                resolved = preResolved;
                point = prePoint;
                endPoint = point;
                if (action == "drag" && !ResolvePoint(null, toTarget, toX, toY, "to", out _, out endPoint, out var toErr)) return toErr;
            }
            else if (action == "drag")
            {
                if (!ResolvePoint(objectId, fromTarget, fromX, fromY, "from", out resolved, out point, out var fromErr)) return fromErr;
                if (!ResolvePoint(null, toTarget, toX, toY, "to", out _, out endPoint, out var toErr)) return toErr;
            }
            else
            {
                if (!ResolvePoint(objectId, target, sx, sy, null, out resolved, out point, out var err)) return err;
                endPoint = point;
            }

            var topHit = PointerTargets.RaycastTop(point, out _);
            string targetPath = resolved != null ? PointerTargets.BuildPath(resolved) : null;
            bool targetHit = resolved == null ? topHit != null
                : topHit != null && (topHit == resolved || PointerTargets.IsSameOrDescendant(resolved, topHit));

            if (dragSteps < 1) dragSteps = 1;
            if (dragSteps > 100) dragSteps = 100;
            int between = TouchSequence.FramesBetween(route, 1);
            int last = Mathf.Max(1, TouchSequence.FramesAfterLast(route));
            var steps = new List<TouchSequence.Step>();
            switch (action)
            {
                case "click":
                    steps.Add(new TouchSequence.Step(DeviceTouchPhase.Began, point, between));
                    steps.Add(new TouchSequence.Step(DeviceTouchPhase.Ended, point, last));
                    break;
                case "double_click":
                    for (int i = 0; i < 2; i++)
                    {
                        steps.Add(new TouchSequence.Step(DeviceTouchPhase.Began, point, between));
                        steps.Add(new TouchSequence.Step(DeviceTouchPhase.Ended, point, last));
                    }
                    break;
                case "press":
                    steps.Add(new TouchSequence.Step(DeviceTouchPhase.Began, point, last));
                    break;
                case "release":
                    steps.Add(new TouchSequence.Step(DeviceTouchPhase.Ended, point, last));
                    break;
                case "drag":
                    steps.Add(new TouchSequence.Step(DeviceTouchPhase.Began, point, between));
                    for (int i = 1; i <= dragSteps; i++)
                        steps.Add(new TouchSequence.Step(DeviceTouchPhase.Moved, Vector2.Lerp(point, endPoint, (float)i / dragSteps), between));
                    steps.Add(new TouchSequence.Step(DeviceTouchPhase.Ended, endPoint, last));
                    break;
            }

            bool focused = GameViewWindows.EnsureGameViewFocusForInputSystem(route);
            List<string> injected;
            int frames;
            try { injected = TouchSequence.Run(route, 0, steps, out frames); }
            catch (System.Exception ex)
            {
                return InputSimulationJson.Error("injection_failed", "Touch injection failed: " + ex.Message);
            }

            var module = EventSystem.current != null && EventSystem.current.currentInputModule != null
                ? EventSystem.current.currentInputModule.GetType().Name : null;
            var notes = new List<string>();
            if (module != null && module.Contains("InputSystemUIInputModule") && !route.Uses(InputRouting.InputSystem))
                notes.Add("The EventSystem reads the Input System (" + module + ") but no Input System touch was injected (" +
                          route.SkippedJson() + "); uGUI will not see this press.");
            if (module != null && module == "StandaloneInputModule" && !route.Uses(InputRouting.LegacyTouch))
                notes.Add("The EventSystem reads legacy Input (StandaloneInputModule) but no legacy touch was injected; uGUI will not see this press.");
            if (route.Uses(InputRouting.LegacyTouch) && !route.Environment.SimulateMouseWithTouches)
                notes.Add("Input.simulateMouseWithTouches is false: legacy mouse readers do not see the touch (touch readers and StandaloneInputModule touch processing still do).");

            var sb = new StringBuilder(512);
            sb.Append("\"routing\":\"raycast_real\"");
            sb.Append(",\"backend\":").Append(route.BackendJson());
            sb.Append(",\"skipped\":").Append(route.SkippedJson());
            sb.Append(",\"action\":").Append(InputSimulationJson.Esc(action));
            sb.Append(",\"target\":").Append(targetPath == null ? "null" : InputSimulationJson.Esc(targetPath));
            if (textTarget != null) sb.Append(",\"textTarget\":").Append(textTarget);
            sb.Append(",\"screenPoint\":[").Append(InputSimulationJson.Num(point.x)).Append(',').Append(InputSimulationJson.Num(point.y)).Append(']');
            if (action == "drag")
                sb.Append(",\"endPoint\":[").Append(InputSimulationJson.Num(endPoint.x)).Append(',').Append(InputSimulationJson.Num(endPoint.y)).Append(']');
            sb.Append(",\"topHit\":").Append(topHit == null ? "null" : InputSimulationJson.Esc(PointerTargets.BuildPath(topHit)));
            sb.Append(",\"targetHit\":").Append(targetHit ? "true" : "false");
            sb.Append(",\"blockedBy\":").Append(resolved != null && topHit != null && !targetHit
                ? InputSimulationJson.Esc(PointerTargets.BuildPath(topHit)) : "null");
            sb.Append(",\"interactable\":").Append(resolved == null || PointerTargets.ComputeInteractable(resolved) ? "true" : "false");
            sb.Append(",\"hasHandler\":").Append(resolved != null && PointerTargets.HasAnyPointerHandler(resolved) ? "true" : "false");
            sb.Append(",\"inputModule\":").Append(module == null ? "null" : InputSimulationJson.Esc(module));
            sb.Append(",\"focusedGameView\":").Append(focused ? "true" : "false");
            sb.Append(",\"framesAdvanced\":").Append(frames);
            sb.Append(",\"injected\":[");
            for (int i = 0; i < injected.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(InputSimulationJson.Esc(injected[i]));
            }
            sb.Append("],\"notes\":[");
            for (int i = 0; i < notes.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(InputSimulationJson.Esc(notes[i]));
            }
            sb.Append(']');
            return InputSimulationJson.Ok(sb.ToString());
        }

        // object_id > target > screen point. A bare screen point is valid even over
        // empty space (the input module decides); `resolved` is then the top hit.
        private static bool ResolvePoint(
            long? objectId, string target, float? x, float? y, string label,
            out GameObject resolved, out Vector2 point, out string error)
        {
            resolved = null;
            point = default;
            error = null;
            var prefix = label == null ? "" : label + "_";
            if (objectId.HasValue)
            {
                resolved = PointerTargets.FindByInstanceId(objectId.Value);
                if (resolved == null)
                {
                    error = InputSimulationJson.Error("object_not_found", $"No live GameObject for object_id {objectId.Value}.");
                    return false;
                }
                point = PointerTargets.ScreenPointOf(resolved);
                return true;
            }
            bool hasTarget = !string.IsNullOrEmpty(target), hasScreen = x.HasValue && y.HasValue;
            if (hasTarget && hasScreen && label != null)
            {
                error = InputSimulationJson.Error("both_endpoint_forms",
                    $"Both target and screen coordinates supplied for {prefix}target/{prefix}x. Pass one form per endpoint.");
                return false;
            }
            if (hasTarget)
            {
                var candidates = new List<string>();
                resolved = PointerTargets.FindByPath(target, candidates);
                if (resolved == null && candidates.Count > 0)
                {
                    error = InputSimulationJson.ErrorWithCandidates("ambiguous_target",
                        $"Target '{target}' matches {candidates.Count} active GameObjects. Pass the full path or an object_id.", candidates);
                    return false;
                }
                if (resolved == null)
                {
                    error = InputSimulationJson.Error("target_not_found", $"No active GameObject found for target '{target}'.");
                    return false;
                }
                point = PointerTargets.ScreenPointOf(resolved);
                return true;
            }
            if (hasScreen)
            {
                point = new Vector2(x.Value, y.Value);
                return true;
            }
            error = InputSimulationJson.Error(label == null ? "no_target_or_screen_point" : "no_endpoint",
                label == null
                    ? "Provide object_id, target (name/path), or both screen_x and screen_y."
                    : $"No target or screen point supplied for {prefix}target/{prefix}x.");
            return false;
        }
    }

    // Registers the uGUI top-hit probe used by inputsim_gesture's trace.
    [UnityEditor.InitializeOnLoad]
    internal static class UiHitProbeRegistration
    {
        static UiHitProbeRegistration()
        {
            InputBackends.Ui = new UiHitProbe();
        }

        private sealed class UiHitProbe : IUiHitProbe
        {
            public string TopHitPath(Vector2 screenPoint)
            {
                if (EventSystem.current == null) return null;
                var hit = PointerTargets.RaycastTop(screenPoint, out _);
                return hit == null ? null : PointerTargets.BuildPath(hit);
            }

            public string InputModuleName => EventSystem.current != null && EventSystem.current.currentInputModule != null
                ? EventSystem.current.currentInputModule.GetType().Name : null;
        }
    }
}
