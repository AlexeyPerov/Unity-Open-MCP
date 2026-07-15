using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityOpenMcpVerify.Rules.ProjectHealth
{
    public static class Scanner
    {
        private static readonly string[] RequiredProjectSettings =
        {
            "ProjectSettings/ProjectSettings.asset",
            "ProjectSettings/ProjectVersion.txt",
            "ProjectSettings/TagManager.asset",
        };

        public static ProjectHealthData Scan(string[] paths, ProjectHealthScanSettings settings, bool fullScan)
        {
            var data = new ProjectHealthData();

            // Orphan meta + duplicate GUID + folder checks narrow to the scoped
            // paths; broken-asset + empty-scene + ProjectSettings are whole-tree
            // checks run only in full-scan mode (mirrors the source scanner,
            // which always walks the whole tree, and the verify gate's Full
            // mode).
            var scopedPaths = ResolveScope(paths);

            if (settings.CheckOrphanedMeta)
                DetectOrphanMetas(scopedPaths, data);
            if (fullScan && settings.CheckDuplicateGuid)
                DetectDuplicateGuids(scopedPaths, fullScan, data);
            if (settings.CheckEmptyFolders || settings.CheckMetaOnlyFolders || settings.CheckDeepNesting || settings.CheckLargeFolders)
                ScanFolders(scopedPaths, settings, data);
            if (fullScan && settings.CheckBrokenAssets)
                ScanBrokenAssets(scopedPaths, data);
            if (fullScan && settings.CheckEmptyScenes)
                ScanEmptyScenes(data);
            if (fullScan && settings.CheckProjectSettings)
                ScanProjectSettingsIntegrity(data);

            return data;
        }

        // -------------------------------------------------------------------
        // Scope resolution
        // -------------------------------------------------------------------

        private static List<string> ResolveScope(string[] paths)
        {
            var result = new List<string>();
            if (paths == null || paths.Length == 0)
            {
                foreach (var p in AssetDatabase.GetAllAssetPaths())
                {
                    if (p.StartsWith("Assets/", StringComparison.Ordinal))
                        result.Add(p.Replace('\\', '/'));
                }
                return result;
            }

            foreach (var raw in paths)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                result.Add(raw.Replace('\\', '/'));
            }
            return result;
        }

        // -------------------------------------------------------------------
        // Orphan .meta detection
        // -------------------------------------------------------------------

        private static void DetectOrphanMetas(List<string> scopedPaths, ProjectHealthData data)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in scopedPaths)
            {
                string dir;
                if (AssetDatabase.IsValidFolder(path))
                {
                    dir = path;
                }
                else
                {
                    var parent = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(parent)) continue;
                    dir = parent;
                }

                // Directory.GetFiles/Exists resolve relative paths against the
                // process working directory, which is NOT the Unity project root
                // under the test runner. Resolve to absolute first, then convert
                // results back to the project-relative form the rest of the rule
                // uses.
                var absDir = ToAbsolutePath(dir);
                if (!Directory.Exists(absDir)) continue;
                if (!seen.Add(dir)) continue;

                string[] metas;
                try { metas = Directory.GetFiles(absDir, "*.meta", SearchOption.TopDirectoryOnly); }
                catch { continue; }

                foreach (var meta in metas)
                {
                    var metaNorm = ToProjectRelative(meta);
                    if (!metaNorm.EndsWith(".meta", StringComparison.Ordinal)) continue;
                    var companion = metaNorm.Substring(0, metaNorm.Length - 5);
                    var absCompanion = ToAbsolutePath(companion);
                    if (File.Exists(absCompanion)) continue;
                    if (AssetDatabase.IsValidFolder(companion)) continue;

                    if (!companion.StartsWith("Assets/", StringComparison.Ordinal) &&
                        !IsUnderAny(companion, scopedPaths)) continue;

                    data.OrphanMetas.Add(new OrphanMetaEntry(metaNorm));
                }
            }
        }

        // -------------------------------------------------------------------
        // Duplicate GUID detection
        // -------------------------------------------------------------------

        private static void DetectDuplicateGuids(List<string> scopedPaths, bool fullScan, ProjectHealthData data)
        {
            var byGuid = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var assetPath in AssetDatabase.GetAllAssetPaths())
            {
                string guid;
                try { guid = AssetDatabase.AssetPathToGUID(assetPath); }
                catch { continue; }
                if (string.IsNullOrEmpty(guid)) continue;
                if (!byGuid.TryGetValue(guid, out var list))
                {
                    list = new List<string>();
                    byGuid[guid] = list;
                }
                list.Add(assetPath.Replace('\\', '/'));
            }

            var scopedSet = fullScan ? null : new HashSet<string>(scopedPaths, StringComparer.Ordinal);

            foreach (var kvp in byGuid)
            {
                if (kvp.Value.Count < 2) continue;

                if (scopedSet != null)
                {
                    var anyInScope = kvp.Value.Any(p => scopedSet.Contains(p));
                    if (!anyInScope) continue;
                }

                data.DuplicateGuids.Add(new DuplicateGuidEntry(kvp.Key, kvp.Value));
            }
        }

        // -------------------------------------------------------------------
        // Folder structure detection (ported verbatim from the source scanner)
        // -------------------------------------------------------------------

        private static void ScanFolders(List<string> scopedPaths, ProjectHealthScanSettings settings, ProjectHealthData data)
        {
            // Enumerate every directory under each scoped root (folder or the
            // parent of a scoped file). Mirrors the source: Directory.EnumerateDirectories
            // with AllDirectories over the Assets root(s).
            var roots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in scopedPaths)
            {
                string dir;
                if (AssetDatabase.IsValidFolder(path)) dir = path;
                else
                {
                    var parent = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(parent)) continue;
                    dir = parent;
                }
                if (Directory.Exists(dir)) roots.Add(dir.Replace('\\', '/'));
            }

            foreach (var root in roots)
            {
                IEnumerable<string> dirs;
                try { dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (var dir in dirs)
                {
                    var dirNorm = dir.Replace('\\', '/');
                    CheckFolder(dirNorm, settings, data);
                }
                // Also check the root itself.
                CheckFolder(root, settings, data);
            }
        }

        private static void CheckFolder(string dir, ProjectHealthScanSettings settings, ProjectHealthData data)
        {
            // Empty / meta-only folder detection. Ported quirk: a truly empty
            // folder is reported as meta-only (the hasOnlyMeta default never
            // flips when there are zero files).
            if (settings.CheckEmptyFolders || settings.CheckMetaOnlyFolders)
            {
                var hasFiles = false;
                var hasOnlyMeta = true;
                var enumerationFailed = false;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        if (!f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        {
                            hasFiles = true;
                            hasOnlyMeta = false;
                            break;
                        }
                    }
                }
                catch { enumerationFailed = true; }

                if (!enumerationFailed && !hasFiles)
                {
                    bool hasSubDirs;
                    try { hasSubDirs = Directory.EnumerateDirectories(dir).Any(); }
                    catch { hasSubDirs = false; }

                    if (!hasSubDirs)
                    {
                        // Ported quirk: hasOnlyMeta stays true for the zero-file
                        // case, so truly-empty folders report as meta-only.
                        if (hasOnlyMeta && settings.CheckMetaOnlyFolders)
                        {
                            data.FolderIssues.Add(new FolderIssue(dir, "project_meta_only_folder",
                                "Folder contains only .meta files with no actual assets"));
                        }
                        else if (!hasOnlyMeta && settings.CheckEmptyFolders)
                        {
                            data.FolderIssues.Add(new FolderIssue(dir, "project_empty_folder",
                                "Empty folder with no files or subdirectories"));
                        }
                    }
                }
            }

            if (settings.CheckDeepNesting)
            {
                var depth = CountPathDepth(dir);
                if (depth > settings.MaxFolderNestingDepth)
                {
                    data.FolderIssues.Add(new FolderIssue(dir, "project_deep_nesting",
                        $"Folder nesting depth {depth} exceeds threshold {settings.MaxFolderNestingDepth}"));
                }
            }

            if (settings.CheckLargeFolders)
            {
                int fileCount;
                try { fileCount = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly).Length; }
                catch { fileCount = 0; }
                if (fileCount > settings.MaxFilesPerFolder)
                {
                    data.FolderIssues.Add(new FolderIssue(dir, "project_large_folder",
                        $"{fileCount} files in single folder (threshold: {settings.MaxFilesPerFolder})"));
                }
            }
        }

        private static int CountPathDepth(string relativePath)
        {
            var depth = 0;
            foreach (var c in relativePath)
                if (c == '/' || c == '\\') depth++;
            return depth;
        }

        // -------------------------------------------------------------------
        // Broken asset detection (ported verbatim)
        // -------------------------------------------------------------------

        private static void ScanBrokenAssets(List<string> scopedPaths, ProjectHealthData data)
        {
            var scopedSet = new HashSet<string>(scopedPaths, StringComparer.Ordinal);
            foreach (var assetPath in AssetDatabase.GetAllAssetPaths())
            {
                if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                if (scopedSet.Count > 0 && !scopedSet.Contains(assetPath)) continue;
                if (assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                if (AssetDatabase.IsValidFolder(assetPath)) continue;
                if (!File.Exists(assetPath)) continue;

                try
                {
                    var obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
                    if (obj == null)
                    {
                        data.BrokenAssets.Add(new BrokenAssetEntry(assetPath,
                            "Asset could not be loaded — possibly corrupted or missing importer"));
                    }
                }
                catch (Exception ex)
                {
                    data.BrokenAssets.Add(new BrokenAssetEntry(assetPath,
                        "Asset threw exception on load: " + ex.Message));
                }
            }
        }

        // -------------------------------------------------------------------
        // Empty scene detection (ported verbatim)
        // -------------------------------------------------------------------

        private static void ScanEmptyScenes(ProjectHealthData data)
        {
            var sceneGuids = AssetDatabase.FindAssets("t:Scene");
            foreach (var guid in sceneGuids)
            {
                var scenePath = AssetDatabase.GUIDToAssetPath(guid);
                if (scenePath.StartsWith("Packages/", StringComparison.Ordinal)) continue;
                if (scenePath.StartsWith("Library/", StringComparison.Ordinal)) continue;

                // T5.5 — same wasOpen hardening as ScenePrefabHealth.Scanner.
                // Only close a scene the scanner itself opened; leave any
                // already-open additive scene intact.
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
                catch { continue; }

                try
                {
                    if (scene.rootCount == 0)
                    {
                        data.EmptyScenes.Add(new EmptySceneEntry(scenePath));
                    }
                }
                finally
                {
                    if (!wasOpen)
                        EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        // -------------------------------------------------------------------
        // ProjectSettings integrity (verify-package addition; source has none)
        // -------------------------------------------------------------------

        private static void ScanProjectSettingsIntegrity(ProjectHealthData data)
        {
            var projectRoot = TryGetProjectRoot();
            if (projectRoot == null) return;

            foreach (var rel in RequiredProjectSettings)
            {
                var abs = Path.Combine(projectRoot, rel).Replace('\\', '/');
                if (!File.Exists(abs))
                {
                    data.SettingIssues.Add(new ProjectSettingIssue(rel, "<file>",
                        "required ProjectSettings file is missing"));
                }
            }

            var versionFile = Path.Combine(projectRoot, "ProjectSettings/ProjectVersion.txt").Replace('\\', '/');
            if (File.Exists(versionFile))
            {
                try
                {
                    var text = File.ReadAllText(versionFile).Trim();
                    if (string.IsNullOrEmpty(text) || !text.Contains("m_EditorVersion:"))
                    {
                        data.SettingIssues.Add(new ProjectSettingIssue(
                            "ProjectSettings/ProjectVersion.txt",
                            "m_EditorVersion",
                            "ProjectVersion.txt does not declare a Unity editor version"));
                    }
                }
                catch
                {
                    data.SettingIssues.Add(new ProjectSettingIssue(
                        "ProjectSettings/ProjectVersion.txt",
                        "<read>",
                        "ProjectVersion.txt could not be read"));
                }
            }
        }

        private static string TryGetProjectRoot()
        {
            try
            {
                var dataPath = UnityEngine.Application.dataPath.Replace('\\', '/');
                var dir = new DirectoryInfo(dataPath).Parent;
                while (dir != null)
                {
                    var settingsDir = Path.Combine(dir.FullName, "ProjectSettings");
                    if (Directory.Exists(settingsDir))
                        return dir.FullName.Replace('\\', '/');
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        private static bool IsUnderAny(string candidate, List<string> roots)
        {
            foreach (var root in roots)
            {
                if (candidate.StartsWith(root + "/", StringComparison.Ordinal) || candidate == root)
                    return true;
            }
            return false;
        }

        // Normalize a filesystem path from Directory.GetFiles back to the
        // project-relative form (Assets/...) so the StartsWith("Assets/") gate
        // and AssetDatabase-relative checks below behave consistently.
        private static string ToProjectRelative(string absoluteOrRelative)
        {
            var norm = absoluteOrRelative.Replace('\\', '/');
            var dataPath = UnityEngine.Application.dataPath.Replace('\\', '/'); // .../Assets
            if (norm.StartsWith(dataPath + "/", StringComparison.Ordinal))
            {
                return "Assets" + norm.Substring(dataPath.Length);
            }
            return norm;
        }

        private static string ToAbsolutePath(string projectRelative)
        {
            if (string.IsNullOrEmpty(projectRelative) ||
                !projectRelative.StartsWith("Assets/", StringComparison.Ordinal))
                return projectRelative;
            var dataPath = UnityEngine.Application.dataPath.Replace('\\', '/'); // .../Assets
            return dataPath + projectRelative.Substring("Assets".Length);
        }
    }
}
