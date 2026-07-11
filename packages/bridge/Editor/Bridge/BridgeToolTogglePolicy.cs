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

        // Bulk policy helpers (M29 Plan 5 — Tools tab bulk actions). These take
        // a tool-name set (the filtered view the operator is acting on) and a
        // target state, then reconcile the persisted disabled list so the
        // change is one settings.json write + one Changed event instead of N.
        //
        //   SetEnabled(filtered, true)  — re-enable the given tools (remove each
        //                                 from the disabled list).
        //   SetEnabled(filtered, false) — disable the given tools (add each to
        //                                 the disabled list, de-duped).
        //
        // Names not in `filtered` keep their current state, so "Enable all" /
        // "Disable all" on a search-narrowed list only touches the visible set.
        // Returns true when the disabled list actually changed.
        public static bool SetEnabled(IEnumerable<string> filtered, bool enabled)
        {
            if (filtered == null) return false;
            var current = new HashSet<string>(BridgeProjectSettings.DisabledTools);
            var beforeCount = current.Count;
            foreach (var name in filtered)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (enabled) current.Remove(name);
                else current.Add(name);
            }
            if (current.Count == beforeCount && SameSet(current, BridgeProjectSettings.DisabledTools))
                return false;
            BridgeProjectSettings.SetDisabledTools(current);
            try { Changed?.Invoke(); } catch { }
            return true;
        }

        // Bulk group action (M29 Plan 5). Disables or enables every tool whose
        // catalog Group matches `group`. Null/empty group matches the synthetic
        // "(always visible)" bucket — pass the catalog group verbatim.
        public static bool SetGroupEnabled(IEnumerable<string> toolNamesInGroup, bool enabled)
        {
            return SetEnabled(toolNamesInGroup, enabled);
        }

        private static bool SameSet(HashSet<string> a, IReadOnlyCollection<string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var x in b)
            {
                if (!a.Contains(x)) return false;
            }
            return true;
        }

        public static string BuildDisabledErrorJson(string toolName)
        {
            var msg = $"Tool '{toolName}' is disabled in the Unity Open MCP Bridge runtime. " +
                      "Re-enable it in the Tools tab (or remove it from `.unity-open-mcp/settings.json` `disabledTools`) and retry.";
            return $"{{\"error\":{{\"code\":\"{DisabledErrorCode}\",\"message\":\"{EscapeStringContent(msg)}\",\"tool\":\"{EscapeStringContent(toolName)}\",\"hint\":\"Enable the tool in the Unity Open MCP Bridge window Tools tab.\"}}}}";
        }

        private static string EscapeStringContent(string s)
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
