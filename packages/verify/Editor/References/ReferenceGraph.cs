using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;

namespace UnityOpenMcpVerify.References
{
    public class ReferenceGraph
    {
        public string QueriedAssetPath { get; }
        public string QueriedAssetGuid { get; }
        public List<string> ReferencedByPaths { get; }

        private ReferenceGraph(string assetPath, string guid, List<string> referencedBy)
        {
            QueriedAssetPath = assetPath;
            QueriedAssetGuid = guid;
            ReferencedByPaths = referencedBy;
        }

        public static ReferenceGraph Find(string assetPathOrGuid, ReferenceGraphOptions options = null)
        {
            options ??= ReferenceGraphOptions.Default;

            string assetPath;
            string guid;

            if (IsGuid(assetPathOrGuid))
            {
                guid = assetPathOrGuid;
                assetPath = AssetDatabase.GUIDToAssetPath(guid);
            }
            else
            {
                assetPath = assetPathOrGuid;
                guid = AssetDatabase.AssetPathToGUID(assetPath);
            }

            if (string.IsNullOrEmpty(assetPath))
                return new ReferenceGraph(assetPathOrGuid, guid ?? "", EmptyList());

            var reverseDeps = BuildReverseDependencyMap(options);
            reverseDeps.TryGetValue(assetPath, out var referencedBy);
            return new ReferenceGraph(assetPath, guid, referencedBy ?? EmptyList());
        }

        private static Dictionary<string, List<string>> BuildReverseDependencyMap(ReferenceGraphOptions options)
        {
            // AssetDatabase.GetAllAssetPaths() returns every asset in the
            // project AND every package asset (URP shaders, package scripts,
            // etc.). Walking all of them with GetDependencies() is the single
            // most expensive step in ReferenceGraph.Find — for a URP project
            // it scans thousands of package assets that can never reference a
            // user fixture.
            //
            // ScanRoots narrows the *source* set (which assets we compute
            // forward-dependencies for) to one or more folders. A target can
            // still live outside the roots: we record a reverse entry for every
            // dependency we encounter, so e.g. querying the built-in Standard
            // shader after scanning only a test fixture folder still finds the
            // fixture's materials as referencers. This is the difference
            // between scanning ~thousands of assets and ~handful.
            List<string> assetPaths;
            if (options.ScanRoots != null && options.ScanRoots.Count > 0)
            {
                assetPaths = new List<string>();
                foreach (var root in options.ScanRoots)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    var guids = AssetDatabase.FindAssets(string.Empty, new[] { root });
                    foreach (var g in guids)
                    {
                        var p = AssetDatabase.GUIDToAssetPath(g);
                        if (!string.IsNullOrEmpty(p))
                            assetPaths.Add(p);
                    }
                }
            }
            else
            {
                assetPaths = AssetDatabase.GetAllAssetPaths().ToList();
            }

            var reverseDeps = new Dictionary<string, List<string>>(assetPaths.Count);
            foreach (var p in assetPaths)
                reverseDeps[p] = new List<string>();

            for (var i = 0; i < assetPaths.Count; i++)
            {
                var deps = options.ScanAssetReferences
                    ? GetAllDependencies(assetPaths[i], options.BinarySerialization, false)
                    : AssetDatabase.GetDependencies(assetPaths[i], false);

                foreach (var dep in deps)
                {
                    // The dependency may live outside the scan roots (e.g. a
                    // built-in shader). Create a reverse entry for it on demand
                    // so lookups by the queried asset path still resolve.
                    if (!reverseDeps.TryGetValue(dep, out var list))
                    {
                        list = new List<string>();
                        reverseDeps[dep] = list;
                    }
                    if (dep != assetPaths[i])
                        list.Add(assetPaths[i]);
                }
            }

            if (options.ScanTerrainData)
                ScanTerrainDataReferences(reverseDeps);

            return reverseDeps;
        }

