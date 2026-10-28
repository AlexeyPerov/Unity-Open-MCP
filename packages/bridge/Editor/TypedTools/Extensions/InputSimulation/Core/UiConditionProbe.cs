// Registers the uGUI screen model as the `ui` condition probe for
// unity_open_mcp_playtest_wait / capture_series `start_on` (bridge root
// assembly, which cannot reference uGUI). The query semantics are ui_snapshot's:
// text_contains is normalised and case-insensitive over a node's display text
// (a control's label; a label folded into its control counts once, as the
// control), path matches a node path or a trailing path suffix, and visibility
// is the snapshot's (`on_screen` = visible).
using System;
using System.Text;
using UnityOpenMcpBridge.Playtest;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    [UnityEditor.InitializeOnLoad]
    internal static class UiConditionProbeRegistration
    {
        static UiConditionProbeRegistration()
        {
            PlaytestProbes.Ui = new UiConditionProbe();
        }
    }

    internal sealed class UiConditionProbe : IUiConditionProbe
    {
        public int Count(string textContains, string path, bool visible, out string firstMatchJson)
        {
            firstMatchJson = null;
            string needle = string.IsNullOrEmpty(textContains) ? null : UiText.Normalize(textContains).ToLowerInvariant();
            string suffix = string.IsNullOrEmpty(path) ? null : path.Trim('/');
            int count = 0;
            foreach (var n in UiScreenModel.Collect(GameViewWindows.MainGameViewSize()))
            {
                if (n.Visible != visible) continue;
                // A label folded into its control counts once, as the control
                // (as ui_snapshot lists it) — unless a path names the label node.
                if (suffix == null && n.FoldedIntoOwner) continue;
                if (suffix != null && n.Path != suffix && !n.Path.EndsWith("/" + suffix, StringComparison.Ordinal)) continue;
                if (needle != null && (n.Text == null || !n.Text.ToLowerInvariant().Contains(needle))) continue;
                if (count == 0)
                {
                    var sb = new StringBuilder(160);
                    sb.Append("{\"path\":").Append(InputSimulationJson.Esc(n.Path));
                    sb.Append(",\"role\":").Append(InputSimulationJson.Esc(n.Role));
                    sb.Append(",\"text\":").Append(InputSimulationJson.Esc(UiText.Clip(n.Text, 80)));
                    sb.Append(",\"visibility\":").Append(InputSimulationJson.Esc(n.Visibility));
                    sb.Append('}');
                    firstMatchJson = sb.ToString();
                }
                count++;
            }
            return count;
        }
    }
}
