// Synchronous, frame-stepped touch sequences across the routed backends.
//
// Used by inputsim_touch and by inputsim_pointer (routing: raycast_real). Each
// step queues one phase into every routed backend, then advances frames so
// polling code and input modules observe it:
//   - legacy_touch: the queued touch is consumed by the next frame's legacy input
//     update, so phases need at least one frame between them (otherwise they
//     collapse) and one frame after the final phase. EditorApplication.Step runs
//     that frame inline.
//   - input_system: the backend's own framed stepping (input update before
//     gameplay Update, restored afterwards); 0 frames processes immediately.
// With both backends the Input System stepping drives the frames, and the legacy
// touch is consumed natively inside the same frames. The caller's pause state is
// always restored.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    internal static class TouchSequence
    {
        internal struct Step
        {
            public DeviceTouchPhase Phase;
            public Vector2 Point;
            public int FramesAfter;

            public Step(DeviceTouchPhase phase, Vector2 point, int framesAfter)
            {
                Phase = phase;
                Point = point;
                FramesAfter = framesAfter;
            }
        }

        internal static string PhaseName(DeviceTouchPhase phase)
        {
            switch (phase)
            {
                case DeviceTouchPhase.Began: return "began";
                case DeviceTouchPhase.Moved: return "moved";
                case DeviceTouchPhase.Stationary: return "stationary";
                case DeviceTouchPhase.Ended: return "ended";
                default: return "canceled";
            }
        }

        // Runs the steps; returns the dispatched phase names and the frames advanced.
        internal static List<string> Run(RouteDecision route, int finger, IList<Step> steps, out int framesAdvanced)
        {
            bool legacy = route.Uses(InputRouting.LegacyTouch);
            bool inputSystem = route.Uses(InputRouting.InputSystem) && InputBackends.InputSystem != null;
            var dispatched = new List<string>(steps.Count);
            framesAdvanced = 0;
            bool paused = EditorApplication.isPaused;
            bool open = false; // a Began went out (to any backend) without its Ended
            var point = default(Vector2);
            try
            {
                if (inputSystem) InputBackends.InputSystem.UpdateNow();
                foreach (var step in steps)
                {
                    point = step.Point;
                    if (step.Phase == DeviceTouchPhase.Began) open = true;
                    if (legacy) EditorInputAdapter.SimulateTouch(finger, step.Point, step.Phase);
                    if (inputSystem) InputBackends.InputSystem.Queue(finger, step.Phase, step.Point);
                    if (step.Phase == DeviceTouchPhase.Ended || step.Phase == DeviceTouchPhase.Canceled) open = false;
                    dispatched.Add(PhaseName(step.Phase));
                    int frames = step.FramesAfter < 0 ? 0 : step.FramesAfter;
                    if (inputSystem) InputBackends.InputSystem.StepFrames(frames);
                    else for (int i = 0; i < frames; i++) EditorApplication.Step();
                    framesAdvanced += frames;
                }
                if (inputSystem) InputBackends.InputSystem.UpdateNow();
            }
            catch
            {
                // A failed sequence never leaves the finger pressed.
                if (open) CancelOpenTouch(legacy, inputSystem, finger, point);
                throw;
            }
            finally
            {
                EditorApplication.isPaused = paused;
            }
            return dispatched;
        }

        // Best effort on a failure path: the caller reports the original error.
        private static void CancelOpenTouch(bool legacy, bool inputSystem, int finger, Vector2 point)
        {
            if (legacy)
            {
                try { EditorInputAdapter.SimulateTouch(finger, point, DeviceTouchPhase.Canceled); }
                catch (System.Exception) { /* entry point gone — nothing was held */ }
            }
            if (inputSystem)
            {
                try
                {
                    InputBackends.InputSystem.Queue(finger, DeviceTouchPhase.Canceled, point);
                    InputBackends.InputSystem.UpdateNow();
                }
                catch (System.Exception) { /* no Touchscreen left to release */ }
            }
        }

        // Legacy phases collapse without a frame between them.
        internal static int FramesBetween(RouteDecision route, int requested)
        {
            if (requested < 0) requested = 0;
            if (requested > 60) requested = 60;
            return route.Uses(InputRouting.LegacyTouch) && requested < 1 ? 1 : requested;
        }

        // Frames after the final phase: legacy needs one so the release is
        // consumed (and an input module can fire its click); the Input System
        // path processes the final phase immediately, as before.
        internal static int FramesAfterLast(RouteDecision route)
            => route.Uses(InputRouting.LegacyTouch) ? 1 : 0;
    }
}
