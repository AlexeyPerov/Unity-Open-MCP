using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace UnityOpenMcpVerify.Rules.ProjectHealth
{
    public static class Scanner
    {
        // ProjectSettings files whose presence + minimal shape we validate.
        // Missing any of these is a strong signal of a corrupt checkout.
        private static readonly string[] RequiredProjectSettings =
        {
            "ProjectSettings/ProjectSettings.asset",
            "ProjectSettings/ProjectVersion.txt",
            "ProjectSettings/TagManager.asset",
        };

        public static ProjectHealthData Scan(string[] paths, bool fullScan)
        {
            var data = new ProjectHealthData();

            // Orphan .meta + duplicate GUID are whole-project checks. Under
            // paths_hint scope we narrow to the supplied paths only (an agent
            // editing one folder does not want a full-tree walk), but the
            // duplicate-GUID check still needs the global index to detect
            // collisions with assets outside scope — so we always build the
            // GUID index and only emit scoped hits.
            var scopedPaths = ResolveScope(paths);
            DetectOrphanMetas(scopedPaths, data);
            DetectDuplicateGuids(scopedPaths, fullScan, data);

            // ProjectSettings integrity is always a full-project check; it is
            // only emitted in full-scan mode so a scoped validate_edit on a
            // single asset does not surface unrelated settings warnings.
            if (fullScan)
            {
                ScanProjectSettingsIntegrity(data);
            }

            return data;
        }

        // -------------------------------------------------------------------
        // Scope resolution
        // -------------------------------------------------------------------

        // When paths_hint is empty/missing we walk the whole Assets tree
        // (full-scan mode). Otherwise we narrow to the supplied folders/files
        // plus their companion .meta files.
        private static List<string> ResolveScope(string[] paths)
        {
            var result = new List<string>();
            if (paths == null || paths.Length == 0)
            {
                // Full-tree walk: every asset path under Assets/.
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
                var p = raw.Replace('\\', '/');
                result.Add(p);
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
                // The AssetDatabase only returns non-meta asset paths, so a
                // missing companion shows up as a stray .meta on disk. We walk
                // the filesystem directly to catch them.
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

                if (!Directory.Exists(dir)) continue;
                if (!seen.Add(dir)) continue;

                string[] metas;
                try { metas = Directory.GetFiles(dir, "*.meta", SearchOption.TopDirectoryOnly); }
                catch { continue; }

                foreach (var meta in metas)
                {
                    var metaNorm = meta.Replace('\\', '/');
                    if (!metaNorm.EndsWith(".meta", StringComparison.Ordinal)) continue;
                    var companion = metaNorm.Substring(0, metaNorm.Length - 5);
                    if (File.Exists(companion)) continue;
                    if (AssetDatabase.IsValidFolder(companion)) continue;

                    // Skip Library/Packages meta noise — not Assets-scoped.
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
            // Build the full GUID -> paths index across the project. Duplicate
            // detection requires the global view: a scoped-only index would
            // miss collisions with assets outside the hint set.
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

                // In scoped mode only emit a duplicate when at least one of the
                // colliding assets is in scope — otherwise every scan would
                // surface the same project-wide collisions.
                if (scopedSet != null)
                {
                    var anyInScope = kvp.Value.Any(p => scopedSet.Contains(p));
                    if (!anyInScope) continue;
                }

                data.DuplicateGuids.Add(new DuplicateGuidEntry(kvp.Key, kvp.Value));
            }
        }

        // -------------------------------------------------------------------
        // ProjectSettings integrity
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
                    data.SettingIssues.Add(new ProjectSettingIssue(rel, "<file>", "required ProjectSettings file is missing"));
                }
            }

            // ProjectVersion.txt must name a Unity version — a missing or empty
            // m_EditorVersion is a corrupt-checkout signal.
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
            // The ProjectSettings folder lives at the project root. Walk up
            // from the data path (Application.dataPath == .../Assets) until we
            // find a ProjectSettings sibling.
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
                if (candidate.StartsWith(root + "/", StringComparison.Ordinal) ||
                    candidate == root) return true;
            }
            return false;
        }
    }
}
