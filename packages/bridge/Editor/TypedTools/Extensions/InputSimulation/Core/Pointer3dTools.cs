// Input simulation — legacy / 3D world-space path (M3 in feedback-input.md).
//
// The uGUI tool dispatches through ExecuteEvents and so cannot reach world-space
// gameplay reading legacy `OnMouseDown` / `OnMouseUp` / `OnMouseUpAsButton`, or
// any physics-raycast-driven interaction. The review's stated reason for leaving
// this uncovered ("no clean API to inject synthetic OS events") only rules out
// OS-level injection; SendMessage sidesteps that the same way ExecuteEvents does
// for uGUI — it calls the magic methods directly. This is the only way to reach
// world-space gameplay in a project running the old Input Manager, which the
// feedback's project-fit table confirms is the target project shape.
//
// Mechanics: Physics.Raycast(cam.ScreenPointToRay(point)) resolves the collider
// under the screen point, then SendMessage("OnMouseDown"/"OnMouseUp"/
// "OnMouseUpAsButton", SendMessageOptions.DontRequireReceiver). For `drag`, the
// press target receives OnMouseDrag once per interpolated step. The dispatch path
// uses only built-in UnityEngine.Physics, but this tool ships in the uGUI sub-
// asmdef (it shares the Core target/screen-resolution helpers), so it compiles
// only when com.unity.ugui is present (UNITY_OPEN_MCP_EXT_INPUTSIM_UGUI) despite
// not consuming EventSystem or uGUI types at the call site. Gate-free, play-mode-only.
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    [BridgeToolType]
    public static class Pointer3dTools
    {
        [BridgeTool("unity_open_mcp_inputsim_pointer3d",
            Title = "Input Simulation: 3D / Legacy Pointer",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = false,
            IdempotentHint = false,
            DestructiveHint = false,
            Lifecycle = LifecyclePolicy.None,
            Group = "input-simulation")]
        [System.ComponentModel.Description(
            "Simulate a world-space / legacy mouse interaction during play mode by " +
            "raycasting from the screen and SendMessage-ing OnMouseDown / OnMouseUp / " +
            "OnMouseUpAsButton / OnMouseDrag. Reaches physics-collider gameplay and " +
            "old-Input-Manager code that inputsim_pointer (uGUI ExecuteEvents) cannot. " +
            "Play-mode only; gate-free. The dispatch path uses only UnityEngine.Physics, " +
            "but the tool ships in the uGUI sub-asmdef and so compiles only when " +
            "com.unity.ugui is present.")]
        public static string Pointer3d(
            string action,
            long? object_id = null,
            string target = null,
            float? screen_x = null,
            float? screen_y = null,
            float? to_x = null,
            float? to_y = null,
            int drag_steps = 8)
        {
            if (!EditorApplication.isPlaying)
                return InputSimulationJson.Error("play_mode_required",
                    "3D pointer simulation requires play mode. Call " +
                    "unity_open_mcp_editor_set_state(state=\"play\") first.");

            // Resolve a screen point: object_id → its screen point, target → its
            // screen point, else explicit screen_x/screen_y.
            Vector2 point;
            GameObject resolvedHint = null;
            if (object_id.HasValue)
            {
                resolvedHint = PointerTargets.FindByInstanceId(object_id.Value);
                if (resolvedHint == null)
                    return InputSimulationJson.Error("object_not_found",
                        $"No live GameObject for object_id {object_id.Value}.");
                point = PointerTargets.ScreenPointOf(resolvedHint);
            }
            else if (!string.IsNullOrEmpty(target))
            {
                var candidates = new List<string>();
                resolvedHint = PointerTargets.FindByPath(target, candidates);
                if (resolvedHint == null && candidates.Count > 0)
                    return InputSimulationJson.ErrorWithCandidates("ambiguous_target",
                        $"Target '{target}' matches {candidates.Count} active GameObjects.",
                        candidates);
                if (resolvedHint == null)
                    return InputSimulationJson.Error("target_not_found",
                        $"No active GameObject found for target '{target}'.");
                point = PointerTargets.ScreenPointOf(resolvedHint);
            }
            else if (screen_x.HasValue && screen_y.HasValue)
            {
                point = new Vector2(screen_x.Value, screen_y.Value);
            }
            else
            {
                return InputSimulationJson.Error("no_target_or_screen_point",
                    "Provide object_id, target (name/path), or both screen_x and screen_y.");
            }

            var cam = Camera.main;
            if (cam == null)
                return InputSimulationJson.Error("no_camera",
                    "Camera.main is null — 3D raycast needs a tagged MainCamera in the scene.");

            // Raycast at the press point. The resolved hint (when provided) is used
            // only to refine the point; the actual target is whatever the ray hits.
            var hit = Physics.Raycast(cam.ScreenPointToRay(point), out var hitInfo);
            if (!hit)
                return InputSimulationJson.Error("no_hit",
                    $"No physics collider under screen point ({point.x}, {point.y}).");

            var dispatched = new List<string>();
            var hitGo = hitInfo.collider.gameObject;
            var hitPath = PointerTargets.BuildPath(hitGo);

            switch (action)
            {
                case "click":
                    Send(hitGo, "OnMouseDown", dispatched);
                    Send(hitGo, "OnMouseUpAsButton", dispatched);
                    Send(hitGo, "OnMouseUp", dispatched);
                    break;
                case "down":
                    Send(hitGo, "OnMouseDown", dispatched);
                    break;
                case "up":
                    Send(hitGo, "OnMouseUp", dispatched);
                    break;
                case "drag":
                    {
                        if (!to_x.HasValue || !to_y.HasValue)
                            return InputSimulationJson.Error("no_drag_to",
                                "3D drag requires to_x and to_y (the end screen point).");
                        // feedback S6 — bound drag steps.
                        if (drag_steps < 1) drag_steps = 1;
                        if (drag_steps > 100) drag_steps = 100;
                        Send(hitGo, "OnMouseDown", dispatched);
                        // OnMouseDrag carries no position argument and handlers read
                        // Input.mousePosition, which synthetic SendMessage dispatch
                        // cannot move (that would require the OS-level injection this
                        // tool exists to avoid). The to_x/to_y endpoint is therefore
                        // recorded for API completeness but does not alter the
                        // per-step dispatch — each OnMouseDrag fires on the press
                        // target as if the mouse held still. For drags that must
                        // convey motion, prefer inputsim_pointer (action: drag).
                        for (int i = 0; i < drag_steps; i++)
                            Send(hitGo, "OnMouseDrag", dispatched);
                        Send(hitGo, "OnMouseUp", dispatched);
                        break;
                    }
                default:
                    return InputSimulationJson.Error("invalid_action",
                        $"Unknown 3D pointer action '{action}'. Valid: click, down, up, drag.");
            }

            var sb = new StringBuilder(192);
            sb.Append("\"target\":").Append(InputSimulationJson.Esc(hitPath));
            sb.Append(",\"screenPoint\":[").Append(point.x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(',').Append(point.y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append(']');
            sb.Append(",\"dispatched\":[");
            for (int i = 0; i < dispatched.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(InputSimulationJson.Esc(dispatched[i]));
            }
            sb.Append(']');
            return InputSimulationJson.Ok(sb.ToString());
        }

        private static void Send(GameObject go, string method, List<string> dispatched)
        {
            go.SendMessage(method, SendMessageOptions.DontRequireReceiver);
            dispatched.Add(method);
        }
    }
}
