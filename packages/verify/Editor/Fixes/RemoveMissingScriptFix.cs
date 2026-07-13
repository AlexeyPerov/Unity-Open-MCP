using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityOpenMcpVerify.Fixes
{
    public class RemoveMissingScriptFix : IFixProvider
    {
        public string FixId => "remove_missing_script";

        public bool CanFix(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var issueCode))
                return false;
            return ruleId == "missing_references" && issueCode == "missing_script";
        }

        public FixDescription Describe(string issueId)
        {
            IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _);
            var ext = Path.GetExtension(assetPath ?? "").ToLowerInvariant();
            var isPrefab = ext == ".prefab";

            return new FixDescription
            {
                FixId = FixId,
                IssueId = issueId,
                AssetPath = assetPath,
                Description = isPrefab
                    ? $"Load prefab '{assetPath}', remove MonoBehaviour components with missing script GUID, save prefab."
                    : $"Remove missing script component(s) from '{assetPath}'.",
                Safe = isPrefab
            };
        }

        public FixResult Apply(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _))
                return new FixResult
                {
                    Success = false,
                    Description = $"Cannot parse issue id: {issueId}",
                    TouchedPaths = null
                };

            if (string.IsNullOrEmpty(assetPath))
                return new FixResult
                {
                    Success = false,
                    Description = "Issue id contains empty asset path.",
                    TouchedPaths = null
                };

            var ext = Path.GetExtension(assetPath).ToLowerInvariant();

            if (ext == ".prefab")
                return FixPrefab(assetPath);

            if (ext == ".unity")
                return FixScene(assetPath);

            return new FixResult
            {
                Success = false,
                Description = $"remove_missing_script only supports .prefab and .unity assets, got '{ext}'.",
                TouchedPaths = null
            };
        }

        private static FixResult FixPrefab(string assetPath)
        {
            var go = PrefabUtility.LoadPrefabContents(assetPath);
            if (go == null)
                return new FixResult
                {
                    Success = false,
                    Description = $"Could not load prefab at '{assetPath}'.",
                    TouchedPaths = null
                };

            try
            {
                var removed = RemoveMissingScriptsRecursive(go);

                if (removed == 0)
                    return new FixResult
                    {
                        Success = true,
                        Description = $"No missing script components found on '{assetPath}'. The issue may have already been resolved.",
                        TouchedPaths = null
                    };

                PrefabUtility.SaveAsPrefabAsset(go, assetPath);

                return new FixResult
                {
                    Success = true,
                    Description = $"Removed {removed} missing script component(s) from prefab '{assetPath}'.",
                    TouchedPaths = new[] { assetPath }
                };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(go);
            }
        }

        private static FixResult FixScene(string assetPath)
        {
            // Mirror ScenePrefabHealth/Scanner.cs: open additively so we don't
            // disrupt the editor's currently-active scene, and close what we
            // opened in a finally block. A scene the user already had open
            // stays open; a scene we opened ourselves is closed after the fix.
            var wasOpen = false;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).path == assetPath)
                {
                    wasOpen = true;
                    break;
                }
            }

            Scene scene;
            try
            {
                scene = wasOpen
                    ? SceneManager.GetSceneByPath(assetPath)
                    : EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
            }
            catch (System.Exception e)
            {
                return new FixResult
                {
                    Success = false,
                    Description = $"Could not open scene at '{assetPath}': {e.Message}",
                    TouchedPaths = null
                };
            }

            try
            {
                if (!scene.isLoaded)
                    return new FixResult
                    {
                        Success = false,
                        Description = $"Could not open scene at '{assetPath}'.",
                        TouchedPaths = null
                    };

                var totalRemoved = 0;
                foreach (var root in scene.GetRootGameObjects())
                    totalRemoved += RemoveMissingScriptsRecursive(root);

                if (totalRemoved > 0)
                    EditorSceneManager.SaveScene(scene);

                return new FixResult
                {
                    Success = true,
                    Description = totalRemoved > 0
                        ? $"Removed {totalRemoved} missing script component(s) from scene '{assetPath}'."
                        : $"No missing script components found in scene '{assetPath}'.",
                    TouchedPaths = totalRemoved > 0 ? new[] { assetPath } : null
                };
            }
            finally
            {
                // Only close the scene if we opened it. A scene the user
                // already had open must stay open (closing it would disrupt
                // their editor state).
                if (!wasOpen)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static int RemoveMissingScriptsRecursive(GameObject go)
        {
            var total = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
            foreach (Transform child in go.transform)
                total += RemoveMissingScriptsRecursive(child.gameObject);
            return total;
        }
    }
}
