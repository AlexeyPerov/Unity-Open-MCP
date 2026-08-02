using System.Text;
using UnityEngine.SceneManagement;

namespace UnityOpenMcpBridge
{
    /// <summary>
    /// Shared helper for the "scenes dirty in memory but clean on disk" signal
    /// (feedback-01-08-glm §9). Structural mutations (gameobject_set_parent,
    /// component ops) mark scenes dirty in memory without writing them, and
    /// reserialize rewrites on-disk YAML without touching the in-memory
    /// hierarchy — leaving the agent to reason against stale state in either
    /// direction. This builds the canonical `dirtySceneCount` + `dirtyScenes`
    /// JSON fragment reused by editor_status and the reserialize response.
    /// </summary>
    internal static class SceneDirtyState
    {
        /// <summary>
        /// Build a `dirtySceneCount` + `dirtyScenes:[{name,path}]` JSON fragment
        /// (no enclosing braces) listing every LOADED scene whose in-memory
        /// hierarchy has unsaved changes. Returns `dirtySceneCount:0` with an
        /// empty array when no scene is dirty.
        /// </summary>
        internal static string BuildDirtyScenesJson()
        {
            var dirty = new System.Collections.Generic.List<Scene>(SceneManager.sceneCount);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded && s.isDirty) dirty.Add(s);
            }

            var sb = new StringBuilder(64 + dirty.Count * 64);
            sb.Append("\"dirtySceneCount\":").Append(dirty.Count);
            sb.Append(",\"dirtyScenes\":[");
            for (int i = 0; i < dirty.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(BridgeJson.EscapeString(dirty[i].name ?? ""));
                sb.Append(",\"path\":").Append(BridgeJson.EscapeString(dirty[i].path ?? ""));
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
