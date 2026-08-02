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
            var sb = new StringBuilder(320);
            sb.Append('{');
            sb.Append("\"isPlaying\":").Append(EditorApplication.isPlaying ? "true" : "false").Append(',');
            sb.Append("\"isCompiling\":").Append(EditorApplication.isCompiling ? "true" : "false").Append(',');
            sb.Append("\"isPaused\":").Append(EditorApplication.isPaused ? "true" : "false").Append(',');
            sb.Append("\"currentScene\":").Append(BridgeJson.EscapeString(GetCurrentScenePath())).Append(',');
            sb.Append("\"unityVersion\":").Append(BridgeJson.EscapeString(Application.unityVersion)).Append(',');
            sb.Append("\"editorType\":").Append(BridgeJson.EscapeString(Application.isEditor ? "editor" : "build")).Append(',');
            // feedback-01-08-glm §9 — surface which opened scenes have unsaved
            // in-memory changes so an agent does not reason against stale on-disk
            // YAML after a structural op (e.g. gameobject_set_parent marks the
            // scene dirty but does not write it). The dirty flag is the editor's
            // own memory-vs-disk signal; without it the agent has no way to know
            // a save is pending until it hits a stale disk read.
            sb.Append(SceneDirtyState.BuildDirtyScenesJson());
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
