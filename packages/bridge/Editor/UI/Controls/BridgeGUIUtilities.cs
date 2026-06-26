using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.UI.Controls
{
    public static class BridgeGUIUtilities
    {
        public static void HorizontalLine(int marginTop = 5, int marginBottom = 5, int height = 2)
        {
            HorizontalLine(marginTop, marginBottom, height, new Color(0.5f, 0.5f, 0.5f, 1f));
        }

        public static void HorizontalLine(int marginTop, int marginBottom, int height, Color color)
        {
            EditorGUILayout.BeginHorizontal();
            var rect = EditorGUILayout.GetControlRect(
                false,
                height,
                new GUIStyle { margin = new RectOffset(0, 0, marginTop, marginBottom) });
            EditorGUI.DrawRect(rect, color);
            EditorGUILayout.EndHorizontal();
        }

        public static void DrawColoredLabel(string text, Color color, int? width = null, string tooltip = null)
        {
            var prevColor = GUI.color;
            GUI.color = color;
            var content = string.IsNullOrEmpty(tooltip) ? new GUIContent(text) : new GUIContent(text, tooltip);
            if (width.HasValue)
                GUILayout.Label(content, EditorStyles.wordWrappedLabel, GUILayout.Width(width.Value));
            else
                GUILayout.Label(content, EditorStyles.wordWrappedLabel);
            GUI.color = prevColor;
        }

        // Left-column label with an optional hover tooltip. Replaces the common
        // `EditorGUILayout.LabelField("Foo", GUILayout.Width(n))` pattern so a
        // one-line call site can carry an explanation without a help box.
        public static void FieldLabel(string text, string tooltip, float width)
        {
            var content = string.IsNullOrEmpty(tooltip) ? new GUIContent(text) : new GUIContent(text, tooltip);
            EditorGUILayout.LabelField(content, GUILayout.Width(width));
        }

        // Label + value row with an optional hover tooltip on the label.
        // Replaces `EditorGUILayout.LabelField("Foo", value)` for detail rows.
        public static void RowLabel(string label, string tooltip, string value)
        {
            var lc = string.IsNullOrEmpty(tooltip) ? new GUIContent(label) : new GUIContent(label, tooltip);
            EditorGUILayout.LabelField(lc, new GUIContent(value ?? ""));
        }

        // Prefix label (GUIContent) bound to the following control, matching
        // `EditorGUILayout.PrefixLabel` but with a hover tooltip. Returns the
        // rect occupied by the label so callers can also place a control rect.
        public static void PrefixLabel(string text, string tooltip)
        {
            var content = string.IsNullOrEmpty(tooltip) ? new GUIContent(text) : new GUIContent(text, tooltip);
            EditorGUILayout.PrefixLabel(content);
        }

        public static bool DrawColoredFoldout(bool value, string text, Color color)
        {
            var prevColor = GUI.color;
            GUI.color = color;
            var result = EditorGUILayout.Foldout(value, text, true);
            GUI.color = prevColor;
            return result;
        }

        public static void DrawLabelAtCenterHorizontally(string text, Color color)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var prevColor = GUI.color;
            GUI.color = color;
            GUILayout.Label(text);
            GUI.color = prevColor;
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        public static Color GetStateColor(bool ok, bool warning)
        {
            if (ok) return new Color(0.6f, 0.9f, 0.6f);
            if (warning) return Color.yellow;
            return new Color(1f, 0.5f, 0.5f);
        }
    }
}
