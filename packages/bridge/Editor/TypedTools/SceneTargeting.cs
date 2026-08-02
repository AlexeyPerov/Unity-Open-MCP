using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityOpenMcpBridge.TypedTools
{
    /// <summary>
    /// Shared creator-tool scene targeting (feedback-01-08-glm §6). The root-
    /// creating tools (gameobject_create, ui_canvas_add, prefab_instantiate)
    /// historically landed every new GameObject in whatever the active scene
    /// was at call time — an invisible, mutable choice that produced
    /// inconsistent results when more than one scene was loaded. This resolves
    /// an optional `scene_path` / `scene_name` selector to a loaded Scene and
    /// moves the newly-created GameObject into it via MoveGameObjectToScene
    /// (without disturbing the global active scene). Returns null (no error)
    /// when no selector is supplied, leaving the active-scene default intact.
    /// </summary>
    internal static class SceneTargeting
    {
        /// <summary>Resolve a target scene from the request body's optional
        /// `scene_path` / `scene_name` selector. Returns a default (invalid)
        /// Scene — meaning "use the active scene" — when neither is supplied.
        /// `error` is set when a selector WAS supplied but resolved to nothing,
        /// so the caller can fail with a precise message.</summary>
        internal static Scene ResolveTargetScene(string body, out string error)
        {
            error = null;
            var scenePath = JsonBody.GetString(body, "scene_path") ?? JsonBody.GetString(body, "scenePath");
            var sceneName = JsonBody.GetString(body, "scene_name") ?? JsonBody.GetString(body, "sceneName");

            if (string.IsNullOrWhiteSpace(scenePath) && string.IsNullOrWhiteSpace(sceneName))
                return new Scene(); // no selector → active-scene default

            if (!string.IsNullOrWhiteSpace(scenePath))
            {
                var scene = ResolveByPath(scenePath);
                if (scene.IsValid() && scene.isLoaded) return scene;
                error = $"Target scene not found / not loaded for scene_path '{scenePath}'. " +
                    "Use unity_open_mcp_scene_open (OpenSceneMode.Additive) to load it first, or omit " +
                    "scene_path/scene_name to create in the active scene.";
                return new Scene();
            }

            var byName = ResolveByName(sceneName);
            if (byName.IsValid() && byName.isLoaded) return byName;
            error = $"Target scene not found / not loaded for scene_name '{sceneName}'. " +
                "Use unity_open_mcp_scene_open (OpenSceneMode.Additive) to load it first, or omit " +
                "scene_path/scene_name to create in the active scene.";
            return new Scene();
        }

        /// <summary>Move `go` into `target` if `target` is valid+loaded and
        /// differs from the GameObject's current scene. No-op (safe) when target
        /// is invalid (the caller fell back to active scene) or already the
        /// owning scene.</summary>
        internal static void MoveToSceneIfDifferent(GameObject go, Scene target)
        {
            if (go == null) return;
            if (!target.IsValid() || !target.isLoaded) return;
            if (go.scene == target) return;
            SceneManager.MoveGameObjectToScene(go, target);
        }

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
