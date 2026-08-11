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
            // V8: the missing_references rule emits missing_script with a
            // per-instance discriminator suffix (missing_script:<scriptGuid>)
            // so the gate delta can see each missing script independently.
            // Match on the bare code, same as RelinkBrokenGuidFix.
            var bareCode = IssueKey.BareIssueCode(issueCode);
            return ruleId == "missing_references" && bareCode == "missing_script";
        }

        public FixDescription Describe(string issueId)
        {
            IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _);
            var isPrefab = IsPrefabAsset(assetPath);

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

        // Safe mirrors Describe() (prefab-only is safe; scenes need a load
        // cycle) without building the FixDescription. Cheap by design — the
        // verdict is a Path.GetExtension check, no asset I/O.
        public bool IsSafe(string issueId)
        {
            IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _);
            return IsPrefabAsset(assetPath);
        }

        // Prefab edits are isolated (load → edit → save); scene edits can
        // collide with an open scene, so only prefabs are Safe under enforce.
        private static bool IsPrefabAsset(string assetPath)
            => Path.GetExtension(assetPath ?? "").ToLowerInvariant() == ".prefab";

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
            //
            // Case-insensitive open-scene resolution (matching
            // RelinkBrokenGuidFix.FindOpenScene): macOS/Windows default to
            // case-insensitive filesystems, so an assetPath whose casing
            // differs from Unity's canonical recorded path must still be
            // recognised as already-open. The previous exact `==` +
            // GetSceneByPath lookups failed the detection on a case mismatch,
            // so wasOpen was false, we opened a DUPLICATE additive copy, saved
            // it (overwriting the file on disk), and closed the duplicate —
            // leaving the user's originally-open scene stale so their next
            // save silently reverted the fix. FindOpenScene returns the actual
            // loaded handle (compared OrdinalIgnoreCase) and we operate on
            // THAT handle rather than a fresh exact-match GetSceneByPath that
            // disagrees on a case-mismatched path.
            //
            // Unlike RelinkBrokenGuidFix this path does NOT refuse a dirty open
            // scene: it edits the live in-memory scene and calls SaveScene,
            // which writes the COMBINED state (the user's edits plus the fix)
            // rather than reloading from disk. There is no discard hazard, so
            // a dirty-scene refusal would only dead-end the common interactive
            // case (the user almost always has the scene open).
            var openScene = FindOpenScene(assetPath);
            bool wasOpen = openScene.IsValid();

            Scene scene;
            try
            {
                scene = wasOpen
                    ? openScene
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

        // The loaded scene (active or additive) whose path matches assetPath,
        // compared OrdinalIgnoreCase so a path that differs only in case
        // (macOS/Windows default to case-insensitive filesystems) is still
        // recognised as open. Returns default(Scene) (IsValid() == false) when
        // no loaded scene matches. Mirrors RelinkBrokenGuidFix.FindOpenScene so
        // both scene-mutating fixes share the same open-scene identity rule.
        //
        // Internal (not private) so the edit-mode tests can exercise the
        // case-insensitive resolution directly — the regression only manifests
        // on case-insensitive filesystems, so a path-argument unit test is the
        // platform-independent way to pin it.
        internal static Scene FindOpenScene(string assetPath)
        {
            if (Path.GetExtension(assetPath ?? "").ToLowerInvariant() != ".unity")
                return default(Scene);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var candidate = SceneManager.GetSceneAt(i);
                if (string.Equals(candidate.path, assetPath,
                    System.StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            return default(Scene);
        }
    }
}