        private static readonly Regex GuidRegex = new Regex(
            @"m_AssetGUID:\s*([0-9a-fA-F]{32})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static string[] GetAllDependencies(string assetPath, bool binarySerialization, bool recursive = true)
        {
            var regular = AssetDatabase.GetDependencies(assetPath, recursive);
            if (!CanContainAssetReferences(assetPath))
                return regular;

            if (binarySerialization)
                return MergeAssetReferenceDependenciesBinary(assetPath, regular);

            return MergeAssetReferenceDependenciesText(assetPath, regular);
        }

        private static string[] MergeAssetReferenceDependenciesBinary(string assetPath, string[] regular)
        {
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (obj == null) return regular;

            HashSet<string> result = null;
            var so = new SerializedObject(obj);
            var it = so.GetIterator();

            while (it.NextVisible(true))
            {
                if (it.propertyType != SerializedPropertyType.Generic ||
                    !it.type.Contains("AssetReference"))
                    continue;

                var guidProp = it.FindPropertyRelative("m_AssetGUID");
                if (guidProp == null || string.IsNullOrEmpty(guidProp.stringValue))
                    continue;

                var refPath = AssetDatabase.GUIDToAssetPath(guidProp.stringValue);
                if (!string.IsNullOrEmpty(refPath))
                {
                    result ??= regular.ToHashSet();
                    result.Add(refPath);
                }
            }

            return result != null ? result.ToArray() : regular;
        }

        private static string[] MergeAssetReferenceDependenciesText(string assetPath, string[] regular)
        {
            if (!File.Exists(assetPath))
                return regular;

            var content = File.ReadAllText(assetPath);
            if (!content.Contains("m_AssetGUID"))
                return regular;

            HashSet<string> set = null;
            foreach (Match match in GuidRegex.Matches(content))
            {
                if (match == null || match.Groups.Count <= 1) continue;
                var guid = match.Groups[1].Value;
                if (string.IsNullOrEmpty(guid)) continue;

                var refPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(refPath))
                {
                    set ??= regular.ToHashSet();
                    set.Add(refPath);
                }
            }

            return set != null ? set.ToArray() : regular;
        }

        private static bool CanContainAssetReferences(string assetPath)
        {
            var ext = Path.GetExtension(assetPath).ToLowerInvariant();
            return ext == ".asset" || ext == ".prefab" || ext == ".unity";
        }

        private static void ScanTerrainDataReferences(Dictionary<string, List<string>> reverseDeps)
        {
            var terrainGuids = AssetDatabase.FindAssets("t:TerrainData");
            if (terrainGuids.Length == 0) return;

            var guidToPath = new Dictionary<string, string>();
            foreach (var guid in terrainGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    guidToPath[guid] = path;
            }

            if (guidToPath.Count == 0) return;

            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".prefab", ".unity" };
            var candidates = AssetDatabase.GetAllAssetPaths()
                .Where(p => exts.Contains(Path.GetExtension(p)))
                .ToList();

            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;

                string content = null;
                foreach (var kvp in guidToPath)
                {
                    content ??= File.ReadAllText(candidate);
                    if (!content.Contains(kvp.Key)) continue;

                    if (reverseDeps.TryGetValue(kvp.Value, out var list) &&
                        !list.Contains(candidate))
                        list.Add(candidate);
                }
            }
        }

        private static bool IsGuid(string input)
        {
            if (string.IsNullOrEmpty(input) || input.Length != 32) return false;
            for (var i = 0; i < 32; i++)
                if (!IsHexDigit(input[i])) return false;
            return true;
        }

        private static bool IsHexDigit(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private static List<string> EmptyList() => new List<string>();
    }

    public class ReferenceGraphOptions
    {
        public static readonly ReferenceGraphOptions Default = new ReferenceGraphOptions();

        public bool ScanAssetReferences;
        public bool ScanTerrainData;
        public bool BinarySerialization;

        // When non-empty, the reverse-dependency scan is restricted to assets
        // under these folder roots instead of AssetDatabase.GetAllAssetPaths()
        // (which would walk every package asset too). A queried asset that
        // lives outside the roots is still resolvable: every dependency we
        // encounter gets a reverse entry on demand. Used by tests to scope
        // scans to a fixture folder and avoid a full-project walk.
        public List<string> ScanRoots;
    }
}
