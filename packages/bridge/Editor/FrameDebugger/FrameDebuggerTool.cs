using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.FrameDebugger
{
    // M20 Plan 1 / T20.1.3 — Frame Debugger meta-tool.
    //
    // Three read-only actions expose Unity's Frame Debugger to agents:
    //   enable  — open the Frame Debugger window and start capturing
    //   disable — stop capturing
    //   list    — enumerate the draw calls of the currently-debugged frame
    //
    // The Frame Debugger is an Editor-internal surface: its public entry point
    // (`UnityEditorInternal.FrameDebuggerUtility`) moved into a child namespace
    // on Unity 6 (`FrameDebuggerInternal.FrameDebuggerUtility`) and its event-
    // data API changed shape (bool GetFrameEventData(int, data) vs the older
    // FrameDebuggerEventData GetFrameEventData(int)). All of that is isolated
    // behind a single reflection helper (FrameDebuggerApi) so future Unity API
    // drift only touches one place.
    //
    // enable/disable is a non-mutating Editor state change, so the tool routes
    // read-only (Gate = Off). The response still records `windowOpened` so
    // agents know they left Editor UI in a changed state. No assets/scenes are
    // modified.
    [BridgeToolType]
    public class Tool_FrameDebugger
    {
        [BridgeTool("unity_senses_frame_debugger", Title = "Frame Debugger",
            IsMutating = false, ReadOnlyHint = true, Gate = GateMode.Off, Lifecycle = LifecyclePolicy.None,
            Group = "agent-senses")]
        [System.ComponentModel.Description(
            "Control Unity's Frame Debugger and list the draw calls of the " +
            "currently-debugged frame. action=enable opens the Frame Debugger " +
            "window and starts capturing; action=disable stops capturing; " +
            "action=list returns the draw-call list (shader, pass, material, " +
            "render target, vertex/index/instance counts per call). Enable is " +
            "a non-mutating Editor state change — no assets/scenes are touched " +
            "and the gate is off, but the response reports windowOpened so the " +
            "agent knows Editor UI may have changed. Requires a live Unity " +
            "Editor connection.")]
        public string FrameDebugger(
            string action = "list",
            int max_draw_calls = 256)
        {
            var act = (action ?? "list").ToLowerInvariant();
            try
            {
                switch (act)
                {
                    case "enable":
                        return Enable();
                    case "disable":
                        return Disable();
                    case "list":
                        return List(max_draw_calls);
                    default:
                        return ErrorJson("validation_error",
                            $"Unknown action '{action}'. Use 'enable', 'disable', or 'list'.");
                }
            }
            catch (Exception e)
            {
                return ErrorJson("execution_error", e.Message);
            }
        }

        // ============================ actions ============================

        private string Enable()
        {
            if (!FrameDebuggerApi.Available)
                return ErrorJson("frame_debugger_unavailable",
                    "FrameDebuggerUtility was not found via reflection. The Frame " +
                    "Debugger is not available in this Unity build.");

            // Open the Frame Debugger window. The window must exist for event
            // capture to populate; GetWindow focuses it as a side effect.
            bool openedDuringCall;
            try
            {
                openedDuringCall = FrameDebuggerApi.OpenWindowIfNeeded();
            }
            catch (Exception e)
            {
                return ErrorJson("window_open_failed",
                    $"Failed to open the Frame Debugger window: {e.Message}");
            }

            // The Frame Debugger only captures a frame while the editor is not
            // mid-render; SetEnabled(true) issues the request and Unity captures
            // the next frame. No play/pause precondition is enforced here — the
            // tool is read-only and we surface the captured event count.
            try
            {
                FrameDebuggerApi.SetEnabled(true);
            }
            catch (Exception e)
            {
                return ErrorJson("enable_failed",
                    $"Failed to enable the Frame Debugger: {e.Message}");
            }

            int eventCount = FrameDebuggerApi.GetEventCount();
            return BuildEnableJson(true, eventCount, openedDuringCall);
        }

        private string Disable()
        {
            if (!FrameDebuggerApi.Available)
                return ErrorJson("frame_debugger_unavailable",
                    "FrameDebuggerUtility was not found via reflection. The Frame " +
                    "Debugger is not available in this Unity build.");

            try
            {
                FrameDebuggerApi.SetEnabled(false);
            }
            catch (Exception e)
            {
                return ErrorJson("disable_failed",
                    $"Failed to disable the Frame Debugger: {e.Message}");
            }

            return BuildEnableJson(false, 0, false);
        }

        private string List(int maxDrawCalls)
        {
            if (!FrameDebuggerApi.Available)
                return ErrorJson("frame_debugger_unavailable",
                    "FrameDebuggerUtility was not found via reflection. The Frame " +
                    "Debugger is not available in this Unity build.");

            if (maxDrawCalls <= 0) maxDrawCalls = 256;

            int total = FrameDebuggerApi.GetEventCount();
            if (total == 0)
                return ErrorJson("no_captured_frame",
                    "The Frame Debugger has no captured events. Call " +
                    "action=enable first (and ensure the editor has rendered a frame).");

            var events = new List<Dictionary<string, object>>();
            int cap = Mathf.Min(total, maxDrawCalls);
            for (int i = 0; i < cap; i++)
            {
                var entry = FrameDebuggerApi.GetEvent(i);
                if (entry != null) events.Add(entry);
            }

            return BuildListJson(total, cap, events);
        }

        // ============================ JSON builders ============================

        private static string BuildEnableJson(bool enabled, int eventCount, bool windowOpened)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"status\":\"ok\",");
            sb.Append("\"enabled\":").Append(enabled ? "true" : "false").Append(',');
            sb.Append("\"eventCount\":").Append(eventCount).Append(',');
            sb.Append("\"windowOpened\":").Append(windowOpened ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildListJson(int total, int returned, List<Dictionary<string, object>> events)
        {
            var sb = new StringBuilder(4096);
            sb.Append('{');
            sb.Append("\"status\":\"ok\",");
            sb.Append("\"totalDrawCalls\":").Append(total).Append(',');
            sb.Append("\"returnedCount\":").Append(events.Count).Append(',');
            sb.Append("\"truncated\":").Append(returned < total ? "true" : "false").Append(',');
            if (returned < total)
                sb.Append("\"truncatedCount\":").Append(total - returned).Append(',');
            sb.Append("\"drawCalls\":[");
            for (int i = 0; i < events.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendEntry(sb, events[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendEntry(StringBuilder sb, Dictionary<string, object> entry)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in entry)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kv.Key).Append("\":");
                AppendValue(sb, kv.Value);
            }
            sb.Append('}');
        }

        private static void AppendValue(StringBuilder sb, object value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }
            if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
                return;
            }
            if (value is int || value is long || value is float || value is double)
            {
                // Integers render without a trailing decimal; floats/doubles
                // use invariant culture so output is stable across locales.
                if (value is float f) sb.Append(f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                else if (value is double d) sb.Append(d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                else sb.Append(value);
                return;
            }
            // Fallback: string-encode everything else.
            sb.Append(Esc(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
        }

        private static string ErrorJson(string code, string message)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append("}}");
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
