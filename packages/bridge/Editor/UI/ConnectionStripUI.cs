using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    // M29 Plan 2 — IMGUI renderer for the connection strip.
    //
    // Takes a pure ConnectionStripModel (built by ConnectionStripBuilder from
    // existing signals) and draws three compact stages at the top of the
    // Status tab: a colored dot + bold label per stage, plus a one-line
    // reason when the stage is degraded. The detailed MCP-connectivity and
    // configure-client panels stay in their foldouts below — the strip is the
    // at-a-glance summary, not a replacement for the diagnostics.
    //
    // Design note (bridge AGENTS.md): tooltips carry the internal-concept
    // explanations; we do NOT stack HelpBoxes next to the strip. Each stage
    // label has a hover tooltip explaining what its signal means.

    internal static class ConnectionStripUI
    {
        private const float DotSize = 10f;
        private const float DotLabelGap = 6f;
        private const float StageGap = 18f;

        // Short hover tooltips for each stage label. Explain the signal in
        // operator terms, no internal jargon / milestone IDs.
        private const string TooltipBridge =
            "The local HTTP listener MCP clients connect to. Green = running, " +
            "yellow = recompiling (dispatch paused), red = stopped.";
        private const string TooltipDiscovery =
            "Whether this Unity instance has published its discovery/heartbeat " +
            "file so the MCP server can auto-find it. Green = lock held, " +
            "yellow = held but busy (compiling/reloading), red/gray = not published.";
        private const string TooltipClient =
            "Whether at least one known AI client config points at this project. " +
            "Green = detected, yellow = none detected, gray = not checked " +
            "(open 'Configure AI client' below).";

        /// <summary>
        /// Draw the three-stage connection strip. Returns the total height
        /// consumed (useful for layout assertions / future UI Toolkit port).
        /// </summary>
        public static float Draw(ConnectionStripModel model)
        {
            var height = 0f;

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            {
                DrawStage(model.Bridge, TooltipBridge, true);
                GUILayout.Space(StageGap);
                DrawStage(model.Discovery, TooltipDiscovery, true);
                GUILayout.Space(StageGap);
                DrawStage(model.Client, TooltipClient, false);
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.EndHorizontal();

            // Reason lines (one per degraded stage) below the dot row. Kept
            // outside the horizontal so they can word-wrap on narrow windows.
            height += DrawReasonIfAny(model.Bridge);
            height += DrawReasonIfAny(model.Discovery);
            height += DrawReasonIfAny(model.Client);

            return height;
        }

        private static void DrawStage(StripStage stage, string tooltip, bool drawReasonInline)
        {
            var color = ColorForState(stage.State);
            var rect = EditorGUILayout.GetControlRect(false, DotSize, GUILayout.Width(DotSize));
            // Vertically center the dot against the bold label on the same row.
            var dot = new Rect(rect.x, rect.y + (rect.height - DotSize) * 0.5f, DotSize, DotSize);
            EditorGUI.DrawRect(dot, color);

            GUILayout.Space(DotLabelGap);

            var prev = GUI.color;
            // Keep the label readable; the color signal is on the dot.
            var label = new GUIContent(stage.Label, tooltip);
            GUILayout.Label(label, EditorStyles.boldLabel);
            GUI.color = prev;
        }

        private static float DrawReasonIfAny(StripStage stage)
        {
            if (string.IsNullOrEmpty(stage.Reason)) return 0f;
            var prev = GUI.color;
            GUI.color = ColorForState(stage.State);
            GUILayout.Label(stage.Reason, EditorStyles.wordWrappedMiniLabel);
            GUI.color = prev;
            return EditorGUIUtility.singleLineHeight;
        }

        private static Color ColorForState(StripStageState state)
        {
            switch (state)
            {
                case StripStageState.Ok: return new Color(0.45f, 0.80f, 0.45f);
                case StripStageState.Warning: return new Color(0.95f, 0.78f, 0.30f);
                case StripStageState.Bad: return new Color(0.90f, 0.45f, 0.45f);
                default: return new Color(0.62f, 0.62f, 0.62f);
            }
        }
    }
}
