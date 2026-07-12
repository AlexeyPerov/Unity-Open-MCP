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
            sb.Append("\"currentScene\":").Append(BridgeJson.EscapeString(GetCurrentScenePath())).Append(',');
            sb.Append("\"unityVersion\":").Append(BridgeJson.EscapeString(Application.unityVersion)).Append(',');
            sb.Append("\"editorType\":").Append(BridgeJson.EscapeString(Application.isEditor ? "editor" : "build"));
            sb.Append('}');
            return sb.ToString();
        }

        private static string GetCurrentScenePath()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return scene.path ?? "";
        }
    }
}
