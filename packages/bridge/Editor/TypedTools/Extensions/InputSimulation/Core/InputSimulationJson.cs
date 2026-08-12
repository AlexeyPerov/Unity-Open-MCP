// Input simulation embedded domain — uGUI pointer half.
//
// Compile-gated on com.unity.ugui (UNITY_OPEN_MCP_EXT_INPUTSIM_UGUI, set by the
// owning sub-asmdef's versionDefines). JSON envelope + escape helpers, plus the
// pointer/probe response builder carrying the honesty fields from feedback-input.md
// (interactable, blockedBy, dropTarget, dropLanded).
//
// Self-contained per the embedded-domain convention (each domain evolves its own
// helper — see UIJson / AudioJson / LightingJson / InputSystemJson). The Input
// System device half carries its own copy so the two sub-asmdefs stay independent.
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    internal static class InputSimulationJson
    {
        public static string Ok(string body)
            => "{\"status\":\"ok\"," + (body ?? "") + "}";

        public static string Error(string code, string message)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append("}}");
            return sb.ToString();
        }

        // Ambiguous-target error carrying candidate paths so the agent can pick.
        public static string ErrorWithCandidates(string code, string message, List<string> candidates)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append(",\"candidates\":[");
            if (candidates != null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Esc(candidates[i]));
                }
            }
            sb.Append("]}}");
            return sb.ToString();
        }

        public static string Esc(string s)
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

        public static string Num(float v)
            => v.ToString("0.##", CultureInfo.InvariantCulture);

        // Pointer response with all honesty fields. Null blockedBy / dropTarget
        // are emitted as JSON null (omitting them would read as "not computed").
        public static string BuildPointerOk(PointerOutcome o)
        {
            var sb = new StringBuilder(320);
            sb.Append("\"target\":").Append(Esc(o.TargetPath ?? ""));
            sb.Append(",\"screenPoint\":[").Append(Num(o.ScreenPoint.x)).Append(',').Append(Num(o.ScreenPoint.y)).Append(']');
            sb.Append(",\"hasHandler\":").Append(o.HasHandler ? "true" : "false");
            sb.Append(",\"interactable\":").Append(o.Interactable ? "true" : "false");
            sb.Append(",\"eventSystem\":").Append(o.EventSystem ? "true" : "false");
            sb.Append(",\"blockedBy\":");
            sb.Append(o.BlockedBy == null ? "null" : Esc(o.BlockedBy));
            if (o.IsDrag)
            {
                sb.Append(",\"dropTarget\":");
                sb.Append(o.DropTarget == null ? "null" : Esc(o.DropTarget));
                sb.Append(",\"dropLanded\":").Append(o.DropLanded ? "true" : "false");
            }
            sb.Append(",\"dispatched\":[");
            for (int i = 0; i < o.Dispatched.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Esc(o.Dispatched[i]));
            }
            sb.Append(']');
            return Ok(sb.ToString());
        }
    }

    // Outcome of a single pointer interaction, carried to the JSON builder.
    internal sealed class PointerOutcome
    {
        public string TargetPath;
        public Vector2 ScreenPoint;
        public bool HasHandler;
        public bool Interactable = true;   // P2: false when disabled / CanvasGroup-blocked
        public bool EventSystem = true;    // false only on the no_event_system path (returned earlier)
        public string BlockedBy;           // P2: top raycast hit when it occludes the target, else null
        public bool IsDrag;
        public string DropTarget;          // P1: resolved drop target path, null when not a drag / nothing hit
        public bool DropLanded;            // P1: did an IDropHandler receive the drop?
        public List<string> Dispatched = new List<string>();
    }
}
