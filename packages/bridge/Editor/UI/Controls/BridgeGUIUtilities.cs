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

        public static void DrawColoredLabel(string text, Color color, int? width = null)
        {
            var prevColor = GUI.color;
            GUI.color = color;
            if (width.HasValue)
                GUILayout.Label(text, EditorStyles.wordWrappedLabel, GUILayout.Width(width.Value));
            else
                GUILayout.Label(text, EditorStyles.wordWrappedLabel);
            GUI.color = prevColor;
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
