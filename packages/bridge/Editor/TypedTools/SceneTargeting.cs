using UnityEngine.SceneManagement;

namespace UnityOpenMcpBridge.TypedTools
{
    /// <summary>
    /// Loaded-scene lookup by asset path or name, shared by the container
    /// selector of the typed tools (`scene_path` / `scene_name`, see
    /// <see cref="TargetContainers.Named"/>) and the UI domain. Which
    /// container a GameObject path resolves in, and where a new root
    /// GameObject goes, is decided by <see cref="TypedTargets"/>.
    /// </summary>
    internal static class SceneTargeting
    {
        internal static Scene ResolveByPath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return new Scene();
            var normalized = rawPath.Replace('\\', '/').Trim();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded || string.IsNullOrEmpty(s.path)) continue;
                if (string.Equals(s.path.Replace('\\', '/').Trim(), normalized,
                    System.StringComparison.OrdinalIgnoreCase))
                    return s;
            }
            return new Scene();
        }

        internal static Scene ResolveByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return new Scene();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded && s.name == name) return s;
            }
            return new Scene();
        }
    }
}
