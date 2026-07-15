using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityOpenMcpVerify.Rules.ScenePrefabHealth
{
    public static class Scanner
    {
        public static void ScanPaths(
            string[] paths,
            ScanSettings settings,
            List<SceneData> scenes,
            List<PrefabData> prefabs)
        {
            foreach (var assetPath in paths)
            {
                if (string.IsNullOrEmpty(assetPath)) continue;
                if (assetPath.StartsWith("Packages/") || assetPath.StartsWith("Library/")) continue;

                if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    var data = AnalyzeScene(assetPath, settings);
                    if (data != null) scenes.Add(data);
                }
                else if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    var data = AnalyzePrefab(assetPath, settings);
                    if (data != null) prefabs.Add(data);
                }
            }
        }

        private static SceneData AnalyzeScene(string scenePath, ScanSettings settings)
        {
            long fileBytes = 0;
            try { if (File.Exists(scenePath)) fileBytes = new FileInfo(scenePath).Length; } catch { }

            var buildScenes = GetBuildScenePaths();

            var data = new SceneData
            {
                Path = scenePath,
                Name = Path.GetFileName(scenePath),
                FileSizeBytes = fileBytes,
                IsBootstrapScene = buildScenes.Contains(scenePath) ||
                                   scenePath.IndexOf("bootstrap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   scenePath.IndexOf("startup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   scenePath.IndexOf("init", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   scenePath.IndexOf("preload", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   scenePath.IndexOf("splash", StringComparison.OrdinalIgnoreCase) >= 0
            };

            // M30-polish Plan 5 / T5.5 — track whether the scanner opened this
            // scene. The previous code opened the scene additively and then, in
            // the finally block, closed it whenever sceneCount > 1. That guard
            // does NOT distinguish a scene the scanner opened from one the
            // agent/user already had open additively (e.g. a scene_create /
            // scene_open additive that just ran as the gate's mutation). The
            // validate phase runs AFTER the mutation, so the additive scene is
            // already in the stack; CloseScene(..., true) then evicted it,
            // making every gated additive scene_create / scene_open appear to
            // "drop" the scene. The wasOpen pattern (same one RemoveMissing-
            // ScriptFix.FixScene uses) only closes what the scanner itself
            // opened, leaving intentional scene-stack side effects intact.
            bool wasOpen = false;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).path == scenePath) { wasOpen = true; break; }
            }

            Scene scene;
            try
            {
                scene = wasOpen
                    ? SceneManager.GetSceneByPath(scenePath)
                    : EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            }
            catch
            {
                return null;
            }

            try
            {
                var roots = scene.GetRootGameObjects();
                data.RootCount = roots.Length;

                var totalObjects = 0;
                var totalComponents = 0;
                var inactiveObjects = 0;
                var inactiveRenderers = 0;

                foreach (var root in roots)
                {
                    var transforms = root.GetComponentsInChildren<Transform>(true);
                    totalObjects += transforms.Length;

                    foreach (var t in transforms)
                    {
                        if (t == null || t.gameObject == null) continue;

                        var components = t.GetComponents<Component>();
                        var validComponents = 0;
                        foreach (var c in components)
                        {
                            if (c == null)
                            {
                                if (settings.DetectBrokenReferences)
                                    data.BrokenReferences.Add("Missing script on '" + GetHierarchyPath(t) + "'.");
                                continue;
                            }
                            validComponents++;
                        }

                        totalComponents += validComponents;

                        if (!t.gameObject.activeSelf)
                        {
                            inactiveObjects++;
                            var renderers = t.GetComponents<Renderer>();
                            if (renderers != null && renderers.Length > 0)
                            {
                                inactiveRenderers++;
                                if (settings.DetectInactiveAntiPatterns)
                                {
                                    data.ExpensiveInactiveObjects.Add(new InactiveObjectInfo
                                    {
                                        ObjectPath = GetHierarchyPath(t),
                                        ComponentType = "Renderer",
                                        Description = "Inactive object with Renderer"
                                    });
                                }
                            }
                        }

                        if (settings.DetectHierarchyHotspots && validComponents > settings.MaxComponentCountPerObject)
                        {
                            data.HotspotPaths.Add(GetHierarchyPath(t) + " (" + validComponents + " components)");
                        }
                    }
                }

                data.TotalObjectCount = totalObjects;
                data.TotalComponentCount = totalComponents;
                data.InactiveObjectCount = inactiveObjects;
                data.InactiveRendererCount = inactiveRenderers;
            }
            finally
            {
                // Only close the scene if the scanner opened it. A scene that
                // was already open (the gate mutation's additive scene_create /
                // scene_open, or a human-opened scene) must be left intact.
                if (!wasOpen)
                    EditorSceneManager.CloseScene(scene, true);
            }

            return data;
        }

        private static PrefabData AnalyzePrefab(string prefabPath, ScanSettings settings)
        {
            long fileBytes = 0;
            try { if (File.Exists(prefabPath)) fileBytes = new FileInfo(prefabPath).Length; } catch { }

            var prefab = AssetDatabase.LoadMainAssetAtPath(prefabPath) as GameObject;
            if (prefab == null) return null;

            var data = new PrefabData
            {
                Path = prefabPath,
                Name = Path.GetFileName(prefabPath),
                FileSizeBytes = fileBytes
            };

            var nestingDepth = CalculateNestingDepth(prefab);
            data.NestingDepth = nestingDepth;

            var transforms = prefab.GetComponentsInChildren<Transform>(true);
            data.ChildCount = transforms.Length;

            var componentCount = 0;
            foreach (var t in transforms)
            {
                if (t == null) continue;
                var comps = t.GetComponents<Component>();
                if (comps != null)
                {
                    foreach (var c in comps)
                    {
                        if (c != null) componentCount++;
                    }
                }
            }
            data.ComponentCount = componentCount;

            var overrideCount = CountPrefabOverrides(prefab);
            data.OverrideCount = overrideCount;

            return data;
        }

        private static int CalculateNestingDepth(GameObject prefab)
        {
            var depth = 0;
            var current = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
            while (current != null)
            {
                depth++;
                current = PrefabUtility.GetCorrespondingObjectFromSource(current);
            }
            return depth;
        }

        private static int CountPrefabOverrides(GameObject prefab)
        {
            var modifications = PrefabUtility.GetPropertyModifications(prefab);
            return modifications != null ? modifications.Length : 0;
        }

        private static HashSet<string> GetBuildScenePaths()
        {
            var result = new HashSet<string>();
            for (var i = 0; i < EditorBuildSettings.scenes.Length; i++)
            {
                if (EditorBuildSettings.scenes[i].enabled)
                    result.Add(EditorBuildSettings.scenes[i].path);
            }
            return result;
        }

        private static string GetHierarchyPath(Transform t)
        {
            var parts = new List<string>();
            var current = t;
            while (current != null)
            {
                parts.Insert(0, current.name);
                current = current.parent;
            }
            return string.Join("/", parts);
        }
    }
}
