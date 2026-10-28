// Input simulation — gestures over real play-mode frames (inputsim_gesture).
//
// The tool validates the plan, decides the input route and starts a
// GestureOperation on the shared PlayModeScheduler, then returns at once with the
// operation id. The scheduler ticks it at the start of every EarlyUpdate:
// keyframes are interpolated per frame (time: realtime, or gameplay — scaled by
// Time.timeScale), the finger is injected into every routed backend, and a
// bounded trace records each keyframe's frame, screen point and uGUI top hit.
// MCP turns the start into a terminal result by polling POST
// /playmode-operations (inline for short gestures, as a `jobs` playmode
// operation for long ones). Cancel, exiting play mode and a domain reload all
// release every held finger (Canceled phase).
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    [BridgeToolType]
    public static class GestureTools
    {
        internal const string ToolName = "unity_open_mcp_inputsim_gesture";

        [BridgeTool(ToolName,
            Title = "Input Simulation: Gesture Over Real Frames",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = false,
            IdempotentHint = false,
            DestructiveHint = false,
            Lifecycle = LifecyclePolicy.None,
            Group = "input-simulation")]
        [System.ComponentModel.Description(
            "Start a multi-frame touch gesture (keyframed finger paths interpolated every " +
            "play-mode frame) on the shared play-mode scheduler. Returns an operation id; " +
            "the MCP server polls it to a terminal trace. Play-mode only; gate-free.")]
        public static string Gesture(
            string fingers = null,
            string coords = "screen",
            string time = "realtime",
            string backend = "auto",
            string operation_id = null)
        {
            if (!EditorApplication.isPlaying)
                return TouchTools.Error("play_mode_required",
                    "Gestures run over real play-mode frames. Call " +
                    "unity_open_mcp_editor_set_state(state=\"play\") first.");

            var plan = GesturePlan.Parse(fingers, coords, time, out var code, out var message);
            if (plan == null) return TouchTools.Error(code, message);

            var route = InputRouting.Decide(InputEnvironment.Detect(), backend, plan.FingerIds());
            if (!route.Ok) return route.ErrorJson();

            if (!plan.ResolvePoints(GameViewWindows.MainGameViewSize(), ResolveTarget, out code, out message))
                return TouchTools.Error(code, message);

            var busy = PlayModeScheduler.RunningWithResource(TouchTools.InputResource);
            if (busy != null && busy != operation_id)
                return PlayModeScheduler.ErrorJson("playmode_operation_busy",
                    "Play-mode operation " + busy + " is still driving input. Wait for it or cancel it first.",
                    "\"operationId\":" + BridgeJson.EscapeString(busy));

            bool focused = GameViewWindows.EnsureGameViewFocusForInputSystem(route);
            var op = new GestureOperation(plan, route, focused);
            var snapshot = PlayModeScheduler.Start(op, operation_id, out var error);
            if (snapshot == null) return error;

            var sb = new StringBuilder(384);
            sb.Append("{\"status\":\"ok\",\"operation\":").Append(snapshot);
            sb.Append(",\"backend\":").Append(route.BackendJson());
            sb.Append(",\"skipped\":").Append(route.SkippedJson());
            sb.Append(",\"durationMs\":").Append(PlayModeScheduler.Num(plan.DurationMs));
            sb.Append(",\"focusedGameView\":").Append(focused ? "true" : "false");
            sb.Append(",\"agentNextSteps\":[").Append(BridgeJson.EscapeString(
                "The gesture runs over real frames. Through MCP this call already waits for the terminal trace " +
                "(or returns a jobs id for long gestures); a direct bridge caller polls POST /playmode-operations " +
                "{\"action\":\"status\",\"operation_id\":\"" + op.Id + "\"}.")).Append("]}");
            return sb.ToString();
        }

        private static (bool Ok, Vector2 Point, string Code, string Message) ResolveTarget(string target)
        {
            return DeviceTargets.TryResolveTarget(target, out var point, out var err)
                ? (true, point, null, null)
                : (false, default(Vector2), err.Code, err.Message);
        }
    }

    internal sealed class GestureOperation : PlayModeOperation
    {
        internal const int MaxTrace = 200;
        internal const int MaxErrors = 20;

        private sealed class FingerRun
        {
            public GestureFinger Finger;
            public bool Down, Up;
            public Vector2 Last;
            public int TracedKeyframe = -1;
        }

        private struct TraceEntry
        {
            public int Finger, Keyframe, Frame;
            public double AtMs, ElapsedMs;
            public Vector2 Point;
            public string Phase, UiTopHit;
        }

        private readonly GesturePlan plan;
        private readonly RouteDecision route;
        private readonly bool focusedGameView;
        private readonly List<FingerRun> runs = new List<FingerRun>();
        private readonly List<TraceEntry> trace = new List<TraceEntry>();
        private readonly List<string> errors = new List<string>();
        private int traceDropped, errorsDropped;
        private int startFrame = -1, lastFrame = -1, endedFrame = -1, frames;
        private double elapsedMs;
        private bool logging;

        internal GestureOperation(GesturePlan plan, RouteDecision route, bool focusedGameView)
        {
            this.plan = plan;
            this.route = route;
            this.focusedGameView = focusedGameView;
            TimeBase = plan.Time;
            ExpectedDurationMs = plan.DurationMs;
            foreach (var finger in plan.Fingers) runs.Add(new FingerRun { Finger = finger });
        }

        public override string Kind => GestureTools.ToolName;
        public override string ExclusiveResource => TouchTools.InputResource;

        protected internal override bool Tick(PlayModeFrame frame)
        {
            if (startFrame < 0)
            {
                startFrame = frame.FrameCount;
                // Subscribed on the first tick so a refused start never leaks a handler.
                Application.logMessageReceived += OnLog;
                logging = true;
            }
            lastFrame = frame.FrameCount;
            frames++;
            elapsedMs = frame.ElapsedMs;

            bool allUp = true;
            foreach (var run in runs)
            {
                var sample = GesturePlan.Sample(run.Finger, frame.ElapsedMs);
                if (sample.State == FingerState.NotStarted) { allUp = false; continue; }
                if (!run.Down)
                {
                    // Began at the down keyframe's point, whatever the frame timing.
                    var downPoint = run.Finger.Path[0].Point;
                    Inject(run.Finger.Id, DeviceTouchPhase.Began, downPoint);
                    run.Down = true;
                    run.Last = downPoint;
                    TraceKeyframes(run, 0, frame, downPoint, "began");
                    // One phase per finger per frame: a second SimulateTouch in
                    // the same frame would overwrite Began. Later keyframes (even
                    // an `up` shorter than one frame) follow on the next ticks.
                    allUp = false;
                    continue;
                }
                if (run.Up) continue;
                if (sample.State == FingerState.Up)
                {
                    Inject(run.Finger.Id, DeviceTouchPhase.Ended, sample.Point);
                    run.Up = true;
                    run.Last = sample.Point;
                    TraceKeyframes(run, sample.Keyframe, frame, sample.Point, "ended");
                    continue;
                }
                allUp = false;
                var phase = (sample.Point - run.Last).sqrMagnitude > 0.0001f ? DeviceTouchPhase.Moved : DeviceTouchPhase.Stationary;
                Inject(run.Finger.Id, phase, sample.Point);
                run.Last = sample.Point;
                TraceKeyframes(run, sample.Keyframe, frame, sample.Point, TouchSequence.PhaseName(phase));
            }

            Phase = allUp ? "settling" : "running";
            if (!allUp) return false;
            // One more frame after the last release so polling code and input
            // modules have consumed it before the result is reported.
            if (endedFrame < 0) { endedFrame = frame.FrameCount; return false; }
            return frame.FrameCount > endedFrame;
        }

        private void Inject(int finger, DeviceTouchPhase phase, Vector2 point)
        {
            if (route.Uses(InputRouting.LegacyTouch)) EditorInputAdapter.SimulateTouch(finger, point, phase);
            if (route.Uses(InputRouting.InputSystem) && InputBackends.InputSystem != null)
                InputBackends.InputSystem.Queue(finger, phase, point);
        }

        private void TraceKeyframes(FingerRun run, int reached, PlayModeFrame frame, Vector2 point, string phase)
        {
            while (run.TracedKeyframe < reached)
            {
                run.TracedKeyframe++;
                if (trace.Count >= MaxTrace) { traceDropped++; continue; }
                string hit = null;
                try { hit = InputBackends.Ui?.TopHitPath(point); } catch { hit = null; }
                trace.Add(new TraceEntry
                {
                    Finger = run.Finger.Id,
                    Keyframe = run.TracedKeyframe,
                    AtMs = run.Finger.Path[run.TracedKeyframe].AtMs,
                    ElapsedMs = frame.ElapsedMs,
                    Frame = frame.FrameCount,
                    Point = point,
                    Phase = phase,
                    UiTopHit = hit,
                });
            }
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (errors.Count >= MaxErrors) { errorsDropped++; return; }
            var text = type + ": " + condition;
            if (type == LogType.Exception && !string.IsNullOrEmpty(stackTrace))
            {
                var firstLine = stackTrace.Split('\n')[0];
                text += " @ " + firstLine;
            }
            errors.Add(text.Length > 500 ? text.Substring(0, 500) : text);
        }

        protected internal override void Cleanup(PlayModeEndReason reason)
        {
            if (logging)
            {
                Application.logMessageReceived -= OnLog;
                logging = false;
            }
            foreach (var run in runs)
            {
                if (!run.Down || run.Up) continue;
                try { Inject(run.Finger.Id, DeviceTouchPhase.Canceled, run.Last); }
                catch (Exception) { /* backend gone (reload) — the adapter's own reload hook releases */ }
                run.Up = true;
            }
        }

        protected internal override string BuildResult(PlayModeEndReason reason, string error)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"kind\":").Append(BridgeJson.EscapeString(Kind));
            sb.Append(",\"reason\":").Append(BridgeJson.EscapeString(PlayModeScheduler.ReasonName(reason)));
            if (error != null) sb.Append(",\"error\":").Append(BridgeJson.EscapeString(error));
            sb.Append(",\"backend\":").Append(route.BackendJson());
            sb.Append(",\"skipped\":").Append(route.SkippedJson());
            sb.Append(",\"coords\":").Append(BridgeJson.EscapeString(plan.Coords));
            sb.Append(",\"time\":").Append(BridgeJson.EscapeString(plan.Time == PlayModeTimeBase.Gameplay ? "gameplay" : "realtime"));
            sb.Append(",\"durationMs\":").Append(PlayModeScheduler.Num(plan.DurationMs));
            sb.Append(",\"elapsedMs\":").Append(PlayModeScheduler.Num(elapsedMs));
            sb.Append(",\"frames\":{\"start\":").Append(startFrame).Append(",\"end\":").Append(lastFrame).Append(",\"ticks\":").Append(frames).Append('}');
            sb.Append(",\"fingers\":").Append(plan.Fingers.Count);
            sb.Append(",\"focusedGameView\":").Append(focusedGameView ? "true" : "false");
            if (route.Uses(InputRouting.LegacyTouch))
                sb.Append(",\"mouseEmulation\":").Append(route.Environment.SimulateMouseWithTouches ? "true" : "false");
            sb.Append(",\"released\":true");
            sb.Append(",\"trace\":[");
            for (int i = 0; i < trace.Count; i++)
            {
                var t = trace[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"finger\":").Append(t.Finger);
                sb.Append(",\"keyframe\":").Append(t.Keyframe);
                sb.Append(",\"atMs\":").Append(PlayModeScheduler.Num(t.AtMs));
                sb.Append(",\"elapsedMs\":").Append(PlayModeScheduler.Num(t.ElapsedMs));
                sb.Append(",\"frame\":").Append(t.Frame);
                sb.Append(",\"point\":[").Append(TouchTools.Num(t.Point.x)).Append(',').Append(TouchTools.Num(t.Point.y)).Append(']');
                sb.Append(",\"phase\":").Append(BridgeJson.EscapeString(t.Phase));
                sb.Append(",\"uiTopHit\":").Append(t.UiTopHit == null ? "null" : BridgeJson.EscapeString(t.UiTopHit));
                sb.Append('}');
            }
            sb.Append("],\"traceDropped\":").Append(traceDropped);
            sb.Append(",\"errors\":[");
            for (int i = 0; i < errors.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(errors[i]));
            }
            sb.Append("],\"errorsDropped\":").Append(errorsDropped);
            sb.Append('}');
            return sb.ToString();
        }
    }
}
