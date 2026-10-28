// Input simulation — backend-aware touch / swipe (inputsim_touch).
//
// Lives in the bridge root assembly so it compiles without com.unity.inputsystem:
// a legacy-only project gets legacy touches; an Input System project gets
// Touchscreen state events (from the Input System sub-assembly's backend); with
// activeInputHandler Both, `auto` injects into both and reports `backend` as an
// array. Routing and refusals: InputRouting. Framing: TouchSequence.
//
// Play-mode only. Gate-free (IsMutating=false — input writes no assets).
// duration_ms is metadata, not a real-time wait; real-time gestures use
// inputsim_gesture.
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    [BridgeToolType]
    public static class TouchTools
    {
        internal const string InputResource = "input";

        [BridgeTool("unity_open_mcp_inputsim_touch",
            Title = "Input Simulation: Touch / Swipe",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = false,
            IdempotentHint = false,
            DestructiveHint = false,
            Lifecycle = LifecyclePolicy.None,
            Group = "input-simulation")]
        [System.ComponentModel.Description(
            "Simulate a touch / swipe during play mode through the project's real input " +
            "backend: legacy Input Manager touches (legacy_touch) and/or Input System " +
            "Touchscreen events (input_system). Play-mode only; gate-free.")]
        public static string Touch(
            string action,
            int finger = 0,
            string target = null,
            float? screen_x = null,
            float? screen_y = null,
            string from_target = null,
            float? from_x = null,
            float? from_y = null,
            string to_target = null,
            float? to_x = null,
            float? to_y = null,
            int duration_ms = 200,
            int steps = 10,
            int advance_frames = 0,
            string backend = "auto",
            string target_text = null,
            string under = null,
            int? index = null,
            string text_match = null)
        {
            if (!EditorApplication.isPlaying)
                return Error("play_mode_required",
                    "Input simulation requires play mode. Call " +
                    "unity_open_mcp_editor_set_state(state=\"play\") first.");

            if (action != "tap" && action != "press" && action != "release" && action != "swipe")
                return Error("invalid_action", $"Unknown touch action '{action}'. Valid: tap, swipe, press, release.");

            var busy = PlayModeScheduler.RunningWithResource(InputResource);
            if (busy != null)
                return Error("playmode_operation_busy",
                    "Play-mode operation " + busy + " (inputsim_gesture) is driving input. Wait for it or cancel it first.");

            if (finger < 0) finger = 0;
            if (finger > 9) finger = 9;
            if (steps < 1) steps = 1;
            if (steps > 100) steps = 100;
            if (advance_frames < 0) advance_frames = 0;
            if (advance_frames > 60) advance_frames = 60;

            bool byText = !string.IsNullOrEmpty(target_text);
            if (!byText && (!string.IsNullOrEmpty(under) || index.HasValue))
                return Error("invalid_arguments", "under and index refine target_text; pass target_text with them.");
            if (byText)
            {
                bool conflict = action == "swipe"
                    ? !string.IsNullOrEmpty(from_target) || from_x.HasValue || from_y.HasValue
                    : !string.IsNullOrEmpty(target) || screen_x.HasValue || screen_y.HasValue;
                if (conflict)
                    return Error("conflicting_targets", action == "swipe"
                        ? "target_text is the swipe start; do not also pass from_target or from_x/from_y."
                        : "Pass one target form: target_text, target, or screen_x/screen_y.");
            }

            var route = InputRouting.Decide(InputEnvironment.Detect(), backend, new[] { finger });
            if (!route.Ok) return route.ErrorJson();

            // Resolve endpoints before injecting anything.
            Vector2 start, end;
            string textTarget = null;
            if (byText)
            {
                if (!DeviceTargets.TryResolveText(target_text, text_match, under, index, null, out start, out textTarget, out var textErr))
                    return textErr;
            }
            else start = default;
            if (action == "swipe")
            {
                if (!byText && !ResolveSwipeEndpoint(from_target, from_x, from_y, true, default, "from", out start, out var fromErr))
                    return fromErr;
                if (!ResolveSwipeEndpoint(to_target, to_x, to_y, false, start, "to", out end, out var toErr))
                    return toErr;
            }
            else
            {
                if (!byText && !DeviceTargets.ResolvePoint(target, screen_x, screen_y, out start, out var err))
                    return Error(err.Code, err.Message);
                end = start;
            }

            bool focused = GameViewWindows.EnsureGameViewFocusForInputSystem(route);
            int between = TouchSequence.FramesBetween(route, advance_frames);
            int last = TouchSequence.FramesAfterLast(route);
            var plan = new List<TouchSequence.Step>();
            switch (action)
            {
                case "tap":
                    plan.Add(new TouchSequence.Step(DeviceTouchPhase.Began, start, between));
                    plan.Add(new TouchSequence.Step(DeviceTouchPhase.Ended, start, last));
                    break;
                case "press":
                    plan.Add(new TouchSequence.Step(DeviceTouchPhase.Began, start, last));
                    break;
                case "release":
                    plan.Add(new TouchSequence.Step(DeviceTouchPhase.Ended, start, last));
                    break;
                case "swipe":
                    plan.Add(new TouchSequence.Step(DeviceTouchPhase.Began, start, between));
                    for (int i = 1; i <= steps; i++)
                        plan.Add(new TouchSequence.Step(DeviceTouchPhase.Moved, Vector2.Lerp(start, end, (float)i / steps), between));
                    plan.Add(new TouchSequence.Step(DeviceTouchPhase.Ended, end, last));
                    break;
            }

            List<string> dispatched;
            int framesAdvanced;
            try { dispatched = TouchSequence.Run(route, finger, plan, out framesAdvanced); }
            catch (System.Exception ex)
            {
                return Error("injection_failed", "Touch injection failed: " + ex.Message);
            }

            var sb = new StringBuilder(320);
            sb.Append("{\"status\":\"ok\",\"device\":\"touchscreen\"");
            sb.Append(",\"backend\":").Append(route.BackendJson());
            sb.Append(",\"skipped\":").Append(route.SkippedJson());
            sb.Append(",\"action\":").Append(BridgeJson.EscapeString(action));
            sb.Append(",\"finger\":").Append(finger);
            sb.Append(",\"endPoint\":[").Append(Num(end.x)).Append(',').Append(Num(end.y)).Append(']');
            if (textTarget != null) sb.Append(",\"textTarget\":").Append(textTarget);
            sb.Append(",\"durationMs\":").Append(duration_ms);
            sb.Append(",\"advanceFrames\":").Append(between);
            sb.Append(",\"framesAdvanced\":").Append(framesAdvanced);
            if (route.Uses(InputRouting.LegacyTouch))
                sb.Append(",\"mouseEmulation\":").Append(route.Environment.SimulateMouseWithTouches ? "true" : "false");
            sb.Append(",\"focusedGameView\":").Append(focused ? "true" : "false");
            sb.Append(",\"queued\":true,\"dispatched\":[");
            for (int i = 0; i < dispatched.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(dispatched[i]));
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // Swipe endpoint: target wins; `to` falls back to the start point.
        private static bool ResolveSwipeEndpoint(
            string target, float? x, float? y, bool required, Vector2 fallback, string label,
            out Vector2 point, out string error)
        {
            error = null;
            point = fallback;
            if (!string.IsNullOrEmpty(target))
            {
                if (DeviceTargets.TryResolveTarget(target, out point, out var err)) return true;
                error = Error(err.Code, $"Swipe {label}_target — {err.Message}");
                return false;
            }
            if (x.HasValue && y.HasValue)
            {
                point = new Vector2(x.Value, y.Value);
                return true;
            }
            if (!required) return true;
            error = Error("no_swipe_from", "Swipe requires a start: pass from_target, or both from_x and from_y.");
            return false;
        }

        internal static string Num(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        internal static string Error(string code, string message)
            => "{\"error\":{\"code\":" + BridgeJson.EscapeString(code) + ",\"message\":" + BridgeJson.EscapeString(message) + "}}";
    }
}
