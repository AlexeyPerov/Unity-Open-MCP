using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    [BridgeToolType]
    public partial class Tool_Editor
    {
        [BridgeTool("unity_open_mcp_editor_status", Title = "Editor Status",
            IsMutating = false, ReadOnlyHint = true, Lifecycle = LifecyclePolicy.None,
            Group = "core")]
        [System.ComponentModel.Description("Returns the current Unity Editor state")]
        public string EditorStatus()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"isPlaying\":").Append(EditorApplication.isPlaying ? "true" : "false").Append(',');
            sb.Append("\"isCompiling\":").Append(EditorApplication.isCompiling ? "true" : "false").Append(',');
            sb.Append("\"isPaused\":").Append(EditorApplication.isPaused ? "true" : "false").Append(',');
            sb.Append("\"currentScene\":").Append(EscapeJsonString(GetCurrentScenePath())).Append(',');
            sb.Append("\"unityVersion\":").Append(EscapeJsonString(Application.unityVersion)).Append(',');
            sb.Append("\"editorType\":").Append(EscapeJsonString(Application.isEditor ? "editor" : "build"));
            sb.Append('}');
            return sb.ToString();
        }

        private static string GetCurrentScenePath()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return scene.path ?? "";
        }

        private static string EscapeJsonString(string s)
        {
            if (s == null) return "null";
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
                        if (c < 32)
                            sb.Append($"\\u{(int)c:X4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
