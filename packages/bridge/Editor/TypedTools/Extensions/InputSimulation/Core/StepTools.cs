// Input simulation — frame advance tool (M1 in feedback-input.md).
//
// The missing middle of the play-mode testing loop. EditorApplication.Step() is
// synchronous: it runs one full player-loop frame inline (Update / FixedUpdate /
// coroutines / render) before returning. A tight loop therefore pumps N frames
// within one tool dispatch and returns immediately after — it does NOT suffer
// the QueuePlayerLoopUpdate + Thread.Sleep deadlock documented in
// ProfilerCaptureFrameTool (the sleeping thread is the one that would service
// the queued update). Play-mode only: Step is a no-op in edit mode, so the guard
// refuses with play_mode_required before looping.
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    [BridgeToolType]
    public static class StepTools
    {
        private const int MaxFramesPerCall = 60;

        [BridgeTool("unity_open_mcp_inputsim_step",
            Title = "Input Simulation: Step Frames",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = false,
            IdempotentHint = false,
            DestructiveHint = false,
            Lifecycle = LifecyclePolicy.None,
            Group = "input-simulation")]
        [System.ComponentModel.Description(
            "Advance the play-mode player loop by N frames via EditorApplication.Step. " +
            "Play-mode only; gate-free.")]
        public static string Step(int frames = 1, int settle_ms = 0)
        {
            if (!EditorApplication.isPlaying)
                return InputSimulationJson.Error("play_mode_required",
                    "Frame advance requires play mode. Call " +
                    "unity_open_mcp_editor_set_state(state=\"play\") first. " +
                    "(EditorApplication.Step is a no-op in edit mode.)");

            if (frames < 0) frames = 0;
            if (frames > MaxFramesPerCall) frames = MaxFramesPerCall;
            if (settle_ms < 0) settle_ms = 0;

            // Save editor pause state so the tool leaves it as-found. Stepping is
            // most predictable while paused (the editor won't auto-advance between
            // our Step calls), but forcing pause would surprise callers; instead
            // we Step in whatever state we found and restore it exactly. Frame-
            // stepping pauses the editor, so without the restore in `finally` any
            // inputsim_step would leave the game frozen for the next dispatch.
            // (feedback S4)
            bool initialPaused = EditorApplication.isPaused;

            int advanced = 0;
            bool steppedPaused = initialPaused;
            try
            {
                for (int i = 0; i < frames; i++)
                {
                    EditorApplication.Step();
                    advanced++;
                }

                // Optional single extra tick — lets one more render land before a
                // screenshot without burning a full Step frame budget. This is a
                // request, not a synchronous pump, so it only helps when the agent
                // screenshots immediately after (the next EditorApplication.update
                // processes it).
                if (settle_ms > 0)
                    EditorApplication.QueuePlayerLoopUpdate();

                steppedPaused = EditorApplication.isPaused;
            }
            catch (System.Exception e)
            {
                return InputSimulationJson.Error("step_failed",
                    $"Frame advance failed after {advanced} frame(s): {e.Message}");
            }
            finally
            {
                // Restore the as-found pause state so frame-stepping does not leave
                // the editor paused for subsequent dispatches.
                EditorApplication.isPaused = initialPaused;
            }

            var sb = new StringBuilder(160);
            sb.Append("\"framesRequested\":").Append(frames);
            sb.Append(",\"framesAdvanced\":").Append(advanced);
            sb.Append(",\"settleMs\":").Append(settle_ms);
            sb.Append(",\"initialPaused\":").Append(initialPaused ? "true" : "false");
            // steppedPaused is the state observed right after the last Step(),
            // before the finally block restored initialPaused — the editor ends at
            // initialPaused; this reports what the stepping itself produced.
            sb.Append(",\"steppedPaused\":").Append(steppedPaused ? "true" : "false");
            sb.Append(",\"pausedRestored\":true");
            sb.Append(",\"maxFramesPerCall\":").Append(MaxFramesPerCall);
            return InputSimulationJson.Ok(sb.ToString());
        }
    }
}
