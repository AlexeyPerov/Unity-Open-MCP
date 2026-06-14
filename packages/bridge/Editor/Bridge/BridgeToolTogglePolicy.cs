// Runtime per-tool enable/disable policy for the bridge dispatcher.
//
// Disable state is persisted in `.unity-open-mcp/settings.json` via `BridgeProjectSettings` and
// survives domain reload. Both hardcoded meta-tools and registry-discovered typed tools
// share the same gate: `IsDisabled(toolName)` is checked in `BridgeHttpServer` before any
// dispatch path. A disabled call returns a structured `tool_disabled` error so agents see
// an explicit failure rather than a silent no-op.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    public static class BridgeToolTogglePolicy
    {
        public const string DisabledErrorCode = "tool_disabled";

        public static event Action Changed;

        public static IReadOnlyCollection<string> DisabledTools => BridgeProjectSettings.DisabledTools;

        public static bool IsDisabled(string toolName)
        {
            return BridgeProjectSettings.IsDisabled(toolName);
        }

        public static void SetDisabled(string toolName, bool disabled)
        {
            if (string.IsNullOrEmpty(toolName)) return;

            var current = new HashSet<string>(BridgeProjectSettings.DisabledTools);
            var changed = disabled ? current.Add(toolName) : current.Remove(toolName);
            if (!changed) return;

            BridgeProjectSettings.SetDisabledTools(current);
            try { Changed?.Invoke(); } catch { }
        }

        public static void Toggle(string toolName)
        {
            SetDisabled(toolName, !IsDisabled(toolName));
        }

        public static void Clear()
        {
            if (DisabledTools.Count == 0) return;
            BridgeProjectSettings.SetDisabledTools(Array.Empty<string>());
            try { Changed?.Invoke(); } catch { }
        }

        public static string BuildDisabledErrorJson(string toolName)
        {
            var msg = $"Tool '{toolName}' is disabled in the Unity Open MCP Bridge runtime. " +
                      "Re-enable it in the Tools tab (or remove it from `.unity-open-mcp/settings.json` `disabledTools`) and retry.";
            return $"{{\"error\":{{\"code\":\"{DisabledErrorCode}\",\"message\":\"{EscapeStringContent(msg)}\",\"tool\":\"{EscapeStringContent(toolName)}\",\"hint\":\"Enable the tool in the Unity Open MCP Bridge window Tools tab.\"}}}}";
        }

        static string EscapeStringContent(string s)
        {
            if (s == null) return "";
            var sb = new System.Text.StringBuilder(s.Length + 4);
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
            return sb.ToString();
        }
    }
}
