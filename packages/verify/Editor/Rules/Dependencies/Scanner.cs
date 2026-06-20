using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityOpenMcpVerify.Internals.RegexPatterns;

namespace UnityOpenMcpVerify.Rules.Dependencies
{
    public static class Scanner
    {
        public static void ScanPaths(string[] paths, List<AssetDependencyData> sink)
        {
            if (paths == null || paths.Length == 0) return;

            var scoped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (p.StartsWith("Packages/", StringComparison.Ordinal)) continue;
                if (p.StartsWith("Library/", StringComparison.Ordinal)) continue;
                scoped.Add(p.Replace('\\', '/'));
            }

            if (scoped.Count == 0) return;

            var byPath = new Dictionary<string, AssetDependencyData>(StringComparer.Ordinal);
            foreach (var path in scoped)
            {
                if (!File.Exists(path)) continue;
                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                if (type == typeof(MonoScript) || type == typeof(DefaultAsset)) continue;

                var data = new AssetDependencyData(path);
                CollectDeclaredEdges(path, data);
                data.ForwardDeps.AddRange(GetUnityDependencies(path, data));
                sink.Add(data);
                byPath[path] = data;
            }

            DetectCycles(byPath, scoped);
        }

        private static void CollectDeclaredEdges(string assetPath, AssetDependencyData data)
        {
            var lines = TryReadLines(assetPath);
            if (lines.Count == 0) return;

            var pptr = SharedRegex.ExternalFileAndGuid;
            var assetRef = SharedRegex.AssetReferenceGuid;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                var pptrMatches = pptr.Matches(line);
                foreach (Match m in pptrMatches)
                {
                    if (!m.Success) continue;
                    var guid = m.Groups[2].Value;
                    if (!IsRealGuid(guid)) continue;
                    if (!seen.Add("pptr:" + guid)) continue;
                    var targetPath = AssetDatabase.GUIDToAssetPath(guid);
                    data.DeclaredEdges.Add(new DependencyEdge(assetPath, guid, targetPath, i + 1, "pptr"));
                }

                if (line.Contains("m_AssetGUID"))
                {
                    var arMatches = assetRef.Matches(line);
                    foreach (Match m in arMatches)
                    {
                        if (!m.Success) continue;
                        var guid = m.Groups[1].Value;
                        if (!IsRealGuid(guid)) continue;
                        if (!seen.Add("assetref:" + guid)) continue;
                        var targetPath = AssetDatabase.GUIDToAssetPath(guid);
                        data.DeclaredEdges.Add(new DependencyEdge(assetPath, guid, targetPath, i + 1, "assetref"));
                    }
                }
            }
        }

        // AssetDatabase.GetDependencies is the authoritative forward edge set Unity
        // itself uses; it walks the serialized PPtr graph (recursive=false so the
        // forward walk stays one hop and cycle detection runs over declared edges).
        // We additionally fold in any m_AssetGUID edges that GetDependencies misses
        // (AssetReference<> fields are not standard PPtrs).
        private static IEnumerable<string> GetUnityDependencies(string assetPath, AssetDependencyData data)
        {
            var deps = new HashSet<string>(StringComparer.Ordinal);
            string[] unityDeps;
            try { unityDeps = AssetDatabase.GetDependencies(assetPath, false); }
            catch { unityDeps = Array.Empty<string>(); }

            foreach (var dep in unityDeps)
            {
                if (string.IsNullOrEmpty(dep)) continue;
                if (dep == assetPath) continue;
                deps.Add(dep.Replace('\\', '/'));
            }

            foreach (var edge in data.DeclaredEdges)
            {
                if (edge.Kind != "assetref") continue;
                if (string.IsNullOrEmpty(edge.TargetPath)) continue;
                deps.Add(edge.TargetPath.Replace('\\', '/'));
            }

            return deps;
        }

        // DFS over the scoped forward graph; a back-edge into the current recursion
        // stack marks a cycle. We only follow edges whose target is itself in scope
        // (cycles through unscoped assets are not actionable from a paths_hint view).
        private static void DetectCycles(Dictionary<string, AssetDependencyData> byPath, HashSet<string> scoped)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var stack = new HashSet<string>(StringComparer.Ordinal);
            var pathStack = new List<string>();

            void Dfs(string node)
            {
                if (!byPath.TryGetValue(node, out var data)) return;
                if (!visited.Add(node)) return;
                stack.Add(node);
                pathStack.Add(node);

                foreach (var dep in data.ForwardDeps)
                {
                    if (!scoped.Contains(dep)) continue;
                    if (stack.Contains(dep))
                    {
                        var cycle = ExtractCycle(pathStack, dep);
                        if (cycle.Count > 0)
                            data.CyclesThrough.Add(cycle);
                        continue;
                    }
                    Dfs(dep);
                }

                stack.Remove(node);
                pathStack.RemoveAt(pathStack.Count - 1);
            }

            foreach (var root in byPath.Keys)
            {
                if (!visited.Contains(root)) Dfs(root);
            }
        }

        private static List<string> ExtractCycle(List<string> pathStack, string entryNode)
        {
            var cycle = new List<string>();
            var idx = pathStack.IndexOf(entryNode);
            if (idx < 0) return cycle;
            for (var i = idx; i < pathStack.Count; i++) cycle.Add(pathStack[i]);
            cycle.Add(entryNode);
            return cycle;
        }

        private static List<string> TryReadLines(string assetPath)
        {
            var result = new List<string>();
            try
            {
                if (!File.Exists(assetPath)) return result;
                var ext = Path.GetExtension(assetPath).ToLowerInvariant();
                if (ext != ".prefab" && ext != ".unity" && ext != ".asset" &&
                    ext != ".mat" && ext != ".controller" && ext != ".anim")
                    return result;
                result.AddRange(File.ReadAllLines(assetPath));
            }
            catch { }
            return result;
        }

        private static bool IsRealGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return false;
            if (guid.Length != 32) return false;
            if (guid.StartsWith("0000000000", StringComparison.Ordinal)) return false;
            for (var i = 0; i < 32; i++)
            {
                var c = guid[i];
                var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex) return false;
            }
            return true;
        }
    }
}
